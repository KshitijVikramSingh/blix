using Silk.NET.Vulkan;

namespace Blix.Graphics.Vulkan;

// Backend compile + teardown for the render graph.
//
// Allocates a VkImage/Memory/ImageView per declared GraphResource, builds
// per-pass machinery (RenderPass + Framebuffer), and registers each color
// resource as a sampleable VkTextureEntry so downstream passes can Read
// it through the standard texture-binding path.

public sealed partial class RenderGraph : IDisposable
{
    internal Dictionary<int, GraphBackendResource> BackendResources { get; } = new();
    internal Dictionary<int, GraphBackendPass> BackendPasses { get; } = new();
    internal bool BackendCompiled { get; private set; }

    internal void CompileBackend()
    {
        if (Device is null) return; // Test-mode graph; skip backend phase.

        AllocateResources();
        BuildPerPassMachinery();
        RegisterPerPassSurfaces();
        if (HasAnyCubeResource()) MoltenVkCubeSmokeGate();
        BackendCompiled = true;
    }

    // Each graphics pass exposes a synthetic RenderSurface so the command
    // list's existing Target-routing resolves to the graph's VkRenderPass +
    // Framebuffer. The surfaceTable entries are marked External and skip
    // device teardown.
    private void RegisterPerPassSurfaces()
    {
        var device = Device!;
        foreach (var passId in PassOrder)
        {
            if (!BackendPasses.TryGetValue(passId, out var bpass)) continue;
            // Compute passes have no render pass + framebuffer; skip.
            if (bpass.RenderPass.Handle == 0) continue;

            var passName = GraphicsPasses.TryGetValue(passId, out var gpass)
                ? gpass.Name
                : $"<pass:{passId}>";
            bpass.HasDepth = gpass is not null && gpass.Depth is not null;
            // Sample count for pipelines created against this pass — taken from
            // its (first) colour target so rasterizationSamples matches the
            // render pass.
            var passSamples = SampleCountFlags.Count1Bit;
            if (gpass is { ColorTargets.Count: > 0 })
                passSamples = SampleCount(Resources[gpass.ColorTargets[0].View.Resource.Id].Samples);
            else if (gpass?.Depth is not null)   // depth-only pass (e.g. MSAA depth pre-pass)
                passSamples = SampleCount(Resources[gpass.Depth.View.Resource.Id].Samples);
            bpass.SurfaceHandle = device.RegisterExternalRenderSurface(
                name: $"graph.{passName}",
                renderPass: bpass.RenderPass,
                framebuffer: bpass.Framebuffer,
                width: bpass.Width,
                height: bpass.Height,
                hasDepth: bpass.HasDepth,
                renderPassLoad: bpass.RenderPassLoad,
                samples: passSamples,
                // How many colour attachments this pass's render pass has. A graph pass can
                // have none — a depth-only shadow caster — and anything routing a
                // commandList.Pass at this surface needs the real number, not a guess.
                colorAttachmentCount: gpass?.ColorTargets.Count ?? 0);
        }
    }

    private bool HasAnyCubeResource()
    {
        foreach (var r in Resources.Values)
            if (r.Kind == GraphResourceKind.DepthCube) return true;
        return false;
    }

    public void Dispose()
    {
        if (Device is null) return;
        Device.SwapchainRecreated -= OnSwapchainRecreated;
        DestroyBackend();
    }

