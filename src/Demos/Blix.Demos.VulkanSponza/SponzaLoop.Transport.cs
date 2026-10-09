using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// Stage 5a: cooked light transport, the first spike (plan.md 5). Most of a world is static and its transport is a fact
// of its geometry, so cook it once and relight at runtime. In memory for now, with the inputs and outputs a file would
// have; judged at the path-trace reference's own points (--probe-reference), joined on pixel.
//
//   R1 patches   scattered over every static triangle in proportion to area (one per --transport-spacing squared), each
//                with its provenance (placement, triangle) -- its albedo is looked up at runtime, never cooked.
//   R2 couplings from each patch, --transport-rays cosine-weighted rays: a hit couples to the nearest patch facing the
//                same way there; the fractions are the form factors x static visibility (pure geometry).
//   R3 global    the sky: the escaped rays' share by direction (16x16 octahedral bins); the sun: a --transport-vis
//                squared octahedral visibility map per patch (one ray a bin), so any sun direction is a lookup.
//
// Runtime here is the CPU, for the spike: light every patch by the sun (its visibility map) and the sky (its bins), then
// iterate outgoing = albedo x (incoming + direct sun) / pi through the couplings. At each reference point, two answers:
// INTERPOLATED from nearby patches (what a renderer would do) and a FINAL GATHER of rays reading the patches' solved light
// (a diagnostic: what the representation knows, apart from how it is interpolated).
internal sealed partial class SponzaLoop
{
    private Vector2? sunOverrideDegrees;
    private bool transportSpike;
    private float transportSpacing = 0.25f;
    private int transportRays = 512;
    private int transportVisRes = 32;
    private const int TransportSkyRes = 16;

    private void ReadTransportArgs(AppArgs args)
    {
        transportSpike = args.Flag("transport");
        if (args.Float("transport-spacing") is { } s) transportSpacing = Math.Clamp(s, 0.02f, 4f);
        if (args.Int("transport-rays") is { } r) transportRays = Math.Clamp(r, 16, 8192);
        if (args.Int("transport-vis") is { } v) transportVisRes = Math.Clamp(v, 4, 128);
    }

    // OctEncode is SponzaLoop.ClipmapTwin's (the same mapping as Blix.Shaders/octahedral.glsl).
    private static Vector3 OctDecode(Vector2 uv)
    {
        var d = new Vector3(uv.X, uv.Y, 1f - MathF.Abs(uv.X) - MathF.Abs(uv.Y));
        if (d.Z < 0f) { var x = d.X; d.X = (1f - MathF.Abs(d.Y)) * (x >= 0f ? 1f : -1f); d.Y = (1f - MathF.Abs(x)) * (d.Y >= 0f ? 1f : -1f); }
        return Vector3.Normalize(d);
    }

    private static int OctBin(Vector3 d, int res)
    {
        var uv = (OctEncode(d) * 0.5f + new Vector2(0.5f)) * res;
        var x = Math.Clamp((int)uv.X, 0, res - 1);
        var y = Math.Clamp((int)uv.Y, 0, res - 1);
        return y * res + x;
    }

