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
    // Exhaustion surfaces as vkAllocateDescriptorSets failure; bump rather
    // than complicate with growable chained pools. One transient set is
    // allocated per non-material set per draw, so set count scales with
    // (draws × sets-per-program). VulkanSponza's cascaded-shadow scene is the
    // current high-water mark: ~455 lit draws × 2 sets (per-frame + per-pass)
    // plus ~1190 shadow-cascade draws × 1 set ≈ 2100 sets/frame. 4096 leaves
    // ~2× headroom for more lights / cascades. The Marches terrain is the
    // per-type high-water mark: ~1200 chunk runs and ~200 pieces a frame, each
    // binding ~9 sampled images (the voxel grid, the light volumes, shadow
    // maps), ≈ 13000 sampled images/frame, past the 8192 it had. Both raised
    // for that with ~4× headroom; the pools are small either way.
    private const uint TransientPoolMaxSets = 16384;
    private const uint TransientPoolPerType = 65536;

    private DescriptorPool[] transientPools = Array.Empty<DescriptorPool>();

    private unsafe void CreateTransientDescriptorPools()
    {
        transientPools = new DescriptorPool[MaxFramesInFlightConst];
        var poolSizes = stackalloc DescriptorPoolSize[7];
        poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = TransientPoolPerType };
        poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = TransientPoolPerType };
        poolSizes[2] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = TransientPoolPerType };
        // Program-owned uniform blocks on sets 0-1 are UNIFORM_BUFFER_DYNAMIC — a DISTINCT pool
        // type, not a flavour of UNIFORM_BUFFER. Omitting it allocates sets the pool never budgeted
        // for; the layers say so, and an implementation is entitled to fail the allocation instead.
        poolSizes[3] = new DescriptorPoolSize { Type = DescriptorType.UniformBufferDynamic, DescriptorCount = TransientPoolPerType };
        // The same holds for every other type a set can carry. Storage images were allocated from pools
        // with no STORAGE_IMAGE budget, which validation warned about on every compute dispatch, and
        // separate images and samplers are sets' types now too. An immutable sampler still takes a
        // SAMPLER descriptor from the pool.
        poolSizes[4] = new DescriptorPoolSize { Type = DescriptorType.StorageImage, DescriptorCount = TransientPoolPerType };
        poolSizes[5] = new DescriptorPoolSize { Type = DescriptorType.SampledImage, DescriptorCount = TransientPoolPerType };
        poolSizes[6] = new DescriptorPoolSize { Type = DescriptorType.Sampler, DescriptorCount = TransientPoolPerType };
        var poolCi = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 7,
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
