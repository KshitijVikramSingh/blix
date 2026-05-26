using Silk.NET.Vulkan;

namespace Blix.Graphics.Vulkan;

// Backend compile + teardown for the render graph (Vector B VB.iii).
//
// Allocates Vulkan resources for each declared GraphResource (VkImage +
// DeviceMemory + ImageView), per-pass machinery (VkRenderPass +
// VkFramebuffer + DescriptorPool), and registers color resources as
// sampleable VkTextureEntry so downstream passes can Read them through
// the existing texture-binding path.
//
// VB.iii scope:
//   - Resource allocation (this file)
//   - Per-pass render pass + framebuffer + descriptor pool (this file)
//   - MoltenVK cube-face smoke gate (this file, conditional on cube
//     declarations)
//   - Hooked into RenderGraph.Compile() after validation (VB.ii)
//
// Deferred to later sub-steps:
//   - Pipeline creation per (pass, shader) — VB.v (graph creates these
//     lazily on first draw or eagerly at compile, decide there)
//   - Barrier inference for cross-pass Reads — VB.iv
//   - Per-frame execute that records into RenderCommandList — VB.v
//   - Resize handling + pipeline-cache invalidation — VB.vi

public sealed partial class RenderGraph : IDisposable
{
    // Per-graph backend state, populated by CompileBackend.
    internal Dictionary<int, GraphBackendResource> BackendResources { get; } = new();
    internal Dictionary<int, GraphBackendPass> BackendPasses { get; } = new();
    internal bool BackendCompiled { get; private set; }

    // VB.iii backend compile. Called from Compile() after Validate
    // succeeds, but only when Device is non-null (test graphs that
    // construct via the internal parameterless ctor skip this — their
    // Compile is validation-only).
    internal void CompileBackend()
    {
        if (Device is null) return; // Test-mode graph; skip backend phase.

        AllocateResources();
        BuildPerPassMachinery();
        RegisterPerPassSurfaces();
        if (HasAnyCubeResource()) MoltenVkCubeSmokeGate();
        BackendCompiled = true;
    }

    // VB.v.b — Register each compiled graphics pass as a synthetic
    // RenderSurface entry so the existing command-list routing
    // (VulkanGraphicsDevice.Swapchain.cs Execute loop) can resolve
    // RenderPassDescription.Target to the graph's VkRenderPass +
    // Framebuffer. External entries skip device teardown.
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
            bpass.HasDepth = GraphicsPasses.TryGetValue(passId, out var gp) && gp.Depth is not null;
            bpass.SurfaceHandle = device.RegisterExternalRenderSurface(
                name: $"graph.{passName}",
                renderPass: bpass.RenderPass,
                framebuffer: bpass.Framebuffer,
                width: bpass.Width,
                height: bpass.Height,
                hasDepth: bpass.HasDepth);
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

    // VB.vi — Resize handler. Fired by VulkanGraphicsDevice after the
    // swapchain + dependent resources have been rebuilt. Walks
    // matchSwapchain-sized graph resources, destroys the old VkImage +
    // memory + views, allocates new at the new extent, and MUTATES the
    // existing VkTextureEntry + VkRenderSurfaceEntry in place so cached
    // TextureHandle / RenderSurfaceHandle ids stay valid.
    //
    // VkRenderPass objects stay (format unchanged on resize → render-pass
    // compatibility unchanged → pipelines stay valid). VkFramebuffer needs
    // recreation since it embeds VkImageView handles. Sampler stays.
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

        // 2. Rebuild per-pass framebuffers. Render passes + descriptor
        // pools stay (format + size-independent setup). Update each
        // synthetic RenderSurfaceEntry in place so its handle stays valid.
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

            // Recompute extent from the first attachment (matches VB.iii.c).
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
            if (p.DescriptorPool.Handle != 0) device.Vk.DestroyDescriptorPool(device.Device, p.DescriptorPool, null);
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

    // --- VB.iii.b — Resource allocation ----------------------------------

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
        var (image, memory, view) = device.AllocateAttachmentImage(
            width, height, format,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
            ImageAspectFlags.ColorBit,
            $"graph.{resource.Name}");

        // Register as a sampleable texture for downstream Reads. Sampler
        // default: LinearClamp matches the common "sample fullscreen
        // intermediate" pattern (step-4 present pass uses LinearClamp).
        var sampler = device.GetOrCreateSampler(SamplerDescription.LinearClamp);
        var handle = device.RegisterExternalTexture(
            image, view, sampler,
            (int)width, (int)height, mipCount: 1, format,
            $"graph.{resource.Name}.tex");

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

