using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Blix.Graphics.Vulkan;

// Vk instance + physical device + logical device creation. Surface and
// swapchain belong elsewhere (next push) because they need the window's
// IVkSurface — the runtime layer plumbs that in via a callback.
public sealed partial class VulkanGraphicsDevice
{
    // Owned by the device, kept around for the process lifetime.
    internal Vk Vk { get; private set; } = null!;
    internal Instance Instance { get; private set; }
    internal PhysicalDevice PhysicalDevice { get; private set; }
    internal Device Device { get; private set; }
    internal Queue GraphicsQueue { get; private set; }
    // A queue of another family with compute, where there is one -- MoltenVK offers four families of one queue each, every
    // one graphics, compute and transfer, each queue its own Metal command queue, run side by side: async compute's
    // (ComputePassBuilder.Async). GPU buffers are shared by both families (concurrent), so neither queue takes ownership.
    // Else the graphics queue, and async passes run in place.
    internal Queue AsyncQueue { get; private set; }
    internal uint AsyncQueueFamily { get; private set; }
    internal bool HasAsyncQueue { get; private set; }
    private uint graphicsFamilyQueueCount = 1;
    private uint? asyncFamilyCandidate;
    // A third family's queue that can present, for decoupled presentation's copy and present (VulkanGraphicsDevice.Present).
    internal Queue PresentQueue { get; private set; }
    internal uint PresentQueueFamily { get; private set; }
    internal bool HasPresentQueue { get; private set; }
    private uint? presentFamilyCandidate;
    private readonly bool asyncComputeWanted = Environment.GetEnvironmentVariable("BLIX_ASYNC_COMPUTE") != "off";
    internal uint GraphicsQueueFamily { get; private set; }
    internal bool PortabilitySubsetRequired { get; private set; }
    // Anisotropic filtering capability, resolved at device creation.
    internal bool AnisotropySupported { get; private set; }
    internal float MaxAnisotropy { get; private set; } = 1.0f;

    /// <summary>The highest MSAA sample count this device can do for colour AND depth together.</summary>
    /// <remarks>
    /// Both, because a render pass requires its colour and depth attachments to agree. Exposed so a
    /// caller can clamp: over-asking is a native Metal assertion that kills the process with no
    /// managed exception and no validation message.
    /// </remarks>
    public int MaxMsaaSamples { get; private set; } = 1;
    // Whether vkCmdDrawIndexedIndirect with drawCount>1 is available (enabled at
    // device creation when supported). Gates the per-material indirect path.
    internal bool MultiDrawIndirectSupported { get; private set; }

    /// <summary>Whether a fragment shader may write storage images — see where it is enabled.</summary>
    public bool FragmentStoresSupported { get; private set; }

    /// <inheritdoc />
    public GraphicsFeatures Features { get; private set; }

    internal KhrSurface KhrSurface { get; private set; } = null!;
    internal SurfaceKHR Surface { get; private set; }

    private DebugUtilsMessengerEXT debugMessenger;