    // Reallocates matchSwapchain-sized resources at the new extent and
    // rebuilds framebuffers. Mutates VkTextureEntry / VkRenderSurfaceEntry
    // in place so cached TextureHandle / RenderSurfaceHandle ids stay live.
    // VkRenderPass + samplers + pipelines survive (format-stable resize).
    private unsafe void OnSwapchainRecreated()
    {
        if (!BackendCompiled || Device is null) return;
        var device = Device;

        // 1. Reallocate matchSwapchain-sized resources in place.
        foreach (var (resourceId, entry) in BackendResources)
        {
            var declared = Resources[resourceId];
            if (declared.Size is not MatchSwapchainGraphSize) continue;

            // Tear down old image + views (whole + cube faces).
            foreach (var v in entry.FaceViews)
            {
                if (v.Handle != 0) device.Vk.DestroyImageView(device.Device, v, null);
            }
            if (entry.WholeImageView.Handle != 0) device.Vk.DestroyImageView(device.Device, entry.WholeImageView, null);
            if (entry.Image.Handle != 0) device.Vk.DestroyImage(device.Device, entry.Image, null);
            if (entry.Memory.Handle != 0) device.Vk.FreeMemory(device.Device, entry.Memory, null);

            // Reallocate at new extent.
            var (width, height) = ResolveSize(declared.Size);
            ReallocateResourceInPlace(entry, declared, width, height);

            // Mutate the registered VkTextureEntry so cached TextureHandles
            // remain valid (the dictionary keeps the same key).
            if (entry.SampleableHandle is { } texHandle)
            {
                var texEntry = device.GetTexture(texHandle);
                texEntry.Image = entry.Image;
                texEntry.View = entry.WholeImageView;
                texEntry.Width = (int)entry.Width;
                texEntry.Height = (int)entry.Height;
            }
        }

        // 2. Rebuild per-pass framebuffers; RenderPass stays (format-stable).
        // Update each synthetic RenderSurfaceEntry in place so the handle
        // remains live.
        foreach (var (passId, bpass) in BackendPasses)
        {
            if (bpass.Framebuffer.Handle == 0) continue; // compute pass
            if (!GraphicsPasses.TryGetValue(passId, out var gpass)) continue;

            // Only rebuild if at least one attachment is a matchSwapchain resource.
            var needsRebuild = false;
            foreach (var t in gpass.ColorTargets)
                if (Resources[t.View.Resource.Id].Size is MatchSwapchainGraphSize) { needsRebuild = true; break; }
            if (!needsRebuild && gpass.Depth is { } d && Resources[d.View.Resource.Id].Size is MatchSwapchainGraphSize)
                needsRebuild = true;
            if (!needsRebuild) continue;

            // Destroy old framebuffer.
            device.Vk.DestroyFramebuffer(device.Device, bpass.Framebuffer, null);

            var firstView = gpass.ColorTargets.Count > 0
                ? gpass.ColorTargets[0].View
                : gpass.Depth!.View;
            var firstResource = BackendResources[firstView.Resource.Id];
            bpass.Width = firstResource.Width;
            bpass.Height = firstResource.Height;

            // Rebuild.
            bpass.Framebuffer = CreatePassFramebuffer(gpass, bpass.RenderPass, bpass.Width, bpass.Height);

            // Mutate the synthetic VkRenderSurfaceEntry in place so cached
            // RenderSurfaceHandles stay valid.
            if (bpass.SurfaceHandle.Id != 0)
            {
                var surfEntry = device.GetRenderSurface(bpass.SurfaceHandle);
                surfEntry.Framebuffer = bpass.Framebuffer;
                surfEntry.Width = bpass.Width;
                surfEntry.Height = bpass.Height;
            }
        }
    }

    // Same logic as Allocate* but writes into the provided existing entry
    // rather than creating a fresh GraphBackendResource. Used by the
    // resize handler to preserve dictionary keys (and thus cached
    // TextureHandles).
    private unsafe void ReallocateResourceInPlace(
        GraphBackendResource entry,
        GraphResourceEntry declared,
        uint width, uint height)
    {
        var device = Device!;
        switch (declared.Kind)
        {
            case GraphResourceKind.ColorTarget:
            {
                var format = VulkanGraphicsDevice.MapTextureFormat(declared.Format!.Value);
                var (img, mem, view) = device.AllocateAttachmentImage(
                    width, height, format,
                    ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
                    ImageAspectFlags.ColorBit,
                    $"graph.{declared.Name}");
                entry.Image = img;
                entry.Memory = mem;
                entry.WholeImageView = view;
                entry.Width = width;
                entry.Height = height;
                entry.Format = format;
                break;
            }
            case GraphResourceKind.DepthTarget:
            {
                var (img, mem, view) = device.AllocateAttachmentImage(
                    width, height, device.GraphDepthFormat,
                    ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
                    ImageAspectFlags.DepthBit,
                    $"graph.{declared.Name}");
                entry.Image = img;
                entry.Memory = mem;
                entry.WholeImageView = view;
                entry.Width = width;
                entry.Height = height;
                entry.Format = device.GraphDepthFormat;
                break;
            }
            case GraphResourceKind.DepthCube:
                // Cubes have FixedGraphSize, never MatchSwapchainGraphSize.
                // Skipped at the call site, so this branch is unreachable.
                throw new InvalidOperationException(
                    "DepthCube resources are not matchSwapchain-sized; resize should skip them.");
        }
    }