    private unsafe void AllocateDepthTarget(GraphResourceEntry resource, uint width, uint height)
    {
        var device = Device!;
        var (image, memory, view) = device.AllocateAttachmentImage(
            width, height, device.GraphDepthFormat,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            ImageAspectFlags.DepthBit,
            $"graph.{resource.Name}");

        BackendResources[resource.Handle.Id] = new GraphBackendResource
        {
            Image = image,
            Memory = memory,
            WholeImageView = view,
            // SampleableHandle deferred — shadow-map sampling lands in step 6.
            Width = width,
            Height = height,
            Format = device.GraphDepthFormat,
        };
    }

    // Cube depth attachment: ImageType.Type2D with CubeCompatible flag,
    // 6 array layers, 6 per-face Type2D views (for attachment use), 1
    // whole-cube TypeCube view (for sampling — sampleable-depth wiring
    // is step 6).
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

        BackendResources[resource.Handle.Id] = new GraphBackendResource
        {
            Image = image,
            Memory = memory,
            WholeImageView = wholeView,
            FaceViews = faceViews,
            // SampleableHandle deferred — same as DepthTarget.
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

    // --- VB.iii.c — Per-pass render pass + framebuffer + descriptor pool ---

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
        var device = Device!;
        // Resolve the framebuffer extent from the first attachment. All
        // attachments must agree on size (Vulkan requirement); VB.iii.c
        // trusts the caller. ShaderLab port may surface a violation here;
        // add an explicit check then.
        var firstView = pass.ColorTargets.Count > 0
            ? pass.ColorTargets[0].View
            : pass.Depth!.View;
        var firstResource = BackendResources[firstView.Resource.Id];
        var width = firstResource.Width;
        var height = firstResource.Height;

        var renderPass = CreateGraphicsPassRenderPass(pass);
        var framebuffer = CreatePassFramebuffer(pass, renderPass, width, height);
        var descriptorPool = pass.Shaders.Count > 0
            ? CreatePerPassDescriptorPool(pass.Shaders[0])
            : default;

        return new GraphBackendPass
        {
            RenderPass = renderPass,
            Framebuffer = framebuffer,
            DescriptorPool = descriptorPool,
            Width = width,
            Height = height,
        };
    }

    private unsafe GraphBackendPass BuildComputePassBackend(ComputePassEntry pass)
    {
        // Compute passes have no render pass / framebuffer. Descriptor pool
        // still needed for set 0 + set 1.
        var pool = pass.Shader is { } s
            ? CreatePerPassDescriptorPool(s)
            : default;
        return new GraphBackendPass
        {
            RenderPass = default,
            Framebuffer = default,
            DescriptorPool = pool,
            Width = 0,
            Height = 0,
        };
    }

