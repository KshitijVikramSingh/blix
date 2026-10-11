using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Blix.Graphics.Vulkan;

// VK_EXT_metal_objects (GraphicsFeatures.MetalObjects): the Metal objects behind this device's Vulkan ones, for a
// native Metal pass that works beside Vulkan on the same memory. Enabled whenever MoltenVK offers it; every GPU buffer
// is then allocated exportable (VulkanGraphicsDevice.GpuBuffers).
//
// What is returned is the raw Objective-C object pointer, NOT retained: it lives exactly as long as the Vulkan object
// it was exported from, and a native caller that keeps it past that must retain it itself.
public sealed partial class VulkanGraphicsDevice
{
    internal const string MetalObjectsExtension = "VK_EXT_metal_objects";

    /// <summary>Whether the device enabled VK_EXT_metal_objects, so its Metal objects can be exported.</summary>
    internal bool MetalObjectsEnabled { get; private set; }

    private ExtMetalObjects? metalObjects;

    private ExtMetalObjects MetalObjects
    {
        get
        {
            ThrowIfDisposed();
            if (!MetalObjectsEnabled)
                throw new InvalidOperationException($"{MetalObjectsExtension} is not enabled on this device; there are no Metal objects to export.");
            if (metalObjects is null && !Vk.TryGetDeviceExtension(Instance, Device, out metalObjects))
                throw new InvalidOperationException($"{MetalObjectsExtension} is enabled but its entry point did not load.");
            return metalObjects!;
        }
    }

    // The frame timeline: a timeline semaphore exported as an MTLSharedEvent, which the frame signals where it splits
    // for an external pass and the Metal work signals back (VulkanGraphicsDevice.Swapchain, RunExternalPass). Its value
    // only rises, by two per external pass.
    private Semaphore frameTimeline;
    private nint frameTimelineEvent;
    private ulong frameTimelineValue;

    private unsafe void CreateFrameTimeline()
    {
        var export = new ExportMetalObjectCreateInfoEXT
        {
            SType = StructureType.ExportMetalObjectCreateInfoExt,
            ExportObjectType = ExportMetalObjectTypeFlagsEXT.ExportMetalObjectTypeMetalSharedEventBitExt,
        };
        var type = new SemaphoreTypeCreateInfo
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            PNext = &export,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = 0,
        };
        var ci = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo, PNext = &type };
        Semaphore semaphore;
        ThrowIfNotSuccess(Vk.CreateSemaphore(Device, in ci, null, &semaphore), "vkCreateSemaphore(frame timeline)");
        frameTimeline = semaphore;

        var eventInfo = new ExportMetalSharedEventInfoEXT { SType = StructureType.ExportMetalSharedEventInfoExt, Semaphore = semaphore };
        var info = new ExportMetalObjectsInfoEXT { SType = StructureType.ExportMetalObjectsInfoExt, PNext = &eventInfo };
        MetalObjects.ExportMetalObjects(Device, &info);
        frameTimelineEvent = (nint)eventInfo.MtlSharedEvent;
        if (frameTimelineEvent == 0)
            throw new InvalidOperationException("the frame timeline exported no MTLSharedEvent (an emulated timeline semaphore?).");
    }

    private unsafe void DestroyFrameTimeline()
    {
        if (frameTimeline.Handle == 0) return;
        Vk.DestroySemaphore(Device, frameTimeline, null);
        frameTimeline = default;
        frameTimelineEvent = 0;
    }

    // Sampled 2D textures (material maps) allocate exportable wherever the device can export, so a native Metal pass can
    // read them (ExportMetalTexture): the request belongs to the image's creation.
    private static ExportMetalObjectCreateInfoEXT MetalTextureExport() => new()
    {
        SType = StructureType.ExportMetalObjectCreateInfoExt,
        ExportObjectType = ExportMetalObjectTypeFlagsEXT.ExportMetalObjectTypeMetalTextureBitExt,
    };

    /// <inheritdoc />
    public unsafe nint ExportMetalTexture(TextureHandle handle)
    {
        var entry = GetTexture(handle);
        var textureInfo = new ExportMetalTextureInfoEXT
        {
            SType = StructureType.ExportMetalTextureInfoExt,
            Image = entry.Image,
            Plane = ImageAspectFlags.Plane0Bit,
        };
        var info = new ExportMetalObjectsInfoEXT { SType = StructureType.ExportMetalObjectsInfoExt, PNext = &textureInfo };
        MetalObjects.ExportMetalObjects(Device, &info);
        if ((nint)textureInfo.MtlTexture == 0)
            throw new InvalidOperationException($"texture {handle.Id} exported no MTLTexture.");
        return (nint)textureInfo.MtlTexture;
    }

    /// <inheritdoc />
    public unsafe nint ExportMetalDevice()
    {
        var deviceInfo = new ExportMetalDeviceInfoEXT { SType = StructureType.ExportMetalDeviceInfoExt };
        var info = new ExportMetalObjectsInfoEXT { SType = StructureType.ExportMetalObjectsInfoExt, PNext = &deviceInfo };
        MetalObjects.ExportMetalObjects(Device, &info);
        return (nint)deviceInfo.MtlDevice;
    }

    /// <inheritdoc />
    /// <remarks>Every GPU buffer has its own allocation bound at offset 0, so the exported buffer IS the GPU buffer.</remarks>
    public unsafe nint ExportMetalBuffer(GpuBufferHandle handle)
    {
        var entry = GetGpuBuffer(handle);
        var bufferInfo = new ExportMetalBufferInfoEXT { SType = StructureType.ExportMetalBufferInfoExt, Memory = entry.Memory };
        var info = new ExportMetalObjectsInfoEXT { SType = StructureType.ExportMetalObjectsInfoExt, PNext = &bufferInfo };
        MetalObjects.ExportMetalObjects(Device, &info);
        if ((nint)bufferInfo.MtlBuffer == 0)
            throw new InvalidOperationException($"GPU buffer '{entry.Name}' exported no MTLBuffer.");
        return (nint)bufferInfo.MtlBuffer;
    }
}
