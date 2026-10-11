using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Blix.Demos.VulkanSponza;

// --lightlet-record (plan.md, LIGHTLETS phase 0b): the sun record's REAL error, before the bake is built. Run at the
// shot with --rt-shadows, on a sample of the traced shadows' surface points (one a half-res pixel).
//
// The record: a grid over the band in hour angle x declination at the band's reference latitude (BandLatitude; a sun
// from any latitude in the band is taken to those coordinates), each node holding the point's SIGNED ANGULAR DISTANCE to
// its visibility boundary -- positive where it sees the sun (facing it and unoccluded), capped at RecordCap. Fine is
// 31 x 15 nodes (~7.3 x 4.1 deg cells), coarse every other node (16 x 8). The truth at a node is measured by rings
// around it (RecordRadii x 12 directions): the distance is the middle of the first ring gap that disagrees.
// At a test sun the record is interpolated bilinearly and judged against the traced visibility there: with threshold
// tau, NEEDS A RAY where |interpolated| < tau, FALSE CERTAIN where |interpolated| >= tau and its sign is wrong -- a
// visible error, so the usable tau is the smallest that keeps it near zero. Also as 4-bit codes (RecordLevels, rounded
// toward zero: a margin is never overstated). ADAPTIVE: the coarse grid, and the fine nodes only inside the coarse cells
// the boundary crosses (a corner under the cap: a crossing boundary passes within half a diagonal of some corner) --
// 5 more nodes a refined cell; interpolated from the fine nodes there and the coarse ones elsewhere.
internal sealed partial class SponzaLoop
{
    private bool lightletRecord;
    private const float RecordCap = 8f;
    private static readonly float[] RecordRadii = { 0.125f, 0.25f, 0.375f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f, 4f, 6f, 8f };
    private static readonly float[] RecordLevels = { 0.125f, 0.25f, 0.5f, 0.75f, 1f, 2f, 4f, 8f };
    private static readonly float[] RecordTaus = { 0f, 0.25f, 0.5f, 1f, 2f };
    private const float RecordHourSpan = 110f;
    private const int RecordNodesHour = 31, RecordNodesDec = 15;

    // Hour angle and declination (degrees) at `latitude` of a scene direction, north at theta.
    private static (float Hour, float Dec) Equatorial(Vector3 d, float latitudeDeg, float theta)
    {
        var north = d.X * MathF.Cos(theta) + d.Z * MathF.Sin(theta);
        var east = -d.X * MathF.Sin(theta) + d.Z * MathF.Cos(theta);
        var up = d.Y;
        var phi = latitudeDeg * MathF.PI / 180f;
        var sinDec = MathF.Sin(phi) * up + MathF.Cos(phi) * north;
        var hour = MathF.Atan2(-east, MathF.Cos(phi) * up - MathF.Sin(phi) * north);
        return (hour * 180f / MathF.PI, MathF.Asin(Math.Clamp(sinDec, -1f, 1f)) * 180f / MathF.PI);
    }

    private static float Quantize(float v)
    {
        var m = MathF.Abs(v);
        var q = 0f;
        foreach (var l in RecordLevels) if (l <= m) q = l;
        return MathF.CopySign(q, v);
    }

