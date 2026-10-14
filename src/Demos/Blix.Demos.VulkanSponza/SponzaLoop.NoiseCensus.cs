using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --split-noise-census: where the split cook's noise comes from. Refining settled ~3% of 7 M texels a round, and neither
// judging the dark against the scene nor aiming rays at the brightest patches (MIS) moved how many the sweep settled
// (9,086,540 / 9,086,446 of 16 M, 2026-10-14). So each sweep pass's composed luminance is split by source
// (texel_nodes_shade.glsl, NodeCensus): the sky's misses, the bounce from a patch, the sun direct at the hits, the bounce a
// hit with no patch took. Each source's variance across passes says which one keeps a texel unsettled; printed when the
// sweep ends, the unsettled against the settled.
// Sponza, 2026-10-14 (512 sweep rays): the 6.95 M unsettled -- the sun at the hits 55% of the variance (the largest in 57%),
// the sky's misses 23%, the patches' bounce 20%, no-patch 1.5%; one ray's spread p50 3.0x the mean (5% wants ~3,600 rays).
// The 9.08 M settled are bounce and sky. Shaded texels seeing a little of the sunlit floor. The light lists counted only a
// patch's bounce (its direct sun is in its sunlets); counting the sunlets' sun onto their patches (BuildLights) changed
// nothing (8.99 M settled, the sun still 55%): reverted.
internal sealed partial class SponzaLoop
{
    private bool noiseCensus;
    private GpuBufferHandle sunNodeCensus;
    // [settled 0 / unsettled 1]: texels; each source's mean share of the variance and of the light; how often each is
    // the largest; the sources' variance summed over the whole's (1 when they are independent); the per-ray spread.
    private readonly long[] censusTexels = new long[2];
    private readonly double[,] censusVarianceShare = new double[2, 4];
    private readonly double[,] censusLightShare = new double[2, 4];
    private readonly long[,] censusDominant = new long[2, 4];
    private readonly double[] censusIndependence = new double[2];
    private readonly List<float>[] censusRaySpread = { new(), new() };
    // By the ray's length (the short-ray design: past R a ray would read its patch): [settled/unsettled, R 0.5/1/2 m] the
    // light's share and the variance's share from beyond R; texels whose near-only noise passes the bar (as if beyond R
    // were noise-free) and texels with nothing within R at all; the sun at hits beyond 1 m's variance share.
    private static readonly float[] CensusRadii = { 0.5f, 1f, 2f };
    private readonly double[,] censusFarLight = new double[2, 3];
    private readonly double[,] censusFarVariance = new double[2, 3];
    private readonly double[,] censusNearVariance = new double[2, 3];
    private readonly long[,] censusNearSettles = new long[2, 3];
    private readonly long[,] censusNearEmpty = new long[2, 3];
    private readonly double[] censusFarSunVariance = new double[2];

    private void BeginNoiseCensus()
    {
        if (!noiseCensus || !splitCooking) return;
        sunNodeCensus = Own(device.CreateGpuBuffer(checked(sunNodeSlice * 96), name: "sponza.sun-nodes.census"));
        Array.Clear(censusTexels);
        Array.Clear(censusVarianceShare);
        Array.Clear(censusLightShare);
        Array.Clear(censusDominant);
        Array.Clear(censusIndependence);
        foreach (var l in censusRaySpread) l.Clear();
        Array.Clear(censusFarLight);
        Array.Clear(censusFarVariance);
        Array.Clear(censusNearVariance);
        Array.Clear(censusNearSettles);
        Array.Clear(censusNearEmpty);
        Array.Clear(censusFarSunVariance);
    }

    private bool CensusOn(int discover) =>
        noiseCensus && splitCooking && discover == 0 && sunNodeRefineRound == 0 && !sunNodeCensus.Equals(default(GpuBufferHandle));

