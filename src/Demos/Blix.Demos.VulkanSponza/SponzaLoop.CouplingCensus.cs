using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Blix.Demos.VulkanSponza;

// --coupling-census N (at the shot): how much a texel would store if it kept its COUPLINGS to the world -- which patches
// its rays reach, how often -- instead of the light they returned for one sun (the split cook's sun part). The question
// before building texel couplings (plan.md, LIGHTLETS; the user, 2026-10-14: store the scene, compute sun and sky at
// runtime).
//   N live texels sampled evenly; each traces the split cook's rays (sunNodeRays, cosine) on the frame tracer; each hit
//   takes the nearest patch within 0.5 m facing the ray (no visibility test: a census of where a texel's light comes
//   from, not the cook's rule). Misses are sky (sky bins, counted apart); hits with no patch near are unmatched.
//   Reported: distinct patches a texel, raw and with far hits merged into clusters (cells of a size by the hit's
//   distance, and the patch normal's dominant axis), at 128 / 256 / all rays; the bytes at 4 a coupling (a 22-bit id, a
//   10-bit count); and the error the merging makes in each texel's light -- the hits' patch incident light at this sun,
//   a cluster's mean in place of its patches' (albedo left out on both sides: it stays per hit in the real thing).
internal sealed partial class SponzaLoop
{
    private int couplingCensus;

    private readonly record struct ClusterScheme(string Name, float Raw, float NearTo, float NearCell, float MidTo, float MidCell, float FarCell);

    private static readonly ClusterScheme[] CouplingSchemes =
    {
        new("raw <1 m, 0.5 m to 4, 2 m to 16, 8 m", 1f, 4f, 0.5f, 16f, 2f, 8f),
        new("raw <1 m, 1 m to 4, 4 m to 16, 16 m", 1f, 4f, 1f, 16f, 4f, 16f),
        new("raw <0.5 m, 0.5 m to 2, 2 m to 8, 8 m", 0.5f, 2f, 0.5f, 8f, 2f, 8f),
    };

    private static float CellFor(in ClusterScheme s, float t) => t < s.Raw ? 0f : t < s.NearTo ? s.NearCell : t < s.MidTo ? s.MidCell : s.FarCell;

    private static int Axis(Vector3 n)
    {
        var a = Vector3.Abs(n);
        var k = a.X >= a.Y && a.X >= a.Z ? 0 : a.Y >= a.Z ? 1 : 2;
        var v = k == 0 ? n.X : k == 1 ? n.Y : n.Z;
        return k * 2 + (v < 0f ? 1 : 0);
    }

    private static long ClusterKey(Vector3 p, float cell, int axis)
    {
        long x = (long)MathF.Floor(p.X / cell) + (1 << 20), y = (long)MathF.Floor(p.Y / cell) + (1 << 20), z = (long)MathF.Floor(p.Z / cell) + (1 << 20);
        var size = (long)MathF.Round(MathF.Log2(cell) + 4f);   // 0.5 m -> 3, 16 m -> 8
        return (((x * 2_097_152L + y) * 2_097_152L + z) * 8L + axis) * 16L + size;
    }

