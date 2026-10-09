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
    private int transportGatherRays = 1024;
    // --transport-sunlet S: the first bounce of direct sun resolved at sunlets of S metres (0: at patch centres, as
    // before). Measured (emulated, Sponza's hall): patch centres 13.4%, sunlets 2 / 5 / 10 cm 5.7 / 6.5 / 8.3%.
    private float transportSunlet = 0.04f;
    private const int TransportSkyRes = 16;

    private void ReadTransportArgs(AppArgs args)
    {
        transportGpu = args.Flag("transport-gpu");
        transportSpike = args.Flag("transport") || transportGpu;
        if (args.Float("transport-spacing") is { } s) transportSpacing = Math.Clamp(s, 0.02f, 4f);
        if (args.Int("transport-rays") is { } r) transportRays = Math.Clamp(r, 16, 8192);
        if (args.Int("transport-vis") is { } v) transportVisRes = Math.Clamp(v, 4, 256);
        if (args.Int("transport-gather") is { } gr) transportGatherRays = Math.Clamp(gr, 16, 1 << 16);
        if (args.Float("transport-sunlet") is { } sl) transportSunlet = Math.Max(0f, sl);
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

    // The cook and the solve run once (at load with --transport-gpu, else at the shot); evaluating at the reference's
    // points is kept as a closure over what they produced, run at the shot.
    private Action? transportEvaluate;
    private bool transportCooked;

    // --transport-gpu (stage 5a, the GPU prototype): cook at load, upload the patches and their solved indirect light,
    // and let screen probes' hits read them (Shaders/screen_probe_kernel.glsl, cookedIndirect).
    private bool transportGpu;
    private GpuBufferHandle cookedPatchBuffer, cookedCellBuffer, cookedIdBuffer;
    private Vector4 cookedGrid, cookedDims;

    private void EnsureCookedPlaceholders()
    {
        if (!cookedPatchBuffer.Equals(default(GpuBufferHandle))) return;
        cookedPatchBuffer = Own(device.CreateGpuBuffer(48, new byte[48], "sponza.cooked.patches.none"));
        cookedCellBuffer = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.cooked.cells.none"));
        cookedIdBuffer = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.cooked.ids.none"));
    }

    // The screen probes' bindings for the cooked patches (placeholders until the cook lands).
    private IEnumerable<ShaderBufferBinding> CookedBuffers()
    {
        EnsureCookedPlaceholders();
        yield return new ShaderBufferBinding("CookedPatches", cookedPatchBuffer);
        yield return new ShaderBufferBinding("CookedCells", cookedCellBuffer);
        yield return new ShaderBufferBinding("CookedIds", cookedIdBuffer);
    }

    // Called each frame: with --transport-gpu, cook once the ray scene and its surfaces are ready (synchronous: seconds
    // for the Cornell box, minutes for Sponza, the window frozen meanwhile -- a prototype).
    private void CookTransportWhenReady()
    {
        if (!transportGpu || transportCooked || !fullyLoaded || !raySurfacesBaked || postLoadFrames < 2) return;
        CookTransport();
    }

    private void UploadCookedPatches(List<Vector3> pos, List<Vector3> nrm, Vector3[] incident, float cell)
    {
        if (!transportGpu) return;
        var min = sceneBoundsMin - new Vector3(cell);
        var span = sceneBoundsSpan + new Vector3(2f * cell);
        var dims = new Int3((int)MathF.Ceiling(span.X / cell) + 1, (int)MathF.Ceiling(span.Y / cell) + 1, (int)MathF.Ceiling(span.Z / cell) + 1);
        var cells = dims.X * dims.Y * dims.Z;
        int CellOf(Vector3 p)
        {
            var c = (p - min) / cell;
            var x = Math.Clamp((int)MathF.Floor(c.X), 0, dims.X - 1); var y = Math.Clamp((int)MathF.Floor(c.Y), 0, dims.Y - 1); var z = Math.Clamp((int)MathF.Floor(c.Z), 0, dims.Z - 1);
            return (z * dims.Y + y) * dims.X + x;
        }
        var start = new uint[cells + 1];
        for (var i = 0; i < pos.Count; i++) start[CellOf(pos[i]) + 1]++;
        for (var c = 0; c < cells; c++) start[c + 1] += start[c];
        var fill = (uint[])start.Clone();
        var ids = new uint[pos.Count];
        for (var i = 0; i < pos.Count; i++) ids[fill[CellOf(pos[i])]++] = (uint)i;
        var packed = new Vector4[pos.Count * 3];
        for (var i = 0; i < pos.Count; i++)
        {
            packed[3 * i] = new Vector4(pos[i], 0f);
            packed[3 * i + 1] = new Vector4(nrm[i], 0f);
            packed[3 * i + 2] = new Vector4(incident[i], 0f);
        }
        cookedPatchBuffer = Own(device.CreateGpuBuffer(packed.Length * 16, MemoryMarshal.AsBytes(packed.AsSpan()), "sponza.cooked.patches"));
        cookedCellBuffer = Own(device.CreateGpuBuffer(start.Length * 4, MemoryMarshal.AsBytes(start.AsSpan()), "sponza.cooked.cells"));
        cookedIdBuffer = Own(device.CreateGpuBuffer(Math.Max(1, ids.Length) * 4, MemoryMarshal.AsBytes(ids.AsSpan()), "sponza.cooked.ids"));
        cookedGrid = new Vector4(min, cell);
        cookedDims = new Vector4(dims.X, dims.Y, dims.Z, 1f);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] transport GPU: {pos.Count:N0} patches uploaded ({packed.Length * 16 / 1048576.0:0.0} MB), grid {dims.X}x{dims.Y}x{dims.Z} at {cell:0.##} m; screen probes' hits read them."));
    }

    private void WriteTransportSpike()
    {
        if (!transportSpike) return;
        if (!transportCooked) CookTransport();
        transportEvaluate?.Invoke();
    }

    private void CookTransport()
    {
        if (transportCooked || rayQueries is not { } scene || rayGpuData is not { } data) return;
        transportCooked = true;
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
                // Patches are sized for architecture, no smaller: a cutout triangle (foliage -- its coverage byte under
                // 255) gets none. Leaves are far finer than a patch, and lighting them with patches is what made the
                // tree's dark inner leaves borrow its sunlit outer ones (the orbit's points, 2.7x bright in the darkest
                // quarter). They stay occluders in the cook (the coverage coin); lighting them is another representation's.
                if ((surfaces[data.RowOf(i, t)] >> 24) < 255u) continue;
                var expected = area / patchArea;
                var count = (int)expected + (rng.NextDouble() < expected - (int)expected ? 1 : 0);
                var front = Vector3.Normalize(cross);
                var alb = Albedo(i, t);
                // Stratified, not uniform: the R2 low-discrepancy sequence from a random start, folded into the triangle.
                // Uniform random placement clustered some patches and left holes between others, which the
                // interpolation then read across (and a hit found no patch near it: 5.25% of rays at 0.125 m).
                var u0 = (float)rng.NextDouble();
                var v0 = (float)rng.NextDouble();
                for (var k = 0; k < count; k++)
                {
                    var u = (u0 + k * 0.7548777f) % 1f;
                    var v = (v0 + k * 0.5698403f) % 1f;
                    if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                    // Both sides are candidates: a thin sheet (a curtain, a leaf) is lit and seen from both, and a hit on
                    // its back must find a patch there (13.8% of Sponza's rays found none when only fronts had them).
                    // A side inside something -- the inner face of a wall slab -- is dropped below.
                    var at = a + (b - a) * u + (c - a) * v;
                    for (var side = 0; side < 2; side++)
                    {
                        pos.Add(at);
                        nrm.Add(side == 0 ? front : -front);
                        albedo.Add(alb);
                    }
                }
            }
        }
        // Drop sides inside something: a patch whose rays mostly meet back faces sits in a solid (a probe's "buried").
        {
            var keep = new bool[pos.Count];
            Parallel.For(0, pos.Count, i =>
            {
                var r = new Random(77 + i);
                var back = 0;
                const int tests = 16;
                for (var k = 0; k < tests; k++)
                {
                    var d = CosineHemisphereCpu(nrm[i], r);
                    if (scene.Closest(new Ray(pos[i] + nrm[i] * 1e-3f, d), 0f, float.PositiveInfinity, (uint)r.Next()) is { } h && !h.FrontFace) back++;
                }
                keep[i] = back * 2 < tests;
            });
            var p2 = new List<Vector3>(); var n2 = new List<Vector3>(); var a2 = new List<Vector3>();
            for (var i = 0; i < pos.Count; i++) if (keep[i]) { p2.Add(pos[i]); n2.Add(nrm[i]); a2.Add(albedo[i]); }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] transport spike: {pos.Count:N0} patch sides placed, {p2.Count:N0} kept (the rest inside a solid)."));
            pos = p2; nrm = n2; albedo = a2;
        }
        var patches = pos.Count;
        // A hash grid over the patches: a ray's hit finds the nearest patch facing the way the hit surface faces.
        // Cells twice the spacing, so the 3x3x3 search reaches past any gap between neighbouring patches.
        var cell = 2f * transportSpacing;
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
        // When no patch near a hit faces its way (2.4% of Sponza's rays), the nearest facing either way rather than none:
        // dropping the ray dropped its light (energy -4%).
        int NearestAny(Vector3 p, Vector3 n)
        {
            var j = Nearest(p, n);
            if (j >= 0) return j;
            var (kx, ky, kz) = Key(p);
            var best = -1; var bestD = float.MaxValue;
            for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
                foreach (var c in list) { var d = Vector3.DistanceSquared(pos[c], p); if (d < bestD) { bestD = d; best = c; } }
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
        long foliageHits = 0;
        // Sunlets: where cooked rays hit architecture, a cell of transportSunlet metres AND a coarse normal bin (so a
        // corner's two walls, or a sheet's two sides, never share one) -- position, normal and albedo of its first hit.
        // A patch's first bounce of direct light is read through them; everything after through the patch couplings.
        var useSunlets = transportSunlet > 0f;
        var sunletIndex = new ConcurrentDictionary<(int, int, int, int), int>();
        var sunletPos = new List<Vector3>(); var sunletNrm = new List<Vector3>(); var sunletAlbedo = new List<Vector3>();
        var sunletLock = new object();
        int SunletOf(Vector3 hp, Vector3 hn, Vector3 alb)
        {
            var key = ((int)MathF.Floor(hp.X / transportSunlet), (int)MathF.Floor(hp.Y / transportSunlet), (int)MathF.Floor(hp.Z / transportSunlet), OctBin(hn, 4));
            if (sunletIndex.TryGetValue(key, out var found)) return found;
            lock (sunletLock)
            {
                if (sunletIndex.TryGetValue(key, out found)) return found;
                var id = sunletPos.Count;
                sunletPos.Add(hp); sunletNrm.Add(hn); sunletAlbedo.Add(alb);
                sunletIndex[key] = id;
                return id;
            }
        }
        var sunletCouplings = new (int To, float W)[patches][];
        bool Cutout(RayHit h) => (surfaces[data.RowOf(h.Instance, h.Triangle)] >> 24) < 255u;
        Parallel.For(0, patches, i =>
        {
            var r = new Random(1000 + i);
            var p = pos[i] + nrm[i] * 1e-3f;
            var n = nrm[i];
            var acc = new Dictionary<int, int>();
            var accSun = new Dictionary<int, int>();
            var lost = 0;
            for (var k = 0; k < transportRays; k++)
            {
                var d = CosineHemisphereCpu(n, r);
                if (scene.Closest(new Ray(p, d), 0f, float.PositiveInfinity, (uint)r.Next()) is { } h)
                {
                    // A leaf has no patch: what it would bounce is not represented yet (absorbed, and counted).
                    if (Cutout(h)) { Interlocked.Increment(ref foliageHits); continue; }
                    var hp = p + d * h.T;
                    var hn = HitNormal(h, d);
                    if (useSunlets)
                    {
                        var sl = SunletOf(hp, hn, Albedo(h.Instance, h.Triangle));
                        accSun[sl] = accSun.GetValueOrDefault(sl) + 1;
                    }
                    var j = useSunlets ? NearestAny(hp, hn) : Nearest(hp, hn);
                    if (j < 0) { lost++; continue; }
                    acc[j] = acc.GetValueOrDefault(j) + 1;
                }
                else
                {
                    sky[i * TransportSkyRes * TransportSkyRes + OctBin(d, TransportSkyRes)] += 1f / transportRays;
                }
            }
            couplings[i] = acc.Select(e => (e.Key, e.Value / (float)transportRays)).ToArray();
            sunletCouplings[i] = accSun.Select(e => (e.Key, e.Value / (float)transportRays)).ToArray();
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
        long sunletNonzero = sunletCouplings.Sum(c => (long)(c?.Length ?? 0));
        var sunlets = sunletPos.Count;
        var bytes = patches * (12L + 12 + 8) + nonzero * 8 + sky.LongLength * 4 + sunVis.LongLength * 8 + sunlets * 36L + sunletNonzero * 8;

        // ---- Runtime (the CPU, for the spike): light by the sun and the sky, then bounce ---------------------------
        var toSun = -Vector3.Normalize(sunDirection);
        var sunIrr = EffectiveSunIrradiance;
        // Each bin's radiance is the sky's mean over the bin (a 4x4 grid inside it), not its centre's: an HDR sky has
        // bright regions much smaller than a 16x16 bin.
        var skyDirs = Enumerable.Range(0, TransportSkyRes * TransportSkyRes).Select(b =>
        {
            var acc = Vector3.Zero;
            for (var sy = 0; sy < 4; sy++)
            for (var sx = 0; sx < 4; sx++)
                acc += SkyRadiance(OctDecode(new Vector2((b % TransportSkyRes + (sx + 0.5f) / 4f) / TransportSkyRes, (b / TransportSkyRes + (sy + 0.5f) / 4f) / TransportSkyRes) * 2f - Vector2.One));
            return acc / 16f;
        }).ToArray();
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
        // How well the sun map stands in for an exact ray toward this sun: over sunward patches, how often they disagree.
        {
            long sunward = 0, disagree = 0; double absDiff = 0;
            for (var i = 0; i < patches; i++)
            {
                if (Vector3.Dot(nrm[i], toSun) <= 0f) continue;
                sunward++;
                var exact = scene.Any(new Ray(pos[i] + nrm[i] * 1e-3f, toSun), 0f, float.PositiveInfinity, (uint)(i + 17)) ? 0f : 1f;
                var mapped = SunVisibility(i);
                absDiff += MathF.Abs(mapped - exact);
                if (MathF.Abs(mapped - exact) > 0.5f) disagree++;
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport spike: sun map against an exact ray from each of {sunward:N0} sunward patches: {100.0 * disagree / Math.Max(1, sunward):0.00}% disagree, mean |difference| {absDiff / Math.Max(1, sunward):0.0000}."));
        }
        // Each sunlet's direct sun at runtime (an exact ray here; the shadow maps on the GPU), and the first bounce it
        // sends every patch that saw it: fixed for this sun, so added outside the bounce iteration.
        var firstBounce = new Vector3[patches];
        var sunletOut = new Vector3[sunlets];
        if (useSunlets)
        {
            Parallel.For(0, sunlets, s =>
            {
                var ndl = Vector3.Dot(sunletNrm[s], toSun);
                if (ndl > 0f && !scene.Any(new Ray(sunletPos[s] + sunletNrm[s] * 1e-3f, toSun), 0f, float.PositiveInfinity, (uint)(s + 3)))
                    sunletOut[s] = sunletAlbedo[s] * sunIrr * ndl / MathF.PI;
            });
            Parallel.For(0, patches, i =>
            {
                var h = Vector3.Zero;
                foreach (var (to, w) in sunletCouplings[i]) h += sunletOut[to] * w;
                firstBounce[i] = h * MathF.PI;
            });
        }
        var incident = (Vector3[])skyIn.Clone();       // indirect irradiance arriving (no direct sun)
        var outgoing = new Vector3[patches];
        const int iterations = 48;
        for (var it = 0; it < iterations; it++)
        {
            // With sunlets, a patch passes on only what it RECEIVED (its own direct sun reached others through sunlets).
            for (var i = 0; i < patches; i++) outgoing[i] = albedo[i] * (incident[i] + (useSunlets ? Vector3.Zero : directSun[i])) / MathF.PI;
            Parallel.For(0, patches, i =>
            {
                var h = Vector3.Zero;
                foreach (var (to, w) in couplings[i]) h += outgoing[to] * w;
                incident[i] = skyIn[i] + firstBounce[i] + h * MathF.PI;
            });
        }
        var solveSeconds = clock.Elapsed.TotalSeconds - cookSeconds;
        // The same solve with one source at a time (it is linear): the sun's bounce and the sky's, held against the
        // reference's own split.
        Vector3[] SolveOutgoing(bool sun, bool skyOn)
        {
            var inc = skyOn ? (Vector3[])skyIn.Clone() : new Vector3[patches];
            var outg = new Vector3[patches];
            for (var it = 0; it < iterations; it++)
            {
                for (var i = 0; i < patches; i++) outg[i] = albedo[i] * (inc[i] + (sun && !useSunlets ? directSun[i] : Vector3.Zero)) / MathF.PI;
                Parallel.For(0, patches, i =>
                {
                    var h = Vector3.Zero;
                    foreach (var (to, w) in couplings[i]) h += outg[to] * w;
                    inc[i] = (skyOn ? skyIn[i] : Vector3.Zero) + (sun && useSunlets ? firstBounce[i] : Vector3.Zero) + h * MathF.PI;
                });
            }
            return outg;
        }
        var outgoingSun = SolveOutgoing(true, false);
        var outgoingSky = SolveOutgoing(false, true);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] transport spike: {patches:N0} patches at {transportSpacing:0.###} m, {nonzero:N0} couplings ({nonzero / (double)Math.Max(1, patches):0.0} a patch), "
            + $"{dropped / (double)Math.Max(1, (long)patches * transportRays) * 100:0.00}% of rays found no patch, {foliageHits / (double)Math.Max(1, (long)patches * transportRays) * 100:0.00}% met foliage (absorbed); {bytes / 1048576.0:0.0} MB "
            + $"(couplings {nonzero * 8 / 1048576.0:0.0}, sky bins {sky.LongLength * 4 / 1048576.0:0.0}, sun visibility {sunVis.LongLength * 8 / 1048576.0:0.0}, "
            + $"sunlets {sunlets:N0} at {transportSunlet * 100:0} cm {sunlets * 36L / 1048576.0:0.0} + their couplings {sunletNonzero:N0} {sunletNonzero * 8 / 1048576.0:0.0}); "
            + $"cooked in {cookSeconds:0.0} s (patches {cookPatches:0.0} s), solved {iterations} bounces in {solveSeconds * 1000:0} ms."));

        UploadCookedPatches(pos, nrm, incident, 2f * transportSpacing);

        // ---- At the reference's points --------------------------------------------------------------------------
        transportEvaluate = () => {
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
                    var w = facing * facing * MathF.Exp(-Vector3.DistanceSquared(pos[j], x) / (transportSpacing * transportSpacing));
                    sum += incident[j] * w; wsum += w;
                }
            }
            var interp = wsum > 0f ? Lum(sum / wsum) : double.NaN;
            // Same plane only (a patch on another wall, even a near one, is another surface): facing within ~18 degrees
            // and within 2 cm of the point's plane; weighted by closeness as above, and by inverse distance.
            var sumP = Vector3.Zero; var wP = 0f; var sumI = Vector3.Zero; var wI = 0f;
            for (var dz = -2; dz <= 2; dz++)
            for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
            {
                if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
                foreach (var j in list)
                {
                    if (Vector3.Dot(nrm[j], n) < 0.95f || MathF.Abs(Vector3.Dot(n, pos[j] - x + n * 0.01f)) > 0.02f) continue;
                    var d2 = Vector3.DistanceSquared(pos[j], x);
                    var wp = MathF.Exp(-d2 / (transportSpacing * transportSpacing));
                    sumP += incident[j] * wp; wP += wp;
                    var wi = 1f / (d2 + 1e-4f);
                    sumI += incident[j] * wi; wI += wi;
                }
            }
            var plane = wP > 0f ? Lum(sumP / wP) : double.NaN;
            // A linear fit over the same plane's patches (moving least squares): H(x) = a + b.u + c.v in the plane's
            // own coordinates, Gaussian-weighted -- an average flattens a gradient, a plane through the patches keeps it.
            var mls = double.NaN;
            {
                var tu = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, n));
                var tv = Vector3.Cross(n, tu);
                double s00 = 0, s01 = 0, s02 = 0, s11 = 0, s12 = 0, s22 = 0, b0 = 0, b1 = 0, b2 = 0;
                var count = 0;
                for (var dz = -2; dz <= 2; dz++)
                for (var dy = -2; dy <= 2; dy++)
                for (var dx = -2; dx <= 2; dx++)
                {
                    if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
                    foreach (var j in list)
                    {
                        if (Vector3.Dot(nrm[j], n) < 0.95f || MathF.Abs(Vector3.Dot(n, pos[j] - x + n * 0.01f)) > 0.02f) continue;
                        var dv = pos[j] - x;
                        var d2 = dv.LengthSquared();
                        if (d2 > 9f * transportSpacing * transportSpacing) continue;
                        double w = MathF.Exp(-d2 / (2f * transportSpacing * transportSpacing));
                        double u = Vector3.Dot(dv, tu), v = Vector3.Dot(dv, tv), h = Lum(incident[j]);
                        s00 += w; s01 += w * u; s02 += w * v; s11 += w * u * u; s12 += w * u * v; s22 += w * v * v;
                        b0 += w * h; b1 += w * h * u; b2 += w * h * v;
                        count++;
                    }
                }
                // Solve the 3x3 normal equations by Cramer's rule; fall back to the weighted mean when it is singular.
                var det = s00 * (s11 * s22 - s12 * s12) - s01 * (s01 * s22 - s12 * s02) + s02 * (s01 * s12 - s11 * s02);
                if (count >= 4 && Math.Abs(det) > 1e-12 * Math.Pow(s00, 3))
                    mls = (b0 * (s11 * s22 - s12 * s12) - s01 * (b1 * s22 - s12 * b2) + s02 * (b1 * s12 - s11 * b2)) / det;
                else if (s00 > 0) mls = b0 / s00;
            }
            var inverse = wI > 0f ? Lum(sumI / wI) : double.NaN;
            // Final gather: rays from x reading the patches' solved light (and the sky).
            // Also by source (sun bounce; sky bounce + direct sky), and with the EXACT sun at each hit point: the hit's own
            // albedo x (its own sun visibility, one ray + the patches' indirect light there) -- is the sun's point
            // sampling at patch centres what the error is?
            var r = new Random(4242 + k);
            var g = Vector3.Zero; var gSun = Vector3.Zero; var gSky = Vector3.Zero; var gExact = Vector3.Zero;
            // Sunlets of a given size, emulated: the exact-sun lookup at the hit snapped to a grid of that size (the cell's
            // centre, projected back onto the hit's surface plane) -- how fine do they need to be?
            float[] snapSizes = { 0.02f, 0.05f, 0.10f };
            var gSnap = new Vector3[snapSizes.Length];
            var gatherRays = transportGatherRays;
            for (var j = 0; j < gatherRays; j++)
            {
                var d = CosineHemisphereCpu(n, r);
                if (scene.Closest(new Ray(x, d), 0f, float.PositiveInfinity, (uint)r.Next()) is { } h)
                {
                    if (Cutout(h)) continue;
                    var hn = HitNormal(h, d);
                    var hp = x + d * h.T;
                    var p = Nearest(hp, hn);
                    if (p >= 0) { g += outgoing[p]; gSun += outgoingSun[p]; gSky += outgoingSky[p]; }
                    if (useSunlets)
                    {
                        // The hit's own direct sun, through its sunlet (exact where no cooked ray recorded one).
                        var key = ((int)MathF.Floor(hp.X / transportSunlet), (int)MathF.Floor(hp.Y / transportSunlet), (int)MathF.Floor(hp.Z / transportSunlet), OctBin(hn, 4));
                        Vector3 ls;
                        if (sunletIndex.TryGetValue(key, out var sid)) ls = sunletOut[sid];
                        else
                        {
                            var nd = Vector3.Dot(hn, toSun);
                            ls = nd > 0f && !scene.Any(new Ray(hp + hn * 1e-3f, toSun), 0f, float.PositiveInfinity, (uint)r.Next()) ? Albedo(h.Instance, h.Triangle) * sunIrr * nd / MathF.PI : Vector3.Zero;
                        }
                        g += ls; gSun += ls;
                    }
                    var ndl = Vector3.Dot(hn, toSun);
                    var sunAt = ndl > 0f && !scene.Any(new Ray(hp + hn * 1e-3f, toSun), 0f, float.PositiveInfinity, (uint)r.Next()) ? sunIrr * ndl : Vector3.Zero;
                    var hitAlbedo = Albedo(h.Instance, h.Triangle);
                    var indirectThere = p >= 0 ? incident[p] : Vector3.Zero;
                    gExact += hitAlbedo * (sunAt + indirectThere) / MathF.PI;
                    for (var q = 0; q < snapSizes.Length; q++)
                    {
                        var sz = snapSizes[q];
                        var centre = new Vector3(MathF.Floor(hp.X / sz) + 0.5f, MathF.Floor(hp.Y / sz) + 0.5f, MathF.Floor(hp.Z / sz) + 0.5f) * sz;
                        var onPlane = centre - hn * Vector3.Dot(hn, centre - hp);
                        var sunSnap = ndl > 0f && !scene.Any(new Ray(onPlane + hn * 1e-3f, toSun), 0f, float.PositiveInfinity, (uint)r.Next()) ? sunIrr * ndl : Vector3.Zero;
                        gSnap[q] += hitAlbedo * (sunSnap + indirectThere) / MathF.PI;
                    }
                }
                else { var sk = SkyRadiance(d); g += sk; gSky += sk; gExact += sk; for (var q = 0; q < gSnap.Length; q++) gSnap[q] += sk; }
            }
            var gather = Lum(g * (MathF.PI / gatherRays));
            var gatherSun = Lum(gSun * (MathF.PI / gatherRays));
            var gatherSky = Lum(gSky * (MathF.PI / gatherRays));
            var gatherExact = Lum(gExact * (MathF.PI / gatherRays));
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"    transport at {px / iw:0.0000},{py / ih:0.0000} interp {interp:0.00000} gather {gather:0.00000} plane {plane:0.00000} inverse {inverse:0.00000} linear {mls:0.00000} cutout {(Cutout(hit) ? 1 : 0)} gsun {gatherSun:0.00000} gsky {gatherSky:0.00000} gexact {gatherExact:0.00000} g2cm {Lum(gSnap[0] * (MathF.PI / gatherRays)):0.00000} g5cm {Lum(gSnap[1] * (MathF.PI / gatherRays)):0.00000} g10cm {Lum(gSnap[2] * (MathF.PI / gatherRays)):0.00000}"));
        });
        foreach (var line in lines.OrderBy(l => l, StringComparer.Ordinal)) Console.WriteLine(line);
        };
    }
}