    private void WriteTransportSpike()
    {
        if (!transportSpike || rayQueries is not { } scene || rayGpuData is not { } data) return;
        var clock = Stopwatch.StartNew();
        var surfaces = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(raySurfaces, 0, data.RowPlacements.Length * 4).AsSpan()).ToArray();
        data.ApplyCoverage(surfaces);
        Vector3 Albedo(int placement, int triangle)
        {
            var word = surfaces[data.RowOf(placement, triangle)];
            static float Linear(uint b) { var c = b / 255f; return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f); }
            return new Vector3(Linear(word & 0xFF), Linear((word >> 8) & 0xFF), Linear((word >> 16) & 0xFF));
        }
        (Vector3 A, Vector3 B, Vector3 C) Tri(int instance, int triangle)
        {
            var inst = scene.Instances[instance];
            var m = inst.Mesh;
            return (Vector3.Transform(m.Positions[m.Indices[triangle * 3]], inst.World),
                    Vector3.Transform(m.Positions[m.Indices[triangle * 3 + 1]], inst.World),
                    Vector3.Transform(m.Positions[m.Indices[triangle * 3 + 2]], inst.World));
        }

        // ---- R1: patches ------------------------------------------------------------------------------------------
        var patchArea = transportSpacing * transportSpacing;
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var albedo = new List<Vector3>();
        var rng = new Random(5);
        for (var i = 0; i < scene.Instances.Count; i++)
        {
            var m = scene.Instances[i].Mesh;
            for (var t = 0; t < m.Indices.Length / 3; t++)
            {
                var (a, b, c) = Tri(i, t);
                var cross = Vector3.Cross(b - a, c - a);
                var area = cross.Length() * 0.5f;
                if (area <= 0f) continue;
                var expected = area / patchArea;
                var count = (int)expected + (rng.NextDouble() < expected - (int)expected ? 1 : 0);
                var n = Vector3.Normalize(cross);
                var alb = Albedo(i, t);
                for (var k = 0; k < count; k++)
                {
                    var u = (float)rng.NextDouble();
                    var v = (float)rng.NextDouble();
                    if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                    pos.Add(a + (b - a) * u + (c - a) * v);
                    nrm.Add(n);
                    albedo.Add(alb);
                }
            }
        }
        var patches = pos.Count;
        // A hash grid over the patches: a ray's hit finds the nearest patch facing the way the hit surface faces.
        var cell = transportSpacing;
        var grid = new Dictionary<(int, int, int), List<int>>();
        (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
        for (var i = 0; i < patches; i++)
        {
            var key = Key(pos[i]);
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
            list.Add(i);
        }
        int Nearest(Vector3 p, Vector3 n)
        {
            var (kx, ky, kz) = Key(p);
            var best = -1;
            var bestD = float.MaxValue;
            for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
                foreach (var j in list)
                {
                    if (Vector3.Dot(nrm[j], n) < 0.5f) continue;
                    var d = Vector3.DistanceSquared(pos[j], p);
                    if (d < bestD) { bestD = d; best = j; }
                }
            }
            return best;
        }
        Vector3 HitNormal(RayHit h, Vector3 dir)
        {
            var (a, b, c) = Tri(h.Instance, h.Triangle);
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            return Vector3.Dot(n, dir) > 0f ? -n : n;
        }
        var cookPatches = clock.Elapsed.TotalSeconds;

        // ---- R2 couplings + R3 sky bins + R3 sun visibility -------------------------------------------------------
        var couplings = new (int To, float W)[patches][];
        var sky = new float[patches * TransportSkyRes * TransportSkyRes];
        var visWords = (transportVisRes * transportVisRes + 63) / 64;
        var sunVis = new ulong[patches * visWords];
        long dropped = 0;
        Parallel.For(0, patches, i =>
        {
            var r = new Random(1000 + i);
            var p = pos[i] + nrm[i] * 1e-3f;
            var n = nrm[i];
            var acc = new Dictionary<int, int>();
            var lost = 0;
            for (var k = 0; k < transportRays; k++)
            {
                var d = CosineHemisphereCpu(n, r);
                if (scene.Closest(new Ray(p, d), 0f, float.PositiveInfinity, (uint)r.Next()) is { } h)
                {
                    var hp = p + d * h.T;
                    var j = Nearest(hp, HitNormal(h, d));
                    if (j < 0) { lost++; continue; }
                    acc[j] = acc.GetValueOrDefault(j) + 1;
                }
                else
                {
                    sky[i * TransportSkyRes * TransportSkyRes + OctBin(d, TransportSkyRes)] += 1f / transportRays;
                }
            }
            couplings[i] = acc.Select(e => (e.Key, e.Value / (float)transportRays)).ToArray();
            Interlocked.Add(ref dropped, lost);
            for (var b = 0; b < transportVisRes * transportVisRes; b++)
            {
                var uv = new Vector2((b % transportVisRes + 0.5f) / transportVisRes, (b / transportVisRes + 0.5f) / transportVisRes) * 2f - Vector2.One;
                var d = OctDecode(uv);
                if (Vector3.Dot(d, n) <= 0f) continue;
                if (!scene.Any(new Ray(p, d), 0f, float.PositiveInfinity, (uint)(i * 7919 + b))) sunVis[i * visWords + b / 64] |= 1UL << (b % 64);
            }
        });
        var cookSeconds = clock.Elapsed.TotalSeconds;
        long nonzero = couplings.Sum(c => (long)c.Length);
        var bytes = patches * (12L + 12 + 8) + nonzero * 8 + sky.LongLength * 4 + sunVis.LongLength * 8;

        // ---- Runtime (the CPU, for the spike): light by the sun and the sky, then bounce ---------------------------
        var toSun = -Vector3.Normalize(sunDirection);
        var sunIrr = EffectiveSunIrradiance;
        var skyDirs = Enumerable.Range(0, TransportSkyRes * TransportSkyRes).Select(b =>
            SkyRadiance(OctDecode(new Vector2((b % TransportSkyRes + 0.5f) / TransportSkyRes, (b / TransportSkyRes + 0.5f) / TransportSkyRes) * 2f - Vector2.One))).ToArray();
        float SunVisibility(int i)
        {
            // Bilinear over the four bins around the sun's direction.
            var uv = (OctEncode(toSun) * 0.5f + new Vector2(0.5f)) * transportVisRes - new Vector2(0.5f);
            var x0 = (int)MathF.Floor(uv.X); var y0 = (int)MathF.Floor(uv.Y);
            var fx = uv.X - x0; var fy = uv.Y - y0;
            float Bit(int x, int y)
            {
                x = Math.Clamp(x, 0, transportVisRes - 1); y = Math.Clamp(y, 0, transportVisRes - 1);
                var b = y * transportVisRes + x;
                return (sunVis[i * visWords + b / 64] >> (b % 64) & 1UL) != 0 ? 1f : 0f;
            }
            return (Bit(x0, y0) * (1 - fx) + Bit(x0 + 1, y0) * fx) * (1 - fy) + (Bit(x0, y0 + 1) * (1 - fx) + Bit(x0 + 1, y0 + 1) * fx) * fy;
        }
        var directSun = new Vector3[patches];
        var skyIn = new Vector3[patches];
        for (var i = 0; i < patches; i++)
        {
            directSun[i] = sunIrr * MathF.Max(Vector3.Dot(nrm[i], toSun), 0f) * SunVisibility(i);
            var s = Vector3.Zero;
            for (var b = 0; b < skyDirs.Length; b++) s += skyDirs[b] * sky[i * skyDirs.Length + b];
            skyIn[i] = s * MathF.PI;
        }
        var incident = (Vector3[])skyIn.Clone();       // indirect irradiance arriving (no direct sun)
        var outgoing = new Vector3[patches];
        const int iterations = 48;
        for (var it = 0; it < iterations; it++)
        {
            for (var i = 0; i < patches; i++) outgoing[i] = albedo[i] * (incident[i] + directSun[i]) / MathF.PI;
            Parallel.For(0, patches, i =>
            {
                var h = Vector3.Zero;
                foreach (var (to, w) in couplings[i]) h += outgoing[to] * w;
                incident[i] = skyIn[i] + h * MathF.PI;
            });
        }
        var solveSeconds = clock.Elapsed.TotalSeconds - cookSeconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] transport spike: {patches:N0} patches at {transportSpacing:0.###} m, {nonzero:N0} couplings ({nonzero / (double)Math.Max(1, patches):0.0} a patch), "
            + $"{dropped / (double)Math.Max(1, (long)patches * transportRays) * 100:0.00}% of rays found no patch; {bytes / 1048576.0:0.0} MB "
            + $"(couplings {nonzero * 8 / 1048576.0:0.0}, sky bins {sky.LongLength * 4 / 1048576.0:0.0}, sun visibility {sunVis.LongLength * 8 / 1048576.0:0.0}); "
            + $"cooked in {cookSeconds:0.0} s (patches {cookPatches:0.0} s), solved {iterations} bounces in {solveSeconds * 1000:0} ms."));

        // ---- At the reference's points --------------------------------------------------------------------------
        var incidentTex = device.ReadTexture(graph.GetColorTexture(incidentHandle), out var iw, out var ih, out _);
        Matrix4x4.Invert(viewProj, out var invViewProj);
        Ray CameraRay(float px, float py)
        {
            var ndc = new Vector2(px / iw * 2f - 1f, py / ih * 2f - 1f);
            var far = Vector4.Transform(new Vector4(ndc, 1f, 1f), invViewProj);
            return new Ray(cameraPosition, Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - cameraPosition));
        }
        static double Lum(Vector3 v) => 0.2126 * v.X + 0.7152 * v.Y + 0.0722 * v.Z;
        const int refGrid = 24;
        var lines = new ConcurrentBag<string>();
        Parallel.For(0, refGrid * refGrid, k =>
        {
            var px = (k % refGrid + 0.5f) / refGrid * iw;
            var py = (k / refGrid + 0.5f) / refGrid * ih;
            var ray = CameraRay(px, py);
            if (scene.Closest(ray) is not { } hit) return;
            var n = HitNormal(hit, ray.Direction);
            var x = ray.PointAt(hit.T) + n * 0.01f;
            // Interpolated: the patches near x that face the same way, weighted by closeness.
            var (kx, ky, kz) = Key(x);
            var sum = Vector3.Zero; var wsum = 0f;
            for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
                foreach (var j in list)
                {
                    var facing = Vector3.Dot(nrm[j], n);
                    if (facing < 0.5f) continue;
                    var w = facing * facing * MathF.Exp(-Vector3.DistanceSquared(pos[j], x) / (cell * cell));
                    sum += incident[j] * w; wsum += w;
                }
            }
            var interp = wsum > 0f ? Lum(sum / wsum) : double.NaN;
            // Final gather: rays from x reading the patches' solved light (and the sky).
            var r = new Random(4242 + k);
            var g = Vector3.Zero;
            const int gatherRays = 1024;
            for (var j = 0; j < gatherRays; j++)
            {
                var d = CosineHemisphereCpu(n, r);
                if (scene.Closest(new Ray(x, d), 0f, float.PositiveInfinity, (uint)r.Next()) is { } h)
                {
                    var p = Nearest(x + d * h.T, HitNormal(h, d));
                    if (p >= 0) g += outgoing[p];
                }
                else g += SkyRadiance(d);
            }
            var gather = Lum(g * (MathF.PI / gatherRays));
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"    transport at {px / iw:0.0000},{py / ih:0.0000} interp {interp:0.00000} gather {gather:0.00000}"));
        });
        foreach (var line in lines.OrderBy(l => l, StringComparer.Ordinal)) Console.WriteLine(line);
    }
}