    private unsafe void DestroyBackend()
    {
        var device = Device!;

        foreach (var p in BackendPasses.Values)
        {
            // Unregister synthetic render-surface FIRST so the device's
            // own teardown (if it runs after) doesn't see a stale entry.
            if (p.SurfaceHandle.Id != 0) device.UnregisterExternalRenderSurface(p.SurfaceHandle);
            if (p.Framebuffer.Handle != 0) device.Vk.DestroyFramebuffer(device.Device, p.Framebuffer, null);
            if (p.RenderPass.Handle != 0) device.Vk.DestroyRenderPass(device.Device, p.RenderPass, null);
        }
        BackendPasses.Clear();

        foreach (var r in BackendResources.Values)
        {
            // Destroy face views first, then whole-image view, then image+memory.
            foreach (var v in r.FaceViews)
            {
                if (v.Handle != 0) device.Vk.DestroyImageView(device.Device, v, null);
            }
            if (r.WholeImageView.Handle != 0) device.Vk.DestroyImageView(device.Device, r.WholeImageView, null);
            if (r.Image.Handle != 0) device.Vk.DestroyImage(device.Device, r.Image, null);
            if (r.Memory.Handle != 0) device.Vk.FreeMemory(device.Device, r.Memory, null);

            // The TextureHandle (if registered) points at a VkTextureEntry
            // that shadows our graph-owned VkImage. Use the unregister-only
            // path so the device doesn't double-free the image we just
            // destroyed.
            if (r.SampleableHandle is { } th)
            {
                device.UnregisterExternalTexture(th);
            }
        }
        BackendResources.Clear();
        BackendCompiled = false;
    }

    // --- Resource allocation ---------------------------------------------

    private unsafe void AllocateResources()
    {
        var device = Device!;
        foreach (var resource in Resources.Values)
        {
            var (width, height) = ResolveSize(resource.Size);
            switch (resource.Kind)
            {
                case GraphResourceKind.ColorTarget:
                    AllocateColorTarget(resource, width, height);
                    break;
                case GraphResourceKind.DepthTarget:
                    AllocateDepthTarget(resource, width, height);
                    break;
                case GraphResourceKind.DepthCube:
                    AllocateDepthCube(resource, width); // height == width for cube
                    break;
                default:
                    throw new InvalidOperationException($"Unknown GraphResourceKind {resource.Kind}");
            }
        }
    }

    private (uint Width, uint Height) ResolveSize(GraphSize size)
    {
        var device = Device!;
        return size switch
        {
            FixedGraphSize fs => ((uint)fs.Width, (uint)fs.Height),
            MatchSwapchainGraphSize ms => (
                (uint)MathF.Max(1, device.SwapchainExtent.Width * ms.Scale),
                (uint)MathF.Max(1, device.SwapchainExtent.Height * ms.Scale)),
            _ => throw new ArgumentException($"Unknown GraphSize {size.GetType().Name}"),
        };
    }

    private unsafe void AllocateColorTarget(GraphResourceEntry resource, uint width, uint height)
    {
        var device = Device!;
        var format = VulkanGraphicsDevice.MapTextureFormat(resource.Format!.Value);
        var msaa = resource.Samples > 1;
        // MSAA colour is render-and-resolve only — never sampled, so no
        // SampledBit and no sampleable handle. Add TransientAttachment so a
        // tiler can keep it in tile memory (it's resolved before store).
        var usage = msaa
            ? ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransientAttachmentBit
            : ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit;
        var (image, memory, view) = device.AllocateAttachmentImage(
            width, height, format, usage, ImageAspectFlags.ColorBit,
            $"graph.{resource.Name}", SampleCount(resource.Samples));

        TextureHandle? handle = null;
        if (!msaa)
        {
            // Default sampler is LinearClamp — fits the typical "sample a
            // fullscreen intermediate" use.
            var sampler = device.GetOrCreateSampler(SamplerDescription.LinearClamp);
            handle = device.RegisterExternalTexture(
                image, view, sampler,
                (int)width, (int)height, mipCount: 1, format,
                $"graph.{resource.Name}.tex");
        }

        BackendResources[resource.Handle.Id] = new GraphBackendResource
        {
            Image = image,
            Memory = memory,
            WholeImageView = view,
            SampleableHandle = handle,
            Width = width,
            Height = height,
            Format = format,
        };
    }

