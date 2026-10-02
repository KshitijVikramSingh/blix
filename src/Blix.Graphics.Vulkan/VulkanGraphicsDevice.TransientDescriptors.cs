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
    // <b>Chained, because a constant is a cliff.</b> One transient set is allocated per non-material set per
    // draw, so the count scales with (draws × sets-per-program), and the pool used to be one fixed size,
    // bumped whenever a scene outgrew it: Sponza's cascaded-shadow frame (~2100 sets) set the last bump.
    // Bistro's exterior is 7403 primitives across the pre-pass, three cascades and the lit pass — some
    // 40k sets — and exhausted it on the first frame. So each frame slot holds a chain: when its current
    // pool is exhausted the next one is used (created the first time it is needed), and the frame's reset
    // resets the whole chain. A frame keeps the pools its heaviest frame needed; nothing is freed until
    // the device goes.
    private const uint TransientPoolMaxSets = 4096;
    private const uint TransientPoolPerType = 8192;

    private List<DescriptorPool>[] transientPools = Array.Empty<List<DescriptorPool>>();
    private int[] transientPoolAt = Array.Empty<int>();

    private void CreateTransientDescriptorPools()
    {
        transientPools = new List<DescriptorPool>[MaxFramesInFlightConst];
        transientPoolAt = new int[MaxFramesInFlightConst];
        for (var i = 0; i < transientPools.Length; i++) transientPools[i] = new List<DescriptorPool> { CreateTransientPool(i, 0) };
    }

    private unsafe DescriptorPool CreateTransientPool(int frameSlot, int link)
    {
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
        DescriptorPool pool;
        ThrowIfNotSuccess(
            Vk.CreateDescriptorPool(Device, in poolCi, null, &pool),
            $"vkCreateDescriptorPool(transient[{frameSlot}].{link})");
        return pool;
    }

    private unsafe void ResetTransientDescriptorPool(int frameSlot)
    {
        if (transientPools.Length == 0) return;
        foreach (var pool in transientPools[frameSlot])
        {
            ThrowIfNotSuccess(Vk.ResetDescriptorPool(Device, pool, 0), $"vkResetDescriptorPool(transient[{frameSlot}])");
        }

        transientPoolAt[frameSlot] = 0;
    }

    private unsafe DescriptorSet AllocateTransientSet(int frameSlot, DescriptorSetLayout layout)
    {
        var chain = transientPools[frameSlot];
        while (true)
        {
            var alloc = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = chain[transientPoolAt[frameSlot]],
                DescriptorSetCount = 1,
                PSetLayouts = &layout,
            };
            DescriptorSet ds;
            var result = Vk.AllocateDescriptorSets(Device, in alloc, &ds);
            if (result is not (Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool))
            {
                ThrowIfNotSuccess(result, "vkAllocateDescriptorSets(transient)");
                return ds;
            }

            // This pool is spent for the frame: move along the chain, growing it the first time.
            var next = ++transientPoolAt[frameSlot];
            if (next == chain.Count) chain.Add(CreateTransientPool(frameSlot, next));
        }
    }

    private unsafe void DestroyTransientDescriptorPools()
    {
        foreach (var chain in transientPools)
        foreach (var pool in chain)
        {
            if (pool.Handle != 0) Vk.DestroyDescriptorPool(Device, pool, null);
        }

        transientPools = Array.Empty<List<DescriptorPool>>();
        transientPoolAt = Array.Empty<int>();
    }
}
