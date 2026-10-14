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
    // <b>Reused by contents.</b> A transient set is never written after its first write, so two draws whose writes
    // are identical -- the same buffers, ranges, images and samplers at the same bindings -- can share one: the safety
    // per-draw allocation gave (no draw's update clobbering another's) holds, and the allocation and the update are
    // paid once a frame instead of once a draw (vkAllocateDescriptorSets + vkUpdateDescriptorSets were 8.8% of the
    // main thread on Sponza, 2026-10-14). Dynamic uniform offsets are given at bind, not in the set. Keyed by the
    // layout and two independent 64-bit hashes of the writes; cleared with the slot's pools.
    private Dictionary<(ulong Layout, ulong A, ulong B), DescriptorSet>[] transientSetCache = Array.Empty<Dictionary<(ulong, ulong, ulong), DescriptorSet>>();

    private void CreateTransientDescriptorPools()
    {
        transientPools = new List<DescriptorPool>[MaxFramesInFlightConst];
        transientPoolAt = new int[MaxFramesInFlightConst];
        transientSetCache = new Dictionary<(ulong, ulong, ulong), DescriptorSet>[MaxFramesInFlightConst];
        for (var i = 0; i < transientSetCache.Length; i++) transientSetCache[i] = new Dictionary<(ulong, ulong, ulong), DescriptorSet>();
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
        transientSetCache[frameSlot].Clear();
    }

    // The set holding these writes this frame: one already written with the same contents, else a new one written.
    private unsafe DescriptorSet TransientSetFor(int frameSlot, DescriptorSetLayout layout, WriteDescriptorSet* writes, int count)
    {
        ulong a = 14695981039346656037UL, b = 0x9E3779B97F4A7C15UL;
        void Mix(ulong v)
        {
            a = (a ^ v) * 1099511628211UL;
            b = (b ^ (v + 0x632BE59BD9B4E019UL)) * 0xBF58476D1CE4E5B9UL;
            b ^= b >> 31;
        }
        Mix((ulong)count);
        for (var i = 0; i < count; i++)
        {
            ref var w = ref writes[i];
            Mix(((ulong)w.DstBinding << 32) | ((ulong)w.DstArrayElement << 8) | (ulong)(uint)w.DescriptorType);
            if (w.PBufferInfo != null) { Mix(w.PBufferInfo->Buffer.Handle); Mix(w.PBufferInfo->Offset); Mix(w.PBufferInfo->Range); }
            if (w.PImageInfo != null) { Mix(w.PImageInfo->Sampler.Handle); Mix(w.PImageInfo->ImageView.Handle); Mix((ulong)w.PImageInfo->ImageLayout); }
        }
        var cache = transientSetCache[frameSlot];
        var key = (layout.Handle, a, b);
        if (cache.TryGetValue(key, out var ds)) return ds;
        ds = AllocateTransientSet(frameSlot, layout);
        for (var i = 0; i < count; i++) writes[i].DstSet = ds;
        if (count > 0) Vk.UpdateDescriptorSets(Device, (uint)count, writes, 0, default(CopyDescriptorSet*));
        cache[key] = ds;
        return ds;
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
