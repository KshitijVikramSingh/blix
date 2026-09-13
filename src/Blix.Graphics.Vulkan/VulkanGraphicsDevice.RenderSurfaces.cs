using Silk.NET.Vulkan;

namespace Blix.Graphics.Vulkan;

// Offscreen render targets: one VkImage per declared color slot
// (registered as a sampleable VkTextureEntry), an optional depth
// attachment, plus a VkRenderPass + VkFramebuffer.
//
// Color final layout is ShaderReadOnlyOptimal so a downstream sampling
// pass doesn't need a manual barrier — the render pass + subpass dep
// pair handles both the layout transition and the memory dependency.
//
// Multi-color attachments are supported by shape but untested. Depth
// today is render-target only (DepthRenderbuffer); DepthTexture /
// DepthCubeFace are reserved enum cases without code paths.
public sealed partial class VulkanGraphicsDevice
{
    private readonly Dictionary<int, VkRenderSurfaceEntry> renderSurfaceTable = new();

    internal sealed class VkRenderSurfaceEntry
    {
        public string Name = string.Empty;
        public uint Width;
        public uint Height;
        public TextureHandle[] ColorAttachments = Array.Empty<TextureHandle>();
        // DepthRenderbuffer is not sampleable; a sampleable depth path
        // would add a TextureHandle field alongside these.
        public Image DepthImage;
        public DeviceMemory DepthMemory;
        public ImageView DepthView;
        public bool HasDepth;
        // MSAA sample count of this surface's colour attachments; pipelines
        // created against it must set rasterizationSamples to match.
        public SampleCountFlags Samples = SampleCountFlags.Count1Bit;
        // Per-surface render pass + framebuffer.
        public Silk.NET.Vulkan.RenderPass RenderPass;

        // LoadOp.Load form, when the owner provides one. Zero means "this surface can only
        // be cleared" — which was true of every surface until a debug view tried to draw
        // over a scene target and wiped it instead.
        public Silk.NET.Vulkan.RenderPass RenderPassLoad;
        public Framebuffer Framebuffer;
        public Format ColorFormat;
        // External entries (set by RegisterExternalRenderSurface) shadow
        // resources owned elsewhere — typically a RenderGraph's
        // per-pass VkRenderPass + Framebuffer. The destruction path
        // (DestroyAllRenderSurfaces) skips those entries.
        public bool IsExternal;

        // <b>How many colour attachments this surface's render pass actually has.</b>
        // Not derivable from ColorAttachments, which is deliberately empty for external
        // (graph-owned) entries — and a pass whose clear list is empty then computed a
        // colour count of zero and wrote the DEPTH clear into colour slot 0. Reinterpreted
        // through the union that is float32[4] = {1, 0, 0, 0}: the target cleared to pure
        // red and whatever was in it was destroyed.
        public int ColorAttachmentCount;
    }

    public unsafe RenderSurface CreateRenderSurface(RenderSurfaceDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (description.ColorAttachments.Count == 0)
        {
            throw new ArgumentException(
                "RenderSurface needs at least one color attachment.", nameof(description));
        }

        var (width, height) = ResolveSize(description.Size);
        var entry = new VkRenderSurfaceEntry
        {
            Name = description.Name,
            Width = width,
            Height = height,
        };

        // --- Color attachments -------------------------------------------------
        var colorHandles = new TextureHandle[description.ColorAttachments.Count];
        Format colorFormat = default;
        for (var i = 0; i < description.ColorAttachments.Count; i++)
        {
            var attachment = description.ColorAttachments[i];
            var format = MapTextureFormat(attachment.Format);
            colorFormat = format;

            var (image, memory, view) = AllocateAttachmentImage(
                width, height,
                format,
                // TransferSrcBit so a colour attachment can be READ BACK. Without it the
                // image cannot be a copy source and capture is impossible at the point the
                // image is made, not at the point somebody asks — which is why Blix had no
                // screenshot path at all and TankArena's model fit was dialled in by eye
                // instead. The flag costs nothing on an attachment that is already
                // device-local and sampleable.
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit
                    | ImageUsageFlags.TransferSrcBit,
                ImageAspectFlags.ColorBit,
                $"{description.Name}.color[{i}]");

            // Register as a sampleable texture so present passes can sample
            // through the standard TextureHandle path. Sampler from cache.
            var sampler = GetOrCreateSampler(attachment.Sampler);
            var texEntry = new VkTextureEntry
            {
                Image = image,
                Memory = memory,
                View = view,
                Sampler = sampler,
                Width = (int)width,
                Height = (int)height,
                MipCount = 1,
                Format = format,
                EngineFormat = attachment.Format,
                ByteSize = attachment.Format.TextureByteCount((int)width, (int)height, 1),
                Name = $"{description.Name}.color[{i}].tex",
            };
            var id = nextResourceId++;
            textureTable[id] = texEntry;
            colorHandles[i] = new TextureHandle(id);
        }
        entry.ColorAttachments = colorHandles;
        entry.ColorAttachmentCount = colorHandles.Length;
        entry.ColorFormat = colorFormat;

        // --- Depth attachment --------------------------------------------------
        if (description.Depth is { } depth)
        {
            switch (depth)
            {
                case DepthRenderbuffer:
                    var (img, mem, view) = AllocateAttachmentImage(
                        width, height,
                        depthFormat,
                        ImageUsageFlags.DepthStencilAttachmentBit,
                        ImageAspectFlags.DepthBit,
                        $"{description.Name}.depth");
                    entry.DepthImage = img;
                    entry.DepthMemory = mem;
                    entry.DepthView = view;
                    entry.HasDepth = true;
                    break;
                case DepthTexture:
                case DepthCubeFace:
                    throw new NotImplementedException(
                        $"{depth.GetType().Name} render-surface depth not implemented — use the render graph's depth-target path.");
            }
        }

        // --- Render pass + framebuffer ----------------------------------------
        CreateSurfaceRenderPass(entry);
        CreateSurfaceFramebuffer(entry);

        var surfId = nextResourceId++;
        var handle = new RenderSurfaceHandle(surfId);
        renderSurfaceTable[surfId] = entry;

        return new RenderSurface(
            handle,
            colorHandles,
            DepthTexture: null);
    }

