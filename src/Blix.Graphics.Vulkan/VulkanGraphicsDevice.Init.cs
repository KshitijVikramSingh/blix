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
    internal uint GraphicsQueueFamily { get; private set; }
    internal bool PortabilitySubsetRequired { get; private set; }

    internal KhrSurface KhrSurface { get; private set; } = null!;
    internal SurfaceKHR Surface { get; private set; }

    private DebugUtilsMessengerEXT debugMessenger;
    private ExtDebugUtils? debugUtils;
    private bool validationEnabled;

    private static readonly string[] ValidationLayers = { "VK_LAYER_KHRONOS_validation" };

    private void InitializeVulkan(IVkSurface windowSurface)
    {
        Vk = Vk.GetApi();
        validationEnabled = ShouldEnableValidation() && AreLayersAvailable(ValidationLayers);

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
            ApiVersion = Vk.Version12,
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
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
            !extensions.Contains("VK_KHR_portability_enumeration"))
        {
            extensions.Add("VK_KHR_portability_enumeration");
        }
        if (validationEnabled)
        {
            extensions.Add("VK_EXT_debug_utils");
        }

        using var extNames = new Utf8ArrayPin(extensions);
        using var layerNames = new Utf8ArrayPin(validationEnabled ? ValidationLayers : Array.Empty<string>());

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = extNames.Ptr,
            EnabledLayerCount = (uint)(validationEnabled ? ValidationLayers.Length : 0),
            PpEnabledLayerNames = layerNames.Ptr,
            Flags = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? InstanceCreateFlags.EnumeratePortabilityBitKhr
                : InstanceCreateFlags.None,
        };

        Instance instance;
        var result = Vk.CreateInstance(in createInfo, null, &instance);
        ThrowIfNotSuccess(result, "vkCreateInstance");
        Instance = instance;
    }

    private unsafe void TryAttachDebugMessenger()
    {
        if (!validationEnabled) return;
        if (!Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils du)) return;
        debugUtils = du;

        var ci = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                            | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                        | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                        | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugCallback),
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
            if (name == "VK_KHR_portability_subset")
            {
                PortabilitySubsetRequired = true;
                break;
            }
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
        for (uint i = 0; i < count; i++)
        {
            if ((families[i].QueueFlags & QueueFlags.GraphicsBit) == 0) continue;
            Silk.NET.Core.Bool32 supported = false;
            KhrSurface.GetPhysicalDeviceSurfaceSupport(device, i, Surface, &supported);
            if (supported)
            {
                family = i;
                return true;
            }
        }
        return false;
    }

    private unsafe void CreateLogicalDevice()
    {
        var priority = 1.0f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = GraphicsQueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        var deviceExts = new List<string> { "VK_KHR_swapchain" };
        if (PortabilitySubsetRequired) deviceExts.Add("VK_KHR_portability_subset");
        using var extNames = new Utf8ArrayPin(deviceExts);

        var features = new PhysicalDeviceFeatures();
        var ci = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
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
            ShadingLanguageVersion: "SPIR-V 1.5");
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

    private static bool ShouldEnableValidation()
    {
        // Opt-in via env var so release runs don't pay validation cost
        // by default. BLIX_VK_VALIDATE=1 lights it up.
        var env = Environment.GetEnvironmentVariable("BLIX_VK_VALIDATE");
        return env == "1" || string.Equals(env, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT type,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        var msg = Marshal.PtrToStringUTF8((nint)data->PMessage) ?? "(no message)";
        var label = severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) ? "ERR " : "WARN";
        Console.Error.WriteLine($"[vk-{label}] {msg}");
        return Vk.False;
    }

    private static uint MakeVersion(int major, int minor, int patch) =>
        (uint)((major << 22) | (minor << 12) | patch);

    private static void ThrowIfNotSuccess(Result result, string op)
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
        if (Device.Handle != 0) Vk.DestroyDevice(Device, null);
        if (Surface.Handle != 0 && KhrSurface is not null)
        {
            KhrSurface.DestroySurface(Instance, Surface, null);
        }
        if (debugMessenger.Handle != 0 && debugUtils is not null)
        {
            debugUtils.DestroyDebugUtilsMessenger(Instance, debugMessenger, null);
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