    // Map an integer sample count to the Vulkan flag.
    private static SampleCountFlags SampleCount(int samples) => samples switch
    {
        <= 1 => SampleCountFlags.Count1Bit,
        2 => SampleCountFlags.Count2Bit,
        4 => SampleCountFlags.Count4Bit,
        8 => SampleCountFlags.Count8Bit,
        _ => throw new ArgumentException($"Unsupported MSAA sample count {samples} (use 1/2/4/8)."),
    };

    private unsafe void AllocateDepthTarget(GraphResourceEntry resource, uint width, uint height)
    {
        var device = Device!;
        var msaa = resource.Samples > 1;
        var usage = msaa
            ? ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.TransientAttachmentBit
            : ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit;
        var (image, memory, view) = device.AllocateAttachmentImage(
            width, height, device.GraphDepthFormat, usage, ImageAspectFlags.DepthBit,
            $"graph.{resource.Name}", SampleCount(resource.Samples));

        TextureHandle? handle = null;
        if (!msaa)
        {
            // LinearClamp default; shaders do the [0,1] range check so
            // ClampToEdge isn't strictly required for shadow sampling.
            var sampler = device.GetOrCreateSampler(SamplerDescription.LinearClamp);
            handle = device.RegisterExternalTexture(
                image, view, sampler,
                (int)width, (int)height, mipCount: 1, device.GraphDepthFormat,
                $"graph.{resource.Name}.tex");
        }

        BackendResources[resource.Handle.Id] = new GraphBackendResource
        {
            Image = image,
            Memory = memory,
            WholeImageView = view,
            SampleableHandle = handle,
            Width = width,
            Height = height,
            Format = device.GraphDepthFormat,
        };
    }

    // Cube depth attachment: Type2D + CubeCompatible, 6 array layers,
    // 6 per-face Type2D views (attachment use), 1 whole-cube TypeCube
    // view (sampler use).
    private unsafe void AllocateDepthCube(GraphResourceEntry resource, uint faceSize)
    {
        var device = Device!;
        var format = device.GraphDepthFormat;

        var imageCi = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            Flags = ImageCreateFlags.CreateCubeCompatibleBit,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(faceSize, faceSize, 1),
            MipLevels = 1,
            ArrayLayers = 6,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Image image;
        VulkanGraphicsDevice.ThrowIfNotSuccess(
            device.Vk.CreateImage(device.Device, in imageCi, null, &image),
            $"vkCreateImage(graph.{resource.Name}.cube)");

        device.Vk.GetImageMemoryRequirements(device.Device, image, out var req);
        var typeIdx = device.FindMemoryTypeIndex(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        var allocCi = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = typeIdx,
        };
        DeviceMemory memory;
        VulkanGraphicsDevice.ThrowIfNotSuccess(
            device.Vk.AllocateMemory(device.Device, in allocCi, null, &memory),
            $"vkAllocateMemory(graph.{resource.Name}.cube)");
        VulkanGraphicsDevice.ThrowIfNotSuccess(
            device.Vk.BindImageMemory(device.Device, image, memory, 0),
            $"vkBindImageMemory(graph.{resource.Name}.cube)");

        // Whole-cube view — for sampling via samplerCube.
        var wholeViewCi = MakeViewCi(image, format, ImageViewType.TypeCube,
            baseLayer: 0, layerCount: 6, aspect: ImageAspectFlags.DepthBit);
        ImageView wholeView;
        VulkanGraphicsDevice.ThrowIfNotSuccess(
            device.Vk.CreateImageView(device.Device, in wholeViewCi, null, &wholeView),
            $"vkCreateImageView(graph.{resource.Name}.cube.whole)");

        // Per-face views — for attachment use, one VkImageView per face.
        var faceViews = new ImageView[6];
        for (var face = 0u; face < 6u; face++)
        {
            var faceViewCi = MakeViewCi(image, format, ImageViewType.Type2D,
                baseLayer: face, layerCount: 1, aspect: ImageAspectFlags.DepthBit);
            ImageView faceView;
            VulkanGraphicsDevice.ThrowIfNotSuccess(
                device.Vk.CreateImageView(device.Device, in faceViewCi, null, &faceView),
                $"vkCreateImageView(graph.{resource.Name}.cube.face{face})");
            faceViews[face] = faceView;
        }

        // The TypeCube view feeds samplerCube through the standard
        // descriptor-write path (it's just an ImageView like any other).
        var sampler = device.GetOrCreateSampler(SamplerDescription.LinearClamp);
        var handle = device.RegisterExternalTexture(
            image, wholeView, sampler,
            (int)faceSize, (int)faceSize, mipCount: 1, format,
            $"graph.{resource.Name}.cube.tex");

        BackendResources[resource.Handle.Id] = new GraphBackendResource
        {
            Image = image,
            Memory = memory,
            WholeImageView = wholeView,
            FaceViews = faceViews,
            SampleableHandle = handle,
            Width = faceSize,
            Height = faceSize,
            Format = format,
        };
    }

