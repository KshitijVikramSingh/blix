using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
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
// The solve runs on the CPU, at load and again for each new sun (transportRelight): light every patch by the sun and the
// sky (its bins), then iterate outgoing = albedo x (incoming + direct sun) / pi through the couplings. The patches go to
// the GPU (UploadCookedPatches), where the texels' gather reads them.
internal sealed partial class SponzaLoop
{
    private Vector2? sunOverrideDegrees;
    private float transportSpacing = 0.25f;
    private int transportRays = 512;
    private int transportVisRes = 32;
    // --transport-sunlet S: the first bounce of direct sun resolved at sunlets of S metres (0: at patch centres, as
    // before). Measured (emulated, Sponza's hall): patch centres 13.4%, sunlets 2 / 5 / 10 cm 5.7 / 6.5 / 8.3%.
    private float transportSunlet = 0.04f;
    private const int TransportSkyRes = 16;
    private const int TransportGuideRes = 8;
    private const int TransportGuideBins = TransportGuideRes * TransportGuideRes;

    private void ReadTransportArgs(AppArgs args)
    {
        ReadTexelArgs(args);
        ReadReferenceArgs(args);
        transportGpu = args.Flag("transport-gpu") || transportTexels;
        transportWait = args.Flag("transport-wait");
        if (args.Float("transport-spacing") is { } s) transportSpacing = Math.Clamp(s, 0.02f, 4f);
        if (args.Int("transport-rays") is { } r) transportRays = Math.Clamp(r, 16, 8192);
        if (args.Int("transport-vis") is { } v) transportVisRes = Math.Clamp(v, 4, 256);
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

    // Called each frame: with --transport-gpu, cook once the ray scene and its surfaces are ready -- in the background
    // (the user: "can we stream / not hang after the textures load"): the frame goes on with the clipmap meanwhile, and
    // what the cook leaves for the GPU (OnMain) runs here once it is done. --transport-wait cooks on the frame thread,
    // as before, so a measurement's frames count from when texels exist.
    private void CookTransportWhenReady()
    {
        if (transportCookTask is { IsCompleted: true } task)
        {
            if (task.Exception is { } e) Console.WriteLine($"[VulkanSponza] transport: the background cook failed: {e.GetBaseException()}");
            while (transportOnMain.TryDequeue(out var action)) action();
            transportCookAsync = false;
            transportCookTask = null;
            Console.WriteLine($"[VulkanSponza] transport: the background cook landed at post-load frame {postLoadFrames}.");
        }
        if (!transportGpu || transportCooked || !fullyLoaded || !raySurfacesBaked || postLoadFrames < 2) return;
        if (transportWait || rayQueries is not { } scene || rayGpuData is not { } data)
        {
            CookTransport();
            return;
        }
        transportCooked = true;
        var surfaces = ReadRaySurfaces(data);
        transportCookAsync = true;
        Console.WriteLine($"[VulkanSponza] transport: cooking in the background from post-load frame {postLoadFrames}.");
        transportCookTask = Task.Run(() => CookTransportFrom(scene, data, surfaces));
    }

    private Task? transportCookTask;
    private bool transportCookAsync;
    private bool transportWait;
    private readonly ConcurrentQueue<Action> transportOnMain = new();

    // GPU work the cook leaves: run now on the frame thread, or queued for it when the cook runs in the background.
    private void OnMain(Action action)
    {
        if (transportCookAsync) transportOnMain.Enqueue(action);
        else action();
    }

    private GpuBufferHandle cookedGuideBuffer;

    private void UploadCookedGuides(float[] cdf)
    {
        if (!transportGpu) return;
        if (!cookedGuideBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(cookedGuideBuffer);
        cookedGuideBuffer = Own(device.CreateGpuBuffer(cdf.Length * 4, MemoryMarshal.AsBytes(cdf.AsSpan()), "sponza.cooked.guides"));
    }

    private void UploadCookedPatches(List<Vector3> pos, List<Vector3> nrm, Vector3[] incident, float[] openness, float cell)
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
            packed[3 * i + 1] = new Vector4(nrm[i], openness[i]);
            packed[3 * i + 2] = new Vector4(incident[i], 0f);
        }
        cookedPatchBuffer = Own(device.CreateGpuBuffer(packed.Length * 16, MemoryMarshal.AsBytes(packed.AsSpan()), "sponza.cooked.patches"));
        cookedPacked = packed;
        cookedCellBuffer = Own(device.CreateGpuBuffer(start.Length * 4, MemoryMarshal.AsBytes(start.AsSpan()), "sponza.cooked.cells"));
        cookedIdBuffer = Own(device.CreateGpuBuffer(Math.Max(1, ids.Length) * 4, MemoryMarshal.AsBytes(ids.AsSpan()), "sponza.cooked.ids"));
        cookedGrid = new Vector4(min, cell);
        cookedDims = new Vector4(dims.X, dims.Y, dims.Z, 1f);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] transport GPU: {pos.Count:N0} patches uploaded ({packed.Length * 16 / 1048576.0:0.0} MB), grid {dims.X}x{dims.Y}x{dims.Z} at {cell:0.##} m; screen probes' hits read them."));
    }