    /// <summary>
    /// The messenger's callback, ROOTED for as long as Vulkan holds a pointer to it.
    /// </summary>
    /// <remarks>
    /// <b>A delegate handed to native code and then dropped is a dangling function pointer.</b> This
    /// was built inline in the create-info — <c>PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(...)</c>
    /// — so the only reference died with that local, the GC was free to collect the reverse-P/Invoke
    /// thunk, and the validation layer went on holding its address. The next message it emitted
    /// called into a collected thunk.
    /// <para>
    /// The signature is an intermittent SIGSEGV with no managed stack, in TEARDOWN, whose macOS crash
    /// report reads <c>UMEntryThunk::Decode</c> under <c>GetDelegateForFunctionPointerInternal</c> —
    /// native calling back into managed through a pointer that is no longer one. Teardown because
    /// that is when validation has the most to say, and intermittent because it is GC timing.
    /// </para>
    /// <para>
    /// <b>This is hygiene, and it is NOT established as the cause of the crash it was written for.</b>
    /// A fuller crash report arrived afterwards and argues against it: the pointer handed to
    /// GetDelegateForFunctionPointer is <c>0xb</c> — eleven — and <c>0xd</c> in another. A collected
    /// thunk would be null or a freed address; a small integer is a NUMBER passed where a POINTER
    /// belongs, which is a different fault. The report also shows the main thread inside Main with a
    /// 2.28-second process lifetime, which is teardown after a bounded capture, and nothing in this
    /// repository calls GetDelegateForFunctionPointer at all — so the call is inside a library that
    /// Window.Dispose drives: Silk.NET's windowing, its input, OpenAL, or the ImGui renderer.
    /// </para>
    /// <para>
    /// Rooting a delegate handed to native code is correct regardless, and it was wrong before. It is
    /// recorded as a fix for a real lifetime bug and not as a fix for that crash, because the
    /// evidence does not support the second claim. See BLIX_TEARDOWN_TRACE in Blix.Runtime.Silk's
    /// Window.Dispose for what will name the step next time it happens.
    /// </para>
    /// </remarks>
    private PfnDebugUtilsMessengerCallbackEXT debugCallback;
    private ExtDebugUtils? debugUtils;
    private readonly bool gpuLabels = System.Environment.GetEnvironmentVariable("BLIX_GPU_LABELS") == "1";
    private bool validationEnabled;

    // <b>On with the validation layers, and for the same reason they are.</b> The uniform-conflict
    // detector costs a dictionary lookup and a byte compare per uniform member per draw — nothing in
    // absolute terms, and still not something an ordinary run should pay for a fault that only
    // appears when a program is shared across passes. See NoteUniformWrite.
    private bool detectUniformConflicts;

    private static readonly string[] ValidationLayers = { "VK_LAYER_KHRONOS_validation" };

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetEnvNative(string name, string value, int overwrite);

    private void InitializeVulkan(IVkSurface windowSurface)
    {
        // MoltenVK's semaphores as Metal events (MVK_CONFIG_VK_SEMAPHORE_SUPPORT_STYLE 2), unless set: its default left the
        // frame's hand-off to the Metal tracer ~1.9 ms after the work it waits for ended, ~0.8 ms with events (Crytek
        // Sponza's trace intervals, 2026-10-28). Natively, before the instance: MoltenVK reads its configuration with
        // getenv, which .NET's own environment does not reach on macOS. Ignored elsewhere.
        if (OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("MVK_CONFIG_VK_SEMAPHORE_SUPPORT_STYLE") is null)
            SetEnvNative("MVK_CONFIG_VK_SEMAPHORE_SUPPORT_STYLE", "2", 0);
        Vk = Vk.GetApi();
        validationEnabled = ShouldEnableValidation() && AreLayersAvailable(ValidationLayers);

        // Deliberately NOT gated on the layers being present: this check is ours, needs no layer,
        // and a machine without them installed is exactly where a silent aliasing bug would live
        // longest.
        detectUniformConflicts = ShouldEnableValidation();

        CreateInstance(windowSurface);
        TryAttachDebugMessenger();
        CreateSurface(windowSurface);
        SelectPhysicalDevice();
        CreateLogicalDevice();
        PopulateInfo();
    }

    private unsafe void CreateInstance(IVkSurface windowSurface)
    {
        using var appNamePin = new Utf8Pin("Blix");
        using var engineNamePin = new Utf8Pin("Blix");

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = appNamePin.Ptr,
            ApplicationVersion = MakeVersion(0, 1, 0),
            PEngineName = engineNamePin.Ptr,
            EngineVersion = MakeVersion(0, 1, 0),
            // 1.4 (MoltenVK 1.4 advertises what the instance asks: at 1.2 the M4 reported 1.2.334). Silk.NET 2.22 has no
            // Version14: made here. The shaders are built for it (Blix.Tools.Shader: --target-env=vulkan1.4, SPIR-V 1.6).
            ApiVersion = (1u << 22) | (4u << 12),
        };