    private void WriteLightletRecordStudy()
    {
        if (!lightletRecord) return;
        if (frameTracer is null || !rtShadows || rtShadowOrigins.Length == 0)
        {
            Console.WriteLine("[VulkanSponza] lightlet record: needs --rt-shadows and the frame tracer; skipped.");
            return;
        }
        device.WaitIdle();
        var slot = device.CurrentFrameSlot;
        var (w, h) = rtShadowSize;
        var origins = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtShadowOrigins[slot], 0, w * h * 16).AsSpan()).ToArray();
        var normalBytes = device.ReadTexture(graph.GetColorTexture(SampleablePrepassNormal), out var fw, out var fh, out _);
        var (half, sx, sy) = rtShadowSlotPixel[slot];
        var theta = BandNorth(out var authoredHour);
        var maxDec = Obliquity + 5f;

        // A deterministic sample of ~32k surface points.
        var all = new List<(Vector3 P, Vector3 N)>();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var o = origins[y * w + x];
            if (o.W <= 0f) continue;
            var b = (Math.Min(half ? 2 * y + sy : y, fh - 1) * fw + Math.Min(half ? 2 * x + sx : x, fw - 1)) * 8;
            var n = new Vector3((float)BitConverter.ToHalf(normalBytes, b), (float)BitConverter.ToHalf(normalBytes, b + 2), (float)BitConverter.ToHalf(normalBytes, b + 4));
            if (n.LengthSquared() < 1e-6f) continue;
            all.Add((new Vector3(o.X, o.Y, o.Z), Vector3.Normalize(n)));
        }
        var stride = Math.Max(1, all.Count / 32768);
        var pts = all.Where((_, i) => i % stride == 0).ToArray();
        var reach = origins.Max(o => o.W);
        var rays = pts.Select(p => new Vector4(p.P, reach)).ToArray();
        var seed = 0x51u;
        bool[] Visible(Vector3 d)
        {
            var occ = frameTracer.OccludedFromHost(rays, d, seed++, out _);
            var v = new bool[pts.Length];
            for (var i = 0; i < pts.Length; i++) v[i] = !occ[i] && Vector3.Dot(pts[i].N, d) > 0f;
            return v;
        }
        Vector3 Dir(float hour, float dec) => ToScene(SunLocal(BandLatitude, dec, hour), theta);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // The truth at every fine node.
        var nodes = RecordNodesHour * RecordNodesDec;
        var dist = new float[nodes, pts.Length];
        for (var j = 0; j < RecordNodesDec; j++)
        for (var i = 0; i < RecordNodesHour; i++)
        {
            var node = j * RecordNodesHour + i;
            var d = Dir(-RecordHourSpan + i * 2f * RecordHourSpan / (RecordNodesHour - 1), -maxDec + j * 2f * maxDec / (RecordNodesDec - 1));
            var v0 = Visible(d);
            var found = new float[pts.Length];
            Array.Fill(found, RecordCap);
            var open = pts.Length;
            var u = Vector3.Normalize(MathF.Abs(d.Y) < 0.999f ? Vector3.Cross(d, Vector3.UnitY) : Vector3.Cross(d, Vector3.UnitX));
            var vv = Vector3.Cross(d, u);
            var prev = 0f;
            for (var r = 0; r < RecordRadii.Length && open > 0; r++)
            {
                var t = MathF.Tan(RecordRadii[r] * MathF.PI / 180f);
                var mid = 0.5f * (prev + RecordRadii[r]);
                for (var a = 0; a < 12; a++)
                {
                    var ang = a * MathF.PI / 6f + r * 0.27f;
                    var v = Visible(Vector3.Normalize(d + (u * MathF.Cos(ang) + vv * MathF.Sin(ang)) * t));
                    for (var p = 0; p < pts.Length; p++)
                        if (found[p] == RecordCap && v[p] != v0[p] && r < RecordRadii.Length) { found[p] = mid; open--; }
                }
                prev = RecordRadii[r];
            }
            for (var p = 0; p < pts.Length; p++) dist[node, p] = v0[p] ? found[p] : -found[p];
        }
        var bakeSeconds = clock.Elapsed.TotalSeconds;
        // Per point, which coarse cells are refined (any corner under the cap), and how many.
        bool Refined(int ci, int cj, int p)
        {
            for (var dj = 0; dj <= 2; dj += 2)
            for (var di = 0; di <= 2; di += 2)
                if (MathF.Abs(dist[(cj + dj) * RecordNodesHour + ci + di, p]) < RecordCap) return true;
            return false;
        }
        long refinedCells = 0;
        for (var p = 0; p < pts.Length; p++)
            for (var cj = 0; cj + 2 < RecordNodesDec; cj += 2)
            for (var ci = 0; ci + 2 < RecordNodesHour; ci += 2)
                if (Refined(ci, cj, p)) refinedCells++;
        var uniform = 0;
        for (var p = 0; p < pts.Length; p++)
        {
            var sign = dist[0, p] > 0f;
            var u = true;
            for (var node = 0; node < nodes && u; node++) u = MathF.Abs(dist[node, p]) >= RecordCap && dist[node, p] > 0f == sign;
            if (u) uniform++;
        }

        // Test suns: the census's, and random ones across the band (latitude 35-45, any day, any hour above 5 deg).
        var suns = new List<Vector3> { ToScene(SunLocal(AuthoredLatitude, Obliquity, authoredHour), theta) };
        foreach (var dec in new[] { -Obliquity, 0f, Obliquity })
        foreach (var hour in new[] { -60f, -30f, 0f, 30f, 60f })
            suns.Add(ToScene(SunLocal(BandLatitude, dec, hour), theta));
        var rng = new Random(1234);
        while (suns.Count < 64)
        {
            var d = ToScene(SunLocal(35f + 10f * rng.NextSingle(), Obliquity * (2f * rng.NextSingle() - 1f), 100f * (2f * rng.NextSingle() - 1f)), theta);
            if (d.Y > MathF.Sin(BandMinElevation * MathF.PI / 180f)) suns.Add(d);
        }
        suns.RemoveAll(d => d.Y <= MathF.Sin(BandMinElevation * MathF.PI / 180f));

        // [grid, quantized, tau]: needs a ray, false certain (counts over points x suns).
        var need = new long[3, 2, RecordTaus.Length];
        var wrong = new long[3, 2, RecordTaus.Length];
        long judged = 0;
        foreach (var s in suns)
        {
            var truth = Visible(s);
            var (hour, dec) = Equatorial(s, BandLatitude, theta);
            var fi = Math.Clamp((hour + RecordHourSpan) / (2f * RecordHourSpan) * (RecordNodesHour - 1), 0f, RecordNodesHour - 1.001f);
            var fj = Math.Clamp((dec + maxDec) / (2f * maxDec) * (RecordNodesDec - 1), 0f, RecordNodesDec - 1.001f);
            for (var g = 0; g < 3; g++)
            {
                var step = g == 0 ? 1 : 2;   // 0 fine, 1 coarse (every other node), 2 adaptive
                var ci = MathF.Floor(fi / step) * step;
                var cj = MathF.Floor(fj / step) * step;
                var i0 = (int)Math.Min(ci, RecordNodesHour - 1 - step);
                var j0 = (int)Math.Min(cj, RecordNodesDec - 1 - step);
                var tx = (fi - i0) / step;
                var ty = (fj - j0) / step;
                for (var p = 0; p < pts.Length; p++)
                {
                    // Adaptive: the fine cell where the coarse cell holding the sun is refined.
                    var (pi0, pj0, ptx, pty, pstep) = (i0, j0, tx, ty, step);
                    if (g == 2 && Refined(i0, j0, p))
                    {
                        pstep = 1;
                        pi0 = (int)Math.Min(MathF.Floor(fi), RecordNodesHour - 2);
                        pj0 = (int)Math.Min(MathF.Floor(fj), RecordNodesDec - 2);
                        ptx = fi - pi0; pty = fj - pj0;
                    }
                    for (var q = 0; q < 2; q++)
                    {
                        float D(int ii, int jj) { var x = dist[jj * RecordNodesHour + ii, p]; return q == 1 ? Quantize(x) : x; }
                        var value = (1 - ptx) * (1 - pty) * D(pi0, pj0) + ptx * (1 - pty) * D(pi0 + pstep, pj0)
                                    + (1 - ptx) * pty * D(pi0, pj0 + pstep) + ptx * pty * D(pi0 + pstep, pj0 + pstep);
                        for (var k = 0; k < RecordTaus.Length; k++)
                        {
                            if (MathF.Abs(value) < RecordTaus[k] || value == 0f) need[g, q, k]++;
                            else if (value > 0f != truth[p]) wrong[g, q, k]++;
                        }
                    }
                }
            }
            judged += pts.Length;
        }
        var nonUniform = Math.Max(1, pts.Length - uniform);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] lightlet record: {pts.Length:N0} points (every {stride}th surface pixel), {nodes} nodes ({RecordNodesHour} x {RecordNodesDec}), truth in {bakeSeconds:0} s; {suns.Count} test suns; uniform over the whole band (2 bits, no grid): {100.0 * uniform / pts.Length:0.0}% of points; adaptive refines {(double)refinedCells / nonUniform:0.0} of {7 * 15} coarse cells a non-uniform point."));
        var adaptiveBytes = 16 * 8 * 4 / 8 + refinedCells * 5 * 4 / 8.0 / nonUniform;
        for (var g = 0; g < 3; g++)
        for (var q = 0; q < 2; q++)
        {
            var bytes = g == 0 ? nodes * 4 / 8.0 : g == 1 ? 16 * 8 * 4 / 8.0 : adaptiveBytes;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    {(g == 0 ? "fine 31x15" : g == 1 ? "coarse 16x8" : "adaptive"),-12} {(q == 1 ? "4-bit" : "float"),-6} ({bytes:0} B/record): ") +
                string.Join("; ", RecordTaus.Select((tau, k) => string.Create(CultureInfo.InvariantCulture,
                    $"tau {tau:0.##} need {100.0 * need[g, q, k] / judged:0.00}% wrong {100.0 * wrong[g, q, k] / judged:0.000}%"))));
        }
    }
}
