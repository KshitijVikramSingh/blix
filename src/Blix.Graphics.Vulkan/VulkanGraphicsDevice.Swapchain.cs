using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Blix.Graphics.Vulkan;

// Swapchain + render pass + framebuffers + per-frame command/sync state.
// Lifetime is "the current swapchain" — resize tears these down and rebuilds
// them. The instance / device / surface / command pool / sync primitives are
// long-lived and live in Init.cs.
public sealed partial class VulkanGraphicsDevice
{
    private const int MaxFramesInFlight = 2;

    // Public accessors for per-frame replicated resources (e.g.
    // MaterialBindings with framesInFlight > 1 for bone-palette SSBOs).
    // CurrentFrameSlot is the slot index being recorded right now — write
    // a slot's payload during OnRender and the bind path will pick it up
    // for this frame's draws automatically.
    public int MaxFramesInFlightCount => MaxFramesInFlight;
    public int CurrentFrameSlot => currentFrame;

    internal KhrSwapchain KhrSwapchain { get; private set; } = null!;
    internal SwapchainKHR Swapchain { get; private set; }
    internal Format SwapchainFormat { get; private set; }
    internal Extent2D SwapchainExtent { get; private set; }
    internal Silk.NET.Vulkan.RenderPass DefaultRenderPass { get; private set; }
    // Second render pass with LoadOp.Load on color (preserve previous pass's
    // pixels) — used when a Pass(...) has empty ClearColors. Lets the runtime
    // append a DebugDraw / overlay pass after the main scene without wiping
    // it. Same attachment formats as DefaultRenderPass, so framebuffers are
    // compatible across both.
    internal Silk.NET.Vulkan.RenderPass OverlayRenderPass { get; private set; }

    private Image[] swapchainImages = Array.Empty<Image>();
    private ImageView[] swapchainImageViews = Array.Empty<ImageView>();
    private Framebuffer[] swapchainFramebuffers = Array.Empty<Framebuffer>();
    private Image depthImage;
    private DeviceMemory depthMemory;
    private ImageView depthView;
    private Format depthFormat = Format.D32Sfloat;
    // Internal accessor for cross-file consumers like the render graph backend.
    internal Format GraphDepthFormat => depthFormat;
    // Per-swapchain-image, not per-frame-slot: present may still hold a
    // signal-pending semaphore when our frame-slot ring recycles, so reusing
    // a frame-slot's semaphore across different image indices is unsafe.
    // See validation guidance under VK_KHR_swapchain semaphore reuse.
    private Semaphore[] perImageRenderFinished = Array.Empty<Semaphore>();
    private CommandPool commandPool;
    private FrameResources[] frames = Array.Empty<FrameResources>();
    private int currentFrame;
    private bool needsRecreate;

    // Timestamp query infrastructure. One pool sized for
    // MaxFramesInFlight * QueriesPerFrameSlot so each slot has its own
    // contiguous range; pendingTimingsPerSlot[i] records what was issued
    // on slot i so the NEXT time slot i is used (fence signaled →
    // GPU done) we can read back tick deltas and push them into
    // pendingGpuTimings for the runtime to drain.
    //
    // KNOWN macOS / MoltenVK CAVEAT — same shape as the GL-on-Mac issue
    // documented in renderer.md. MoltenVK's vkCmdWriteTimestamp maps to
    // Metal's MTLCounterSampleBuffer, whose resolution is sometimes still
    // pending after our inFlight VkFence has signaled. The result is
    // vkGetQueryPoolResults returning NotReady indefinitely on Apple
    // Silicon even with ResultWaitBit set. The native Vulkan drivers on
    // Linux + Windows resolve correctly. We degrade gracefully: pending
    // entries stay queued until either (a) the queries eventually report
    // ready (rare on macOS) or (b) the pending list exceeds
    // MaxPendingPerSlot, after which it's flushed to avoid unbounded
    // growth. Timings on macOS are typically absent; CPU timers (`frame`,
    // `build-commands`, `execute`, `swap`) carry the per-frame story
    // instead, same as the GL backend.
    private const uint QueriesPerFrameSlot = 64;
    private const int MaxPendingPerSlot = 32;
    private QueryPool gpuTimingPool;
    private float timestampPeriodNs;
    private bool timestampsSupported;
    private List<PendingPassTiming>[] pendingTimingsPerSlot = Array.Empty<List<PendingPassTiming>>();

    private struct PendingPassTiming
    {
        public string PassName;
        public uint StartIndex;
        public uint EndIndex;
        public int IssuedFrame;
    }

    private struct FrameResources
    {
        public CommandBuffer CommandBuffer;
        public Semaphore ImageAvailable;
        public Fence InFlight;
    }