    private static ImageViewCreateInfo MakeViewCi(
        Image image, Format format, ImageViewType viewType,
        uint baseLayer, uint layerCount, ImageAspectFlags aspect) => new()
    {
        SType = StructureType.ImageViewCreateInfo,
        Image = image,
        ViewType = viewType,
        Format = format,
        Components = new ComponentMapping(
            ComponentSwizzle.Identity, ComponentSwizzle.Identity,
            ComponentSwizzle.Identity, ComponentSwizzle.Identity),
        SubresourceRange = new ImageSubresourceRange
        {
            AspectMask = aspect,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = baseLayer,
            LayerCount = layerCount,
        },
    };

    // --- Per-pass render pass + framebuffer ------------------------------

    private unsafe void BuildPerPassMachinery()
    {
        foreach (var passId in PassOrder)
        {
            if (GraphicsPasses.TryGetValue(passId, out var gpass))
            {
                BackendPasses[passId] = BuildGraphicsPassBackend(gpass);
            }
            else if (ComputePasses.TryGetValue(passId, out var cpass))
            {
                BackendPasses[passId] = BuildComputePassBackend(cpass);
            }
        }
    }

    private unsafe GraphBackendPass BuildGraphicsPassBackend(GraphicsPassEntry pass)
    {
        // All attachments must agree on size (Vulkan requirement); the
        // first one drives framebuffer extent.
        var firstView = pass.ColorTargets.Count > 0
            ? pass.ColorTargets[0].View
            : pass.Depth!.View;
        var firstResource = BackendResources[firstView.Resource.Id];
        var width = firstResource.Width;
        var height = firstResource.Height;

        var renderPass = CreateGraphicsPassRenderPass(pass);
        var renderPassLoad = CreateGraphicsPassRenderPass(pass, loadVariant: true);

        // One framebuffer serves both: Vulkan render-pass compatibility turns on attachment
        // formats, counts and sample counts, and a load op is none of those.
        var framebuffer = CreatePassFramebuffer(pass, renderPass, width, height);

        return new GraphBackendPass
        {
            RenderPass = renderPass,
            RenderPassLoad = renderPassLoad,
            Framebuffer = framebuffer,
            Width = width,
            Height = height,
        };
    }

    private unsafe GraphBackendPass BuildComputePassBackend(ComputePassEntry pass)
    {
        // No render pass / framebuffer for compute.
        return new GraphBackendPass
        {
            RenderPass = default,
            Framebuffer = default,
            Width = 0,
            Height = 0,
        };
    }

