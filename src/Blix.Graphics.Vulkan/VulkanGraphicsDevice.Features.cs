using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace Blix.Graphics.Vulkan;

// The features Vulkan 1.1-1.4 added, enabled where the device offers them and reported as GraphicsFeatures
// capabilities. Until 2026-10-26 the device was created with core 1.0's features and timeline semaphores only, though
// the shaders compiled for 1.4 and MoltenVK on the M4 offers nearly all of these (vulkaninfo, 1.4.334): nothing could
// use 16-bit types, required subgroup sizes, synchronization2, dynamic rendering or its local reads.
//
// Each is enabled only when asked for by a capability below -- an enabled feature can cost the driver freedom (it is
// what fragmentStoresAndAtomics' note in Init says) -- and only when supported. Not enabled: buffer device addresses
// and descriptor indexing (nothing here uses them yet), host image copies (for streaming, later). Not offered by
// MoltenVK: drawIndirectCount, 64-bit buffer atomics.
public sealed partial class VulkanGraphicsDevice
{
    // VkPhysicalDeviceVulkan14Features, which Silk.NET 2.22 does not declare (its 1.4 features come as the KHR/EXT
    // structs they were promoted from). The layout is the registry's: sType, pNext, then 21 VkBool32 in this order.
    private unsafe struct PhysicalDeviceVulkan14Features
    {
        public StructureType SType;   // VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_4_FEATURES (55)
        public void* PNext;
        public Bool32 GlobalPriorityQuery;
        public Bool32 ShaderSubgroupRotate;
        public Bool32 ShaderSubgroupRotateClustered;
        public Bool32 ShaderFloatControls2;
        public Bool32 ShaderExpectAssume;
        public Bool32 RectangularLines;
        public Bool32 BresenhamLines;
        public Bool32 SmoothLines;
        public Bool32 StippledRectangularLines;
        public Bool32 StippledBresenhamLines;
        public Bool32 StippledSmoothLines;
        public Bool32 VertexAttributeInstanceRateDivisor;
        public Bool32 VertexAttributeInstanceRateZeroDivisor;
        public Bool32 IndexTypeUint8;
        public Bool32 DynamicRenderingLocalRead;
        public Bool32 Maintenance5;
        public Bool32 Maintenance6;
        public Bool32 PipelineProtectedAccess;
        public Bool32 PipelineRobustness;
        public Bool32 HostImageCopy;
        public Bool32 PushDescriptor;
    }

    private const StructureType PhysicalDeviceVulkan14FeaturesType = (StructureType)55;

    // What device creation chains onto its create-info: a struct, so the caller's local holds the four feature structs
    // on its stack, where the chain's pointers stay valid until vkCreateDevice returns.
    private struct ModernFeatures
    {
        public PhysicalDeviceVulkan11Features V11;
        public PhysicalDeviceVulkan12Features V12;
        public PhysicalDeviceVulkan13Features V13;
        public PhysicalDeviceVulkan14Features V14;
        public bool ShaderInt16;
        public GraphicsFeatures Capabilities;
        public uint MinSubgroupSize, MaxSubgroupSize;
        public bool ComputeSizeRequired;
    }

    /// <summary>The smallest and largest subgroup sizes the device can run compute at (Vulkan 1.3's subgroup size
    /// control); a pipeline asks for one with ComputePipelineOptions.</summary>
    internal (uint Min, uint Max) SubgroupSizeRange { get; private set; }

    // Whether a compute pipeline can require its subgroup size (else it runs at the device's one size).
    private bool computeSubgroupSizeRequired;