    private void CookTransport()
    {
        if (transportCooked || rayQueries is not { } scene || rayGpuData is not { } data) return;
        transportCooked = true;
        CookTransportFrom(scene, data, ReadRaySurfaces(data));
    }

    private uint[] ReadRaySurfaces(RayQueryGpuData data) =>
        MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(raySurfaces, 0, data.RowPlacements.Length * 4).AsSpan()).ToArray();

    private void CookTransportFrom(RayQueryScene scene, RayQueryGpuData data, uint[] surfaces)
    {
        var clock = Stopwatch.StartNew();
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

        // The cook's cache (SponzaLoop.TransportCache.cs): a run whose inputs match reads what the cook would make.
        var cacheKey = TransportCacheKey(scene, surfaces);
        DecideBakeScene(cacheKey);
        var cached = LoadTransportCache(cacheKey);

        // ---- R1: patches ------------------------------------------------------------------------------------------
        var patchArea = transportSpacing * transportSpacing;
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var albedo = new List<Vector3>();
        if (cached is null)
        {
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
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] transport: {pos.Count:N0} patch sides placed, {p2.Count:N0} kept (the rest inside a solid)."));
            pos = p2; nrm = n2; albedo = a2;
        }
        }
        else
        {
            pos = cached.Pos.ToList(); nrm = cached.Nrm.ToList(); albedo = cached.Albedo.ToList();
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
        if (cached is not null)
        {
            couplings = cached.Couplings; sunletCouplings = cached.SunletCouplings; sky = cached.Sky; sunVis = cached.SunVis;
            sunletPos.AddRange(cached.SunletPos); sunletNrm.AddRange(cached.SunletNrm); sunletAlbedo.AddRange(cached.SunletAlbedo);
        }
        else
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
        float SunVisibility(int i, Vector3 toSunDir)
        {
            // Bilinear over the four bins around the sun's direction.
            var uv = (OctEncode(toSunDir) * 0.5f + new Vector2(0.5f)) * transportVisRes - new Vector2(0.5f);
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
            directSun[i] = sunIrr * MathF.Max(Vector3.Dot(nrm[i], toSun), 0f) * SunVisibility(i, toSun);
            var s = Vector3.Zero;
            for (var b = 0; b < skyDirs.Length; b++) s += skyDirs[b] * sky[i * skyDirs.Length + b];
            skyIn[i] = s * MathF.PI;
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
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] transport spike: {patches:N0} patches at {transportSpacing:0.###} m, {nonzero:N0} couplings ({nonzero / (double)Math.Max(1, patches):0.0} a patch), "
            + $"{dropped / (double)Math.Max(1, (long)patches * transportRays) * 100:0.00}% of rays found no patch, {foliageHits / (double)Math.Max(1, (long)patches * transportRays) * 100:0.00}% met foliage (absorbed); {bytes / 1048576.0:0.0} MB "
            + $"(couplings {nonzero * 8 / 1048576.0:0.0}, sky bins {sky.LongLength * 4 / 1048576.0:0.0}, sun visibility {sunVis.LongLength * 8 / 1048576.0:0.0}, "
            + $"sunlets {sunlets:N0} at {transportSunlet * 100:0} cm {sunlets * 36L / 1048576.0:0.0} + their couplings {sunletNonzero:N0} {sunletNonzero * 8 / 1048576.0:0.0}); "
            + $"cooked in {cookSeconds:0.0} s (patches {cookPatches:0.0} s), solved {iterations} bounces in {solveSeconds * 1000:0} ms."));

        // Guides (texel_gather.comp): per patch, where its light comes from -- an 8x8 octahedral histogram over world
        // directions of (cosine-weighted ray fraction x radiance) from its couplings (the patches it sees, by their
        // light), its sunlet couplings (their sun) and its sky bins, as a CDF. A texel samples half its rays from its
        // nearest patch's: a bounce-lit balcony's light comes from a few bright patches cosine rays mostly miss.
        static float Lum3(Vector3 v) => 0.2126f * v.X + 0.7152f * v.Y + 0.0722f * v.Z;
        float[] BuildGuides(Vector3[] outg, Vector3[] sOut)
        {
            var cdf = new float[patches * TransportGuideBins];
            Parallel.For(0, patches, i =>
            {
                var h = new float[TransportGuideBins];
                foreach (var (to, w) in couplings[i])
                {
                    var d = pos[to] - pos[i];
                    if (d.LengthSquared() > 1e-8f) h[OctBin(Vector3.Normalize(d), TransportGuideRes)] += w * Lum3(outg[to]);
                }
                if (sOut.Length > 0)
                    foreach (var (to, w) in sunletCouplings[i])
                    {
                        var d = sunletPos[to] - pos[i];
                        if (d.LengthSquared() > 1e-8f) h[OctBin(Vector3.Normalize(d), TransportGuideRes)] += w * Lum3(sOut[to]);
                    }
                for (var b = 0; b < skyDirs.Length; b++)
                {
                    var f = sky[i * skyDirs.Length + b];
                    if (f <= 0f) continue;
                    var uv = new Vector2((b % TransportSkyRes + 0.5f) / TransportSkyRes, (b / TransportSkyRes + 0.5f) / TransportSkyRes) * 2f - Vector2.One;
                    h[OctBin(OctDecode(uv), TransportGuideRes)] += f * Lum3(skyDirs[b]);
                }
                var total = 0f;
                for (var b = 0; b < TransportGuideBins; b++) total += h[b];
                var acc = 0f;
                for (var b = 0; b < TransportGuideBins; b++)
                {
                    acc += total > 0f ? h[b] / total : 1f / TransportGuideBins;
                    cdf[i * TransportGuideBins + b] = acc;
                }
                cdf[i * TransportGuideBins + TransportGuideBins - 1] = 1f;
            });
            return cdf;
        }

        // The same solve for another sun (SponzaLoop.Texels.cs, FollowSun): only the sun's terms change -- its direct
        // light at patches (sun map) or sunlets (an exact ray each), and the bounces after. The couplings, sky bins and
        // sunlets are the cook's. Warm-started from the last answer. Off the frame thread: Sponza's ~9M sunlet rays and
        // 48 bounces take seconds.
        var lastIncident = (Vector3[])incident.Clone();
        transportRelight = (toSunNew, sunIrrNew) =>
        {
            var direct = new Vector3[patches];
            var first = new Vector3[patches];
            var sOut = new Vector3[useSunlets ? sunlets : 0];
            var relightClock = Stopwatch.StartNew();
            var raysOn = "the CPU";
            if (useSunlets)
            {
                // The sunlets' shadow rays on Metal when the frame's tracer is there (the scene they were cooked on):
                // ~9 M on Sponza, seconds on the CPU.
                if (Volatile.Read(ref frameTracer) is { } metal)
                {
                    var origins = new Vector4[sunlets];
                    for (var s = 0; s < sunlets; s++)
                        origins[s] = Vector3.Dot(sunletNrm[s], toSunNew) > 0f
                            ? new Vector4(sunletPos[s] + sunletNrm[s] * 1e-3f, float.PositiveInfinity) : Vector4.Zero;
                    var blocked = metal.OccludedFromHost(origins, toSunNew, 3u, out var shadowGpuMs);
                    for (var s = 0; s < sunlets; s++)
                        if (origins[s].W > 0f && !blocked[s])
                            sOut[s] = sunletAlbedo[s] * sunIrrNew * Vector3.Dot(sunletNrm[s], toSunNew) / MathF.PI;
                    raysOn = string.Create(CultureInfo.InvariantCulture, $"Metal ({shadowGpuMs:0} ms GPU)");
                }
                else
                Parallel.For(0, sunlets, s =>
                {
                    var ndl = Vector3.Dot(sunletNrm[s], toSunNew);
                    if (ndl > 0f && !scene.Any(new Ray(sunletPos[s] + sunletNrm[s] * 1e-3f, toSunNew), 0f, float.PositiveInfinity, (uint)(s + 3)))
                        sOut[s] = sunletAlbedo[s] * sunIrrNew * ndl / MathF.PI;
                });
                Parallel.For(0, patches, i =>
                {
                    var h = Vector3.Zero;
                    foreach (var (to, w) in sunletCouplings[i]) h += sOut[to] * w;
                    first[i] = h * MathF.PI;
                });
            }
            else
            {
                for (var i = 0; i < patches; i++) direct[i] = sunIrrNew * MathF.Max(Vector3.Dot(nrm[i], toSunNew), 0f) * SunVisibility(i, toSunNew);
            }
            var inc = (Vector3[])lastIncident.Clone();
            var outg = new Vector3[patches];
            for (var it = 0; it < iterations; it++)
            {
                for (var i = 0; i < patches; i++) outg[i] = albedo[i] * (inc[i] + direct[i]) / MathF.PI;
                Parallel.For(0, patches, i =>
                {
                    var h = Vector3.Zero;
                    foreach (var (to, w) in couplings[i]) h += outg[to] * w;
                    inc[i] = skyIn[i] + first[i] + h * MathF.PI;
                });
            }
            lastIncident = inc;
            var raysMs = relightClock.Elapsed.TotalMilliseconds;
            var guides = BuildGuides(outg, sOut);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport relight: {(useSunlets ? sunlets : patches):N0} sun rays on {raysOn} and {iterations} bounces in {raysMs:0} ms, guides in {relightClock.Elapsed.TotalMilliseconds - raysMs:0} ms."));
            return (inc, guides);
        };
        transportRelightSun = (toSun, sunIrr);
        // Each patch's openness: the fraction of its cosine rays that meet nothing within TexelOpenReach -- what a texel's
        // own openness is held against (texel_gather.comp): the patches' light, scaled by how much more or less open
        // the texel is than the patches around it.
        var openness = new float[patches];
        if (cached is not null) openness = cached.Openness;
        else if (transportGpu)
            Parallel.For(0, patches, i =>
            {
                var r = new Random(6151 + i);
                var free = 0;
                const int tests = 256;
                for (var t = 0; t < tests; t++)
                    if (!scene.Any(new Ray(pos[i] + nrm[i] * 0.01f, CosineHemisphereCpu(nrm[i], r)), 0f, TexelOpenReach, (uint)r.Next())) free++;
                openness[i] = free / (float)tests;
            });
        var guides = BuildGuides(outgoing, sunletOut);
        OnMain(() =>
        {
            UploadCookedPatches(pos, nrm, incident, openness, transportSpacing);
            UploadCookedGuides(guides);
        });
        // --lightmap: the lightmap atlas's texels are the finest level of the surfaces in the atlas (SponzaLoop.Lightmap.cs),
        // the hashed cells their coarser levels and every level of everything else (the foliage the cook leaves out). Null
        // when the atlas asks for more than the budget: the hashed texels then serve every surface at every level.
        var lightmapBake = lightmapEnabled && transportTexels && lightmapSources.Count > 0 ? BakeLightmapTexels(scene, cacheKey) : null;
        TexelBake? texelBake;
        if (cached?.Texels is { } cachedTexels)
        {
            texelBake = cachedTexels;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] texels: {cachedTexels.Count:N0} from the cache."));
        }
        else texelBake = CookTexels(scene, Tri, (i, t) => (surfaces[data.RowOf(i, t)] >> 24) < 255u,
            (i, t) => lightmapBake is not null && rayPlacementLightmapped[(int)data.RowPlacements[data.RowOf(i, t)]] ? 1 : 0);
        if (texelBake is { } hashed)
        {
            // The baked light (SponzaLoop.LightBake.cs), read here off the frame thread; without it, the bake begins
            // once the texels are up, toward the same file.
            var texelTotal = hashed.Count + (lightmapBake?.Count ?? 0);
            var lightPath = lightBakeOff ? null : LightBakePathFor(cacheKey, transportRelightSun);
            lightBakeKey = cacheKey;
            var light = lightPath is null ? null : ReadLightBake(lightPath, texelTotal);
            // Its own copy of the atlas: the method's locals are dropped at its end (below), before this may run.
            var atlasForUpload = lightmapBake;
            OnMain(() =>
            {
                if (atlasForUpload is { } atlas) UploadLightmapTexels(hashed, atlas, light, lightPath);
                else UploadTexels(hashed, light, lightPath);
                // Uploaded: let the CPU's texels go (the relight closure shares this scope and lives on).
                hashed = null!;
                atlasForUpload = null;
                light = null;
            });
        }
        if (transportGpu && (cached is null || (transportTexels && cached.Texels is null)))
            SaveTransportCache(cacheKey, new TransportCache
            {
                Pos = pos.ToArray(), Nrm = nrm.ToArray(), Albedo = albedo.ToArray(), Openness = openness,
                Couplings = couplings, SunletCouplings = sunletCouplings,
                SunletPos = sunletPos.ToArray(), SunletNrm = sunletNrm.ToArray(), SunletAlbedo = sunletAlbedo.ToArray(),
                Sky = sky, SunVis = sunVis, Texels = texelBake,
            });
        // transportRelight (above) closes over this method's locals, and a closure keeps every local it shares a scope
        // with: drop the big ones it never reads (the cache as read, the texels and the atlas's records, both uploaded).
        cached = null;
        texelBake = null;
        lightmapBake = null;
    }
}