    private unsafe Silk.NET.Vulkan.RenderPass CreateGraphicsPassRenderPass(GraphicsPassEntry pass)
    {
        var device = Device!;
        var attachmentCount = pass.ColorTargets.Count + (pass.Depth is not null ? 1 : 0);
        var attachments = stackalloc AttachmentDescription[attachmentCount];
        var colorRefs = stackalloc AttachmentReference[Math.Max(1, pass.ColorTargets.Count)];

        for (var i = 0; i < pass.ColorTargets.Count; i++)
        {
            var target = pass.ColorTargets[i];
            var resource = BackendResources[target.View.Resource.Id];
            attachments[i] = new AttachmentDescription
            {
                Format = resource.Format,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = MapLoadOp(target.Load),
                StoreOp = MapStoreOp(target.Store),
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                // Color always lands as SHADER_READ_ONLY_OPTIMAL so downstream
                // Reads sample without a manual barrier. Matches step 4's
                // offscreen-pass posture.
                FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            colorRefs[i] = new AttachmentReference((uint)i, ImageLayout.ColorAttachmentOptimal);
        }

        AttachmentReference depthRef = default;
        var hasDepth = pass.Depth is not null;
        if (hasDepth)
        {
            var depthIdx = pass.ColorTargets.Count;
            var depthResource = BackendResources[pass.Depth!.View.Resource.Id];
            attachments[depthIdx] = new AttachmentDescription
            {
                Format = depthResource.Format,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = MapLoadOp(pass.Depth.Load),
                StoreOp = MapStoreOp(pass.Depth.Store),
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                // Depth finalLayout: SHADER_READ_ONLY_OPTIMAL so the resource
                // is sampleable downstream (shadow maps in step 6). Comes
                // at no cost for pure depth-test passes that don't reuse it.
                FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            depthRef = new AttachmentReference((uint)depthIdx, ImageLayout.DepthStencilAttachmentOptimal);
        }

        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = (uint)pass.ColorTargets.Count,
            PColorAttachments = pass.ColorTargets.Count > 0 ? colorRefs : null,
            PDepthStencilAttachment = hasDepth ? &depthRef : null,
        };

        // Subpass dependency pair from step 4 — covers external↔subpass
        // memory-barrier semantics so downstream Reads see writes.
        var deps = stackalloc SubpassDependency[2];
        deps[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.FragmentShaderBit,
            SrcAccessMask = AccessFlags.ShaderReadBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            DependencyFlags = DependencyFlags.ByRegionBit,
        };
        deps[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.LateFragmentTestsBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            DstStageMask = PipelineStageFlags.FragmentShaderBit,
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
            $"vkCreateRenderPass(graph.{pass.Name})");
        return rp;
    }

    private unsafe Framebuffer CreatePassFramebuffer(
        GraphicsPassEntry pass,
        Silk.NET.Vulkan.RenderPass renderPass,
        uint width, uint height)
    {
        var device = Device!;
        var viewCount = pass.ColorTargets.Count + (pass.Depth is not null ? 1 : 0);
        var views = stackalloc ImageView[viewCount];
        for (var i = 0; i < pass.ColorTargets.Count; i++)
        {
            views[i] = ResolveView(pass.ColorTargets[i].View);
        }
        if (pass.Depth is { } d)
        {
            views[pass.ColorTargets.Count] = ResolveView(d.View);
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

    // Pool sized for set 0 + set 1 of the FIRST declared shader times
    // MaxFramesInFlight. Set-1 layout compatibility across multiple
    // shaders sharing a pass is deferred (per VB.ii note); when that
    // check lands, this sizing widens to the union/identical-required shape.
    private unsafe DescriptorPool CreatePerPassDescriptorPool(ShaderInterface shader)
    {
        var device = Device!;
        var perTypeCount = new Dictionary<DescriptorType, uint>();
        foreach (var slot in shader.Slots)
        {
            if (slot.Set != 0 && slot.Set != 1) continue; // set 2 = material; set 3 = push
            var t = VulkanGraphicsDevice.MapDescriptorType(slot.Type);
            perTypeCount.TryGetValue(t, out var current);
            perTypeCount[t] = current + (uint)(slot.Count * VulkanGraphicsDevice.MaxFramesInFlightConst);
        }
        if (perTypeCount.Count == 0) return default;

        var sizes = stackalloc DescriptorPoolSize[perTypeCount.Count];
        var idx = 0;
        foreach (var (type, count) in perTypeCount)
        {
            sizes[idx++] = new DescriptorPoolSize { Type = type, DescriptorCount = count };
        }
        var poolCi = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = (uint)perTypeCount.Count,
            PPoolSizes = sizes,
            // Two sets per frame (set 0 + set 1) × MaxFramesInFlight.
            MaxSets = 2u * (uint)VulkanGraphicsDevice.MaxFramesInFlightConst,
        };
        DescriptorPool pool;
        VulkanGraphicsDevice.ThrowIfNotSuccess(
            device.Vk.CreateDescriptorPool(device.Device, in poolCi, null, &pool),
            $"vkCreateDescriptorPool(graph.pass)");
        return pool;
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

    // --- VB.iii.e — MoltenVK cube-face smoke gate -----------------------

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
    // Empty for non-cube. For DepthCube: 6 entries, one Type2D view per face.
    public ImageView[] FaceViews = Array.Empty<ImageView>();
    // TextureHandle into device.textureTable for downstream sampling.
    // Set for ColorTarget. Null for DepthTarget / DepthCube in VB.iii
    // (sampleable-depth path lands when ShaderLab port needs shadow-map
    // sampling — likely VB.iv or step 6).
    public TextureHandle? SampleableHandle;
    public uint Width;
    public uint Height;
    public Format Format;
}

internal sealed class GraphBackendPass
{
    public Silk.NET.Vulkan.RenderPass RenderPass;
    public Framebuffer Framebuffer;
    public DescriptorPool DescriptorPool;
    public uint Width;
    public uint Height;
    // Set by VB.v's CompileBackend tail — synthetic RenderSurfaceHandle
    // registered into device.renderSurfaceTable so the existing Execute
    // routing finds this pass's VkRenderPass + Framebuffer. Default
    // (id 0) means "not yet registered" (compute passes, or pre-VB.v
    // state). Unregistered at DestroyBackend.
    public RenderSurfaceHandle SurfaceHandle;
    // Set to true if this pass has a depth attachment, mirroring
    // VkRenderSurfaceEntry.HasDepth so the existing Execute path's
    // clear-value count math works.
    public bool HasDepth;
}
