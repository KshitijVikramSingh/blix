using Silk.NET.Vulkan;

namespace Blix.Graphics.Vulkan;

// Per-frame descriptor pool for per-draw allocations. Reset is safe only
// after the frame's InFlight fence signals — by then last cycle's
// descriptors are provably no longer referenced by the GPU.
//
// Per-draw allocation (not per-(program, frame) caching) is what lets two
// draws share a shader program but bind different inputs without
// vkUpdateDescriptorSets clobbering one with the other — they get
// independent sets. Material descriptors keep their own per-instance
// path; this pool handles everything else.
public sealed partial class VulkanGraphicsDevice
{
    // Conservative — exhaustion surfaces as vkAllocateDescriptorSets failure,
    // at which point bump rather than complicate with growable chained pools.
    private const uint TransientPoolMaxSets = 1024;
    private const uint TransientPoolPerType = 8192;

    private DescriptorPool[] transientPools = Array.Empty<DescriptorPool>();

    private unsafe void CreateTransientDescriptorPools()
    {
        transientPools = new DescriptorPool[MaxFramesInFlightConst];
        var poolSizes = stackalloc DescriptorPoolSize[3];
        poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = TransientPoolPerType };
        poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = TransientPoolPerType };
        poolSizes[2] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = TransientPoolPerType };
        var poolCi = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 3,
            PPoolSizes = poolSizes,
            MaxSets = TransientPoolMaxSets,
        };
        for (var i = 0; i < transientPools.Length; i++)
        {
            DescriptorPool pool;
            ThrowIfNotSuccess(
                Vk.CreateDescriptorPool(Device, in poolCi, null, &pool),
                $"vkCreateDescriptorPool(transient[{i}])");
            transientPools[i] = pool;
        }
    }

    private unsafe void ResetTransientDescriptorPool(int frameSlot)
    {
        if (transientPools.Length == 0) return;
        ThrowIfNotSuccess(
            Vk.ResetDescriptorPool(Device, transientPools[frameSlot], 0),
            $"vkResetDescriptorPool(transient[{frameSlot}])");
    }

    private unsafe DescriptorSet AllocateTransientSet(int frameSlot, DescriptorSetLayout layout)
    {
        var alloc = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = transientPools[frameSlot],
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        DescriptorSet ds;
        ThrowIfNotSuccess(
            Vk.AllocateDescriptorSets(Device, in alloc, &ds),
            "vkAllocateDescriptorSets(transient)");
        return ds;
    }

    private unsafe void DestroyTransientDescriptorPools()
    {
        for (var i = 0; i < transientPools.Length; i++)
        {
            if (transientPools[i].Handle != 0)
            {
                Vk.DestroyDescriptorPool(Device, transientPools[i], null);
                transientPools[i] = default;
            }
        }
        transientPools = Array.Empty<DescriptorPool>();
    }
}
