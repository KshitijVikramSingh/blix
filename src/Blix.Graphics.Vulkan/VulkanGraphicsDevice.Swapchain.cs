using System.Diagnostics;
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

    // Per-frame-replicated payloads (e.g. bone palettes) write into
    // CurrentFrameSlot during OnRender; the bind path reads from the
    // matching slot automatically.
    public int MaxFramesInFlightCount => MaxFramesInFlight;
    public int CurrentFrameSlot => currentFrame;

    internal KhrSwapchain KhrSwapchain { get; private set; } = null!;
    internal SwapchainKHR Swapchain { get; private set; }
    internal Format SwapchainFormat { get; private set; }
    internal Extent2D SwapchainExtent { get; private set; }
    internal Silk.NET.Vulkan.RenderPass DefaultRenderPass { get; private set; }
    // LoadOp.Load variant — picked when a Pass(...) has empty ClearColors so
    // an overlay paints over the scene. Framebuffer-compatible with DefaultRenderPass.
    internal Silk.NET.Vulkan.RenderPass OverlayRenderPass { get; private set; }

    private Image[] swapchainImages = Array.Empty<Image>();
    private ImageView[] swapchainImageViews = Array.Empty<ImageView>();
    private Framebuffer[] swapchainFramebuffers = Array.Empty<Framebuffer>();
    private Image depthImage;
    private DeviceMemory depthMemory;
    private ImageView depthView;
    private Format depthFormat = Format.D32Sfloat;
    internal Format GraphDepthFormat => depthFormat;
    // Keyed on swapchain-image, not frame-slot: present can still hold a
    // signal-pending semaphore as the frame-slot ring recycles, so a
    // frame-slot's renderFinished semaphore is unsafe to reuse across
    // different image indices (VK_KHR_swapchain semaphore reuse rules).
    private Semaphore[] perImageRenderFinished = Array.Empty<Semaphore>();
    private CommandPool commandPool;
    private FrameResources[] frames = Array.Empty<FrameResources>();
    private int currentFrame;
    private bool needsRecreate;

    // One pool with MaxFramesInFlight contiguous slot ranges. Each slot
    // queues PendingPassTiming entries that get drained when the slot's
    // fence signals next cycle.
    //
    // MoltenVK caveat: vkCmdWriteTimestamp lowers to Metal counter samplers
    // that often resolve AFTER our InFlight fence signals — vkGetQueryPoolResults
    // returns NotReady even with ResultWaitBit. We cap the pending list at
    // MaxPendingPerSlot and flush stale entries instead of waiting forever.
    // Native Vulkan drivers on Linux/Windows resolve correctly. On macOS the
    // CPU timers (frame/build-commands/execute/swap) carry the perf story.
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
        CreateTransientDescriptorPools();
    }

    private unsafe void CreateDepthBuffer()
    {
        // D32_SFLOAT is spec-mandatory. Single shared depth image is safe
        // because InFlight fences serialize per-slot rendering.
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
        // Guard on TimestampValidBits > 0 and non-zero TimestampPeriod —
        // both hold on every driver we ship on, but cheaper to gate than
        // to debug silent zeros if a future driver lies.
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

        // Prefer B8G8R8A8 sRGB to match the GL backend's linear→sRGB path.
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

        // Prefer Mailbox (uncapped) so the perf HUD shows true frame cost;
        // FIFO is the universal vsync fallback.
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
        // Framebuffer compatibility between DefaultRenderPass and
        // OverlayRenderPass requires their subpass deps to MATCH bit-for-bit.
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
        // LoadOp.Load on color (paint over the scene); depth DontCare both
        // ways so overlay lines stay always-on-top. initialLayout matches
        // DefaultRenderPass's finalLayout for clean two-pass transitions.
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
            // Signaled so the first WaitForFences passes immediately.
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

    // Wait → acquire → record → submit → present. Returns true if any
    // pass targeted the default (swapchain) surface — caller uses this to
    // decide whether the frame actually painted something visible.
    private unsafe bool AcquireRecordSubmitPresent(RenderCommandList commandList)
    {
        if (needsRecreate)
        {
            RecreateSwapchain();
            needsRecreate = false;
            return false;
        }

        ref var f = ref frames[currentFrame];
        // CPU-phase timing: wait (fence/vsync throttle) → encode (record vkCmds)
        // → submit/present. Surfaced via LastCpuFrameTiming so a diagnostics pass
        // can isolate the draw-encode cost from the GPU-bound wait.
        var swWait = Stopwatch.GetTimestamp();
        Vk.WaitForFences(Device, 1, in f.InFlight, true, ulong.MaxValue);

        // Slot's previous GPU work is complete — drain its timestamps
        // before reusing the query range.
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
        // Fence above guarantees last cycle's transient descriptors are
        // no longer in use — safe to recycle the whole pool.
        ResetTransientDescriptorPool(currentFrame);

        var swEncode = Stopwatch.GetTimestamp();
        var waitMs = Stopwatch.GetElapsedTime(swWait, swEncode).TotalMilliseconds;

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        ThrowIfNotSuccess(Vk.BeginCommandBuffer(f.CommandBuffer, in beginInfo), "vkBeginCommandBuffer");

        // vkCmdResetQueryPool must run outside a render pass.
        var slotQueryBase = (uint)(currentFrame * (int)QueriesPerFrameSlot);
        if (timestampsSupported)
        {
            Vk.CmdResetQueryPool(f.CommandBuffer, gpuTimingPool, slotQueryBase, QueriesPerFrameSlot);
        }
        var nextQueryIndex = slotQueryBase;

        // One stackalloc shared across passes (CA2014 — no per-iteration
        // alloc). 8 = 7 color + 1 depth; bump for wider MRT passes.
        var clearValues = stackalloc ClearValue[8];
        var defaultPasses = 0;
        foreach (var pass in commandList.Passes)
        {
            // Compute pass: dispatch outside any render pass (the prior pass
            // already ended its render pass). No framebuffer / clears / timing.
            if (pass.Description.Compute)
            {
                TranslateComputePass(f.CommandBuffer, pass, currentFrame);
                continue;
            }

            // Offscreen surfaces currently only have a Clear variant — passes
            // targeting them must declare clear colors. Load/Store flexibility
            // lives in the render graph, not the imperative path.
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

            // Empty ClearColors on the default target → OverlayRenderPass
            // (LoadOp.Load). Custom surfaces always clear (no Load variant).
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
                hasClear = true;
            }
            else
            {
                renderPassToUse = hasClear ? DefaultRenderPass : OverlayRenderPass;
                framebufferToUse = swapchainFramebuffers[imageIndex];
                extentToUse = SwapchainExtent;
            }

            // pClearValues is indexed by attachment, so depth lives at
            // `colorCount`, not a hardcoded 1. A 0-color shadow pass needs
            // its depth clear at index 0 — getting this wrong clears depth
            // to 0.0 (reinterpreted ClearColorValue zero) and every fragment
            // reads back near-plane depth from the "shadow map".
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
            // Timestamps INSIDE the pass — MoltenVK resolves counter samplers
            // at draw boundaries; ColorAttachmentOutputBit pairs naturally.
            if (canTimePass)
            {
                Vk.CmdWriteTimestamp(f.CommandBuffer, PipelineStageFlags.ColorAttachmentOutputBit, gpuTimingPool, startIndex);
            }

            // Viewport+scissor match the current target's extent.
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
                else if (renderCmd is DrawIndexedIndirectCommand di)
                {
                    TranslateDrawIndexedIndirect(f.CommandBuffer, di, currentFrame);
                }
                else if (renderCmd is DispatchCommand)
                {
                    throw new InvalidOperationException(
                        $"Graphics pass '{pass.Name}' contains a DispatchCommand. Dispatches belong in a compute pass (RenderCommandList.ComputePass / RenderGraph.Dispatch).");
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
        var swSubmit = Stopwatch.GetTimestamp();
        var encodeMs = Stopwatch.GetElapsedTime(swEncode, swSubmit).TotalMilliseconds;

        // Always submit, even with zero default-target passes — skipping
        // would strand the acquired imageAvailable semaphore and deadlock
        // the next acquire.
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

        var submitPresentMs = Stopwatch.GetElapsedTime(swSubmit, Stopwatch.GetTimestamp()).TotalMilliseconds;
        lastCpuFrameTiming = new VkCpuFrameTiming(waitMs, encodeMs, submitPresentMs);

        currentFrame = (currentFrame + 1) % MaxFramesInFlight;
        return defaultPasses > 0;
    }

    // Fires under DeviceWaitIdle once the new swapchain is up — subscribers
    // (e.g. RenderGraph) rebuild matchSwapchain-sized resources here.
    internal event Action? SwapchainRecreated;

    private void RecreateSwapchain()
    {
        Vk.DeviceWaitIdle(Device);
        DestroySwapchainResources();
        CreateSwapchain();
        CreateImageViews();
        CreateDepthBuffer();
        // RenderPass + command pool + sync survive: format + queue family
        // don't change on resize. Framebuffers do — attachment views changed.
        CreateFramebuffers();
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

        // WaitBit because MoltenVK resolves timestamps asynchronously after
        // the fence signals; the wait is microseconds in practice.
        var slotQueryBase = (uint)(slot * (int)QueriesPerFrameSlot);
        var results = stackalloc ulong[(int)QueriesPerFrameSlot];
        var status = Vk.GetQueryPoolResults(
            Device, gpuTimingPool, slotQueryBase, QueriesPerFrameSlot,
            (nuint)(QueriesPerFrameSlot * sizeof(ulong)),
            results, sizeof(ulong),
            QueryResultFlags.Result64Bit | QueryResultFlags.ResultWaitBit);
        if (status != Result.Success)
        {
            // NotReady on MoltenVK — retry next cycle, cap to bound growth.
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
        if (pipe.IsCompute)
        {
            throw new InvalidOperationException(
                $"DrawIndexed bound a compute pipeline '{pipe.Name}'. Compute pipelines can only be dispatched (RenderCommandList.ComputePass / RenderGraph.Dispatch).");
        }
        var vb = GetVertexBuffer(d.VertexBuffer);
        var ib = GetIndexBuffer(d.IndexBuffer);
        var prog = shaderProgramTable[pipe.ShaderProgram.Id];

        // Per-draw uniforms still share the program's per-frame UBO bytes —
        // two draws sharing a program but writing different uniforms will
        // race. Push constants / dynamic-offset UBOs are the next step.
        if (d.Uniforms.Count > 0) WriteUniformsAcrossSets(prog, frameSlot, d.Uniforms);

        Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipe.Pipeline);
        BindTransientDescriptorSets(cmd, prog, pipe.Layout, frameSlot, d.Textures);

        // Modulo lets the static (FramesInFlight=1) and replicated cases
        // share one bind path — static always picks Sets[0].
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

        // Per-draw scissor (ImGui clip rects). Scissor is dynamic state on
        // every pipeline, so we just narrow it for this draw. Clamp to >= 0
        // because ImGui clip rects can run slightly negative at edges, which
        // Vulkan rejects. Draws without a scissor inherit the pass-wide
        // scissor set at pass begin.
        if (d.Scissor is { } sc)
        {
            var rect = new Rect2D(
                new Offset2D(Math.Max(0, sc.X), Math.Max(0, sc.Y)),
                new Extent2D((uint)Math.Max(0, sc.Width), (uint)Math.Max(0, sc.Height)));
            Vk.CmdSetScissor(cmd, 0, 1, in rect);
        }

        ulong offset = 0;
        var buffer = vb.Buffer;
        Vk.CmdBindVertexBuffers(cmd, 0, 1, &buffer, &offset);
        Vk.CmdBindIndexBuffer(cmd, ib.Buffer, 0, ib.IndexType);
        // vertexOffset (5th arg) lets concatenated ImGui cmd-lists index
        // per-list off one shared vertex buffer.
        Vk.CmdDrawIndexed(cmd, (uint)d.IndexCount, 1, (uint)d.IndexOffset, d.VertexOffset, 0);
    }

    // Per-material indirect multi-draw. Identical bind sequence to
    // TranslateDrawIndexed (pipeline / set0 uniforms+textures / set2 material /
    // push / shared VB+IB), then one vkCmdDrawIndexedIndirect reading drawCount
    // commands from the current frame's slot of the indirect buffer.
    private unsafe void TranslateDrawIndexedIndirect(CommandBuffer cmd, DrawIndexedIndirectCommand d, int frameSlot)
    {
        var pipe = GetPipeline(d.Pipeline);
        if (pipe.IsCompute)
        {
            throw new InvalidOperationException(
                $"DrawIndexedIndirect bound a compute pipeline '{pipe.Name}'.");
        }
        var vb = GetVertexBuffer(d.VertexBuffer);
        var ib = GetIndexBuffer(d.IndexBuffer);
        var prog = shaderProgramTable[pipe.ShaderProgram.Id];

        if (d.Uniforms.Count > 0) WriteUniformsAcrossSets(prog, frameSlot, d.Uniforms);

        Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipe.Pipeline);
        BindTransientDescriptorSets(cmd, prog, pipe.Layout, frameSlot, d.Textures);

        if (d.Material is { } matHandle)
        {
            var mat = materialTable[matHandle.Id];
            var matSet = mat.Sets[frameSlot % mat.FramesInFlight];
            Vk.CmdBindDescriptorSets(
                cmd, PipelineBindPoint.Graphics, pipe.Layout,
                firstSet: (uint)mat.SetIndex, descriptorSetCount: 1, &matSet,
                dynamicOffsetCount: 0, pDynamicOffsets: null);
        }
        if (d.PushConstants is { } pcBytes)
        {
            PushConstantsToCommandBuffer(cmd, pipe.Layout, prog.Interface.PushConstants, pcBytes);
        }

        ulong offset = 0;
        var buffer = vb.Buffer;
        Vk.CmdBindVertexBuffers(cmd, 0, 1, &buffer, &offset);
        Vk.CmdBindIndexBuffer(cmd, ib.Buffer, 0, ib.IndexType);

        var indirect = GetIndirectBuffer(d.IndirectBuffer, frameSlot);
        Vk.CmdDrawIndexedIndirect(
            cmd, indirect.Buffer, (ulong)d.IndirectByteOffset,
            (uint)d.DrawCount, (uint)IndirectCommandStride);
    }

    // Name-keyed ShaderUniform → byte offsets across every UBO/SSBO slot
    // the program declares. First match wins; std140 assumed.
    //
    // System.Numerics.Matrix4x4 is row-major bytes; GLSL std140 reads
    // column-major. Writing the .NET matrix directly IS the transpose —
    // M_row in memory = M_col as GLSL sees it, so `M * v` in GLSL matches
    // `v * M` in .NET. No explicit Transpose() needed.
    //
    // Scratch is shared across calls (single-threaded render path) and
    // cleared, not re-allocated.
    private readonly Dictionary<(int Set, int Binding), nint> uniformMappedPtrs = new();
    private readonly List<VkBufferEntry> uniformMappedBuffers = new();

    // Record a compute pass: a single dispatch with the storage-image barriers
    // it needs. Runs outside any render pass (the pass loop ends the prior
    // render pass before this). Storage-image targets are transitioned to
    // GENERAL (discarding prior contents — the dispatch fully rewrites them),
    // dispatched, then transitioned to SHADER_READ so a later graphics pass can
    // sample them.
    private unsafe void TranslateComputePass(CommandBuffer cmd, RenderPass pass, int frameSlot)
    {
        foreach (var rc in pass.Commands)
        {
            if (rc is not DispatchCommand d)
            {
                throw new InvalidOperationException(
                    $"Compute pass '{pass.Name}' contains a {rc.GetType().Name}; compute passes may only contain DispatchCommands.");
            }
            var pipe = GetPipeline(d.Pipeline);
            if (!pipe.IsCompute)
            {
                throw new InvalidOperationException(
                    $"Dispatch in compute pass '{pass.Name}' bound a graphics pipeline '{pipe.Name}'. Use a compute pipeline (CreateComputePipeline).");
            }
            var prog = shaderProgramTable[pipe.ShaderProgram.Id];

            for (var i = 0; i < d.Textures.Count; i++)
            {
                var b = d.Textures[i];
                if (!IsStorageBinding(prog, b.Slot)) continue;
                RecordStorageBarrier(cmd, textureTable[b.Texture.Id],
                    ImageLayout.Undefined, ImageLayout.General,
                    PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit,
                    PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderWriteBit);
            }

            if (d.Uniforms.Count > 0) WriteUniformsAcrossSets(prog, frameSlot, d.Uniforms);
            Vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipe.Pipeline);
            BindTransientDescriptorSets(cmd, prog, pipe.Layout, frameSlot, d.Textures, PipelineBindPoint.Compute);
            if (d.PushConstants is { } pc)
            {
                PushConstantsToCommandBuffer(cmd, pipe.Layout, prog.Interface.PushConstants, pc);
            }
            Vk.CmdDispatch(cmd, (uint)d.GroupsX, (uint)d.GroupsY, (uint)d.GroupsZ);

            for (var i = 0; i < d.Textures.Count; i++)
            {
                var b = d.Textures[i];
                if (!IsStorageBinding(prog, b.Slot)) continue;
                RecordStorageBarrier(cmd, textureTable[b.Texture.Id],
                    ImageLayout.General, ImageLayout.ShaderReadOnlyOptimal,
                    PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderWriteBit,
                    PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit);
            }
        }
    }

    private static bool IsStorageBinding(VkShaderProgramEntry prog, int binding)
    {
        foreach (var set in prog.Sets)
        {
            if (set is null) continue;
            foreach (var s in set.Slots)
                if (s.Binding == binding && s.Type == ShaderResourceType.StorageImage) return true;
        }
        return false;
    }

    private unsafe void RecordStorageBarrier(
        CommandBuffer cmd, VkTextureEntry tex, ImageLayout oldLayout, ImageLayout newLayout,
        PipelineStageFlags srcStage, AccessFlags srcAccess, PipelineStageFlags dstStage, AccessFlags dstAccess)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = tex.Image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, (uint)tex.MipCount, 0, 1),
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
        };
        Vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    private unsafe void WriteUniformsAcrossSets(
        VkShaderProgramEntry prog,
        int frameSlot,
        IReadOnlyList<ShaderUniform> uniforms)
    {
        // Lazy-map per (set, binding): each buffer is mapped at most once
        // per draw regardless of how many of its members are written.
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

    // Allocates a fresh transient set per non-material declared set, batches
    // all UBO + texture descriptor writes for the set into one
    // vkUpdateDescriptorSets, then binds. ShaderTextureBinding.Slot is the
    // binding number within a set; a texture lands in any set whose layout
    // declares an image at that binding (unique in practice, so usually
    // one set).
    private unsafe void BindTransientDescriptorSets(
        CommandBuffer cmd,
        VkShaderProgramEntry prog,
        PipelineLayout pipeLayout,
        int frameSlot,
        IReadOnlyList<ShaderTextureBinding> textures,
        PipelineBindPoint bindPoint = PipelineBindPoint.Graphics)
    {
        // Hoist scratch above the loop (CA2014). Size = the widest set's
        // potential write count.
        var maxWritesPerSet = 0;
        for (var setIdx = 0; setIdx < prog.Sets.Length; setIdx++)
        {
            if (prog.Sets[setIdx] is not { } sr2) continue;
            if (setIdx == MaterialOwnedSet) continue;
            if (sr2.Slots.Count == 0) continue;
            var w = sr2.Slots.Count + textures.Count;
            if (w > maxWritesPerSet) maxWritesPerSet = w;
        }
        if (maxWritesPerSet == 0) return;

        var writes = stackalloc WriteDescriptorSet[maxWritesPerSet];
        var bufInfos = stackalloc DescriptorBufferInfo[maxWritesPerSet];
        var imgInfos = stackalloc DescriptorImageInfo[maxWritesPerSet];

        for (var setIdx = 0; setIdx < prog.Sets.Length; setIdx++)
        {
            if (prog.Sets[setIdx] is not { } sr) continue;
            if (setIdx == MaterialOwnedSet) continue;
            if (sr.Slots.Count == 0) continue;

            var ds = AllocateTransientSet(frameSlot, sr.Layout);
            var writeIdx = 0;

            foreach (var slot in sr.Slots)
            {
                if (slot.BlockLayout is not { } block) continue;
                if (!sr.BuffersPerBinding.TryGetValue(slot.Binding, out var buffers)) continue;
                var buf = buffers[frameSlot];
                bufInfos[writeIdx] = new DescriptorBufferInfo
                {
                    Buffer = buf.Buffer,
                    Offset = 0,
                    Range = (ulong)block.TotalSize,
                };
                writes[writeIdx] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = ds,
                    DstBinding = (uint)slot.Binding,
                    DstArrayElement = 0,
                    DescriptorType = MapDescriptorType(slot.Type),
                    DescriptorCount = 1,
                    PBufferInfo = &bufInfos[writeIdx],
                };
                writeIdx++;
            }

            for (var i = 0; i < textures.Count; i++)
            {
                var b = textures[i];
                if (b.Slot < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(textures),
                        $"ShaderTextureBinding '{b.Name}' has negative Slot ({b.Slot}).");
                }
                var imageSlotType = ImageSlotTypeAtBinding(sr, b.Slot);
                if (imageSlotType is null) continue;

                var tex = textureTable[b.Texture.Id];
                // Storage images bind as STORAGE_IMAGE in GENERAL layout (no
                // sampler); sampled images as COMBINED_IMAGE_SAMPLER in
                // shader-read layout.
                var isStorage = imageSlotType == ShaderResourceType.StorageImage;
                imgInfos[writeIdx] = new DescriptorImageInfo
                {
                    Sampler = isStorage ? default : tex.Sampler,
                    ImageView = tex.View,
                    ImageLayout = isStorage ? ImageLayout.General : ImageLayout.ShaderReadOnlyOptimal,
                };
                writes[writeIdx] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = ds,
                    DstBinding = (uint)b.Slot,
                    // Count>1 sampler arrays (e.g. uSpotShadowMaps[N]).
                    DstArrayElement = (uint)b.ArrayIndex,
                    DescriptorType = isStorage ? DescriptorType.StorageImage : DescriptorType.CombinedImageSampler,
                    DescriptorCount = 1,
                    PImageInfo = &imgInfos[writeIdx],
                };
                writeIdx++;
            }

            if (writeIdx > 0)
            {
                Vk.UpdateDescriptorSets(Device, (uint)writeIdx, writes, 0, default(CopyDescriptorSet*));
            }

            Vk.CmdBindDescriptorSets(
                cmd,
                bindPoint,
                pipeLayout,
                firstSet: (uint)setIdx,
                descriptorSetCount: 1,
                &ds,
                dynamicOffsetCount: 0,
                pDynamicOffsets: null);
        }
    }

    // The image-resource type at a binding (SampledImage / StorageImage /
    // Sampler), or null if the binding isn't an image slot in this set.
    private static ShaderResourceType? ImageSlotTypeAtBinding(VkShaderSetResources sr, int binding)
    {
        foreach (var s in sr.Slots)
        {
            if (s.Binding != binding) continue;
            if (s.Type is ShaderResourceType.SampledImage or ShaderResourceType.StorageImage or ShaderResourceType.Sampler)
                return s.Type;
        }
        return null;
    }

    // Slices the payload across the shader's declared push-constant ranges
    // and emits one vkCmdPushConstants per range.
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
                var mat = m.Value;
                fixed (byte* p = dst) *((System.Numerics.Matrix4x4*)p) = mat;
                break;
            case Vector4Uniform v4:
                fixed (byte* p = dst) *((System.Numerics.Vector4*)p) = v4.Value;
                break;
            case Vector3Uniform v3:
                // std140 vec3 is 16-byte-aligned but occupies 12 bytes;
                // UniformBlockLayout offsets bake in the trailing padding.
                fixed (byte* p = dst) *((System.Numerics.Vector3*)p) = v3.Value;
                break;
            case Vector2Uniform v2:
                fixed (byte* p = dst) *((System.Numerics.Vector2*)p) = v2.Value;
                break;
            case FloatUniform f:
                fixed (byte* p = dst) *((float*)p) = f.Value;
                break;
            // Array variants (F-009). std140 array layout rules:
            //   mat4[] — element stride 64 (already 16-aligned; no padding).
            //   vec3[] — element stride 16 (the trailing 4 bytes are pad);
            //            write only the 12 useful bytes per element.
            //   float[]— element stride 16 (each scalar rounded up to a vec4
            //            slot). The caller's UniformBlockMember.Size must
            //            reflect these strides (count × stride).
            case Matrix4x4ArrayUniform ma:
                fixed (byte* p = dst)
                {
                    var arr = ma.Value;
                    for (var i = 0; i < arr.Length; i++)
                        *((System.Numerics.Matrix4x4*)(p + i * 64)) = arr[i];
                }
                break;
            case Vector3ArrayUniform va3:
                fixed (byte* p = dst)
                {
                    var arr = va3.Value;
                    for (var i = 0; i < arr.Length; i++)
                        *((System.Numerics.Vector3*)(p + i * 16)) = arr[i];
                }
                break;
            case FloatArrayUniform fa:
                fixed (byte* p = dst)
                {
                    var arr = fa.Value;
                    for (var i = 0; i < arr.Length; i++)
                        *((float*)(p + i * 16)) = arr[i];
                }
                break;
            default:
                throw new NotImplementedException($"Vulkan UBO write for {value.GetType().Name} not implemented yet.");
        }
    }
}