    // SunNodeReadBack, a sweep slice: each texel's sources against its whole.
    private void ReadNoiseCensus(Vector4[] sums, int count, int suns, int passes)
    {
        if (!noiseCensus || sunNodeCensus.Equals(default(GpuBufferHandle))) return;
        var census = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(sunNodeCensus, 0, count * 96).AsSpan()).ToArray();
        for (var t = 0; t < count; t++)
        {
            float total = 0f;
            for (var k = 0; k < suns; k++) total += Luminance(new Vector3(sums[(long)t * suns + k].X, sums[(long)t * suns + k].Y, sums[(long)t * suns + k].Z));
            var mean = total / passes;
            var whole = MathF.Max(sums[(long)t * suns].W / passes - mean * mean, 0f);
            var s = census[6 * t];
            var q = census[6 * t + 1];
            var m = s / passes;
            var v = Vector4.Max(q / passes - m * m, Vector4.Zero);
            var vSum = v.X + v.Y + v.Z + v.W;
            var bar = texelNoise * MathF.Max(mean, MathF.Max(TexelNoiseFloor, sunNodeDarkBar));
            var unsettled = MathF.Sqrt(whole / passes) > bar ? 1 : 0;
            censusTexels[unsettled]++;
            if (vSum > 0f)
            {
                censusVarianceShare[unsettled, 0] += v.X / vSum;
                censusVarianceShare[unsettled, 1] += v.Y / vSum;
                censusVarianceShare[unsettled, 2] += v.Z / vSum;
                censusVarianceShare[unsettled, 3] += v.W / vSum;
                var top = v.X >= v.Y && v.X >= v.Z && v.X >= v.W ? 0 : v.Y >= v.Z && v.Y >= v.W ? 1 : v.Z >= v.W ? 2 : 3;
                censusDominant[unsettled, top]++;
                if (whole > 0f) censusIndependence[unsettled] += Math.Min(vSum / whole, 10f);
            }
            var lightSum = m.X + m.Y + m.Z + m.W;
            if (lightSum > 0f)
            {
                censusLightShare[unsettled, 0] += m.X / lightSum;
                censusLightShare[unsettled, 1] += m.Y / lightSum;
                censusLightShare[unsettled, 2] += m.Z / lightSum;
                censusLightShare[unsettled, 3] += m.W / lightSum;
            }
            var fm = census[6 * t + 2] / passes;
            var fv = Vector4.Max(census[6 * t + 3] / passes - fm * fm, Vector4.Zero);
            var nm = census[6 * t + 4] / passes;
            var nv = Vector4.Max(census[6 * t + 5] / passes - nm * nm, Vector4.Zero);
            for (var i = 0; i < 3; i++)
            {
                float F(Vector4 v) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;
                if (mean > 0f) censusFarLight[unsettled, i] += Math.Clamp(F(fm) / mean, 0f, 1f);
                if (whole > 0f)
                {
                    censusFarVariance[unsettled, i] += Math.Min(F(fv) / whole, 10f);
                    censusNearVariance[unsettled, i] += Math.Min(F(nv) / whole, 10f);
                }
                if (MathF.Sqrt(F(nv) / passes) <= bar) censusNearSettles[unsettled, i]++;
                if (F(nm) <= 0f) censusNearEmpty[unsettled, i]++;
            }
            if (whole > 0f) censusFarSunVariance[unsettled] += Math.Min(fv.W / whole, 10f);
            // A pass's spread over its mean, times sqrt(32): one ray's (as if the rays were independent).
            if (mean > 0f && (t & 15) == 0) censusRaySpread[unsettled].Add(MathF.Sqrt(whole * 32f) / mean);
        }
    }

    // BeginSunNodeRefineRound, after the sweep: the census printed, its buffer let go.
    private void ReportNoiseCensus()
    {
        if (!noiseCensus || sunNodeCensus.Equals(default(GpuBufferHandle))) return;
        RetireGpuBuffer(sunNodeCensus);
        sunNodeCensus = default;
        string[] names = { "sky misses", "patch bounce", "sun at hits", "no-patch bounce" };
        for (var u = 1; u >= 0; u--)
        {
            var n = Math.Max(censusTexels[u], 1);
            var spread = censusRaySpread[u].OrderBy(x => x).ToArray();
            string P(int p) => spread.Length > 0 ? spread[Math.Min(spread.Length - 1, spread.Length * p / 100)].ToString("0.00", CultureInfo.InvariantCulture) : "-";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] noise census, {(u == 1 ? "UNSETTLED" : "settled")} after the sweep: {censusTexels[u]:N0} texels; one ray's spread over the mean p25/p50/p75/p90 {P(25)}/{P(50)}/{P(75)}/{P(90)}; sources' variances over the whole's {censusIndependence[u] / n:0.00}."));
            for (var i = 0; i < 4; i++)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza]   {names[i],-16} light {100 * censusLightShare[u, i] / n,5:0.0}%  variance {100 * censusVarianceShare[u, i] / n,5:0.0}%  the largest in {100.0 * censusDominant[u, i] / n,5:0.0}% of texels"));
            for (var i = 0; i < 3; i++)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza]   beyond {CensusRadii[i]:0.0} m    light {100 * censusFarLight[u, i] / n,5:0.0}%  variance {100 * censusFarVariance[u, i] / n,5:0.0}% (within: {100 * censusNearVariance[u, i] / n,5:0.0}%)  settled on the near rays alone {100.0 * censusNearSettles[u, i] / n,5:0.0}%  nothing within {100.0 * censusNearEmpty[u, i] / n,5:0.0}%"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza]   the sun at hits beyond 1 m: variance {100 * censusFarSunVariance[u] / n,5:0.0}%"));
        }
    }
}
