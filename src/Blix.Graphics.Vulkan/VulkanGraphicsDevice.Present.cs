using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Blix.Graphics.Vulkan;

// Decoupled presentation (vsync off, a third queue family, the frame timeline; BLIX_DECOUPLED_PRESENT=off: the old path).
// The frame renders into one of BackbufferCount offscreen images (the default target, swapchainImageViews' images), never
// the swapchain's own; a present thread on its own queue copies the newest finished one into a swapchain image and
// presents it. MoltenVK takes the drawable inside the vkQueueSubmit that first draws to it, and the window's three come
// back at the compositor's refresh: the render loop waited there for most of its host time (1840 of 2211 samples in
// -[CAMetalLayer nextDrawable], Crytek Sponza 1080p, 2026-10-28), so frames took one or two vblanks and the GPU's own
// time never showed. Now only the present thread waits for drawables.
//
// The ring, as a mailbox: the render loop takes a slot neither queued for the present thread (the newest finished) nor
// being copied, so with three it always finds one; a finished frame the present thread has not taken yet is replaced by
// the next. A slot's next frame waits on the GPU for its last copy (presentTimeline).
public sealed partial class VulkanGraphicsDevice
{
    private const int BackbufferCount = 3;
    private const int PresentRing = 3;

    // On for the current swapchain (decided in CreateSwapchain).
    private bool decoupledPresent;
    private readonly bool decoupledPresentWanted = Environment.GetEnvironmentVariable("BLIX_DECOUPLED_PRESENT") != "off";

    private Image[] backbufferImages = Array.Empty<Image>();
    private DeviceMemory[] backbufferMemory = Array.Empty<DeviceMemory>();

    // The present thread's: its pool, command buffers and fences (a ring), acquire semaphores (the ring's) and present
    // semaphores (per swapchain image), and the timeline its copies signal.
    private CommandPool presentPool;
    private CommandBuffer[] presentBuffers = Array.Empty<CommandBuffer>();
    private Fence[] presentFences = Array.Empty<Fence>();
    private Semaphore[] presentAcquire = Array.Empty<Semaphore>();
    private Semaphore[] presentReady = Array.Empty<Semaphore>();
    private Semaphore presentTimeline;
    private ulong presentTimelineValue;

    private Thread? presentThread;
    private readonly object presentLock = new();
    private bool presentStop;
    // The newest finished frame (its slot and the frame timeline value its default target is written by), -1 none; the
    // slot being copied, -1 none; each slot's last copy's presentTimeline value.
    private int presentLatestSlot = -1;
    private ulong presentLatestValue;
    private int presentCopyingSlot = -1;
    private readonly ulong[] presentCopyDone = new ulong[BackbufferCount];
    private int presentNextSlot;

    // Whether this swapchain presents decoupled: vsync off (on, FIFO paces the loop anyway, and a frame rendered and
    // dropped is power spent for nothing), the present queue and the frame timeline there.
    private bool DecoupledPresentPossible => !vsyncEnabled && decoupledPresentWanted && HasPresentQueue && frameTimeline.Handle != 0;

    private ImageLayout BackbufferLayout => decoupledPresent ? ImageLayout.TransferSrcOptimal : ImageLayout.PresentSrcKhr;

