using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The sun nodes' cook, refined until settled (under --split-light): the sweep's 512 rays a texel left the dark corners
// of the crevices blotchy, where the light bake gave each texel still noisy more rays, up to --texel-max-rays. Each
// slice read back after the sweep keeps the texels whose light is still noisy (the bake's rule, TexelSettled: the
// standard error of the composed light -- every column summed -- over its mean, under --texel-noise), with their sums;
// then rounds of RefinePasses more passes over those alone, the sums added, until every one settles or reaches the most
// rays. The shade keeps the moment (texel_nodes_shade.comp: column 0's w); a round's rays carry on the texel's pass
// numbering, so no ray repeats one before it.
internal sealed partial class SponzaLoop
{
    private int sunNodeRefineRound;          // 0 the sweep; 1.. the refining rounds
    private uint[] sunNodeRefineIds = Array.Empty<uint>();
    private GpuBufferHandle sunNodeRefineBuffer;
    // The texels of the round under way (by their place in sunNodeRefineIds): each column's sum of pass means, the sum of
    // the composed light's squared pass luminance, the passes; and those the round leaves unsettled, for the next.
    private Vector3[] sunNodeRefineSums = Array.Empty<Vector3>();
    private float[] sunNodeRefineSquares = Array.Empty<float>();
    private int[] sunNodeRefinePasses = Array.Empty<int>();
    private List<uint> sunNodeNextIds = new();
    private List<Vector3> sunNodeNextSums = new();
    private List<float> sunNodeNextSquares = new();
    private List<int> sunNodeNextPasses = new();
    private long sunNodeRefineRays;

    private bool SunNodeRefines => splitCooking && sunNodeStride == 1 && !sunNodeExact;

    private static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    // TexelSettled's rule over a texel's columns: past the most rays, or the composed light's standard error under the noise.
    // The noise is judged against what the eye sees, not the texel's own light alone: under its own light a dark texel's
    // bar shrinks with it, and proving a corner at 1% of the floor's light takes ~100x the floor's rays -- Sponza's dark
    // texels ran every round to --texel-max-rays (512 + 12 x 128 = 2,048) and never settled (2026-10-14). So the bar is
    // the noise share of the larger of its light and --texel-dark-floor (0.1) of the scene's median texel light
    // (sunNodeLightHistogram, filled as the sweep's slices land): a texel far darker than the scene settles once its
    // noise is small against that.
    private bool CookSettled(ReadOnlySpan<Vector3> sums, float squares, int passes)
    {
        if (passes * 32 >= texelMaxRays) return true;
        var mean = 0f;
        foreach (var s in sums) mean += Luminance(s);
        mean /= passes;
        var variance = MathF.Max(squares / passes - mean * mean, 0f);
        return MathF.Sqrt(variance / passes) <= texelNoise * MathF.Max(mean, MathF.Max(TexelNoiseFloor, sunNodeDarkBar));
    }

    // --texel-dark-floor: the share of the scene's median texel light under which a texel's noise is judged against that
    // (0: against its own light alone, as before).
    private float texelDarkFloor = 0.1f;
    private float sunNodeDarkBar;
    // The sweep's composed texel light, log2 bins of a quarter stop from 2^-24: its median sets the bar.
    private readonly long[] sunNodeLightHistogram = new long[128];

    private void CountSweepLight(ReadOnlySpan<Vector3> merged, int count, int suns, int passes)
    {
        if (texelDarkFloor <= 0f) return;
        for (var t = 0; t < count; t += 7)
        {
            var mean = 0f;
            for (var k = 0; k < suns; k++) mean += Luminance(merged[t * suns + k]);
            mean /= passes;
            var bin = mean > 0f ? (int)Math.Clamp((MathF.Log2(mean) + 24f) * 4f, 0f, sunNodeLightHistogram.Length - 1) : 0;
            sunNodeLightHistogram[bin]++;
        }
        long total = 0;
        foreach (var c in sunNodeLightHistogram) total += c;
        long seen = 0;
        for (var b = 0; b < sunNodeLightHistogram.Length; b++)
        {
            seen += sunNodeLightHistogram[b];
            if (seen * 2 < total) continue;
            sunNodeDarkBar = texelDarkFloor * MathF.Pow(2f, (b + 0.5f) / 4f - 24f);
            return;
        }
    }

    // A slice read back (the sweep's or a round's): each texel's light into the results, and the unsettled kept.
    private void RefineSlice(Vector4[] sums, int first, int count, int suns, int passes)
    {
        var results = sunNodeResults;
        var refining = sunNodeRefineRound > 0;
        var ids = refining ? sunNodeRefineIds : sunNodeTraceIds;
        var settled = new bool[count];
        var merged = new Vector3[count * suns];
        var squares = new float[count];
        var total = new int[count];
        Parallel.For(0, count, t =>
        {
            var at = first + t;
            var id = (int)ids[at];
            total[t] = (refining ? sunNodeRefinePasses[at] : 0) + passes;
            squares[t] = (refining ? sunNodeRefineSquares[at] : 0f) + sums[(long)t * suns].W;
            for (var k = 0; k < suns; k++)
            {
                var s = sums[(long)t * suns + k];
                var m = (refining ? sunNodeRefineSums[(long)at * suns + k] : Vector3.Zero) + new Vector3(s.X, s.Y, s.Z);
                merged[t * suns + k] = m;
                if (k < results.Length)
                {
                    results[k][2 * id] = PackRgb9e5(m / total[t]);
                    results[k][2 * id + 1] = 0xFFFFu | 0x10000u;
                }
            }
            settled[t] = CookSettled(merged.AsSpan(t * suns, suns), squares[t], total[t]);
        });
        if (!refining) CountSweepLight(merged, count, suns, passes);
        for (var t = 0; t < count; t++)
        {
            if (settled[t]) continue;
            sunNodeNextIds.Add(ids[first + t]);
            for (var k = 0; k < suns; k++) sunNodeNextSums.Add(merged[t * suns + k]);
            sunNodeNextSquares.Add(squares[t]);
            sunNodeNextPasses.Add(total[t]);
        }
    }

