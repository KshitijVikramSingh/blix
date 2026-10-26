namespace Blix.Graphics;

/// <summary>Optional device features an application can depend on, and the device enabled.</summary>
/// <remarks>
/// A feature listed here is one the device supports and turned on at creation. An application that relies
/// on one checks <see cref="IGraphicsDevice.Features"/> and refuses by name when it is absent, rather than
/// recording commands that are invalid without it: validation cannot always see the difference (it cannot
/// read an indirect buffer's contents), so the device being silent is not the same as it being fine.
/// </remarks>
[Flags]
public enum GraphicsFeatures
{
    None = 0,

    /// <summary>One indirect draw command issuing several sub-draws (multiDrawIndirect).</summary>
    MultiDrawIndirect = 1 << 0,

    /// <summary>Indirect draw records whose firstInstance is not zero (drawIndirectFirstInstance).</summary>
    DrawIndirectFirstInstance = 1 << 1,

    /// <summary>Fragment shaders that write storage images or buffers (fragmentStoresAndAtomics).</summary>
    FragmentStoresAndAtomics = 1 << 2,

    /// <summary>The Metal objects behind the device and its GPU buffers can be exported (VK_EXT_metal_objects), for a
    /// native Metal pass on the same memory: <see cref="IGraphicsDevice.ExportMetalDevice"/>,
    /// <see cref="IGraphicsDevice.ExportMetalBuffer"/>.</summary>
    MetalObjects = 1 << 3,

    // Capabilities from Vulkan 1.1-1.4 (VulkanGraphicsDevice.Features.cs), named by what an algorithm asks for rather
    // than the version that made them core: a 1.4 device need not offer any of them.

    /// <summary>16-bit float arithmetic in shaders (shaderFloat16): float16_t, f16vec*.</summary>
    ShaderFloat16 = 1 << 4,

    /// <summary>16-bit integers and floats in storage and uniform buffers (shaderInt16, storageBuffer16BitAccess,
    /// uniformAndStorageBuffer16BitAccess).</summary>
    Storage16Bit = 1 << 5,

    /// <summary>8-bit integers in storage buffers (shaderInt8, storageBuffer8BitAccess).</summary>
    Storage8Bit = 1 << 6,

    /// <summary>C-like packing of buffer blocks (scalarBlockLayout): a vec3 followed by a float in 16 bytes.</summary>
    ScalarBlockLayout = 1 << 7,

    /// <summary>A compute pipeline may require 32-lane subgroups, every one full (subgroupSizeControl,
    /// computeFullSubgroups, 32 within the device's range for the compute stage): what a shader that reduces or
    /// ballots over a 32-lane tile relies on (ComputePipelineOptions.FullSubgroups32).</summary>
    ComputeSubgroups32 = 1 << 8,

    /// <summary>Lane rotation in subgroups (shaderSubgroupRotate): subgroupRotate.</summary>
    SubgroupRotate = 1 << 9,

    /// <summary>vkCmdPipelineBarrier2 and its stage/access vocabulary (synchronization2).</summary>
    Synchronization2 = 1 << 10,

    /// <summary>Graphics passes begun with their attachments, no VkRenderPass object (dynamicRendering).</summary>
    DynamicRendering = 1 << 11,

    /// <summary>A pass reading its own attachments at the same pixel while rendering (dynamicRenderingLocalRead): on a
    /// tile-based GPU the value stays in tile memory.</summary>
    DynamicRenderingLocalRead = 1 << 12,

    /// <summary>Descriptors written straight into the command buffer (pushDescriptor).</summary>
    PushDescriptors = 1 << 13,
}

/// <summary>What a compute program needs of how it runs (IGraphicsDevice.CreateComputePipeline).</summary>
[Flags]
public enum ComputePipelineOptions
{
    None = 0,

    /// <summary>Every subgroup 32 lanes and full: for a shader whose subgroup operations assume a 32-lane tile (its
    /// local size X a multiple of 32). Needs <see cref="GraphicsFeatures.ComputeSubgroups32"/>.</summary>
    FullSubgroups32 = 1 << 0,
}