    // Supported, then the wanted subset: the 1.1-1.4 feature structs to enable, and the capabilities they amount to.
    // timelineWanted carries Init's frame-timeline choice (the one 1.2 feature enabled before this existed).
    private unsafe ModernFeatures ChooseModernFeatures(bool timelineWanted, PhysicalDeviceFeatures core)
    {
        var s11 = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features };
        var s12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, PNext = &s11 };
        var s13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features, PNext = &s12 };
        var s14 = new PhysicalDeviceVulkan14Features { SType = PhysicalDeviceVulkan14FeaturesType, PNext = &s13 };
        var supported = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, PNext = &s14 };
        Vk.GetPhysicalDeviceFeatures2(PhysicalDevice, &supported);
        var p11 = new PhysicalDeviceVulkan11Properties { SType = StructureType.PhysicalDeviceVulkan11Properties };
        var p13 = new PhysicalDeviceVulkan13Properties { SType = StructureType.PhysicalDeviceVulkan13Properties, PNext = &p11 };
        var props = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &p13 };
        Vk.GetPhysicalDeviceProperties2(PhysicalDevice, &props);
        // A 1.3 device that does not know the 1.4 struct leaves it zeroed: every 1.4 feature reads absent.

        var m = new ModernFeatures
        {
            V11 = new PhysicalDeviceVulkan11Features { SType = StructureType.PhysicalDeviceVulkan11Features },
            V12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, TimelineSemaphore = timelineWanted },
            V13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features },
            V14 = new PhysicalDeviceVulkan14Features { SType = PhysicalDeviceVulkan14FeaturesType },
            MinSubgroupSize = p13.MinSubgroupSize,
            MaxSubgroupSize = p13.MaxSubgroupSize,
        };
        var caps = GraphicsFeatures.None;

        if (s12.ShaderFloat16)
        {
            m.V12.ShaderFloat16 = true;
            caps |= GraphicsFeatures.ShaderFloat16;
        }
        if (core.ShaderInt16 && s11.StorageBuffer16BitAccess && s11.UniformAndStorageBuffer16BitAccess)
        {
            m.ShaderInt16 = true;
            m.V11.StorageBuffer16BitAccess = true;
            m.V11.UniformAndStorageBuffer16BitAccess = true;
            caps |= GraphicsFeatures.Storage16Bit;
        }
        if (s12.ShaderInt8 && s12.StorageBuffer8BitAccess)
        {
            m.V12.ShaderInt8 = true;
            m.V12.StorageBuffer8BitAccess = true;
            caps |= GraphicsFeatures.Storage8Bit;
        }
        if (s12.ScalarBlockLayout)
        {
            m.V12.ScalarBlockLayout = true;
            caps |= GraphicsFeatures.ScalarBlockLayout;
        }
        // 32 lanes, all full: full subgroups asked for (computeFullSubgroups), and 32 either required (a device that
        // takes a required size for compute) or the size the device always runs (subgroupSize, used when no size is
        // required and none may vary). MoltenVK takes no required size in any stage (requiredSubgroupSizeStages none)
        // and runs Metal's 32-lane SIMD groups: the second.
        m.ComputeSizeRequired = s13.SubgroupSizeControl && (p13.RequiredSubgroupSizeStages & ShaderStageFlags.ComputeBit) != 0
            && p13.MinSubgroupSize <= 32 && p13.MaxSubgroupSize >= 32;
        if (s13.SubgroupSizeControl && s13.ComputeFullSubgroups && (m.ComputeSizeRequired || p11.SubgroupSize == 32))
        {
            m.V13.SubgroupSizeControl = true;
            m.V13.ComputeFullSubgroups = true;
            caps |= GraphicsFeatures.ComputeSubgroups32;
        }
        if (s13.Synchronization2)
        {
            m.V13.Synchronization2 = true;
            caps |= GraphicsFeatures.Synchronization2;
        }
        if (s13.DynamicRendering)
        {
            m.V13.DynamicRendering = true;
            caps |= GraphicsFeatures.DynamicRendering;
        }
        if (s13.DynamicRendering && s14.DynamicRenderingLocalRead)
        {
            m.V14.DynamicRenderingLocalRead = true;
            caps |= GraphicsFeatures.DynamicRenderingLocalRead;
        }
        if (s14.ShaderSubgroupRotate)
        {
            m.V14.ShaderSubgroupRotate = true;
            caps |= GraphicsFeatures.SubgroupRotate;
        }
        if (s14.PushDescriptor)
        {
            m.V14.PushDescriptor = true;
            caps |= GraphicsFeatures.PushDescriptors;
        }
        m.Capabilities = caps;
        return m;
    }

    // The enabled structs chained (1.4 -> 1.3 -> 1.2 -> 1.1) for DeviceCreateInfo.PNext; m is the caller's stack local.
    private static unsafe void* ChainModernFeatures(ModernFeatures* m)
    {
        m->V12.PNext = &m->V11;
        m->V13.PNext = &m->V12;
        m->V14.PNext = &m->V13;
        return &m->V14;
    }
}