    public unsafe void DestroyRenderSurface(RenderSurfaceHandle handle)
    {
        if (!renderSurfaceTable.Remove(handle.Id, out var entry)) return;

        if (entry.Framebuffer.Handle != 0) Vk.DestroyFramebuffer(Device, entry.Framebuffer, null);
        if (entry.RenderPass.Handle != 0) Vk.DestroyRenderPass(Device, entry.RenderPass, null);
        if (entry.HasDepth)
        {
            if (entry.DepthView.Handle != 0) Vk.DestroyImageView(Device, entry.DepthView, null);
            if (entry.DepthImage.Handle != 0) Vk.DestroyImage(Device, entry.DepthImage, null);
            if (entry.DepthMemory.Handle != 0) Vk.FreeMemory(Device, entry.DepthMemory, null);
        }
        foreach (var color in entry.ColorAttachments) DestroyTexture(color);
    }

    internal VkRenderSurfaceEntry GetRenderSurface(RenderSurfaceHandle h) => renderSurfaceTable[h.Id];

    // Wrap an externally-owned VkRenderPass + Framebuffer as a surface
    // entry so the Target-routing path can resolve it. Destruction skips
    // external entries — the owner (RenderGraph) tears them down itself.
    internal RenderSurfaceHandle RegisterExternalRenderSurface(
        string name,
        Silk.NET.Vulkan.RenderPass renderPass,
        Framebuffer framebuffer,
        uint width, uint height,
        bool hasDepth,
        Silk.NET.Vulkan.RenderPass renderPassLoad = default,
        SampleCountFlags samples = SampleCountFlags.Count1Bit,
        int colorAttachmentCount = 1)
    {
        var entry = new VkRenderSurfaceEntry
        {
            Name = name,
            Width = width,
            Height = height,
            RenderPass = renderPass,
            RenderPassLoad = renderPassLoad,
            Framebuffer = framebuffer,
            HasDepth = hasDepth,
            Samples = samples,
            IsExternal = true,
            ColorAttachmentCount = colorAttachmentCount,
            // Color/depth attachment ownership stays with the caller.
            ColorAttachments = Array.Empty<TextureHandle>(),
        };
        var id = nextResourceId++;
        renderSurfaceTable[id] = entry;
        return new RenderSurfaceHandle(id);
    }

    internal void UnregisterExternalRenderSurface(RenderSurfaceHandle handle)
    {
        renderSurfaceTable.Remove(handle.Id);
    }

    private (uint Width, uint Height) ResolveSize(RenderSurfaceSize size) => size switch
    {
        FixedRenderSurfaceSize fixedSize => ((uint)fixedSize.Width, (uint)fixedSize.Height),
        MatchDefaultRenderSurfaceSize match => (
            (uint)MathF.Max(1, SwapchainExtent.Width * match.Scale),
            (uint)MathF.Max(1, SwapchainExtent.Height * match.Scale)),
        _ => throw new ArgumentException($"Unknown RenderSurfaceSize {size.GetType().Name}", nameof(size)),
    };