    // The sweep or a round is done: the next round over what is left unsettled. False when none is.
    private bool BeginSunNodeRefineRound()
    {
        // After the sweep: the texels kept before the median was whole judged again against it.
        if (sunNodeRefineRound == 0) ReportNoiseCensus();
        if (sunNodeRefineRound == 0) ReportFarPool("after the sweep");
        if (sunNodeRefineRound == 0 && sunNodeDarkBar > 0f) RejudgeAfterSweep();
        var left = sunNodeNextIds.Count;
        var of = sunNodeRefineRound == 0 ? sunNodeTraceIds.Length : sunNodeRefineIds.Length;
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sun nodes: {of - left:N0} of {of:N0} texels settled after {(sunNodeRefineRound == 0 ? "the sweep" : $"round {sunNodeRefineRound}")} ({sunNodeClock.Elapsed.TotalMinutes:0.0} min); {left:N0} refine."));
        if (left == 0) return false;
        // A round that settled under splitRefineMinShare of what it traced: stop -- what is left is bias more rays do not
        // move, not noise. Sponza's rounds 3 and 4 each settled ~3% of ~7M texels for a minute (2026-10-13). The texels
        // left keep the light they have.
        if (sunNodeRefineRound > 0 && of - left < splitRefineMinShare * of)
        {
            Console.WriteLine(string.Create(Inv,
                $"[VulkanSponza] sun nodes: round {sunNodeRefineRound} settled {of - left:N0} of {of:N0} ({(of - left) * 100.0 / Math.Max(1, of):0.0}%, under {splitRefineMinShare * 100:0}%): refining stops; {left:N0} keep their light as it is."));
            return false;
        }
        sunNodeRefineIds = sunNodeNextIds.ToArray();
        sunNodeRefineSums = sunNodeNextSums.ToArray();
        sunNodeRefineSquares = sunNodeNextSquares.ToArray();
        sunNodeRefinePasses = sunNodeNextPasses.ToArray();
        sunNodeNextIds = new(); sunNodeNextSums = new(); sunNodeNextSquares = new(); sunNodeNextPasses = new();
        if (!sunNodeRefineBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(sunNodeRefineBuffer);
        sunNodeRefineBuffer = Own(device.CreateGpuBuffer(left * 4, MemoryMarshal.AsBytes(sunNodeRefineIds.AsSpan()), "sponza.sun-nodes.refine"));
        sunNodeRefineRound++;
        sunNodeRefineRays += (long)left * RefinePasses * 32;
        sunNodeFirst = 0;
        sunNodePass = 0;
        return true;
    }

    private void RejudgeAfterSweep()
    {
        var suns = sunNodeSuns!.Length;
        List<uint> ids = new(); List<Vector3> sums = new(); List<float> squares = new(); List<int> passes = new();
        for (var i = 0; i < sunNodeNextIds.Count; i++)
        {
            var mine = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sunNodeNextSums).Slice(i * suns, suns);
            if (CookSettled(mine, sunNodeNextSquares[i], sunNodeNextPasses[i])) continue;
            ids.Add(sunNodeNextIds[i]);
            sums.AddRange(mine.ToArray());
            squares.Add(sunNodeNextSquares[i]);
            passes.Add(sunNodeNextPasses[i]);
        }
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] sun nodes: the dark bar {sunNodeDarkBar:G3} ({texelDarkFloor:G2} of the median texel light); judged again, {sunNodeNextIds.Count - ids.Count:N0} more texels settled."));
        sunNodeNextIds = ids; sunNodeNextSums = sums; sunNodeNextSquares = squares; sunNodeNextPasses = passes;
    }

    // The cook's refinement state let go (FinishSunNodes, a new cook).
    private void ResetSunNodeRefine()
    {
        Array.Clear(sunNodeLightHistogram);
        sunNodeDarkBar = 0f;
        if (!sunNodeRefineBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(sunNodeRefineBuffer);
        sunNodeRefineBuffer = default;
        sunNodeRefineRound = 0;
        sunNodeRefineIds = Array.Empty<uint>();
        sunNodeRefineSums = Array.Empty<Vector3>();
        sunNodeRefineSquares = Array.Empty<float>();
        sunNodeRefinePasses = Array.Empty<int>();
        sunNodeNextIds = new(); sunNodeNextSums = new(); sunNodeNextSquares = new(); sunNodeNextPasses = new();
    }
}
