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
// Runtime here is the CPU, for the spike: light every patch by the sun (its visibility map) and the sky (its bins), then
// iterate outgoing = albedo x (incoming + direct sun) / pi through the couplings. At each reference point, two answers:
// INTERPOLATED from nearby patches (what a renderer would do) and a FINAL GATHER of rays reading the patches' solved light
// (a diagnostic: what the representation knows, apart from how it is interpolated).
internal sealed partial class SponzaLoop
{
    private Vector2? sunOverrideDegrees;
    private bool transportSpike;
    // The CPU evaluation at the reference's points runs with --transport only (minutes on Sponza).
    private bool transportEvaluateAtShot;
    private float transportSpacing = 0.25f;
    private int transportRays = 512;
    private int transportVisRes = 32;
    private int transportGatherRays = 1024;
    // --transport-sunlet S: the first bounce of direct sun resolved at sunlets of S metres (0: at patch centres, as
    // before). Measured (emulated, Sponza's hall): patch centres 13.4%, sunlets 2 / 5 / 10 cm 5.7 / 6.5 / 8.3%.
    private float transportSunlet = 0.04f;
    // --transport-charts: patches carry surface identity. Triangles joined across shared edges into charts (adjacent
    // normals within ~25 degrees); every chart gets at least one patch, and a point -- or a cooked ray's hit -- reads
    // only its own chart's patches. Uniform area sampling left 40% of the hall's visible points (mouldings, fluting,
    // edges) without a patch on their own surface within 15 cm: interp 36% there against 13% where one was.
    private bool transportCharts;
    private const int TransportSkyRes = 16;
    private const int TransportGuideRes = 8;
    private const int TransportGuideBins = TransportGuideRes * TransportGuideRes;