    internal unsafe (Image image, DeviceMemory memory, ImageView view) AllocateAttachmentImage(
        uint width, uint height,
        Format format,
        ImageUsageFlags usage,
        ImageAspectFlags aspect,
        string label,
        SampleCountFlags samples = SampleCountFlags.Count1Bit)
    {
        var imageCi = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = samples,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Image image;
        ThrowIfNotSuccess(Vk.CreateImage(Device, in imageCi, null, &image), $"vkCreateImage({label})");

        Vk.GetImageMemoryRequirements(Device, image, out var req);
        var typeIdx = FindMemoryTypeIndex(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit);
        var allocCi = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = typeIdx,
        };
        DeviceMemory memory;
        ThrowIfNotSuccess(Vk.AllocateMemory(Device, in allocCi, null, &memory), $"vkAllocateMemory({label})");
        ThrowIfNotSuccess(Vk.BindImageMemory(Device, image, memory, 0), $"vkBindImageMemory({label})");

        var viewCi = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            Components = new ComponentMapping(
                ComponentSwizzle.Identity, ComponentSwizzle.Identity,
                ComponentSwizzle.Identity, ComponentSwizzle.Identity),
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = aspect,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
        };
        ImageView view;
        ThrowIfNotSuccess(Vk.CreateImageView(Device, in viewCi, null, &view), $"vkCreateImageView({label})");
        return (image, memory, view);
    }

    private unsafe void CreateSurfaceRenderPass(VkRenderSurfaceEntry entry)
    {
        var attachmentCount = entry.HasDepth ? 2 : 1;
        var attachments = stackalloc AttachmentDescription[attachmentCount];
        attachments[0] = new AttachmentDescription
        {
            Format = entry.ColorFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            // SHADER_READ_ONLY_OPTIMAL at end-of-pass so the next pass that
            // samples this surface doesn't need a manual barrier.
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        if (entry.HasDepth)
        {
            attachments[1] = new AttachmentDescription
            {
                Format = depthFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.DontCare,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
            };
        }

        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PDepthStencilAttachment = entry.HasDepth ? &depthRef : null,
        };

        // Two dependencies — external→0 guarantees any prior sampling of this
        // image completes before we overwrite it; 0→external guarantees our
        // color writes complete before the next pass's fragment shader reads.
        var deps = stackalloc SubpassDependency[2];
        deps[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.FragmentShaderBit,
            SrcAccessMask = AccessFlags.ShaderReadBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DependencyFlags = DependencyFlags.ByRegionBit,
        };
        deps[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
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
        ThrowIfNotSuccess(Vk.CreateRenderPass(Device, in ci, null, &rp), $"vkCreateRenderPass({entry.Name})");
        entry.RenderPass = rp;
    }

    private unsafe void CreateSurfaceFramebuffer(VkRenderSurfaceEntry entry)
    {
        var viewCount = entry.HasDepth ? 2 : 1;
        var views = stackalloc ImageView[viewCount];
        // Color attachment 0 is the only one we register today (single-color
        // surfaces are the demo case; multi-color is supported by the
        // attachment array shape but untested).
        var firstColor = textureTable[entry.ColorAttachments[0].Id];
        views[0] = firstColor.View;
        if (entry.HasDepth) views[1] = entry.DepthView;

        var ci = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = entry.RenderPass,
            AttachmentCount = (uint)viewCount,
            PAttachments = views,
            Width = entry.Width,
            Height = entry.Height,
            Layers = 1,
        };
        Framebuffer fb;
        ThrowIfNotSuccess(Vk.CreateFramebuffer(Device, in ci, null, &fb), $"vkCreateFramebuffer({entry.Name})");
        entry.Framebuffer = fb;
    }

    private void DestroyAllRenderSurfaces()
    {
        foreach (var entry in renderSurfaceTable.Values)
        {
            if (entry.IsExternal) continue; // graph (or other owner) handles teardown
            unsafe
            {
                if (entry.Framebuffer.Handle != 0) Vk.DestroyFramebuffer(Device, entry.Framebuffer, null);
                if (entry.RenderPass.Handle != 0) Vk.DestroyRenderPass(Device, entry.RenderPass, null);
                if (entry.HasDepth)
                {
                    if (entry.DepthView.Handle != 0) Vk.DestroyImageView(Device, entry.DepthView, null);
                    if (entry.DepthImage.Handle != 0) Vk.DestroyImage(Device, entry.DepthImage, null);
                    if (entry.DepthMemory.Handle != 0) Vk.FreeMemory(Device, entry.DepthMemory, null);
                }
            }
            // Color textures get destroyed via the texture cleanup pass.
        }
        renderSurfaceTable.Clear();
    }
}