        // Instance extensions: GLFW reports the platform-correct set via
        // IVkSurface.GetRequiredExtensions (VK_KHR_surface + VK_EXT_metal_surface
        // on macOS, VK_KHR_surface + VK_KHR_xlib_surface or _win32_surface
        // elsewhere). We add VK_KHR_portability_enumeration on macOS (required
        // to enumerate MoltenVK as a portable device) and VK_EXT_debug_utils
        // when validation is on.
        uint glfwExtCount = 0;
        var glfwExtNames = windowSurface.GetRequiredExtensions(out glfwExtCount);
        var extensions = new List<string>();
        for (var i = 0; i < glfwExtCount; i++)
        {
            var name = Marshal.PtrToStringUTF8((nint)glfwExtNames[i]);
            if (name is not null) extensions.Add(name);
        }
        // <b>Asked for only when the runtime actually has it.</b> VK_KHR_portability_enumeration
        // is the Khronos LOADER's extension, offered when it can see a portability driver behind
        // it. Talking to MoltenVK directly -- which is what a shipped bundle carrying MoltenVK and
        // no loader does -- there is no loader to offer it, and requesting it anyway fails
        // instance creation outright with ErrorExtensionNotPresent. Querying costs one call at
        // startup and turns "this configuration is unsupported" into "this configuration needs no
        // portability bit".
        var portability = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            && (extensions.Contains("VK_KHR_portability_enumeration")
                || InstanceExtensionAvailable("VK_KHR_portability_enumeration"));
        if (portability && !extensions.Contains("VK_KHR_portability_enumeration"))
        {
            extensions.Add("VK_KHR_portability_enumeration");
        }
        if (validationEnabled || (gpuLabels && InstanceExtensionAvailable("VK_EXT_debug_utils")))
        {
            extensions.Add("VK_EXT_debug_utils");
        }

        using var extNames = new Utf8ArrayPin(extensions);
        using var layerNames = new Utf8ArrayPin(validationEnabled ? ValidationLayers : Array.Empty<string>());