    private void ReadTransportArgs(AppArgs args)
    {
        ReadTexelArgs(args);
        ReadReferenceArgs(args);
        transportGpu = args.Flag("transport-gpu") || transportTexels;
        transportSpike = args.Flag("transport") || transportGpu;
        transportEvaluateAtShot = args.Flag("transport");
        transportWait = args.Flag("transport-wait");
        if (args.Float("transport-spacing") is { } s) transportSpacing = Math.Clamp(s, 0.02f, 4f);
        if (args.Int("transport-rays") is { } r) transportRays = Math.Clamp(r, 16, 8192);
        if (args.Int("transport-vis") is { } v) transportVisRes = Math.Clamp(v, 4, 256);
        if (args.Int("transport-gather") is { } gr) transportGatherRays = Math.Clamp(gr, 16, 1 << 16);
        if (args.Float("transport-sunlet") is { } sl) transportSunlet = Math.Max(0f, sl);
        transportCharts = args.Flag("transport-charts");
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
        if (transportWait || transportEvaluateAtShot || rayQueries is not { } scene || rayGpuData is not { } data)
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

    private void WriteTransportSpike()
    {
        if (!transportEvaluateAtShot) return;
        transportCookTask?.Wait();
        CookTransportWhenReady();
        if (!transportCooked) CookTransport();
        transportEvaluate?.Invoke();
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

        // The cook's cache (SponzaLoop.TransportCache.cs): a run whose inputs match reads what the cook would make. The
        // CPU evaluation (--transport) reads the cook's internals as well, so it always cooks.
        var cacheKey = TransportCacheKey(scene, surfaces);
        var cached = transportEvaluateAtShot ? null : LoadTransportCache(cacheKey);

        // ---- Charts: surface identity ------------------------------------------------------------------------------
        // Per instance, triangles sharing an edge (positions welded at 0.1 mm) whose normals agree within ~25 degrees.
        var chartOf = new int[scene.Instances.Count][];
        var charts = 0;
        // Only when something reads them (--transport-charts, or the evaluation): Sponza is 759k charts.
        var computeCharts = transportCharts || transportEvaluateAtShot;
        if (!computeCharts)
        {
            for (var i = 0; i < scene.Instances.Count; i++) chartOf[i] = new int[scene.Instances[i].Mesh.Indices.Length / 3];
            charts = 1;
        }
        else
        for (var i = 0; i < scene.Instances.Count; i++)
        {
            var m = scene.Instances[i].Mesh;
            var tris = m.Indices.Length / 3;
            var parent = new int[tris];
            for (var t = 0; t < tris; t++) parent[t] = t;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            var normals = new Vector3[tris];
            var weld = new Dictionary<(long, long, long), int>();
            var corner = new int[tris * 3];
            for (var t = 0; t < tris; t++)
            {
                var (a, b, c) = Tri(i, t);
                var cr = Vector3.Cross(b - a, c - a);
                normals[t] = cr.LengthSquared() > 0f ? Vector3.Normalize(cr) : Vector3.Zero;
                Vector3[] v = { a, b, c };
                for (var q = 0; q < 3; q++)
                {
                    var key = ((long)MathF.Round(v[q].X * 1e4f), (long)MathF.Round(v[q].Y * 1e4f), (long)MathF.Round(v[q].Z * 1e4f));
                    if (!weld.TryGetValue(key, out var id)) weld[key] = id = weld.Count;
                    corner[t * 3 + q] = id;
                }
            }
            var edges = new Dictionary<(int, int), int>();
            for (var t = 0; t < tris; t++)
            for (var q = 0; q < 3; q++)
            {
                int va = corner[t * 3 + q], vb = corner[t * 3 + (q + 1) % 3];
                if (va == vb) continue;
                var key = (Math.Min(va, vb), Math.Max(va, vb));
                if (edges.TryGetValue(key, out var other))
                {
                    if (MathF.Abs(Vector3.Dot(normals[t], normals[other])) >= 0.9f) parent[Find(t)] = Find(other);
                }
                else edges[key] = t;
            }
            var local = new Dictionary<int, int>();
            chartOf[i] = new int[tris];
            for (var t = 0; t < tris; t++)
            {
                var root = Find(t);
                if (!local.TryGetValue(root, out var id)) local[root] = id = charts + local.Count;
                chartOf[i][t] = id;
            }
            charts += local.Count;
        }

        // ---- R1: patches ------------------------------------------------------------------------------------------
        var patchArea = transportSpacing * transportSpacing;
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var albedo = new List<Vector3>();
        var patchChart = new List<int>();
        var chartPlaced = new bool[charts];
        var chartLargest = new (int Instance, int Triangle, float Area)[charts];
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
                var ch = chartOf[i][t];
                if (area > chartLargest[ch].Area) chartLargest[ch] = (i, t, area);
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
                        patchChart.Add(ch);
                    }
                    chartPlaced[ch] = true;
                }
            }
        }
        // Every chart at least one patch (both sides), at its largest triangle's centroid -- a moulding smaller than a
        // patch's area still has its own light, instead of borrowing the wall's beside it.
        var forced = 0;
        if (transportCharts)
            for (var ch = 0; ch < charts; ch++)
            {
                if (chartPlaced[ch] || chartLargest[ch].Area <= 0f) continue;
                var (ci, ct, _) = chartLargest[ch];
                var (a, b, c) = Tri(ci, ct);
                var front = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                var alb = Albedo(ci, ct);
                for (var side = 0; side < 2; side++)
                {
                    pos.Add((a + b + c) / 3f);
                    nrm.Add(side == 0 ? front : -front);
                    albedo.Add(alb);
                    patchChart.Add(ch);
                }
                forced++;
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
            var p2 = new List<Vector3>(); var n2 = new List<Vector3>(); var a2 = new List<Vector3>(); var c2 = new List<int>();
            for (var i = 0; i < pos.Count; i++) if (keep[i]) { p2.Add(pos[i]); n2.Add(nrm[i]); a2.Add(albedo[i]); c2.Add(patchChart[i]); }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] transport spike: {pos.Count:N0} patch sides placed, {p2.Count:N0} kept (the rest inside a solid); {charts:N0} charts, {forced:N0} given a patch of their own{(transportCharts ? "" : " (off)")}."));
            pos = p2; nrm = n2; albedo = a2; patchChart = c2;
        }
        }
        else
        {
            pos = cached.Pos.ToList(); nrm = cached.Nrm.ToList(); albedo = cached.Albedo.ToList();
            patchChart = new List<int>(new int[pos.Count]);
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
        // A hit's patch on its own chart (with --transport-charts): the nearest facing it there; else as before.
        int NearestOwn(Vector3 p, Vector3 n, RayHit h, bool any)
        {
            if (transportCharts)
            {
                var ch = chartOf[h.Instance][h.Triangle];
                var (kx, ky, kz) = Key(p);
                var best = -1; var bestD = float.MaxValue;
                for (var dz = -1; dz <= 1; dz++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
                    foreach (var j in list)
                    {
                        if (patchChart[j] != ch || Vector3.Dot(nrm[j], n) <= 0f) continue;
                        var d = Vector3.DistanceSquared(pos[j], p);
                        if (d < bestD) { bestD = d; best = j; }
                    }
                }
                if (best >= 0) return best;
            }
            return any ? NearestAny(p, n) : Nearest(p, n);
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
                    var j = NearestOwn(hp, hn, h, useSunlets);
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
        // How well the sun map stands in for an exact ray toward this sun: over sunward patches, how often they disagree.
        // (A diagnostic: with the evaluation only.)
        if (transportEvaluateAtShot)
        {
            long sunward = 0, disagree = 0; double absDiff = 0;
            for (var i = 0; i < patches; i++)
            {
                if (Vector3.Dot(nrm[i], toSun) <= 0f) continue;
                sunward++;
                var exact = scene.Any(new Ray(pos[i] + nrm[i] * 1e-3f, toSun), 0f, float.PositiveInfinity, (uint)(i + 17)) ? 0f : 1f;
                var mapped = SunVisibility(i, toSun);
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
            if (useSunlets)
            {
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
            return (inc, BuildGuides(outg, sOut));
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
        TexelBake? texelBake;
        if (cached?.Texels is { } cachedTexels)
        {
            OnMain(() => UploadTexels(cachedTexels));
            texelBake = cachedTexels;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] texels: {cachedTexels.Count:N0} from the cache."));
        }
        else texelBake = CookTexels(scene, Tri, (i, t) => (surfaces[data.RowOf(i, t)] >> 24) < 255u);
        if (transportGpu && (cached is null || (transportTexels && cached.Texels is null)))
            SaveTransportCache(cacheKey, new TransportCache
            {
                Pos = pos.ToArray(), Nrm = nrm.ToArray(), Albedo = albedo.ToArray(), Openness = openness,
                Couplings = couplings, SunletCouplings = sunletCouplings,
                SunletPos = sunletPos.ToArray(), SunletNrm = sunletNrm.ToArray(), SunletAlbedo = sunletAlbedo.ToArray(),
                Sky = sky, SunVis = sunVis, Texels = texelBake,
            });

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
        // One ray's light read from the cooked representation (a hit's patch light + its sunlet; the sky on a miss), and
        // how far it went -- the near/far split below adds these up on either side of a distance R.
        (Vector3 C, float T) HitLight(Vector3 o, Vector3 d, Random rr)
        {
            if (scene.Closest(new Ray(o, d), 0f, float.PositiveInfinity, (uint)rr.Next()) is not { } h) return (SkyRadiance(d), float.PositiveInfinity);
            if (Cutout(h)) return (Vector3.Zero, h.T);
            var hn = HitNormal(h, d);
            var hp = o + d * h.T;
            var p = NearestOwn(hp, hn, h, false);
            var c = p >= 0 ? outgoing[p] : Vector3.Zero;
            if (useSunlets)
            {
                var key = ((int)MathF.Floor(hp.X / transportSunlet), (int)MathF.Floor(hp.Y / transportSunlet), (int)MathF.Floor(hp.Z / transportSunlet), OctBin(hn, 4));
                if (sunletIndex.TryGetValue(key, out var sid)) c += sunletOut[sid];
                else
                {
                    var nd = Vector3.Dot(hn, toSun);
                    if (nd > 0f && !scene.Any(new Ray(hp + hn * 1e-3f, toSun), 0f, float.PositiveInfinity, (uint)rr.Next())) c += Albedo(h.Instance, h.Triangle) * sunIrr * nd / MathF.PI;
                }
            }
            return (c, h.T);
        }
        // Directional patches: incident radiance projected to SH L2 per patch (fresh cosine rays from its centre, read
        // through the cooked light), so a point evaluates irradiance at ITS normal -- a patch's one irradiance is for
        // its own normal, and 60% of the hall's points are on curved or tilted surfaces where no nearby patch shares it.
        static float[] ShBasis(Vector3 d) => new[] { 0.282095f, 0.488603f * d.Y, 0.488603f * d.Z, 0.488603f * d.X,
            1.092548f * d.X * d.Y, 1.092548f * d.Y * d.Z, 0.315392f * (3f * d.Z * d.Z - 1f), 1.092548f * d.X * d.Z, 0.546274f * (d.X * d.X - d.Y * d.Y) };
        const int shRays = 1024;
        var shOf = new ConcurrentDictionary<int, Vector3[]>();
        Vector3[] ShOf(int j) => shOf.GetOrAdd(j, jj =>
        {
            var rr = new Random(1777 + jj);
            var c = new Vector3[9];
            for (var t = 0; t < shRays; t++)
            {
                var d = CosineHemisphereCpu(nrm[jj], rr);
                var cos = MathF.Max(Vector3.Dot(d, nrm[jj]), 1e-3f);
                var L = HitLight(pos[jj] + nrm[jj] * 0.01f, d, rr).C * (MathF.PI / (cos * shRays));
                var y = ShBasis(d);
                for (var q = 0; q < 9; q++) c[q] += L * y[q];
            }
            return c;
        });
        // Irradiance at normal n from the SH (clamped-cosine convolution: pi, 2pi/3, pi/4), L1 or L2.
        static Vector3 ShIrradiance(Vector3[] c, Vector3 n, bool l2)
        {
            var y = ShBasis(n);
            var e = c[0] * (MathF.PI * y[0]);
            for (var q = 1; q < 4; q++) e += c[q] * (2f * MathF.PI / 3f * y[q]);
            if (l2) for (var q = 4; q < 9; q++) e += c[q] * (MathF.PI / 4f * y[q]);
            return Vector3.Max(e, Vector3.Zero);
        }
        // Near/far split: R per arm; a patch's FAR light (rays beyond R from its centre) is what a cook would store, one
        // colour a patch per R. Cached across points, since neighbouring points share patches.
        float[] splitR = { 0f, 0.25f, 1.0f };
        // Far maps (the rethink after texels settled too slowly): per patch, the RADIANCE arriving from beyond R by
        // direction (octahedral bins), from its own rays; a point traces rays and takes, for each that runs past R,
        // its patch's map in that direction -- its own near occluders decide what gets through, the map what arrives.
        float[] mapR = { 0.5f, 1.0f };
        int[] mapRes = { 8, 16 };
        const int mapRays = 4096;
        var farMaps = new ConcurrentDictionary<int, Vector3[][]>();
        Vector3[][] FarMap(int j) => farMaps.GetOrAdd(j, jj =>
        {
            var rr = new Random(4049 + jj);
            var maps = new Vector3[mapR.Length * mapRes.Length][];
            var counts = new int[maps.Length][];
            var all = new Vector3[maps.Length]; var allCount = new int[maps.Length];
            for (var q = 0; q < maps.Length; q++) { var bins = mapRes[q % mapRes.Length] * mapRes[q % mapRes.Length]; maps[q] = new Vector3[bins]; counts[q] = new int[bins]; }
            for (var t = 0; t < mapRays; t++)
            {
                var d = CosineHemisphereCpu(nrm[jj], rr);
                var (c, len) = HitLight(pos[jj] + nrm[jj] * 0.01f, d, rr);
                for (var ri = 0; ri < mapR.Length; ri++)
                {
                    if (len < mapR[ri]) continue;
                    for (var ci = 0; ci < mapRes.Length; ci++)
                    {
                        var q = ri * mapRes.Length + ci;
                        var b = OctBin(d, mapRes[ci]);
                        maps[q][b] += c; counts[q][b]++; all[q] += c; allCount[q]++;
                    }
                }
            }
            for (var q = 0; q < maps.Length; q++)
            {
                var mean = allCount[q] > 0 ? all[q] / allCount[q] : Vector3.Zero;
                for (var b = 0; b < maps[q].Length; b++) maps[q][b] = counts[q][b] > 0 ? maps[q][b] / counts[q][b] : mean;
            }
            return maps;
        });
        const int farRays = 256;
        var farOf = new ConcurrentDictionary<int, Vector3[]>();
        Vector3[] FarOf(int j) => farOf.GetOrAdd(j, jj =>
        {
            var rr = new Random(977 + jj);
            var acc = new Vector3[splitR.Length];
            for (var t = 0; t < farRays; t++)
            {
                var (c, len) = HitLight(pos[jj] + nrm[jj] * 0.01f, CosineHemisphereCpu(nrm[jj], rr), rr);
                for (var q = 0; q < splitR.Length; q++) if (len >= splitR[q]) acc[q] += c;
            }
            for (var q = 0; q < splitR.Length; q++) acc[q] *= MathF.PI / farRays;
            return acc;
        });
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
            // The far part interpolated as interp is (facing patches near x, by closeness).
            var farX = new Vector3[splitR.Length]; var farW = 0f;
            var dNear = float.PositiveInfinity; var dPlane = float.PositiveInfinity;
            // Own chart only: the patches of the surface x is on (both reconstruction and the far part of the split).
            var hc = chartOf[hit.Instance][hit.Triangle];
            var ownSum = Vector3.Zero; var ownW = 0f; var ownFar = new Vector3[splitR.Length]; var dOwn = float.PositiveInfinity;
            for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
                foreach (var j in list)
                {
                    if (patchChart[j] != hc || Vector3.Dot(nrm[j], n) <= 0f) continue;
                    var d2 = Vector3.DistanceSquared(pos[j], x);
                    dOwn = MathF.Min(dOwn, MathF.Sqrt(d2));
                    var w = MathF.Max(MathF.Exp(-d2 / (transportSpacing * transportSpacing)), 1e-6f);
                    ownSum += incident[j] * w; ownW += w;
                    var f = FarOf(j);
                    for (var q = 0; q < splitR.Length; q++) ownFar[q] += f[q] * w;
                }
            }
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
                    dNear = MathF.Min(dNear, Vector3.Distance(pos[j], x));
                    if (facing > 0.95f && MathF.Abs(Vector3.Dot(n, pos[j] - x + n * 0.01f)) < 0.02f) dPlane = MathF.Min(dPlane, Vector3.Distance(pos[j], x));
                    if (w < 1e-3f) continue;
                    var f = FarOf(j);
                    for (var q = 0; q < splitR.Length; q++) farX[q] += f[q] * w;
                    farW += w;
                }
            }
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
            int[] counts = { 16, 64, 256 };
            var partial = new Vector3[counts.Length]; var near = new Vector3[splitR.Length]; var nearAt = new Vector3[counts.Length, splitR.Length];
            for (var j = 0; j < gatherRays; j++)
            {
                var d = CosineHemisphereCpu(n, r);
                var gBefore = g; var rayT = float.PositiveInfinity;
                if (scene.Closest(new Ray(x, d), 0f, float.PositiveInfinity, (uint)r.Next()) is { } h)
                {
                    rayT = h.T;
                    if (Cutout(h)) { RecordRay(j); continue; }
                    var hn = HitNormal(h, d);
                    var hp = x + d * h.T;
                    var p = NearestOwn(hp, hn, h, false);
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
                RecordRay(j);
                void RecordRay(int jj)
                {
                    var c = g - gBefore;
                    for (var q = 0; q < splitR.Length; q++) if (rayT < splitR[q]) near[q] += c;
                    for (var q = 0; q < counts.Length; q++) if (jj + 1 == counts[q]) { partial[q] = g; for (var u = 0; u < splitR.Length; u++) nearAt[q, u] = near[u]; }
                }
            }
            var gather = Lum(g * (MathF.PI / gatherRays));
            var gatherSun = Lum(gSun * (MathF.PI / gatherRays));
            var gatherSky = Lum(gSky * (MathF.PI / gatherRays));
            var gatherExact = Lum(gExact * (MathF.PI / gatherRays));
            // Ray-count sweep (the first N of the same rays) and the near/far split: near rays from x at full count and at
            // each N, plus the far part interpolated from the patches.
            var extra = new StringBuilder();
            // Occlusion ratio (no light rays): the patches' interpolated light, scaled by how open x is within R
            // against how open its patches are -- cosine rays that meet nothing within R. Local occluders are what
            // patches cannot know (the swap test put the error on position); openness is a 0/1 average, quick to settle.
            {
                float[] aoR = { 0.25f, 0.5f, 1.0f };
                float Open(Vector3 o, Vector3 nn, float reach, int rays, Random rr)
                {
                    var free = 0;
                    for (var t = 0; t < rays; t++)
                        if (!scene.Any(new Ray(o, CosineHemisphereCpu(nn, rr)), 0f, reach, (uint)rr.Next())) free++;
                    return free / (float)rays;
                }
                var rx = new Random(919 + k);
                foreach (var aoReach in aoR)
                {
                    // The patches' weighted openness, with the same weights as interp.
                    var wOpen = 0f; var wSum = 0f;
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
                            if (w < 1e-3f) continue;
                            wOpen += w * Open(pos[j] + nrm[j] * 0.01f, nrm[j], aoReach, 256, new Random(511 + j));
                            wSum += w;
                        }
                    }
                    var patchOpen = wSum > 0f ? wOpen / wSum : 0f;
                    foreach (var rays in new[] { 64, 1024 })
                    {
                        var open = Open(x, n, aoReach, rays, rx);
                        var ratio = patchOpen > 1e-3f ? open / patchOpen : 1f;
                        extra.Append(CultureInfo.InvariantCulture, $" ao{(int)MathF.Round(aoReach * 100)}n{rays} {(double.IsNaN(interp) ? double.NaN : interp * ratio):0.00000}");
                    }
                }
            }
            {
                var jm = Nearest(x, n);
                if (jm >= 0)
                {
                    var maps = FarMap(jm);
                    var rr = new Random(733 + k);
                    int[] ns = { 32, 128, 1024 };
                    var acc = new Vector3[maps.Length];
                    for (var t = 0; t < 1024; t++)
                    {
                        var d = CosineHemisphereCpu(n, rr);
                        var (c, len) = HitLight(x, d, rr);
                        for (var ri = 0; ri < mapR.Length; ri++)
                        for (var ci = 0; ci < mapRes.Length; ci++)
                        {
                            var q = ri * mapRes.Length + ci;
                            acc[q] += len < mapR[ri] ? c : maps[q][OctBin(d, mapRes[ci])];
                        }
                        if (Array.IndexOf(ns, t + 1) is var at && at >= 0)
                            for (var ri = 0; ri < mapR.Length; ri++)
                            for (var ci = 0; ci < mapRes.Length; ci++)
                            {
                                var q = ri * mapRes.Length + ci;
                                extra.Append(CultureInfo.InvariantCulture, $" far{(int)MathF.Round(mapR[ri] * 100)}r{mapRes[ci]}n{t + 1} {Lum(acc[q] * (MathF.PI / (t + 1))):0.00000}");
                            }
                    }
                }
            }
            // Fine texels, emulated: the gather from x snapped to a grid of S metres (the cell's centre projected onto x's
            // plane) -- how fine must a surface cache be that reads the cooked light at its own position and normal?
            foreach (var sz in new[] { 0.025f, 0.05f, 0.10f, 0.20f })
            {
                var centre = new Vector3(MathF.Floor(x.X / sz) + 0.5f, MathF.Floor(x.Y / sz) + 0.5f, MathF.Floor(x.Z / sz) + 0.5f) * sz;
                var xs = centre - n * Vector3.Dot(n, centre - x);
                var rr = new Random(53 + k);
                var acc = Vector3.Zero;
                const int texelRays = 1024;
                for (var t = 0; t < texelRays; t++) acc += HitLight(xs, CosineHemisphereCpu(n, rr), rr).C;
                extra.Append(CultureInfo.InvariantCulture, $" texel{(int)MathF.Round(sz * 1000)} {Lum(acc * (MathF.PI / texelRays)):0.00000}");
            }
            // SH patches interpolated as interp is, each evaluated at x's normal; and the nearest one alone.
            {
                Vector3 s1 = Vector3.Zero, s2 = Vector3.Zero; var sw = 0f;
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
                        if (w < 1e-3f) continue;
                        var c = ShOf(j);
                        s1 += ShIrradiance(c, n, false) * w; s2 += ShIrradiance(c, n, true) * w; sw += w;
                    }
                }
                var jn = Nearest(x, n);
                extra.Append(CultureInfo.InvariantCulture, $" shl1 {(sw > 0f ? Lum(s1 / sw) : double.NaN):0.00000} shl2 {(sw > 0f ? Lum(s2 / sw) : double.NaN):0.00000}");
                extra.Append(CultureInfo.InvariantCulture, $" nearl1 {(jn >= 0 ? Lum(ShIrradiance(ShOf(jn), n, false)) : double.NaN):0.00000} nearl2 {(jn >= 0 ? Lum(ShIrradiance(ShOf(jn), n, true)) : double.NaN):0.00000}");
            }
            // Position or normal? The nearest facing patch's light traced fresh from ITS position with x's normal, and
            // from x with ITS normal -- whichever of the two keeps the error is what the reconstruction misses.
            {
                var jn = Nearest(x, n);
                var swapPos = double.NaN; var swapNrm = double.NaN; var angle = double.NaN; var dist = double.NaN;
                if (jn >= 0)
                {
                    var rr = new Random(31 + k);
                    Vector3 a1 = Vector3.Zero, a2 = Vector3.Zero;
                    const int swapRays = 1024;
                    for (var t = 0; t < swapRays; t++)
                    {
                        a1 += HitLight(pos[jn] + n * 0.01f, CosineHemisphereCpu(n, rr), rr).C;
                        a2 += HitLight(x, CosineHemisphereCpu(nrm[jn], rr), rr).C;
                    }
                    swapPos = Lum(a1 * (MathF.PI / swapRays)); swapNrm = Lum(a2 * (MathF.PI / swapRays));
                    angle = MathF.Acos(Math.Clamp(Vector3.Dot(nrm[jn], n), -1f, 1f)) * 180f / MathF.PI;
                    dist = Vector3.Distance(pos[jn], x);
                }
                extra.Append(CultureInfo.InvariantCulture, $" atpatchpos {swapPos:0.00000} patchnormal {swapNrm:0.00000} nearestinc {(jn >= 0 ? Lum(incident[jn]) : double.NaN):0.00000} nangle {angle:0.0} ndist {dist:0.000}");
            }
            extra.Append(CultureInfo.InvariantCulture, $" dnear {(float.IsFinite(dNear) ? dNear : 9f):0.000} dplane {(float.IsFinite(dPlane) ? dPlane : 9f):0.000} wsum {wsum:0.0000} down {(float.IsFinite(dOwn) ? dOwn : 9f):0.000}");
            extra.Append(CultureInfo.InvariantCulture, $" own {(ownW > 0f ? Lum(ownSum / ownW) : double.NaN):0.00000}");
            for (var u = 0; u < splitR.Length; u++)
            {
                var tag = (int)MathF.Round(splitR[u] * 100);
                extra.Append(CultureInfo.InvariantCulture, $" ownsplit{tag} {(ownW > 0f ? Lum(near[u] * (MathF.PI / gatherRays) + ownFar[u] / ownW) : double.NaN):0.00000}");
                if (gatherRays >= 16) extra.Append(CultureInfo.InvariantCulture, $" ownsplit{tag}n16 {(ownW > 0f ? Lum(nearAt[0, u] * (MathF.PI / 16) + ownFar[u] / ownW) : double.NaN):0.00000}");
            }
            for (var q = 0; q < counts.Length; q++)
                if (counts[q] <= gatherRays) extra.Append(CultureInfo.InvariantCulture, $" g{counts[q]} {Lum(partial[q] * (MathF.PI / counts[q])):0.00000}");
            for (var u = 0; u < splitR.Length; u++)
            {
                var farPart = farW > 0f ? farX[u] / farW : Vector3.Zero;
                var tag = (int)MathF.Round(splitR[u] * 100);
                extra.Append(CultureInfo.InvariantCulture, $" split{tag} {(farW > 0f ? Lum(near[u] * (MathF.PI / gatherRays) + farPart) : double.NaN):0.00000}");
                for (var q = 0; q < counts.Length; q++)
                    if (counts[q] <= gatherRays && farW > 0f) extra.Append(CultureInfo.InvariantCulture, $" split{tag}n{counts[q]} {Lum(nearAt[q, u] * (MathF.PI / counts[q]) + farPart):0.00000}");
            }
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"    transport at {px / iw:0.0000},{py / ih:0.0000} interp {interp:0.00000} gather {gather:0.00000} plane {plane:0.00000} inverse {inverse:0.00000} linear {mls:0.00000} cutout {(Cutout(hit) ? 1 : 0)} gsun {gatherSun:0.00000} gsky {gatherSky:0.00000} gexact {gatherExact:0.00000} g2cm {Lum(gSnap[0] * (MathF.PI / gatherRays)):0.00000} g5cm {Lum(gSnap[1] * (MathF.PI / gatherRays)):0.00000} g10cm {Lum(gSnap[2] * (MathF.PI / gatherRays)):0.00000}{extra}"));
        });
        foreach (var line in lines.OrderBy(l => l, StringComparer.Ordinal)) Console.WriteLine(line);
        };
    }
}
