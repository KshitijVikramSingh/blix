using System.Globalization;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --refine-worst (with --refine-need): a round refines the unsettled texels a round's rays take most error off first --
// each scored by the resolve (slice_resolve.comp, NextScores) as its relative standard error times what a round of
// RefinePasses more passes would take off it, rel x (1 - sqrt(n / (n + P))): the noisy and the little-traced first, a
// texel traced deep and still noisy sinking as its n grows. A round is at most 1 / RefineWorstRounds of the gather's
// refine budget; the candidates not taken stay in the pool (with their score) for the next round. With --refine-need
// alone every unsettled texel took 4 passes a round, the hopeless with them, and a round settled ~2% of ~470 K: the
// budget ran out at 4-5 rounds (Crytek Sponza, 2026-10-25).
internal sealed partial class SponzaLoop
{
    private bool refineWorst;
    // A round's share of the budget: 1 / --refine-worst-rounds (16). At a quarter a round took every unsettled texel of
    // Crytek Sponza's (608 K of room, 558 K unsettled) and ranked nothing.
    private int RefineWorstRounds = 16;
    private readonly GpuBufferHandle[] resolveScores = new GpuBufferHandle[2];
    private uint[] worstPoolIds = Array.Empty<uint>(), worstPoolPlaces = Array.Empty<uint>();
    private float[] worstPoolScores = Array.Empty<float>();

    private void ParseRefineWorst(AppArgs args)
    {
        refineWorst = !args.Flag("no-refine-worst") && refineNeed;
        if (args.Int("refine-worst-rounds") is { } wr) RefineWorstRounds = Math.Max(1, wr);
    }

    private float RefineWorstUniform => refineWorst ? RefinePasses : 0f;

    private GpuBufferHandle ScoresBinding(GpuBufferHandle stand) =>
        refineWorst && !resolveScores[resolveNext].Equals(default(GpuBufferHandle)) ? resolveScores[resolveNext] : stand;

    // BeginGpuResolve: the scores beside the lists, as large.
    private void EnsureResolveScores(int n)
    {
        if (!refineWorst) return;
        for (var i = 0; i < 2; i++)
        {
            if (!resolveScores[i].Equals(default(GpuBufferHandle))) RetireGpuBuffer(resolveScores[i]);
            resolveScores[i] = Own(device.CreateReadableGpuBuffer(Math.Max(16, n * 4), "sponza.resolve.scores"));
        }
    }

    // The round's end (host): list k's unsettled with their scores, merged with the pool; the next round the best scored
    // under its share of the budget, in id order, written back as list k; the rest the pool. Returns the round's ids.
    private uint[] SelectWorstRound(int k, int count)
    {
        device.WaitIdle();
        var ids = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(resolveIds[k], 0, Math.Max(4, count * 4)).AsSpan())[..count].ToArray();
        var places = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(resolvePlaces[k], 0, Math.Max(4, count * 4)).AsSpan())[..count].ToArray();
        var scores = new float[count];
        device.ReadMappedGpuBuffer(resolveScores[k], 0, MemoryMarshal.AsBytes(scores.AsSpan()));
        var (nextIds, nextPlaces) = RankWorst(ids, places, scores, count);
        device.WriteMappedGpuBuffer(resolveIds[k], 0, MemoryMarshal.AsBytes(nextIds.AsSpan()));
        device.WriteMappedGpuBuffer(resolvePlaces[k], 0, MemoryMarshal.AsBytes(nextPlaces.AsSpan()));
        return nextIds;
    }

    // The ranking (the host's round end and the frame's, on a worker): the round's unsettled with the pool, the best
    // under the round's share, in id order; the rest the pool. --patch-rank: each score by its patch's screen weight.
    private (uint[] Ids, uint[] Places) RankWorst(uint[] ids, uint[] places, float[] scores, int count)
    {
        if (sunNodeRefineRound == 0) { worstPoolIds = Array.Empty<uint>(); worstPoolPlaces = Array.Empty<uint>(); worstPoolScores = Array.Empty<float>(); }
        if (patchWeight is not null)
            for (var i = 0; i < scores.Length; i++) scores[i] *= PatchWeightOf(ids[i]);
        var allIds = worstPoolIds.Concat(ids).ToArray();
        var allPlaces = worstPoolPlaces.Concat(places).ToArray();
        var allScores = worstPoolScores.Concat(scores).ToArray();
        var order = Enumerable.Range(0, allIds.Length).OrderByDescending(i => allScores[i]).ToArray();
        var sweepRays = (long)sunNodeTraceIds.Length * sunNodeRays;
        var perRound = (long)(refineBudget * sweepRays / RefineWorstRounds);
        var take = (int)Math.Min(order.Length, Math.Max(1, perRound / (RefinePasses * 32)));
        var picked = order[..take];
        var rest = order[take..];
        var nextIds = picked.Select(i => allIds[i]).ToArray();
        var nextPlaces = picked.Select(i => allPlaces[i]).ToArray();
        Array.Sort(nextIds, nextPlaces);
        worstPoolIds = rest.Select(i => allIds[i]).ToArray();
        worstPoolPlaces = rest.Select(i => allPlaces[i]).ToArray();
        worstPoolScores = rest.Select(i => allScores[i]).ToArray();
        var cut = take > 0 ? allScores[picked[^1]] : 0f;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] refine worst: {take:N0} of {allIds.Length:N0} candidates (the round's {count:N0} unsettled, the pool's {allIds.Length - count:N0}) take the next round, their score down to {cut:G3} (the best {(order.Length > 0 ? allScores[order[0]] : 0f):G3}){(patchWeight is not null ? ", by patch" : "")}."));
        return (nextIds, nextPlaces);
    }
}