        // VK_EXT_metal_objects asks for this on the instance before the device's MTLDevice may be exported
        // (VulkanGraphicsDevice.MetalObjects). It is a device extension, so whether it is there is only known
        // after the instance exists; the struct is chained on macOS, where MoltenVK is the only driver.
        var metalDeviceExport = new ExportMetalObjectCreateInfoEXT
        {
            SType = StructureType.ExportMetalObjectCreateInfoExt,
            ExportObjectType = ExportMetalObjectTypeFlagsEXT.ExportMetalObjectTypeMetalDeviceBitExt,
        };

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PNext = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? &metalDeviceExport : null,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = extNames.Ptr,
            EnabledLayerCount = (uint)(validationEnabled ? ValidationLayers.Length : 0),
            PpEnabledLayerNames = layerNames.Ptr,
            // The flag is meaningless without the extension, and illegal with it absent.
            Flags = portability
                ? InstanceCreateFlags.EnumeratePortabilityBitKhr
                : InstanceCreateFlags.None,
        };

        Instance instance;
        var result = Vk.CreateInstance(in createInfo, null, &instance);
        ThrowIfNotSuccess(result, "vkCreateInstance");
        Instance = instance;
    }

    /// <summary>Whether the Vulkan runtime in this process offers a given instance extension.</summary>
    private unsafe bool InstanceExtensionAvailable(string name)
    {
        uint count = 0;
        if (Vk.EnumerateInstanceExtensionProperties((byte*)null, ref count, null) != Result.Success) return false;
        if (count == 0) return false;

        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = properties)
        {
            if (Vk.EnumerateInstanceExtensionProperties((byte*)null, ref count, p) != Result.Success) return false;
            for (var i = 0; i < count; i++)
            {
                if (Marshal.PtrToStringUTF8((nint)p[i].ExtensionName) == name) return true;
            }
        }

        return false;
    }

    private unsafe void TryAttachDebugMessenger()
    {
        if (!validationEnabled && !gpuLabels) return;
        if (!Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils du)) return;
        debugUtils = du;
        if (!validationEnabled) return;

        // Built into the FIELD first. Inline in the initialiser, its only reference is the local
        // create-info, which the GC may reclaim the moment this method returns.
        debugCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugCallback);

        var ci = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                            | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                        | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                        | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = debugCallback,
        };

        DebugUtilsMessengerEXT messenger;
        var result = debugUtils.CreateDebugUtilsMessenger(Instance, in ci, null, &messenger);
        if (result == Result.Success)
        {
            debugMessenger = messenger;
        }
    }

    private unsafe void CreateSurface(IVkSurface windowSurface)
    {
        if (!Vk.TryGetInstanceExtension(Instance, out KhrSurface khr))
        {
            throw new InvalidOperationException("VK_KHR_surface not available on the created instance.");
        }
        KhrSurface = khr;
        var handle = windowSurface.Create<AllocationCallbacks>(Instance.ToHandle(), null);
        Surface = handle.ToSurface();
    }

    private unsafe void SelectPhysicalDevice()
    {
        uint count = 0;
        Vk.EnumeratePhysicalDevices(Instance, &count, null);
        if (count == 0)
        {
            throw new InvalidOperationException("No Vulkan-capable physical devices found.");
        }
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
        {
            Vk.EnumeratePhysicalDevices(Instance, &count, p);
        }

        // Score: prefer discrete > integrated > anything else, gated on the
        // device having at least one queue family that can both render
        // graphics and present to our surface. On Apple Silicon there's one
        // device + one queue family that does both; on multi-GPU Linux /
        // Windows machines the gate matters.
        PhysicalDevice = default;
        var bestScore = -1;
        foreach (var candidate in devices)
        {
            if (!TryFindGraphicsAndPresentQueueFamily(candidate, out _)) continue;
            var props = Vk.GetPhysicalDeviceProperties(candidate);
            var score = props.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 2,
                PhysicalDeviceType.IntegratedGpu => 1,
                _ => 0,
            };
            if (score > bestScore)
            {
                bestScore = score;
                PhysicalDevice = candidate;
            }
        }
        if (PhysicalDevice.Handle == 0)
        {
            throw new InvalidOperationException("No physical device exposes a queue family that can both render and present to the window surface.");
        }

        // VK_KHR_portability_subset must be enabled at device-create time when
        // the device advertises it (MoltenVK does). Detect now so device
        // extension list construction below stays self-contained.
        uint extCount = 0;
        Vk.EnumerateDeviceExtensionProperties(PhysicalDevice, (byte*)null, &extCount, null);
        var exts = new ExtensionProperties[extCount];
        fixed (ExtensionProperties* p = exts)
        {
            Vk.EnumerateDeviceExtensionProperties(PhysicalDevice, (byte*)null, &extCount, p);
        }
        foreach (var ext in exts)
        {
            var name = Marshal.PtrToStringUTF8((nint)ext.ExtensionName);
            if (name == "VK_KHR_portability_subset") PortabilitySubsetRequired = true;
            else if (name == MetalObjectsExtension) MetalObjectsEnabled = true;
            else if (name == DisplayTimingExtension) displayTimingSupported = true;
        }

        if (!TryFindGraphicsAndPresentQueueFamily(PhysicalDevice, out var qf))
        {
            throw new InvalidOperationException("Selected physical device has no graphics+present queue family (race?).");
        }
        GraphicsQueueFamily = qf;
    }

    private unsafe bool TryFindGraphicsAndPresentQueueFamily(PhysicalDevice device, out uint family)
    {
        family = 0;
        uint count = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = families)
        {
            Vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, p);
        }
        if (Environment.GetEnvironmentVariable("BLIX_LIST_QUEUES") is not null)
            for (uint i = 0; i < count; i++) Console.WriteLine($"[Vulkan] queue family {i}: {families[i].QueueFlags}, {families[i].QueueCount} queues");
        for (uint i = 0; i < count; i++)
        {
            if ((families[i].QueueFlags & QueueFlags.GraphicsBit) == 0) continue;
            Silk.NET.Core.Bool32 supported = false;
            KhrSurface.GetPhysicalDeviceSurfaceSupport(device, i, Surface, &supported);
            if (supported)
            {
                family = i;
                graphicsFamilyQueueCount = families[i].QueueCount;
                asyncFamilyCandidate = null;
                for (uint j = 0; j < count; j++)
                    if (j != i && (families[j].QueueFlags & QueueFlags.ComputeBit) != 0) { asyncFamilyCandidate = j; break; }
                presentFamilyCandidate = null;
                for (uint j = 0; j < count; j++)
                {
                    if (j == i || j == asyncFamilyCandidate || (families[j].QueueFlags & (QueueFlags.GraphicsBit | QueueFlags.TransferBit)) == 0) continue;
                    Silk.NET.Core.Bool32 presents = false;
                    KhrSurface.GetPhysicalDeviceSurfaceSupport(device, j, Surface, &presents);
                    if (presents) { presentFamilyCandidate = j; break; }
                }
                return true;
            }
        }
        return false;
    }

    private unsafe void CreateLogicalDevice()
    {
        var priority = 1.0f;
        var asyncFamily = asyncComputeWanted ? asyncFamilyCandidate : null;
        var queueInfos = stackalloc DeviceQueueCreateInfo[3];
        var queueCount = 1u;
        queueInfos[0] = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = GraphicsQueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        if (asyncFamily is { } af)
            queueInfos[queueCount++] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = af,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
        if (presentFamilyCandidate is { } pf)
            queueInfos[queueCount++] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = pf,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };

        var deviceExts = new List<string> { "VK_KHR_swapchain" };
        if (PortabilitySubsetRequired) deviceExts.Add("VK_KHR_portability_subset");
        if (MetalObjectsEnabled) deviceExts.Add(MetalObjectsExtension);
        if (displayTimingSupported && presentPaceWanted) { deviceExts.Add(DisplayTimingExtension); displayTimingEnabled = true; }
        using var extNames = new Utf8ArrayPin(deviceExts);

        // Enable anisotropic filtering when the device offers it. Without it,
        // minified textures at grazing angles (e.g. a checker floor seen
        // edge-on) alias into crawling moiré. samplerAnisotropy is ubiquitous
        // (incl. MoltenVK), but gate on support so we stay portable.
        Vk.GetPhysicalDeviceFeatures(PhysicalDevice, out var supportedFeatures);
        AnisotropySupported = supportedFeatures.SamplerAnisotropy;

        // Kept for CheckDescriptorLimits, which compares every new program against them.
        Vk.GetPhysicalDeviceProperties(PhysicalDevice, out var limitProps);
        descriptorLimits = limitProps.Limits;
        if (AnisotropySupported)
        {
            Vk.GetPhysicalDeviceProperties(PhysicalDevice, out var props);
            MaxAnisotropy = props.Limits.MaxSamplerAnisotropy;

            // <b>The highest sample count BOTH colour and depth can do, because a render pass needs
            // them to match.</b> Reported rather than discovered: asking Metal for 8x on a device
            // that does 4x is a native assertion — "MTLTextureDescriptor sampleCount (8) is not
            // supported by device" — which kills the process with no managed exception and no
            // Vulkan validation message. A caller that can see the limit can clamp to it and say so.
            var shared = (uint)props.Limits.FramebufferColorSampleCounts & (uint)props.Limits.FramebufferDepthSampleCounts;
            MaxMsaaSamples = 1;
            foreach (var n in new[] { 2, 4, 8, 16, 32, 64 })
            {
                if ((shared & (uint)n) != 0) MaxMsaaSamples = n;
            }

            // Every dynamic offset handed to vkCmdBindDescriptorSets must be a multiple of this.
            // Read rather than assumed: 256 on plenty of hardware, 16 on some, and a hard-coded
            // guess is either wasteful or invalid with no middle ground.
            uniformOffsetAlignment = (int)System.Math.Max(1ul, props.Limits.MinUniformBufferOffsetAlignment);
        }

        // multiDrawIndirect: one vkCmdDrawIndexedIndirect issuing drawCount>1
        // sub-draws from a buffer — the basis of the per-material indirect path.
        MultiDrawIndirectSupported = supportedFeatures.MultiDrawIndirect;

        // fragmentStoresAndAtomics: a FRAGMENT shader may write a storage image or buffer. Off by
        // default in Vulkan because it costs the driver some freedom to reorder, and the whole
        // pipeline until now only ever read in the fragment stage.
        //
        // It is enabled for feedback: shading is the only stage that knows which probes it actually
        // sampled, and that fact cannot be derived anywhere else. Measured on Sponza, no bake-time
        // test can separate a probe inside the model's shell from one in the arcade — they are
        // geometrically identical, and what distinguishes them is that nothing renders against one
        // of them. A fragment marking what it read is the only place that truth exists.
        FragmentStoresSupported = supportedFeatures.FragmentStoresAndAtomics;

        // drawIndirectFirstInstance: an indirect record's firstInstance may be non-zero. Without it every
        // indirect sub-draw starts at instance 0, so per-instance data cannot be reached through an offset —
        // which is how an instanced indirect draw finds its run of placements (gl_InstanceIndex includes
        // firstInstance). Validation cannot read an indirect buffer, so a missing feature is silent: the
        // application checks Features instead.
        var firstInstance = supportedFeatures.DrawIndirectFirstInstance;

        // Timeline semaphores, only beside VK_EXT_metal_objects: the frame timeline that orders native Metal work
        // (RenderGraph.External) against the frame is one, exported as an MTLSharedEvent. Without it, Metal objects
        // can still be exported but no Metal pass can run inside a frame, so the feature is not offered.
        var supported12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features };
        var supported2 = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &supported12 };
        Vk.GetPhysicalDeviceFeatures2(PhysicalDevice, &supported2);
        var frameTimelineWanted = MetalObjectsEnabled && supported12.TimelineSemaphore;
        // The 1.1-1.4 features (VulkanGraphicsDevice.Features.cs), the timeline among them.
        var modern = ChooseModernFeatures(frameTimelineWanted, supportedFeatures);
        SubgroupSizeRange = (modern.MinSubgroupSize, modern.MaxSubgroupSize);
        computeSubgroupSizeRequired = modern.ComputeSizeRequired;

        Features = (MultiDrawIndirectSupported ? GraphicsFeatures.MultiDrawIndirect : GraphicsFeatures.None)
            | (firstInstance ? GraphicsFeatures.DrawIndirectFirstInstance : GraphicsFeatures.None)
            | (FragmentStoresSupported ? GraphicsFeatures.FragmentStoresAndAtomics : GraphicsFeatures.None)
            | (frameTimelineWanted ? GraphicsFeatures.MetalObjects : GraphicsFeatures.None)
            | modern.Capabilities;
        Console.WriteLine($"[Vulkan] capabilities: {Features}; compute subgroups {modern.MinSubgroupSize}-{modern.MaxSubgroupSize} lanes.");
        var features = new PhysicalDeviceFeatures
        {
            SamplerAnisotropy = AnisotropySupported,
            MultiDrawIndirect = MultiDrawIndirectSupported,
            DrawIndirectFirstInstance = firstInstance,
            FragmentStoresAndAtomics = FragmentStoresSupported,
            ShaderInt16 = modern.ShaderInt16,
        };
        var ci = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = ChainModernFeatures(&modern),
            QueueCreateInfoCount = queueCount,
            PQueueCreateInfos = queueInfos,
            EnabledExtensionCount = (uint)deviceExts.Count,
            PpEnabledExtensionNames = extNames.Ptr,
            PEnabledFeatures = &features,
        };

        Device dev;
        var result = Vk.CreateDevice(PhysicalDevice, in ci, null, &dev);
        ThrowIfNotSuccess(result, "vkCreateDevice");
        Device = dev;

        Vk.GetDeviceQueue(Device, GraphicsQueueFamily, 0, out var queue);
        GraphicsQueue = queue;
        AsyncQueue = queue;
        AsyncQueueFamily = GraphicsQueueFamily;
        if (asyncFamily is { } family)
        {
            Vk.GetDeviceQueue(Device, family, 0, out var second);
            AsyncQueue = second;
            AsyncQueueFamily = family;
            HasAsyncQueue = true;
            Features |= GraphicsFeatures.AsyncCompute;
        }
        if (presentFamilyCandidate is { } presentFamily)
        {
            Vk.GetDeviceQueue(Device, presentFamily, 0, out var third);
            PresentQueue = third;
            PresentQueueFamily = presentFamily;
            HasPresentQueue = true;
        }
        Console.WriteLine(HasAsyncQueue
            ? $"[Vulkan] async compute: family {AsyncQueueFamily}'s queue beside the graphics family {GraphicsQueueFamily}'s."
            : "[Vulkan] async compute: none (one queue family with compute, or BLIX_ASYNC_COMPUTE=off).");
        if (frameTimelineWanted) CreateFrameTimeline();
    }

    private unsafe void PopulateInfo()
    {
        var props = Vk.GetPhysicalDeviceProperties(PhysicalDevice);
        var vendor = VendorIdName(props.VendorID);
        var renderer = Marshal.PtrToStringUTF8((nint)props.DeviceName) ?? "(unknown)";
        var apiMajor = (int)((props.ApiVersion >> 22) & 0x7F);
        var apiMinor = (int)((props.ApiVersion >> 12) & 0x3FF);
        var apiPatch = (int)(props.ApiVersion & 0xFFF);
        var driverVer = $"0x{props.DriverVersion:x}";

        Info = new GraphicsDeviceInfo(
            Vendor: $"{vendor} (driver {driverVer})",
            Renderer: renderer,
            Version: $"Vulkan {apiMajor}.{apiMinor}.{apiPatch}",
            ShadingLanguageVersion: "SPIR-V 1.6");
        // What the shaders' subgroup operations may use (subgroupAdd, ballots, broadcasts): read once, logged, so a shader
        // that leans on them can be checked against the device it runs on (MoltenVK maps them to Metal's SIMD groups).
        var subgroup = new PhysicalDeviceSubgroupProperties { SType = StructureType.PhysicalDeviceSubgroupProperties };
        var props2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &subgroup };
        Vk.GetPhysicalDeviceProperties2(PhysicalDevice, &props2);
        Console.WriteLine($"Subgroups: {subgroup.SubgroupSize} lanes; stages {subgroup.SupportedStages}; operations {subgroup.SupportedOperations}; quad ops in all stages {(bool)subgroup.QuadOperationsInAllStages}.");
    }

    private static string VendorIdName(uint vendorId) => vendorId switch
    {
        0x1002 => "AMD",
        0x10DE => "NVIDIA",
        0x8086 => "Intel",
        0x106B => "Apple",
        0x1414 => "Microsoft",
        0x13B5 => "ARM",
        _ => $"0x{vendorId:X4}",
    };

    private unsafe bool AreLayersAvailable(IReadOnlyList<string> requested)
    {
        uint count = 0;
        Vk.EnumerateInstanceLayerProperties(&count, null);
        var props = new LayerProperties[count];
        fixed (LayerProperties* p = props)
        {
            Vk.EnumerateInstanceLayerProperties(&count, p);
        }
        var available = new HashSet<string>();
        foreach (var lp in props)
        {
            var name = Marshal.PtrToStringUTF8((nint)lp.LayerName);
            if (name is not null) available.Add(name);
        }
        foreach (var r in requested)
        {
            if (!available.Contains(r)) return false;
        }
        return true;
    }

    private static int validationErrors;

    /// <summary>Validation-layer errors reported in this process so far, teardown included.</summary>
    public static int ValidationErrors => Volatile.Read(ref validationErrors);

    private static bool ShouldEnableValidation()
    {
        // Opt-in via env var so release runs don't pay validation cost by default.
        // BLIX_VK_VALIDATE=1 lights it up.
        //
        // Read from RenderCommandDiagnostics rather than parsed again here. The record-time
        // fingerprint has to be taken in Blix.Graphics — a command is fingerprinted where it is
        // built, which is a layer below this one — so that type owns the switch, and one question
        // keeps one answer. Two readers of one variable is how a process ends up half-enabled,
        // which for a record/execute check means reporting a mismatch about itself.
        return RenderCommandDiagnostics.Enabled;
    }

    private static unsafe uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT type,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        var msg = Marshal.PtrToStringUTF8((nint)data->PMessage) ?? "(no message)";
        // The objects the message is about, by debug name where one was set: a synchronization hazard's text names
        // only commands, so without these it cannot be traced to a resource.
        if (data->ObjectCount > 0 && data->PObjects is not null)
        {
            var names = new List<string>();
            for (var i = 0; i < data->ObjectCount; i++)
            {
                var o = data->PObjects[i];
                var name = o.PObjectName is null ? null : Marshal.PtrToStringUTF8((nint)o.PObjectName);
                names.Add($"{o.ObjectType}{(name is null ? "" : $" '{name}'")}");
            }
            msg += $" [objects: {string.Join(", ", names)}]";
        }
        var isError = severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt);
        if (isError) Interlocked.Increment(ref validationErrors);
        var label = isError ? "ERR " : "WARN";
        Console.Error.WriteLine($"[vk-{label}] {msg}");
        return Vk.False;
    }

    private static uint MakeVersion(int major, int minor, int patch) =>
        (uint)((major << 22) | (minor << 12) | patch);

    internal static void ThrowIfNotSuccess(Result result, string op)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{op} failed with {result}.");
        }
    }

    private unsafe void DestroyVulkan()
    {
        if (Vk is null) return;
        DestroySwapchain();
        DestroyFrameTimeline();
        if (Device.Handle != 0) Vk.DestroyDevice(Device, null);
        if (Surface.Handle != 0 && KhrSurface is not null)
        {
            KhrSurface.DestroySurface(Instance, Surface, null);
        }
        if (debugMessenger.Handle != 0 && debugUtils is not null)
        {
            debugUtils.DestroyDebugUtilsMessenger(Instance, debugMessenger, null);
            debugMessenger = default;
            // Only now is it safe for the callback to go: until the messenger is destroyed, the
            // layer can still call it.
            GC.KeepAlive(debugCallback);
        }
        if (Instance.Handle != 0) Vk.DestroyInstance(Instance, null);
        Vk.Dispose();
    }
}

// Helpers for marshalling UTF-8 byte buffers without leaking IntPtr juggling
// into the call sites.
internal ref struct Utf8Pin
{
    private GCHandle handle;
    public unsafe byte* Ptr;

    public unsafe Utf8Pin(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        Ptr = (byte*)handle.AddrOfPinnedObject();
    }

    public void Dispose()
    {
        if (handle.IsAllocated) handle.Free();
    }
}

internal ref struct Utf8ArrayPin
{
    private readonly GCHandle[] entries;
    private readonly GCHandle pointersHandle;
    public unsafe byte** Ptr;

    public unsafe Utf8ArrayPin(IReadOnlyList<string> values)
    {
        entries = new GCHandle[values.Count];
        var pointers = new nint[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(values[i] + "\0");
            entries[i] = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            pointers[i] = entries[i].AddrOfPinnedObject();
        }
        pointersHandle = GCHandle.Alloc(pointers, GCHandleType.Pinned);
        Ptr = (byte**)pointersHandle.AddrOfPinnedObject();
    }

    public void Dispose()
    {
        foreach (var e in entries)
        {
            if (e.IsAllocated) e.Free();
        }
        if (pointersHandle.IsAllocated) pointersHandle.Free();
    }
}