    private void WriteCouplingCensus()
    {
        if (couplingCensus <= 0) return;
        if (frameTracer is not { } tracer || cookedPacked is not { } packed || texelLiveIds is not { } live || texelCount == 0)
        {
            Console.WriteLine("[VulkanSponza] coupling census: skipped (needs the frame tracer, the cooked patches and the texels).");
            return;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var patches = packed.Length / 3;
        var pPos = new Vector3[patches];
        var pNrm = new Vector3[patches];
        var pLum = new float[patches];
        for (var i = 0; i < patches; i++)
        {
            pPos[i] = new Vector3(packed[3 * i].X, packed[3 * i].Y, packed[3 * i].Z);
            pNrm[i] = new Vector3(packed[3 * i + 1].X, packed[3 * i + 1].Y, packed[3 * i + 1].Z);
            var e = packed[3 * i + 2];
            pLum[i] = 0.2126f * e.X + 0.7152f * e.Y + 0.0722f * e.Z;
        }
        // The patches by 0.5 m cell, for the nearest-facing lookup.
        const float Grid = 0.5f;
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (var i = 0; i < patches; i++)
        {
            var c = ((int)MathF.Floor(pPos[i].X / Grid), (int)MathF.Floor(pPos[i].Y / Grid), (int)MathF.Floor(pPos[i].Z / Grid));
            if (!grid.TryGetValue(c, out var list)) grid[c] = list = new List<int>(4);
            list.Add(i);
        }
        int NearestFacing(Vector3 p, Vector3 d)
        {
            var cx = (int)MathF.Floor(p.X / Grid); var cy = (int)MathF.Floor(p.Y / Grid); var cz = (int)MathF.Floor(p.Z / Grid);
            var best = -1; var bestD = 0.25f;   // 0.5 m, squared
            for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var list)) continue;
                foreach (var j in list)
                {
                    if (Vector3.Dot(pNrm[j], d) > -0.1f) continue;
                    var dd = Vector3.DistanceSquared(pPos[j], p);
                    if (dd < bestD) { bestD = dd; best = j; }
                }
            }
            return best;
        }
        // The clusters at every cell size the schemes use: their patches' mean incident light, by key.
        var cellsInUse = CouplingSchemes.SelectMany(s => new[] { s.NearCell, s.MidCell, s.FarCell }).Distinct().ToArray();
        var meanByCell = new Dictionary<float, Dictionary<long, (double Sum, int Count)>>();
        foreach (var cell in cellsInUse)
        {
            var m = new Dictionary<long, (double, int)>();
            for (var i = 0; i < patches; i++)
            {
                var k = ClusterKey(pPos[i], cell, Axis(pNrm[i]));
                m[k] = m.TryGetValue(k, out var v) ? (v.Item1 + pLum[i], v.Item2 + 1) : (pLum[i], 1);
            }
            meanByCell[cell] = m;
        }

        // The texels' records, read back once (position + samples, normal + level).
        var records = ReadTexelRecords(0, texelCount);
        var n = Math.Min(couplingCensus, live.Length);
        var stride = live.Length / (double)n;
        var R = sunNodeRays;
        var bounds = rayQueries!.Nodes[0].Bounds;
        var reach = (bounds.Max - bounds.Min).Length();
        const int Batch = 2048;
        var prefixes = new[] { 128, 256, R };
        var rawDistinct = new int[prefixes.Length][];
        for (var q = 0; q < prefixes.Length; q++) rawDistinct[q] = new int[n];
        var schemeDistinct = new int[CouplingSchemes.Length][];
        var schemeErr = new double[CouplingSchemes.Length][];
        for (var s = 0; s < CouplingSchemes.Length; s++) { schemeDistinct[s] = new int[n]; schemeErr[s] = new double[n]; }
        var exactLight = new double[n];
        // The noise floor: each texel's light from its first half of rays against its second (the 512-ray estimate's
        // relative standard deviation ~ |m1 - m2| / 2 over the mean).
        var halfNoise = new double[n];
        // Near couplings only: distinct patches within these distances.
        var nearCuts = new[] { 0.5f, 1f, 2f };
        var nearDistinct = new int[nearCuts.Length][];
        for (var c = 0; c < nearCuts.Length; c++) nearDistinct[c] = new int[n];
        // The hierarchy: the texel's own hits within D, its nearest patch's (traced the same way) beyond -- the far field
        // inherited. Error against the texel's own estimate, NaN where no patch is near.
        var inheritErr = new double[nearCuts.Length][];
        for (var c = 0; c < nearCuts.Length; c++) { inheritErr[c] = new double[n]; Array.Fill(inheritErr[c], double.NaN); }
        var patchRays = new Vector4[Batch * R * 2];
        var patchHits = new uint[Batch * R * 12];
        var patchDirs = new Vector3[Batch * R];
        var patchOfTexel = new int[Batch];
        long sky = 0, unmatched = 0, matched = 0;
        var anySky = 0;
        var rays = new Vector4[Batch * R * 2];
        var hits = new uint[Batch * R * 12];
        var dirs = new Vector3[Batch * R];
        double gpuMs = 0;
        for (var first = 0; first < n; first += Batch)
        {
            var count = Math.Min(Batch, n - first);
            Parallel.For(0, count, b =>
            {
                var id = (int)live[(int)((first + b) * stride)];
                var p = new Vector3(records[2 * id].X, records[2 * id].Y, records[2 * id].Z);
                var nr = new Vector3(records[2 * id + 1].X, records[2 * id + 1].Y, records[2 * id + 1].Z);
                var nn = nr.LengthSquared() > 1e-12f ? Vector3.Normalize(nr) : Vector3.UnitY;
                var r = new Random(91_000 + id);
                for (var k = 0; k < R; k++)
                {
                    var d = CosineHemisphereCpu(nn, r);
                    var at = b * R + k;
                    dirs[at] = d;
                    rays[2 * at] = new Vector4(p + nn * 0.01f, 0f);
                    rays[2 * at + 1] = new Vector4(d, reach);
                }
            });
            tracer.TraceFromHost(rays.AsSpan(0, count * R * 2), hits, (uint)(first * R), closestOnly: true, out var ms);
            gpuMs += ms;
            // Each texel's own patch (the nearest facing its way) traces as the texel did: its far field to inherit.
            Parallel.For(0, count, b =>
            {
                var id = (int)live[(int)((first + b) * stride)];
                var p = new Vector3(records[2 * id].X, records[2 * id].Y, records[2 * id].Z);
                var nr = new Vector3(records[2 * id + 1].X, records[2 * id + 1].Y, records[2 * id + 1].Z);
                var nn = nr.LengthSquared() > 1e-12f ? Vector3.Normalize(nr) : Vector3.UnitY;
                var j = NearestFacing(p, -nn);
                patchOfTexel[b] = j;
                var pp = j >= 0 ? pPos[j] : p;
                var pn = j >= 0 && pNrm[j].LengthSquared() > 1e-12f ? Vector3.Normalize(pNrm[j]) : nn;
                var r = new Random(77_000 + id);
                for (var k = 0; k < R; k++)
                {
                    var d = CosineHemisphereCpu(pn, r);
                    var at = b * R + k;
                    patchDirs[at] = d;
                    patchRays[2 * at] = new Vector4(pp + pn * 0.01f, 0f);
                    patchRays[2 * at + 1] = new Vector4(d, reach);
                }
            });
            tracer.TraceFromHost(patchRays.AsSpan(0, count * R * 2), patchHits, (uint)(first * R) + 0x40000000u, closestOnly: true, out var pms);
            gpuMs += pms;
            var skyLocal = new long[count];
            var unmatchedLocal = new long[count];
            Parallel.For(0, count, b =>
            {
                var t = first + b;
                var id = (int)live[(int)(t * stride)];
                var origin = new Vector3(rays[2 * b * R].X, rays[2 * b * R].Y, rays[2 * b * R].Z);
                var patchOf = new int[R];
                var dist = new float[R];
                for (var k = 0; k < R; k++)
                {
                    var at = b * R + k;
                    if ((hits[at * 12 + 3] & 1u) == 0) { patchOf[k] = -2; skyLocal[b]++; continue; }
                    var tt = BitConverter.UInt32BitsToSingle(hits[at * 12]);
                    var hp = origin + dirs[at] * tt;
                    patchOf[k] = NearestFacing(hp, dirs[at]);
                    dist[k] = tt;
                    if (patchOf[k] < 0) unmatchedLocal[b]++;
                }
                var seen = new HashSet<int>();
                var q = 0;
                for (var k = 0; k < R; k++)
                {
                    if (patchOf[k] >= 0) seen.Add(patchOf[k]);
                    while (q < prefixes.Length && k + 1 == prefixes[q]) { rawDistinct[q][t] = seen.Count; q++; }
                }
                double exact = 0;
                for (var k = 0; k < R; k++) if (patchOf[k] >= 0) exact += pLum[patchOf[k]];
                exactLight[t] = exact / R;
                double h1 = 0, h2 = 0;
                for (var k = 0; k < R; k++) if (patchOf[k] >= 0) { if (k < R / 2) h1 += pLum[patchOf[k]]; else h2 += pLum[patchOf[k]]; }
                halfNoise[t] = exact > 0 ? Math.Abs(h1 - h2) / R / (exact / R) : 0;   // (|m1 - m2| / 2) / mean, m = sum / (R/2)
                for (var c = 0; c < nearCuts.Length; c++)
                {
                    var near = new HashSet<int>();
                    for (var k = 0; k < R; k++) if (patchOf[k] >= 0 && dist[k] < nearCuts[c]) near.Add(patchOf[k]);
                    nearDistinct[c][t] = near.Count;
                }
                if (patchOfTexel[b] >= 0 && exact > 0)
                {
                    var pOrigin = new Vector3(patchRays[2 * b * R].X, patchRays[2 * b * R].Y, patchRays[2 * b * R].Z);
                    var pFarAt = new double[nearCuts.Length];
                    for (var k = 0; k < R; k++)
                    {
                        var at = b * R + k;
                        if ((patchHits[at * 12 + 3] & 1u) == 0) continue;
                        var tt = BitConverter.UInt32BitsToSingle(patchHits[at * 12]);
                        var j = NearestFacing(pOrigin + patchDirs[at] * tt, patchDirs[at]);
                        if (j < 0) continue;
                        for (var c = 0; c < nearCuts.Length; c++) if (tt >= nearCuts[c]) pFarAt[c] += pLum[j];
                    }
                    for (var c = 0; c < nearCuts.Length; c++)
                    {
                        double ownNear = 0;
                        for (var k = 0; k < R; k++) if (patchOf[k] >= 0 && dist[k] < nearCuts[c]) ownNear += pLum[patchOf[k]];
                        inheritErr[c][t] = (ownNear + pFarAt[c]) / R - exact / R;
                    }
                }
                for (var s = 0; s < CouplingSchemes.Length; s++)
                {
                    var sc = CouplingSchemes[s];
                    var keys = new HashSet<long>();
                    double merged = 0;
                    for (var k = 0; k < R; k++)
                    {
                        var j = patchOf[k];
                        if (j < 0) continue;
                        var cell = CellFor(sc, dist[k]);
                        if (cell == 0f) { keys.Add(-1L - j); merged += pLum[j]; continue; }
                        var key = ClusterKey(pPos[j], cell, Axis(pNrm[j]));
                        keys.Add(key);
                        var (sum, c) = meanByCell[cell][key];
                        merged += sum / c;
                    }
                    schemeDistinct[s][t] = keys.Count;
                    schemeErr[s][t] = merged / R - exact / R;
                }
                _ = id;
            });
            foreach (var v in skyLocal) { sky += v; if (v > 0) anySky++; }
            foreach (var v in unmatchedLocal) unmatched += v;
        }
        matched = (long)n * R - sky - unmatched;
        static string Pct(double[] sorted, double p) => sorted[Math.Clamp((int)(p * (sorted.Length - 1)), 0, sorted.Length - 1)].ToString("0", CultureInfo.InvariantCulture);
        string Dist(int[] values)
        {
            var s = values.Select(v => (double)v).OrderBy(v => v).ToArray();
            return string.Create(CultureInfo.InvariantCulture, $"mean {s.Average():0.0}, p50 {Pct(s, 0.5)}, p90 {Pct(s, 0.9)}, p99 {Pct(s, 0.99)}");
        }
        double Gb(int[] values) => values.Average() * 4.0 * live.Length / 1e9;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] coupling census: {n:N0} of {live.Length:N0} live texels, {R} rays each ({(long)n * R:N0} rays, {gpuMs / 1000:0.0} s GPU, {clock.Elapsed.TotalSeconds:0.0} s); hits on a patch {100.0 * matched / ((long)n * R):0.0}%, sky {100.0 * sky / ((long)n * R):0.0}% ({100.0 * anySky / n:0.0}% of texels see some), no patch within 0.5 m {100.0 * unmatched / ((long)n * R):0.0}%."));
        for (var q = 0; q < prefixes.Length; q++)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    raw, {prefixes[q]} rays: distinct patches a texel {Dist(rawDistinct[q])}; at 4 B ~{Gb(rawDistinct[q]):0.00} GB for every live texel."));
        var lightSum = exactLight.Sum();
        for (var s = 0; s < CouplingSchemes.Length; s++)
        {
            var rel = new double[n];
            for (var t = 0; t < n; t++) rel[t] = Math.Abs(schemeErr[s][t]) / Math.Max(exactLight[t], 1e-9);
            var weighted = schemeErr[s].Select(Math.Abs).Sum() / Math.Max(lightSum, 1e-12);
            var sorted = rel.OrderBy(v => v).ToArray();
            var over10 = rel.Count(v => v > 0.10) * 100.0 / n;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    {CouplingSchemes[s].Name}, {R} rays: entries a texel {Dist(schemeDistinct[s])}; ~{Gb(schemeDistinct[s]):0.00} GB; light error |merged - raw| over the light {weighted:0.0000} (p50 {sorted[n / 2]:0.0000}, p90 {sorted[(int)(0.9 * (n - 1))]:0.0000}; {over10:0.00}% of texels over 10%)."));
        }
        {
            var w = Enumerable.Range(0, n).Sum(t => halfNoise[t] * exactLight[t]) / Math.Max(lightSum, 1e-12);
            var sorted = halfNoise.OrderBy(v => v).ToArray();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    noise floor, {R} rays (first half against second): relative std over the light {w:0.0000} (p50 {sorted[n / 2]:0.0000}, p90 {sorted[(int)(0.9 * (n - 1))]:0.0000}; {halfNoise.Count(v => v > 0.10) * 100.0 / n:0.00}% of texels over 10%)."));
        }
        for (var c = 0; c < nearCuts.Length; c++)
        {
            var errs = Enumerable.Range(0, n).Where(t => !double.IsNaN(inheritErr[c][t])).ToArray();
            var rel = errs.Select(t => Math.Abs(inheritErr[c][t]) / Math.Max(exactLight[t], 1e-9)).OrderBy(v => v).ToArray();
            var w = errs.Sum(t => Math.Abs(inheritErr[c][t])) / Math.Max(errs.Sum(t => exactLight[t]), 1e-12);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    hierarchy, own hits under {nearCuts[c]:0.#} m + the patch's beyond: near entries a texel {Dist(nearDistinct[c])}, ~{Gb(nearDistinct[c]):0.00} GB; light error over the light {w:0.0000} (p50 {rel[rel.Length / 2]:0.0000}, p90 {rel[(int)(0.9 * (rel.Length - 1))]:0.0000}; {rel.Count(v => v > 0.10) * 100.0 / rel.Length:0.00}% over 10%; {errs.Length:N0} texels with a patch)."));
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"    sky: {100.0 * anySky / n:0.0}% of texels see sky; 16 bins of 4 B each would add ~{anySky / (double)n * 16 * 4 * live.Length / 1e9:0.00} GB."));
    }
}