    private unsafe void CreateBackbuffers()
    {
        backbufferImages = new Image[BackbufferCount];
        backbufferMemory = new DeviceMemory[BackbufferCount];
        swapchainImageViews = new ImageView[BackbufferCount];
        var families = stackalloc uint[2] { GraphicsQueueFamily, PresentQueueFamily };
        for (var i = 0; i < BackbufferCount; i++)
        {
            var ci = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = SwapchainFormat,
                Extent = new Extent3D(SwapchainExtent.Width, SwapchainExtent.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
                // Drawn on the graphics queue, copied on the present queue: shared, no ownership transfers.
                SharingMode = SharingMode.Concurrent,
                QueueFamilyIndexCount = 2,
                PQueueFamilyIndices = families,
                InitialLayout = ImageLayout.Undefined,
            };
            Image image;
            ThrowIfNotSuccess(Vk.CreateImage(Device, in ci, null, &image), "vkCreateImage(backbuffer)");
            backbufferImages[i] = image;
            Vk.GetImageMemoryRequirements(Device, image, out var req);
            var ai = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = req.Size,
                MemoryTypeIndex = FindMemoryTypeIndex(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            DeviceMemory mem;
            ThrowIfNotSuccess(Vk.AllocateMemory(Device, in ai, null, &mem), "vkAllocateMemory(backbuffer)");
            backbufferMemory[i] = mem;
            ThrowIfNotSuccess(Vk.BindImageMemory(Device, image, mem, 0), "vkBindImageMemory(backbuffer)");
            var vci = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = SwapchainFormat,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            ImageView view;
            ThrowIfNotSuccess(Vk.CreateImageView(Device, in vci, null, &view), "vkCreateImageView(backbuffer)");
            swapchainImageViews[i] = view;
        }
        Array.Clear(presentCopyDone);
        presentLatestSlot = -1;
        presentCopyingSlot = -1;
        presentNextSlot = 0;
    }

    private unsafe void DestroyBackbuffers()
    {
        for (var i = 0; i < backbufferImages.Length; i++)
        {
            if (backbufferImages[i].Handle != 0) Vk.DestroyImage(Device, backbufferImages[i], null);
            if (backbufferMemory[i].Handle != 0) Vk.FreeMemory(Device, backbufferMemory[i], null);
        }
        backbufferImages = Array.Empty<Image>();
        backbufferMemory = Array.Empty<DeviceMemory>();
    }

    // The present thread's long-lived objects, made once.
    private unsafe void CreatePresentResources()
    {
        if (presentPool.Handle != 0) return;
        var pci = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = PresentQueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
        };
        CommandPool pool;
        ThrowIfNotSuccess(Vk.CreateCommandPool(Device, in pci, null, &pool), "vkCreateCommandPool(present)");
        presentPool = pool;
        presentBuffers = new CommandBuffer[PresentRing];
        var ai = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = presentPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = PresentRing,
        };
        fixed (CommandBuffer* p = presentBuffers) ThrowIfNotSuccess(Vk.AllocateCommandBuffers(Device, in ai, p), "vkAllocateCommandBuffers(present)");
        presentFences = new Fence[PresentRing];
        presentAcquire = new Semaphore[PresentRing];
        var fci = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
        var sci = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        for (var i = 0; i < PresentRing; i++)
        {
            Fence fence;
            ThrowIfNotSuccess(Vk.CreateFence(Device, in fci, null, &fence), "vkCreateFence(present)");
            presentFences[i] = fence;
            Semaphore s;
            ThrowIfNotSuccess(Vk.CreateSemaphore(Device, in sci, null, &s), "vkCreateSemaphore(present acquire)");
            presentAcquire[i] = s;
        }
        var type = new SemaphoreTypeCreateInfo { SType = StructureType.SemaphoreTypeCreateInfo, SemaphoreType = SemaphoreType.Timeline, InitialValue = 0 };
        var tci = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &type };
        Semaphore timeline;
        ThrowIfNotSuccess(Vk.CreateSemaphore(Device, in tci, null, &timeline), "vkCreateSemaphore(present timeline)");
        presentTimeline = timeline;
    }

    private unsafe void DestroyPresentResources()
    {
        if (presentPool.Handle == 0) return;
        foreach (var f in presentFences) Vk.DestroyFence(Device, f, null);
        foreach (var s in presentAcquire) Vk.DestroySemaphore(Device, s, null);
        Vk.DestroySemaphore(Device, presentTimeline, null);
        Vk.DestroyCommandPool(Device, presentPool, null);
        presentPool = default;
        presentFences = Array.Empty<Fence>();
        presentAcquire = Array.Empty<Semaphore>();
        presentBuffers = Array.Empty<CommandBuffer>();
        presentTimeline = default;
    }

    // Per swapchain image: the copy's signal the present waits for (made with the swapchain, as perImageRenderFinished).
    private unsafe void CreatePresentReady()
    {
        presentReady = new Semaphore[swapchainImages.Length];
        var sci = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        for (var i = 0; i < presentReady.Length; i++)
        {
            Semaphore s;
            ThrowIfNotSuccess(Vk.CreateSemaphore(Device, in sci, null, &s), "vkCreateSemaphore(present ready)");
            presentReady[i] = s;
        }
    }

    private unsafe void DestroyPresentReady()
    {
        foreach (var s in presentReady) if (s.Handle != 0) Vk.DestroySemaphore(Device, s, null);
        presentReady = Array.Empty<Semaphore>();
    }

    private void StartPresentThread()
    {
        presentStop = false;
        presentThread = new Thread(PresentLoop) { Name = "blix-present", IsBackground = true };
        presentThread.Start();
    }

    // Stopped before the swapchain or the backbuffers go: the thread finishes the copy it is on, and its queue drains.
    private void StopPresentThread()
    {
        if (presentThread is null) return;
        lock (presentLock)
        {
            presentStop = true;
            Monitor.PulseAll(presentLock);
        }
        presentThread.Join();
        presentThread = null;
        Vk.QueueWaitIdle(PresentQueue);
    }

    // The render loop's slot for this frame and the presentTimeline value its last copy signals (0: none to wait for).
    private int TakeBackbufferSlot(out ulong copyWait)
    {
        lock (presentLock)
        {
            for (var k = 0; k < BackbufferCount; k++)
            {
                var s = (presentNextSlot + k) % BackbufferCount;
                if (s == presentLatestSlot || s == presentCopyingSlot) continue;
                presentNextSlot = (s + 1) % BackbufferCount;
                copyWait = presentCopyDone[s];
                return s;
            }
        }
        throw new InvalidOperationException("No backbuffer free: three slots and at most two held.");
    }

    // The frame in `slot` is written once the frame timeline reaches `value`: the newest, for the present thread.
    private void PublishBackbuffer(int slot, ulong value)
    {
        lock (presentLock)
        {
            presentLatestSlot = slot;
            presentLatestValue = value;
            Monitor.Pulse(presentLock);
        }
    }

    private unsafe void PresentLoop()
    {
        var ring = 0;
        // Once, out of the loop: a stackalloc lives until the method returns, and in the loop each frame's took more of
        // the thread's stack until it overflowed.
        var waits = stackalloc Semaphore[2];
        var waitValues = stackalloc ulong[2];
        var stages = stackalloc PipelineStageFlags[2] { PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit };
        var signals = stackalloc Semaphore[2];
        var signalValues = stackalloc ulong[2];
        while (true)
        {
            int slot;
            ulong value;
            lock (presentLock)
            {
                while (!presentStop && presentLatestSlot < 0) Monitor.Wait(presentLock);
                if (presentStop) return;
                slot = presentLatestSlot;
                value = presentLatestValue;
                presentLatestSlot = -1;
                presentCopyingSlot = slot;
            }

            var fence = presentFences[ring];
            Vk.WaitForFences(Device, 1, in fence, true, ulong.MaxValue);
            uint image;
            var acquired = KhrSwapchain.AcquireNextImage(Device, Swapchain, ulong.MaxValue, presentAcquire[ring], default, &image);
            if (acquired == Result.ErrorOutOfDateKhr || (acquired != Result.Success && acquired != Result.SuboptimalKhr))
            {
                needsRecreate = true;
                lock (presentLock) presentCopyingSlot = -1;
                continue;
            }
            Vk.ResetFences(Device, 1, in fence);

            var cmd = presentBuffers[ring];
            Vk.ResetCommandBuffer(cmd, 0);
            var bi = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            Vk.BeginCommandBuffer(cmd, in bi);
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            var toDst = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = swapchainImages[image],
                SubresourceRange = range,
            };
            Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &toDst);
            var layers = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1);
            var copy = new ImageCopy
            {
                SrcSubresource = layers,
                DstSubresource = layers,
                Extent = new Extent3D(SwapchainExtent.Width, SwapchainExtent.Height, 1),
            };
            Vk.CmdCopyImage(cmd, backbufferImages[slot], ImageLayout.TransferSrcOptimal, swapchainImages[image], ImageLayout.TransferDstOptimal, 1, &copy);
            var toPresent = toDst with
            {
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = 0,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
            };
            Vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit, 0, 0, null, 0, null, 1, &toPresent);
            Vk.EndCommandBuffer(cmd);

            waits[0] = presentAcquire[ring];
            waits[1] = frameTimeline;
            waitValues[0] = 0;
            waitValues[1] = value;
            signals[0] = presentReady[image];
            signals[1] = presentTimeline;
            var done = ++presentTimelineValue;
            signalValues[0] = 0;
            signalValues[1] = done;
            var timelineInfo = new TimelineSemaphoreSubmitInfo
            {
                SType = StructureType.TimelineSemaphoreSubmitInfo,
                WaitSemaphoreValueCount = 2,
                PWaitSemaphoreValues = waitValues,
                SignalSemaphoreValueCount = 2,
                PSignalSemaphoreValues = signalValues,
            };
            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                PNext = &timelineInfo,
                WaitSemaphoreCount = 2,
                PWaitSemaphores = waits,
                PWaitDstStageMask = stages,
                CommandBufferCount = 1,
                PCommandBuffers = &cmd,
                SignalSemaphoreCount = 2,
                PSignalSemaphores = signals,
            };
            // Where MoltenVK waits for the drawable: here, on this thread.
            ThrowIfNotSuccess(Vk.QueueSubmit(PresentQueue, 1, in submit, fence), "vkQueueSubmit(present copy)");
            lock (presentLock)
            {
                presentCopyDone[slot] = done;
                presentCopyingSlot = -1;
            }

            var swapchain = Swapchain;
            var ready = presentReady[image];
            var present = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &ready,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &image,
            };
            var presented = KhrSwapchain.QueuePresent(PresentQueue, in present);
            if (presented == Result.ErrorOutOfDateKhr || presented == Result.SuboptimalKhr) needsRecreate = true;
            ring = (ring + 1) % PresentRing;
        }
    }
}