    private void InitializeSwapchain()
    {
        if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrSwapchain khr))
        {
            throw new InvalidOperationException("VK_KHR_swapchain not available on the logical device.");
        }
        KhrSwapchain = khr;

        CreateSwapchain();
        CreateImageViews();
        CreateDepthBuffer();
        CreateDefaultRenderPass();
        CreateFramebuffers();
        CreateCommandPool();
        CreateFrameResources();
        CreateGpuTimingPool();
    }

    private unsafe void CreateDepthBuffer()
    {
        // D32_SFLOAT is mandatory-supported for depth attachment per the
        // Vulkan spec, no need to probe formats. Single shared depth image
        // is fine — we serialize per-slot rendering via inFlight fences,
        // so no two frames touch the depth buffer simultaneously.
        var ci = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = depthFormat,
            Extent = new Extent3D(SwapchainExtent.Width, SwapchainExtent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Image image;
        ThrowIfNotSuccess(Vk.CreateImage(Device, in ci, null, &image), "vkCreateImage(depth)");
        depthImage = image;

        Vk.GetImageMemoryRequirements(Device, depthImage, out var req);
        var ai = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryTypeIndex(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        DeviceMemory mem;
        ThrowIfNotSuccess(Vk.AllocateMemory(Device, in ai, null, &mem), "vkAllocateMemory(depth)");
        depthMemory = mem;
        ThrowIfNotSuccess(Vk.BindImageMemory(Device, depthImage, depthMemory, 0), "vkBindImageMemory(depth)");

        var viewCi = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = depthImage,
            ViewType = ImageViewType.Type2D,
            Format = depthFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1),
        };
        ImageView view;
        ThrowIfNotSuccess(Vk.CreateImageView(Device, in viewCi, null, &view), "vkCreateImageView(depth)");
        depthView = view;
    }

    private unsafe void CreateGpuTimingPool()
    {
        // Two preconditions for honest GPU timing:
        //   1. The graphics queue family must report TimestampValidBits > 0.
        //   2. The device must report a non-zero TimestampPeriod.
        // Both are true on MoltenVK / Apple Silicon, but cheaper to check
        // than to debug zeros later. If either fails we leave
        // timestampsSupported=false and the per-frame path no-ops cleanly.
        var props = Vk.GetPhysicalDeviceProperties(PhysicalDevice);
        timestampPeriodNs = props.Limits.TimestampPeriod;

        uint qfCount = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &qfCount, null);
        var families = new QueueFamilyProperties[qfCount];
        fixed (QueueFamilyProperties* p = families)
        {
            Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &qfCount, p);
        }
        var validBits = families[GraphicsQueueFamily].TimestampValidBits;
        timestampsSupported = timestampPeriodNs > 0 && validBits > 0;
        if (!timestampsSupported) return;

        var ci = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = QueriesPerFrameSlot * MaxFramesInFlight,
        };
        QueryPool pool;
        ThrowIfNotSuccess(Vk.CreateQueryPool(Device, in ci, null, &pool), "vkCreateQueryPool");
        gpuTimingPool = pool;

        pendingTimingsPerSlot = new List<PendingPassTiming>[MaxFramesInFlight];
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            pendingTimingsPerSlot[i] = new List<PendingPassTiming>(capacity: 16);
        }
    }

    private unsafe void CreateSwapchain()
    {
        SurfaceCapabilitiesKHR caps;
        KhrSurface.GetPhysicalDeviceSurfaceCapabilities(PhysicalDevice, Surface, &caps);

        // Pick format: prefer B8G8R8A8 sRGB so post-process linear→sRGB still
        // works out the same as the GL path; fall back to whatever the
        // surface offers if our preferred format isn't supported.
        uint fmtCount = 0;
        KhrSurface.GetPhysicalDeviceSurfaceFormats(PhysicalDevice, Surface, &fmtCount, null);
        var formats = new SurfaceFormatKHR[fmtCount];
        fixed (SurfaceFormatKHR* p = formats)
        {
            KhrSurface.GetPhysicalDeviceSurfaceFormats(PhysicalDevice, Surface, &fmtCount, p);
        }
        var chosenFormat = formats[0];
        foreach (var f in formats)
        {
            if (f.Format == Format.B8G8R8A8Srgb && f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                chosenFormat = f;
                break;
            }
        }
        SwapchainFormat = chosenFormat.Format;

        // Present mode: prefer MailboxKhr (uncapped, no tearing) for desktop
        // dev so the perf HUD shows true frame cost; fall back to FifoKhr
        // (vsync) which is the universal guaranteed mode. The IRenderHost
        // VSync toggle (set later) can swap between them — currently the
        // swapchain is created once with our preferred mode.
        uint pmCount = 0;
        KhrSurface.GetPhysicalDeviceSurfacePresentModes(PhysicalDevice, Surface, &pmCount, null);
        var presentModes = new PresentModeKHR[pmCount];
        fixed (PresentModeKHR* p = presentModes)
        {
            KhrSurface.GetPhysicalDeviceSurfacePresentModes(PhysicalDevice, Surface, &pmCount, p);
        }
        var chosenPresent = PresentModeKHR.FifoKhr;
        foreach (var pm in presentModes)
        {
            if (pm == PresentModeKHR.MailboxKhr) { chosenPresent = pm; break; }
        }

        // Extent: use currentExtent when the platform pins it (most do); else
        // clamp our framebuffer size to min/max.
        var extent = caps.CurrentExtent;
        if (caps.CurrentExtent.Width == uint.MaxValue)
        {
            extent.Width = Math.Clamp((uint)defaultSurfaceWidth, caps.MinImageExtent.Width, caps.MaxImageExtent.Width);
            extent.Height = Math.Clamp((uint)defaultSurfaceHeight, caps.MinImageExtent.Height, caps.MaxImageExtent.Height);
        }
        SwapchainExtent = extent;

        var imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount) imageCount = caps.MaxImageCount;

        var ci = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = Surface,
            MinImageCount = imageCount,
            ImageFormat = chosenFormat.Format,
            ImageColorSpace = chosenFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = chosenPresent,
            Clipped = true,
            OldSwapchain = default,
        };

        SwapchainKHR sc;
        var result = KhrSwapchain.CreateSwapchain(Device, in ci, null, &sc);
        ThrowIfNotSuccess(result, "vkCreateSwapchainKHR");
        Swapchain = sc;

        uint actualCount = 0;
        KhrSwapchain.GetSwapchainImages(Device, Swapchain, &actualCount, null);
        swapchainImages = new Image[actualCount];
        fixed (Image* p = swapchainImages)
        {
            KhrSwapchain.GetSwapchainImages(Device, Swapchain, &actualCount, p);
        }

        var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        perImageRenderFinished = new Semaphore[actualCount];
        for (var i = 0; i < actualCount; i++)
        {
            Semaphore s;
            ThrowIfNotSuccess(Vk.CreateSemaphore(Device, in semInfo, null, &s), "vkCreateSemaphore(renderFinished)");
            perImageRenderFinished[i] = s;
        }
    }

    private unsafe void CreateImageViews()
    {
        swapchainImageViews = new ImageView[swapchainImages.Length];
        for (var i = 0; i < swapchainImages.Length; i++)
        {
            var ci = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = SwapchainFormat,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = new ImageSubresourceRange(
                    ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            ImageView view;
            ThrowIfNotSuccess(Vk.CreateImageView(Device, in ci, null, &view), "vkCreateImageView");
            swapchainImageViews[i] = view;
        }
    }

    private unsafe void CreateDefaultRenderPass()
    {
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription
        {
            Format = SwapchainFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };
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
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PDepthStencilAttachment = &depthRef,
        };
        // External → subpass dependency. Conservative shape used identically
        // by both DefaultRenderPass and OverlayRenderPass — the spec requires
        // dependencies to MATCH for two render passes to be framebuffer-
        // compatible (validator flags any diff). Shape covers both clear-
        // and load-mode load ops plus depth attachment transitions.
        var dep = SharedSubpassDependency();

        var ci = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dep,
        };
        Silk.NET.Vulkan.RenderPass rp;
        ThrowIfNotSuccess(Vk.CreateRenderPass(Device, in ci, null, &rp), "vkCreateRenderPass");
        DefaultRenderPass = rp;

        CreateOverlayRenderPass();
    }

    private static SubpassDependency SharedSubpassDependency() => new SubpassDependency
    {
        SrcSubpass = Vk.SubpassExternal,
        DstSubpass = 0,
        SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
        SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
        DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
        DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
    };

    private unsafe void CreateOverlayRenderPass()
    {
        // Same attachment shape, but LoadOp.Load on color so we paint OVER
        // whatever the default pass left on the swapchain image. Depth is
        // DontCare both ways — overlay draws don't depth-test against the
        // scene; lines are intentionally always-on-top for debugging.
        // initialLayout=PresentSrcKhr matches the default pass's finalLayout,
        // so the layout transition is correct in a two-pass frame.
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription
        {
            Format = SwapchainFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.PresentSrcKhr,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };
        attachments[1] = new AttachmentDescription
        {
            Format = depthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PDepthStencilAttachment = &depthRef,
        };
        var dep = SharedSubpassDependency();
        var ci = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dep,
        };
        Silk.NET.Vulkan.RenderPass rp;
        ThrowIfNotSuccess(Vk.CreateRenderPass(Device, in ci, null, &rp), "vkCreateRenderPass(overlay)");
        OverlayRenderPass = rp;
    }

    private unsafe void CreateFramebuffers()
    {
        swapchainFramebuffers = new Framebuffer[swapchainImageViews.Length];
        var attachments = stackalloc ImageView[2];
        attachments[1] = depthView;
        for (var i = 0; i < swapchainImageViews.Length; i++)
        {
            attachments[0] = swapchainImageViews[i];
            var ci = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = DefaultRenderPass,
                AttachmentCount = 2,
                PAttachments = attachments,
                Width = SwapchainExtent.Width,
                Height = SwapchainExtent.Height,
                Layers = 1,
            };
            Framebuffer fb;
            ThrowIfNotSuccess(Vk.CreateFramebuffer(Device, in ci, null, &fb), "vkCreateFramebuffer");
            swapchainFramebuffers[i] = fb;
        }
    }

    private unsafe void CreateCommandPool()
    {
        var ci = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = GraphicsQueueFamily,
        };
        CommandPool pool;
        ThrowIfNotSuccess(Vk.CreateCommandPool(Device, in ci, null, &pool), "vkCreateCommandPool");
        commandPool = pool;
    }

    private unsafe void CreateFrameResources()
    {
        frames = new FrameResources[MaxFramesInFlight];

        var ai = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = MaxFramesInFlight,
        };
        var buffers = new CommandBuffer[MaxFramesInFlight];
        fixed (CommandBuffer* p = buffers)
        {
            ThrowIfNotSuccess(Vk.AllocateCommandBuffers(Device, in ai, p), "vkAllocateCommandBuffers");
        }

        var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            // Signaled so the first frame's WaitForFences passes immediately
            // instead of blocking on a fence that no submit has touched yet.
            Flags = FenceCreateFlags.SignaledBit,
        };
        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            Semaphore ia;
            Fence inf;
            ThrowIfNotSuccess(Vk.CreateSemaphore(Device, in semInfo, null, &ia), "vkCreateSemaphore(imageAvailable)");
            ThrowIfNotSuccess(Vk.CreateFence(Device, in fenceInfo, null, &inf), "vkCreateFence(inFlight)");
            frames[i] = new FrameResources
            {
                CommandBuffer = buffers[i],
                ImageAvailable = ia,
                InFlight = inf,
            };
        }
    }

    // Walks command list, drives the per-frame Vulkan flow: wait for prior
    // frame's GPU work, acquire next swapchain image, record clear-only
    // commands for each pass that targets the default surface, submit, and
    // present. Drawing per-pass (DrawIndexedCommand) is a future-push
    // concern — current responsibility is just clearing to the requested
    // color, which is enough to demonstrate end-to-end Vk on screen.
    private unsafe bool AcquireRecordSubmitPresent(RenderCommandList commandList)
    {
        if (needsRecreate)
        {
            RecreateSwapchain();
            needsRecreate = false;
            return false;
        }

        ref var f = ref frames[currentFrame];
        Vk.WaitForFences(Device, 1, in f.InFlight, true, ulong.MaxValue);

        // GPU work on this slot's previous submission is now complete —
        // safe to read back its timestamp queries before reusing the slot.
        DrainSlotTimings(currentFrame);

        uint imageIndex;
        var acquire = KhrSwapchain.AcquireNextImage(
            Device, Swapchain, ulong.MaxValue, f.ImageAvailable, default, &imageIndex);
        if (acquire == Result.ErrorOutOfDateKhr)
        {
            needsRecreate = true;
            return false;
        }
        if (acquire != Result.Success && acquire != Result.SuboptimalKhr)
        {
            ThrowIfNotSuccess(acquire, "vkAcquireNextImageKHR");
        }

        Vk.ResetFences(Device, 1, in f.InFlight);
        Vk.ResetCommandBuffer(f.CommandBuffer, 0);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        ThrowIfNotSuccess(Vk.BeginCommandBuffer(f.CommandBuffer, in beginInfo), "vkBeginCommandBuffer");

        // Reset this slot's range of the query pool before any writes.
        // vkCmdResetQueryPool must run outside a render pass — convenient
        // here because we haven't entered one yet.
        var slotQueryBase = (uint)(currentFrame * (int)QueriesPerFrameSlot);
        if (timestampsSupported)
        {
            Vk.CmdResetQueryPool(f.CommandBuffer, gpuTimingPool, slotQueryBase, QueriesPerFrameSlot);
        }
        var nextQueryIndex = slotQueryBase;

        // Reused across all passes this frame — Vulkan's render-pass-begin
        // reads from the pointer at vkCmdBeginRenderPass time, so the buffer
        // only needs to be valid for the duration of that one call. Allocating
        // it inside the loop trips CA2014 (stack growth across iterations).
        // Sized for the largest attachment count we currently produce:
        // N color + 1 depth. 8 is headroom for a future multi-target G-buffer
        // pass — bump if a pass declares more than 7 color attachments.
        var clearValues = stackalloc ClearValue[8];
        var defaultPasses = 0;
        foreach (var pass in commandList.Passes)
        {
            // Resolve render-pass + framebuffer + extent based on Target.
            // Default target hits the swapchain via DefaultRenderPass /
            // OverlayRenderPass; non-default targets resolve through
            // renderSurfaceTable. Step-4 limitation: offscreen surfaces have
            // only the Clear render-pass variant; passes targeting them must
            // declare clear colors. Vector B's graph will handle Load/Store
            // declaratively.
            VkRenderSurfaceEntry? customSurface = null;
            if (pass.Description.Target.Id != RenderSurfaceHandle.Default.Id)
            {
                if (!renderSurfaceTable.TryGetValue(pass.Description.Target.Id, out customSurface))
                {
                    throw new InvalidOperationException(
                        $"RenderPass '{pass.Name}' targets unknown RenderSurfaceHandle id {pass.Description.Target.Id}.");
                }
            }
            else
            {
                defaultPasses++;
            }

            // Pass selection: a Pass(...) with empty ClearColors on the
            // default target uses OverlayRenderPass (LoadOp.Load) — preserves
            // the previous pass's pixels. Otherwise the default clearing
            // pass. Custom-surface passes always use the surface's own
            // Clear render pass.
            var hasClear = false;
            for (var ci2 = 0; ci2 < pass.Description.ClearColors.Count; ci2++)
            {
                if (pass.Description.ClearColors[ci2].HasValue) { hasClear = true; break; }
            }
            Silk.NET.Vulkan.RenderPass renderPassToUse;
            Framebuffer framebufferToUse;
            Extent2D extentToUse;
            if (customSurface is { } surf)
            {
                renderPassToUse = surf.RenderPass;
                framebufferToUse = surf.Framebuffer;
                extentToUse = new Extent2D(surf.Width, surf.Height);
                // Force-clear on offscreen for now (no Load variant yet).
                hasClear = true;
            }
            else
            {
                renderPassToUse = hasClear ? DefaultRenderPass : OverlayRenderPass;
                framebufferToUse = swapchainFramebuffers[imageIndex];
                extentToUse = SwapchainExtent;
            }

            // Attachment count for clear-values indexing. Vulkan reads
            // pClearValues[i] as the clear for render-pass attachment i,
            // so depth's slot is `colorCount` (not a hardcoded 1).
            //
            //   Default swapchain pass: 1 color + 1 depth
            //   Custom surface pass:    ClearColors.Count color + (HasDepth ? 1 depth : 0)
            //
            // A shadow pass with 0 color attachments needs the depth clear
            // at index 0, NOT index 1 — getting this wrong clears the
            // depth target to a default ClearValue (depth=0.0 reinterpreted
            // from a zero ClearColorValue), which makes every fragment read
            // back depth=0 from a "shadow map" that's all near-plane.
            var colorCount = customSurface is null ? 1 : pass.Description.ClearColors.Count;
            var hasDepthAttachment = customSurface is null ? true : customSurface.HasDepth;
            for (var i = 0; i < colorCount; i++)
            {
                clearValues[i] = default;
                if (hasClear && i < pass.Description.ClearColors.Count
                    && pass.Description.ClearColors[i] is { } cc)
                {
                    clearValues[i].Color = new ClearColorValue(cc.Red, cc.Green, cc.Blue, cc.Alpha);
                }
            }
            if (hasDepthAttachment)
            {
                clearValues[colorCount] = default;
                clearValues[colorCount].DepthStencil = new ClearDepthStencilValue(1.0f, 0);
            }
            var clearCount = (uint)(hasClear ? colorCount + (hasDepthAttachment ? 1 : 0) : 0);
            var rpBegin = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = renderPassToUse,
                Framebuffer = framebufferToUse,
                RenderArea = new Rect2D(new Offset2D(0, 0), extentToUse),
                ClearValueCount = clearCount,
                PClearValues = clearCount > 0 ? clearValues : null,
            };
            var startIndex = nextQueryIndex;
            var endIndex = nextQueryIndex + 1;
            var canTimePass = timestampsSupported && nextQueryIndex + 1 < slotQueryBase + QueriesPerFrameSlot;
            if (!canTimePass) startIndex = endIndex = uint.MaxValue;
            else nextQueryIndex += 2;

            Vk.CmdBeginRenderPass(f.CommandBuffer, in rpBegin, SubpassContents.Inline);
            // Timestamps INSIDE the render pass: MoltenVK maps these to
            // Metal counter samplers that resolve at draw boundaries.
            // ColorAttachmentOutputBit is the natural matching stage on
            // a color-write render pass.
            if (canTimePass)
            {
                Vk.CmdWriteTimestamp(f.CommandBuffer, PipelineStageFlags.ColorAttachmentOutputBit, gpuTimingPool, startIndex);
            }

            // Dynamic viewport+scissor — pipelines declare these as dynamic
            // so a single pipeline survives window resize. Match the current
            // target's extent (swapchain for default, surface size for
            // custom render-surface passes).
            var viewport = new Viewport(0, 0, extentToUse.Width, extentToUse.Height, 0, 1);
            Vk.CmdSetViewport(f.CommandBuffer, 0, 1, in viewport);
            var scissor = new Rect2D(new Offset2D(0, 0), extentToUse);
            Vk.CmdSetScissor(f.CommandBuffer, 0, 1, in scissor);

            foreach (var renderCmd in pass.Commands)
            {
                if (renderCmd is DrawIndexedCommand d)
                {
                    TranslateDrawIndexed(f.CommandBuffer, d, currentFrame);
                }
            }

            if (canTimePass)
            {
                Vk.CmdWriteTimestamp(f.CommandBuffer, PipelineStageFlags.ColorAttachmentOutputBit, gpuTimingPool, endIndex);
            }
            Vk.CmdEndRenderPass(f.CommandBuffer);

            if (canTimePass)
            {
                pendingTimingsPerSlot[currentFrame].Add(new PendingPassTiming
                {
                    PassName = pass.Name,
                    StartIndex = startIndex,
                    EndIndex = endIndex,
                    IssuedFrame = currentGpuFrameNumber,
                });
            }
        }
        ThrowIfNotSuccess(Vk.EndCommandBuffer(f.CommandBuffer), "vkEndCommandBuffer");

        // Don't submit if there was nothing to clear — keep the frame
        // semaphore state coherent by acquiring then NOT signalling
        // renderFinished. The next acquire would then wait on a never-
        // signalled imageAvailable semaphore, deadlocking. Simplest fix:
        // always submit, even an empty render pass cleared nothing.
        // (We already begin/end an empty render pass above when there
        // are zero default-target passes, but that path produces nothing
        // useful. For the demo's case there's always one pass.)
        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        var imageAvail = f.ImageAvailable;
        var renderDone = perImageRenderFinished[imageIndex];
        var cmd = f.CommandBuffer;
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &imageAvail,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &renderDone,
        };
        ThrowIfNotSuccess(Vk.QueueSubmit(GraphicsQueue, 1, in submit, f.InFlight), "vkQueueSubmit");

        var swapchain = Swapchain;
        var present = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &renderDone,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };
        var presentResult = KhrSwapchain.QueuePresent(GraphicsQueue, in present);
        if (presentResult == Result.ErrorOutOfDateKhr || presentResult == Result.SuboptimalKhr)
        {
            needsRecreate = true;
        }
        else if (presentResult != Result.Success)
        {
            ThrowIfNotSuccess(presentResult, "vkQueuePresentKHR");
        }

        currentFrame = (currentFrame + 1) % MaxFramesInFlight;
        return defaultPasses > 0;
    }

    // Subscribers (currently RenderGraph) get notified after the swapchain
    // and dependent resources have been rebuilt — they then walk their
    // matchSwapchain-sized resources and rebuild them at the new extent.
    // The signal fires under DeviceWaitIdle so subscribers can safely
    // destroy + recreate without worrying about in-flight work.
    internal event Action? SwapchainRecreated;

    private void RecreateSwapchain()
    {
        // Quiet the device first so we don't tear down resources still in use
        // by in-flight frames. Runs only on resize so the stall is rare.
        Vk.DeviceWaitIdle(Device);
        DestroySwapchainResources();
        CreateSwapchain();
        CreateImageViews();
        CreateDepthBuffer();
        // RenderPass + CommandPool + per-frame sync are swapchain-format-
        // and queue-family-bound, both of which are unchanged. Skip the
        // recreate to save work; rebuild framebuffers since the attachment
        // views all changed.
        CreateFramebuffers();

        // Notify graph + future subscribers. DeviceWaitIdle above already
        // quiesced; subscribers can destroy/recreate without further sync.
        SwapchainRecreated?.Invoke();
    }

    private unsafe void DestroySwapchainResources()
    {
        if (depthView.Handle != 0)
        {
            Vk.DestroyImageView(Device, depthView, null);
            depthView = default;
        }
        if (depthImage.Handle != 0)
        {
            Vk.DestroyImage(Device, depthImage, null);
            depthImage = default;
        }
        if (depthMemory.Handle != 0)
        {
            Vk.FreeMemory(Device, depthMemory, null);
            depthMemory = default;
        }
        for (var i = 0; i < swapchainFramebuffers.Length; i++)
        {
            if (swapchainFramebuffers[i].Handle != 0)
            {
                Vk.DestroyFramebuffer(Device, swapchainFramebuffers[i], null);
            }
        }
        swapchainFramebuffers = Array.Empty<Framebuffer>();
        for (var i = 0; i < swapchainImageViews.Length; i++)
        {
            if (swapchainImageViews[i].Handle != 0)
            {
                Vk.DestroyImageView(Device, swapchainImageViews[i], null);
            }
        }
        swapchainImageViews = Array.Empty<ImageView>();
        for (var i = 0; i < perImageRenderFinished.Length; i++)
        {
            if (perImageRenderFinished[i].Handle != 0)
            {
                Vk.DestroySemaphore(Device, perImageRenderFinished[i], null);
            }
        }
        perImageRenderFinished = Array.Empty<Semaphore>();
        if (Swapchain.Handle != 0 && KhrSwapchain is not null)
        {
            KhrSwapchain.DestroySwapchain(Device, Swapchain, null);
            Swapchain = default;
        }
    }

    private unsafe void DestroySwapchain()
    {
        if (Vk is null || Device.Handle == 0) return;
        Vk.DeviceWaitIdle(Device);
        for (var i = 0; i < frames.Length; i++)
        {
            ref var f = ref frames[i];
            if (f.ImageAvailable.Handle != 0) Vk.DestroySemaphore(Device, f.ImageAvailable, null);
            if (f.InFlight.Handle != 0) Vk.DestroyFence(Device, f.InFlight, null);
        }
        frames = Array.Empty<FrameResources>();
        if (commandPool.Handle != 0)
        {
            Vk.DestroyCommandPool(Device, commandPool, null);
            commandPool = default;
        }
        if (gpuTimingPool.Handle != 0)
        {
            Vk.DestroyQueryPool(Device, gpuTimingPool, null);
            gpuTimingPool = default;
        }
        pendingTimingsPerSlot = Array.Empty<List<PendingPassTiming>>();
        DestroySwapchainResources();
        if (DefaultRenderPass.Handle != 0)
        {
            Vk.DestroyRenderPass(Device, DefaultRenderPass, null);
            DefaultRenderPass = default;
        }
        if (OverlayRenderPass.Handle != 0)
        {
            Vk.DestroyRenderPass(Device, OverlayRenderPass, null);
            OverlayRenderPass = default;
        }
    }

    internal void MarkSwapchainOutOfDate()
    {
        needsRecreate = true;
    }

    private unsafe void DrainSlotTimings(int slot)
    {
        if (!timestampsSupported) return;
        var pending = pendingTimingsPerSlot[slot];
        if (pending.Count == 0) return;

        // Batch-fetch the whole slot's query range in one call. The slot's
        // previous submission has fence-signaled, but on MoltenVK the
        // timestamp resolution is async with respect to the fence — pass
        // WaitBit so the loader blocks until the queries are actually
        // available. The wait is short (microseconds) because the GPU
        // really is done by now.
        var slotQueryBase = (uint)(slot * (int)QueriesPerFrameSlot);
        var results = stackalloc ulong[(int)QueriesPerFrameSlot];
        var status = Vk.GetQueryPoolResults(
            Device, gpuTimingPool, slotQueryBase, QueriesPerFrameSlot,
            (nuint)(QueriesPerFrameSlot * sizeof(ulong)),
            results, sizeof(ulong),
            QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit);
        if (status != Result.Success)
        {
            // NotReady on macOS/MoltenVK: see class-level CAVEAT. Leave
            // entries queued for a future retry, but cap to avoid unbounded
            // growth when the queries genuinely never resolve.
            if (pending.Count > MaxPendingPerSlot) pending.Clear();
            return;
        }

        foreach (var t in pending)
        {
            var startTicks = results[t.StartIndex - slotQueryBase];
            var endTicks = results[t.EndIndex - slotQueryBase];
            var deltaTicks = endTicks - startTicks;
            var deltaMs = deltaTicks * timestampPeriodNs / 1_000_000.0;
            pendingGpuTimings.Add(new VkGpuPassTiming(t.PassName, deltaMs, t.IssuedFrame));
        }
        pending.Clear();
    }

    private unsafe void TranslateDrawIndexed(CommandBuffer cmd, DrawIndexedCommand d, int frameSlot)
    {
        var pipe = GetPipeline(d.Pipeline);
        var vb = GetVertexBuffer(d.VertexBuffer);
        var ib = GetIndexBuffer(d.IndexBuffer);
        var prog = shaderProgramTable[pipe.ShaderProgram.Id];

        // Per-draw uniforms → UBO offsets. Name-keyed writes search every
        // buffer slot across every declared set for the first matching
        // member; first match wins. Image/sampler slots are skipped (their
        // binding is image-handle-based, not uniform-name based).
        // FRICTION: today every draw clobbers the SAME per-frame UBO. Multiple
        // draws sharing the program but with different uniform values would
        // collide. Need either per-draw descriptor sets, dynamic-offset UBOs,
        // or push constants — none modeled in the cross-backend API yet.
        // (Logged as F-007 in docs/vulkan-friction.md; per-draw lifetime
        // lands in Vector A 2e via push constants.)
        if (d.Uniforms.Count > 0) WriteUniformsAcrossSets(prog, frameSlot, d.Uniforms);
        if (d.Textures.Count > 0) WriteTextureBindings(prog, frameSlot, d.Textures);

        Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipe.Pipeline);

        // Bind every declared set at its set index. Gap sets and
        // material-owned sets (PerFrame.Length == 0) are skipped here —
        // material-owned sets get bound below via the MaterialBindings.
        for (var setIdx = 0; setIdx < prog.Sets.Length; setIdx++)
        {
            if (prog.Sets[setIdx] is not { } sr || sr.PerFrame.Length == 0) continue;
            var ds = sr.PerFrame[frameSlot];
            Vk.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                pipe.Layout,
                firstSet: (uint)setIdx,
                descriptorSetCount: 1,
                &ds,
                dynamicOffsetCount: 0,
                pDynamicOffsets: null);
        }

        // Bind the material's descriptor set at its declared set index.
        // For static materials (FramesInFlight == 1) Sets[0] is the single
        // long-lived set. For per-frame replicated materials, Sets[frameSlot]
        // selects the slot whose buffer was written this frame.
        // Material lookup helper: static materials (FramesInFlight == 1)
        // always pick Sets[0]; per-frame replicated materials pick the
        // slot matching the current frame index. Modulo handles both
        // shapes uniformly so the legacy single-set static path keeps
        // working without a special branch.
        if (d.Material is { } matHandle)
        {
            var mat = materialTable[matHandle.Id];
            var matSet = mat.Sets[frameSlot % mat.FramesInFlight];
            Vk.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                pipe.Layout,
                firstSet: (uint)mat.SetIndex,
                descriptorSetCount: 1,
                &matSet,
                dynamicOffsetCount: 0,
                pDynamicOffsets: null);
        }
        if (d.PerDrawMaterial is { } perDrawHandle)
        {
            var perDraw = materialTable[perDrawHandle.Id];
            var perDrawSet = perDraw.Sets[frameSlot % perDraw.FramesInFlight];
            Vk.CmdBindDescriptorSets(
                cmd,
                PipelineBindPoint.Graphics,
                pipe.Layout,
                firstSet: (uint)perDraw.SetIndex,
                descriptorSetCount: 1,
                &perDrawSet,
                dynamicOffsetCount: 0,
                pDynamicOffsets: null);
        }

        // Push constants — slice the byte payload across the program's
        // declared PushConstantRanges and emit one vkCmdPushConstants per
        // range. For the cube demo this is one range (Vertex, 0, 64).
        if (d.PushConstants is { } pcBytes)
        {
            PushConstantsToCommandBuffer(cmd, pipe.Layout, prog.Interface.PushConstants, pcBytes);
        }

        ulong offset = 0;
        var buffer = vb.Buffer;
        Vk.CmdBindVertexBuffers(cmd, 0, 1, &buffer, &offset);
        Vk.CmdBindIndexBuffer(cmd, ib.Buffer, 0, ib.IndexType);
        Vk.CmdDrawIndexed(cmd, (uint)d.IndexCount, 1, (uint)d.IndexOffset, 0, 0);
    }

    // Translate name-keyed ShaderUniform writes into byte offsets across
    // every UBO/SSBO slot the program declared. First match wins. std140
    // layout assumed.
    //
    // .NET's System.Numerics.Matrix4x4 stores row-major bytes; GLSL std140
    // reads mat4 column-major. That difference IS the transpose we want:
    // .NET's row-vector matrix M_row written directly becomes GLSL's
    // column-vector M_col = M_row^T, and `clip = M_col * v_col` in GLSL
    // is mathematically equivalent to `clip_row = v_row * M_row` in .NET.
    // Writing without an explicit Transpose() is correct — see F-008.
    //
    // For now we map each unique buffer at most once per draw — multiple
    // writes into the same buffer share the mapping. Buffers that receive
    // no writes this draw are not mapped at all.
    // Per-draw scratch reused across calls. The draw path runs on a single
    // thread (the render thread) so one shared pair is safe and saves a
    // dict + list allocation per draw call. Cleared, not re-allocated.
    private readonly Dictionary<(int Set, int Binding), nint> uniformMappedPtrs = new();
    private readonly List<VkBufferEntry> uniformMappedBuffers = new();

    private unsafe void WriteUniformsAcrossSets(
        VkShaderProgramEntry prog,
        int frameSlot,
        IReadOnlyList<ShaderUniform> uniforms)
    {
        // Lazy-mapped per (set, binding) so we touch each underlying buffer
        // exactly once across all uniform writes in this draw.
        uniformMappedPtrs.Clear();
        uniformMappedBuffers.Clear();

        foreach (var u in uniforms)
        {
            if (!FindBufferMember(prog, u.Name, out var setIdx, out var binding, out var member)) continue;
            var buf = prog.Sets[setIdx]!.BuffersPerBinding[binding][frameSlot];
            if (!uniformMappedPtrs.TryGetValue((setIdx, binding), out var ptr))
            {
                void* raw;
                ThrowIfNotSuccess(
                    Vk.MapMemory(Device, buf.Memory, 0, buf.Size, 0, &raw),
                    $"vkMapMemory({prog.Name}.set{setIdx}.binding{binding})");
                ptr = (nint)raw;
                uniformMappedPtrs[(setIdx, binding)] = ptr;
                uniformMappedBuffers.Add(buf);
            }
            var dst = new Span<byte>((void*)ptr, (int)buf.Size);
            WriteUniformValue(dst.Slice(member.Offset, member.Size), u.Value);
        }

        foreach (var buf in uniformMappedBuffers) Vk.UnmapMemory(Device, buf.Memory);
    }

    // Update the per-frame descriptor sets with (texture, sampler) bindings
    // before the descriptor sets get bound for this draw.
    //
    // ShaderTextureBinding.Slot semantics differ per backend:
    //   - GL: GL_TEXTURE0+slot texture unit; uniform sampler points at unit
    //   - Vulkan: the descriptor binding number within the slot's set
    //
    // The set is inferred by matching against the first SampledImage slot
    // in any declared set whose Binding equals the requested number. This
    // works deterministically when at most one set has an image at that
    // binding number — i.e. SETS 0 OR 1 ONLY. By engine convention set 2
    // is material-owned (see MaterialBindings) and set 3 is per-draw push
    // constants, so the search is unambiguous in practice as long as
    // sampler bindings stay confined to sets 0–1 in the ShaderInterface.
    //
    // For materials' per-material textures (set 2 by convention),
    // MaterialBindings.SetTexture writes the descriptor directly via
    // explicit (binding) addressing — no name/set inference needed.
    // That's the production path for textures going forward; this inline
    // path remains for shaders that genuinely need a global / per-pass
    // sampler at sets 0 or 1.
    //
    // The descriptor write happens every draw — wasteful when bindings don't
    // change between draws, fine for correctness. A "skip if unchanged"
    // cache keyed on (frameSlot, set, binding) → (texture, sampler) is a
    // follow-up perf optimization.
    private unsafe void WriteTextureBindings(
        VkShaderProgramEntry prog,
        int frameSlot,
        IReadOnlyList<ShaderTextureBinding> bindings)
    {
        foreach (var b in bindings)
        {
            if (b.Slot < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bindings),
                    $"ShaderTextureBinding '{b.Name}' has negative Slot ({b.Slot}).");
            }
            if (!FindImageSlot(prog, b.Slot, out var setIdx, out var binding))
            {
                // No matching SampledImage slot in any declared set. Silently
                // skip to mirror UBO-uniform behavior — programs that don't
                // sample the texture just ignore the binding.
                continue;
            }
            var tex = textureTable[b.Texture.Id];
            var sr = prog.Sets[setIdx]!;
            var imgInfo = new DescriptorImageInfo
            {
                Sampler = tex.Sampler,
                ImageView = tex.View,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = sr.PerFrame[frameSlot],
                DstBinding = (uint)binding,
                // Array element for Count>1 sampler arrays (e.g. uSpotShadowMaps[N]).
                // 0 for the common single-texture binding.
                DstArrayElement = (uint)b.ArrayIndex,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                PImageInfo = &imgInfo,
            };
            Vk.UpdateDescriptorSets(Device, 1, in write, 0, default(CopyDescriptorSet*));
        }
    }

    // Emit vkCmdPushConstants for each declared PushConstantRange in the
    // shader's interface. Bytes are sliced contiguously across ranges
    // matching the offset+size each range declares.
    //
    // The cube demo declares one range (Vertex, 0, 64) carrying uModel —
    // single push. ShaderLab's lit shader will declare two ranges (uModel,
    // uNormalMatrix) totaling 128 bytes — two pushes per draw.
    private unsafe void PushConstantsToCommandBuffer(
        CommandBuffer cmd,
        PipelineLayout layout,
        IReadOnlyList<PushConstantRange> ranges,
        byte[] payload)
    {
        if (ranges.Count == 0)
        {
            throw new InvalidOperationException(
                "DrawIndexedCommand.PushConstants supplied but the shader interface declares no push-constant ranges.");
        }

        var totalDeclared = 0;
        foreach (var r in ranges) totalDeclared += r.Size;
        if (payload.Length != totalDeclared)
        {
            throw new InvalidOperationException(
                $"DrawIndexedCommand.PushConstants payload length {payload.Length} does not match the shader's declared total push-constant size {totalDeclared}.");
        }

        fixed (byte* basePtr = payload)
        {
            foreach (var r in ranges)
            {
                Vk.CmdPushConstants(
                    cmd,
                    layout,
                    MapStageFlags(r.Stages),
                    (uint)r.Offset,
                    (uint)r.Size,
                    basePtr + r.Offset);
            }
        }
    }

    private static bool FindImageSlot(VkShaderProgramEntry prog, int bindingNumber, out int setIdx, out int binding)
    {
        for (var i = 0; i < prog.Sets.Length; i++)
        {
            if (prog.Sets[i] is not { } sr) continue;
            foreach (var slot in sr.Slots)
            {
                if (slot.Binding != bindingNumber) continue;
                if (slot.Type is not (ShaderResourceType.SampledImage or ShaderResourceType.StorageImage or ShaderResourceType.Sampler)) continue;
                setIdx = i;
                binding = slot.Binding;
                return true;
            }
        }
        setIdx = 0;
        binding = 0;
        return false;
    }

    private static bool FindBufferMember(
        VkShaderProgramEntry prog,
        string name,
        out int setIdx,
        out int binding,
        out UniformBlockMember member)
    {
        for (var i = 0; i < prog.Sets.Length; i++)
        {
            if (prog.Sets[i] is not { } sr) continue;
            foreach (var slot in sr.Slots)
            {
                if (slot.BlockLayout is not { } block) continue;
                for (var j = 0; j < block.Members.Count; j++)
                {
                    if (block.Members[j].Name != name) continue;
                    setIdx = i;
                    binding = slot.Binding;
                    member = block.Members[j];
                    return true;
                }
            }
        }
        setIdx = 0;
        binding = 0;
        member = null!;
        return false;
    }

    private static unsafe void WriteUniformValue(Span<byte> dst, ShaderUniformValue value)
    {
        switch (value)
        {
            case Matrix4x4Uniform m:
                // No explicit transpose — see comment on WriteUniformsToUbo.
                var mat = m.Value;
                fixed (byte* p = dst) *((System.Numerics.Matrix4x4*)p) = mat;
                break;
            case Vector4Uniform v4:
                fixed (byte* p = dst) *((System.Numerics.Vector4*)p) = v4.Value;
                break;
            case Vector3Uniform v3:
                // std140: vec3 occupies 12 bytes but is 16-byte-aligned —
                // caller's UniformBlockLayout offsets reflect that padding;
                // we only write the 12 useful bytes.
                fixed (byte* p = dst) *((System.Numerics.Vector3*)p) = v3.Value;
                break;
            case Vector2Uniform v2:
                fixed (byte* p = dst) *((System.Numerics.Vector2*)p) = v2.Value;
                break;
            case FloatUniform f:
                fixed (byte* p = dst) *((float*)p) = f.Value;
                break;
            // Array variants + Matrix4x4ArrayUniform deferred — friction
            // for the array-stride+padding cases logged as F-009.
            default:
                throw new NotImplementedException($"Vulkan UBO write for {value.GetType().Name} not implemented yet.");
        }
    }
}
