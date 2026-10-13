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
    private bool CookSettled(ReadOnlySpan<Vector3> sums, float squares, int passes)
    {
        if (passes * 32 >= texelMaxRays) return true;
        var mean = 0f;
        foreach (var s in sums) mean += Luminance(s);
        mean /= passes;
        var variance = MathF.Max(squares / passes - mean * mean, 0f);
        return MathF.Sqrt(variance / passes) <= texelNoise * MathF.Max(mean, TexelNoiseFloor);
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

    // The cook's refinement state let go (FinishSunNodes, a new cook).
    private void ResetSunNodeRefine()
    {
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