    /// <summary>
    /// The render pass for a graphics pass, optionally in its LOAD form.
    /// </summary>
    /// <remarks>
    /// <b>A second variant exists so something can draw OVER a graph target.</b> A graph pass declares its
    /// own load ops, which is right for the pass itself — but anything routed at the same surface from
    /// outside the graph (the runtime's debug-line pass, aimed at a view whose Target is this surface) got
    /// this pass and therefore its CLEAR. Debug geometry drawn into a scene target landed on a freshly
    /// wiped image: the capture that found it shows a grid and a capsule over nothing at all.
    /// <para>
    /// The load variant differs in exactly two ways — every attachment loads instead of clearing, and the
    /// colour attachments start in SHADER_READ_ONLY_OPTIMAL, which is where this pass's own FinalLayout
    /// left them. Same formats, same counts, same subpass, so it stays render-pass compatible with the
    /// pipelines and the framebuffer built for the clearing form.
    /// </para>
    /// </remarks>
    private unsafe Silk.NET.Vulkan.RenderPass CreateGraphicsPassRenderPass(
        GraphicsPassEntry pass, bool loadVariant = false)
    {
        var device = Device!;
        var colorCount = pass.ColorTargets.Count;
        var hasDepth = pass.Depth is not null;
        var resolveCount = pass.ResolveTargets.Count;
        var attachmentCount = colorCount + (hasDepth ? 1 : 0) + resolveCount;
        var attachments = stackalloc AttachmentDescription[attachmentCount];
        var colorRefs = stackalloc AttachmentReference[Math.Max(1, colorCount)];
        var resolveRefs = stackalloc AttachmentReference[Math.Max(1, colorCount)];

        for (var i = 0; i < colorCount; i++)
        {
            var target = pass.ColorTargets[i];
            var resource = BackendResources[target.View.Resource.Id];
            var samples = Resources[target.View.Resource.Id].Samples;
            var msaa = samples > 1;
            attachments[i] = new AttachmentDescription
            {
                Format = resource.Format,
                Samples = SampleCount(samples),
                LoadOp = loadVariant ? AttachmentLoadOp.Load : MapLoadOp(target.Load),
                // MSAA colour is resolved (not stored/sampled), so DontCare on
                // store; non-MSAA stores for downstream sampling.
                StoreOp = msaa ? AttachmentStoreOp.DontCare : MapStoreOp(target.Store),
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = loadVariant && !msaa
                    ? ImageLayout.ShaderReadOnlyOptimal
                    : ImageLayout.Undefined,
                // Non-MSAA → ShaderReadOnly for downstream Reads. MSAA stays a
                // colour attachment (only the resolve target is sampled).
                FinalLayout = msaa ? ImageLayout.ColorAttachmentOptimal : ImageLayout.ShaderReadOnlyOptimal,
            };
            colorRefs[i] = new AttachmentReference((uint)i, ImageLayout.ColorAttachmentOptimal);
            resolveRefs[i] = new AttachmentReference(Vk.AttachmentUnused, ImageLayout.Undefined);
        }

        AttachmentReference depthRef = default;
        if (hasDepth)
        {
            var depthIdx = colorCount;
            var depthResource = BackendResources[pass.Depth!.View.Resource.Id];
            var depthSamples = Resources[pass.Depth!.View.Resource.Id].Samples;
            var msaaDepth = depthSamples > 1;
            attachments[depthIdx] = new AttachmentDescription
            {
                Format = depthResource.Format,
                Samples = SampleCount(depthSamples),
                LoadOp = MapLoadOp(pass.Depth.Load),
                StoreOp = MapStoreOp(pass.Depth.Store),
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                // LoadOp.Load preserves a prior pass's depth (depth pre-pass →
                // lit), so the initial layout must already be the depth layout —
                // Undefined would discard it. Clear/DontCare start fresh.
                InitialLayout = pass.Depth.Load == LoadOp.Load
                    ? ImageLayout.DepthStencilAttachmentOptimal
                    : ImageLayout.Undefined,
                // Non-MSAA depth stays shader-readable (shadow maps); MSAA
                // depth isn't sampled, so leave it a depth attachment.
                FinalLayout = msaaDepth ? ImageLayout.DepthStencilAttachmentOptimal : ImageLayout.ShaderReadOnlyOptimal,
            };
            depthRef = new AttachmentReference((uint)depthIdx, ImageLayout.DepthStencilAttachmentOptimal);
        }

        // Resolve attachments: 1× destinations for the MSAA colour targets,
        // mapped to colour i by index. Placed after colour + depth.
        var resolveBase = colorCount + (hasDepth ? 1 : 0);
        for (var i = 0; i < resolveCount; i++)
        {
            var resource = BackendResources[pass.ResolveTargets[i].Resource.Id];
            var idx = resolveBase + i;
            attachments[idx] = new AttachmentDescription
            {
                Format = resource.Format,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.DontCare,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            resolveRefs[i] = new AttachmentReference((uint)idx, ImageLayout.ColorAttachmentOptimal);
        }

        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = (uint)colorCount,
            PColorAttachments = colorCount > 0 ? colorRefs : null,
            PResolveAttachments = resolveCount > 0 ? resolveRefs : null,
            PDepthStencilAttachment = hasDepth ? &depthRef : null,
        };

        // External→subpass + subpass→external pair, so downstream sampled
        // Reads observe this pass's writes without a manual barrier.
        var deps = stackalloc SubpassDependency[2];
        deps[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            // Compute included: a prior compute pass may have sampled this
            // target (e.g. froxel fog reading shadow maps) and must finish
            // before we overwrite it. LateFragmentTests+DepthWrite included so
            // a prior pass's depth write (depth pre-pass) is available to this
            // pass's depth load/test (EarlyFragmentTests).
            SrcStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.LateFragmentTestsBit,
            SrcAccessMask = AccessFlags.ShaderReadBit | AccessFlags.DepthStencilAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit,
            DependencyFlags = DependencyFlags.ByRegionBit,
        };
        deps[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.LateFragmentTestsBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            // Compute included so a downstream compute pass (froxel fog) can
            // sample this pass's colour/depth output without a manual barrier.
            DstStageMask = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            DependencyFlags = DependencyFlags.ByRegionBit,
        };

        var ci = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = (uint)attachmentCount,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = deps,
        };
        Silk.NET.Vulkan.RenderPass rp;
        VulkanGraphicsDevice.ThrowIfNotSuccess(
            device.Vk.CreateRenderPass(device.Device, in ci, null, &rp),
            $"vkCreateRenderPass(graph.{pass.Name}{(loadVariant ? ".load" : string.Empty)})");
        return rp;
    }

    private unsafe Framebuffer CreatePassFramebuffer(
        GraphicsPassEntry pass,
        Silk.NET.Vulkan.RenderPass renderPass,
        uint width, uint height)
    {
        var device = Device!;
        var colorCount = pass.ColorTargets.Count;
        var hasDepth = pass.Depth is not null;
        var resolveCount = pass.ResolveTargets.Count;
        // Attachment order must match CreateGraphicsPassRenderPass:
        // [colour…, depth, resolve…].
        var viewCount = colorCount + (hasDepth ? 1 : 0) + resolveCount;
        var views = stackalloc ImageView[viewCount];
        for (var i = 0; i < colorCount; i++)
        {
            views[i] = ResolveView(pass.ColorTargets[i].View);
        }
        if (pass.Depth is { } d)
        {
            views[colorCount] = ResolveView(d.View);
        }
        var resolveBase = colorCount + (hasDepth ? 1 : 0);
        for (var i = 0; i < resolveCount; i++)
        {
            views[resolveBase + i] = ResolveView(pass.ResolveTargets[i]);
        }
        var ci = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = renderPass,
            AttachmentCount = (uint)viewCount,
            PAttachments = views,
            Width = width,
            Height = height,
            Layers = 1,
        };
        Framebuffer fb;
        VulkanGraphicsDevice.ThrowIfNotSuccess(
            device.Vk.CreateFramebuffer(device.Device, in ci, null, &fb),
            $"vkCreateFramebuffer(graph.{pass.Name})");
        return fb;
    }

    private ImageView ResolveView(TextureView view)
    {
        var resource = BackendResources[view.Resource.Id];
        if (view.Face is { } f) return resource.FaceViews[f];
        return resource.WholeImageView;
    }

    private static AttachmentLoadOp MapLoadOp(LoadOp op) => op switch
    {
        LoadOp.Clear => AttachmentLoadOp.Clear,
        LoadOp.Load => AttachmentLoadOp.Load,
        LoadOp.DontCare => AttachmentLoadOp.DontCare,
        _ => AttachmentLoadOp.DontCare,
    };

    private static AttachmentStoreOp MapStoreOp(StoreOp op) => op switch
    {
        StoreOp.Store => AttachmentStoreOp.Store,
        StoreOp.DontCare => AttachmentStoreOp.DontCare,
        _ => AttachmentStoreOp.DontCare,
    };

    // --- MoltenVK cube-face smoke gate -----------------------------------

    // MoltenVK has historical quirks around VK_IMAGE_VIEW_TYPE_2D on a
    // cube image with VK_IMAGE_CREATE_CUBE_COMPATIBLE_BIT. Begin + end a
    // render pass against the first cube's face 0 at startup to surface
    // those quirks early — fails loud with a Vulkan error rather than
    // mysteriously misbehaving at first frame.
    //
    // Reuses the first declared cube resource (no extra allocation).
    // Transient VkRenderPass + VkFramebuffer for the smoke pass; destroyed
    // after the test runs.
    private unsafe void MoltenVkCubeSmokeGate()
    {
        var device = Device!;

        // Find first cube resource (FaceViews.Length == 6).
        GraphBackendResource? cubeResource = null;
        foreach (var r in BackendResources.Values)
        {
            if (r.FaceViews.Length > 0) { cubeResource = r; break; }
        }
        if (cubeResource is null) return; // no cube declared; nothing to test

        // Transient render pass with one depth attachment matching the cube format.
        var attachment = new AttachmentDescription
        {
            Format = cubeResource.Format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var depthRef = new AttachmentReference(0, ImageLayout.DepthStencilAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 0,
            PDepthStencilAttachment = &depthRef,
        };
        var ci = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
        };
        Silk.NET.Vulkan.RenderPass smokePass;
        VulkanGraphicsDevice.ThrowIfNotSuccess(
            device.Vk.CreateRenderPass(device.Device, in ci, null, &smokePass),
            "vkCreateRenderPass(graph.molten-vk-smoke)");

        var faceView = cubeResource.FaceViews[0];
        var fbCi = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = smokePass,
            AttachmentCount = 1,
            PAttachments = &faceView,
            Width = cubeResource.Width,
            Height = cubeResource.Height,
            Layers = 1,
        };
        Framebuffer smokeFb;
        VulkanGraphicsDevice.ThrowIfNotSuccess(
            device.Vk.CreateFramebuffer(device.Device, in fbCi, null, &smokeFb),
            "vkCreateFramebuffer(graph.molten-vk-smoke)");

        // Begin + end the render pass on a single-time command buffer.
        // If MoltenVK rejects the cube-face attachment, vkCmdBeginRenderPass
        // or vkQueueSubmit raises here.
        var cmd = device.BeginSingleTimeCommands();
        var clearVal = new ClearValue();
        clearVal.DepthStencil = new ClearDepthStencilValue(1.0f, 0);
        var rpBegin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = smokePass,
            Framebuffer = smokeFb,
            RenderArea = new Rect2D(new Offset2D(0, 0),
                new Extent2D(cubeResource.Width, cubeResource.Height)),
            ClearValueCount = 1,
            PClearValues = &clearVal,
        };
        device.Vk.CmdBeginRenderPass(cmd, in rpBegin, SubpassContents.Inline);
        device.Vk.CmdEndRenderPass(cmd);
        device.EndSingleTimeCommands(cmd);

        // Teardown transients.
        device.Vk.DestroyFramebuffer(device.Device, smokeFb, null);
        device.Vk.DestroyRenderPass(device.Device, smokePass, null);
    }
}

internal sealed class GraphBackendResource
{
    public Image Image;
    public DeviceMemory Memory;
    public ImageView WholeImageView;
    // DepthCube only: 6 Type2D face views.
    public ImageView[] FaceViews = Array.Empty<ImageView>();
    // Set on color and depth targets — null only for resources never
    // sampled downstream (none in current scope).
    public TextureHandle? SampleableHandle;
    public uint Width;
    public uint Height;
    public Format Format;
}

internal sealed class GraphBackendPass
{
    public Silk.NET.Vulkan.RenderPass RenderPass;

    // The same pass with every attachment loading instead of clearing, for anything routed
    // at this surface from outside the graph that means to draw OVER what is there.
    public Silk.NET.Vulkan.RenderPass RenderPassLoad;

    public Framebuffer Framebuffer;
    public uint Width;
    public uint Height;
    // Synthetic handle registered into device.renderSurfaceTable so the
    // command-list Execute path resolves Target→pass. Default = compute
    // pass / unregistered.
    public RenderSurfaceHandle SurfaceHandle;
    // Mirrors VkRenderSurfaceEntry.HasDepth for the Execute path's
    // clear-value count math.
    public bool HasDepth;
}
