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
    // --transport-candidates N: how many of a hit's nearest patches NearestVisible tries (8). Rays that found none,
    // cornell / crevices: 0.36% / 0.41% at 4, 0.19 / 0.23 at 8, 0.09 / 0.14 at 16, 0.02 / 0.07 at 64 -- most of them
    // inside a slab, their light not real. Sponza's cook: 363 s at 4, 915 s at 16 on the CPU (2026-10-13). On Metal, with
    // the fill, 8 against 4: 74 s against 78 s (fewer fill patches), visible wrong-facing hits 0.72% against 1.02%, visible
    // misses 0.12% against 0.22% (2026-10-13).
    private int transportCandidates = 8;
    // --transport-vis-lift M: how far off the surfaces NearestVisible's ray runs, at the hit and at the patch (0.02 m). At
    // 1 mm it grazed the surface: Sponza's rays with no patch 7.99% at 1 mm, 6.14% at 2 cm (4 candidates). It passes over
    // obstacles under 2 cm, far below the patches' spacing.
    private float transportVisLift = 0.02f;
    // --transport-own-fallback (SponzaLoop.Transport.cs, OwnFallback), and how near its patch must be.
    private bool transportOwnFallback;
    // --transport-seed N (0): the couplings' rays drawn from another sequence -- the cook's noise measured by recooking
    // the same scene (0: the seeds as always; another, its own couplings cache).
    private int transportSeed;
    private int CouplingSeed(int patch) => 1000 + patch + transportSeed * 7_368_787;
    // --transport-miss-why's accounting of how hits found their patch (NearestVisibleOr, OwnFallback): the rank seen,
    // HowAnyFacing, HowFallback or HowMiss; and the time spent past BaseCandidates and in the fallback.
    private const int BaseCandidates = 4, HowAnyFacing = 64, HowFallback = 65, HowMiss = 66;
    // The lifts EitherWhy tries the line to the nearest facing patch at.
    private static readonly float[] Lifts = { 0.002f, 0.005f, 0.01f, 0.05f };
    [ThreadStatic] private static int nearestHow;
    private readonly long[] nearestHows = new long[67];
    private long lateRankTicks, fallbackTicks;
    private const float OwnFallbackReach = 0.5f;
    // --transport-facing-cos C: a patch counts as facing a hit's way down to this cosine between their normals (0.5, 60
    // degrees). --transport-facing-weight W (0): candidates ranked by distance with a patch turned away counting
    // farther, by W spacings at 90 degrees. Tried at 0.26 (75 degrees) and 1 for the 12.8% of Sponza's front-face hits
    // that read a patch facing another way: 6.49M -> 6.25M, and the cook 838 -> 942 s. 93% of them had a patch within
    // 60 degrees and 0.5 m that none of their candidates' lines reached: not curvature (2026-10-13).
    private float transportFacingCos = 0.5f, transportFacingWeight = 0f;
    // --transport-far-cutoff M: a hit whose nearest facing patch is farther tries only BaseCandidates of them (1 m).
    private float transportFarCutoff = 1f;
    // --transport-fill [--transport-fill-rays N]: patches added where hits find none facing their way (FillPatches).
    private bool transportFill;
    private int transportFillRays = 32, transportFillPasses = 2;
    // --transport-cpu-sunvis: the sun visibility bins on the CPU, not Metal; --transport-sunvis-check: both, compared.
    private bool transportCpuSunVis, transportSunVisCheck;
    // --transport-cpu-couplings: the couplings' rays on the CPU, not Metal (CouplingsOnMetal); its blocks of patches,
    // and how many run side by side (one waits on Metal while another runs on the CPU).
    private bool transportCpuCouplings;
    private const int CouplingBlock = 2048, CouplingWorkers = 2;

    // A Metal couplings worker's block arrays, made once and reused block after block.
    private sealed class CouplingWorker
    {
        public readonly Vector4[] Rays, Vis;
        public readonly Vector3[] Dirs, Hp, Hn;
        public readonly int[] State, Inst, How, Cand, AnyCand, NearestOf, SunletOf, TriOf, SunOrder, CandOut, VisIds, VisTargets;
        public readonly bool[] Front, VisNone;
        public readonly byte[] Tried, AnyTried;
        public readonly ulong[] SunKeys;
        public readonly uint[] TriedOut, Hits, Answers;

        public CouplingWorker(int maxTotal, int c)
        {
            Rays = new Vector4[maxTotal * 2]; Vis = new Vector4[maxTotal * 2];
            Dirs = new Vector3[maxTotal]; Hp = new Vector3[maxTotal]; Hn = new Vector3[maxTotal];
            State = new int[maxTotal]; Inst = new int[maxTotal]; How = new int[maxTotal];
            Cand = new int[maxTotal * c]; AnyCand = new int[maxTotal * c]; NearestOf = new int[maxTotal];
            SunletOf = new int[maxTotal]; TriOf = new int[maxTotal]; SunOrder = new int[maxTotal]; CandOut = new int[maxTotal * c];
            VisIds = new int[maxTotal]; VisTargets = new int[maxTotal];
            Front = new bool[maxTotal]; VisNone = new bool[maxTotal];
            Tried = new byte[maxTotal]; AnyTried = new byte[maxTotal];
            SunKeys = new ulong[maxTotal];
            TriedOut = new uint[maxTotal * 2]; Hits = new uint[maxTotal * 12]; Answers = new uint[maxTotal * 12];
        }
    }

    // A patch's couplings from its rays' targets: each distinct target and its share of the rays.
    private static (int To, float W)[] Runs(Span<int> targets, int rays)
    {
        if (targets.Length == 0) return Array.Empty<(int, float)>();
        targets.Sort();
        var distinct = 1;
        for (var k = 1; k < targets.Length; k++) if (targets[k] != targets[k - 1]) distinct++;
        var result = new (int To, float W)[distinct];
        int at = 0, run = 1;
        for (var k = 1; k <= targets.Length; k++)
        {
            if (k < targets.Length && targets[k] == targets[k - 1]) { run++; continue; }
            result[at++] = (targets[k - 1], run / (float)rays);
            run = 1;
        }
        return result;
    }
    private const int SunVisCheckPatches = 4096;
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
        if (args.Int("transport-candidates") is { } tc) transportCandidates = Math.Clamp(tc, 1, 64);
        // The cook's diagnostics are on unless asked off: what they cost is worth paying while its rules are settled.
        transportMissWhy = !args.Flag("no-transport-miss-why");
        transportOwnFallback = args.Flag("transport-own-fallback");
        transportSeed = args.Int("transport-seed") ?? 0;
        if (args.Float("transport-far-cutoff") is { } fc) transportFarCutoff = Math.Max(0f, fc);
        transportFill = args.Flag("transport-fill");
        if (args.Int("transport-fill-rays") is { } fr) transportFillRays = Math.Clamp(fr, 1, 4096);
        if (args.Int("transport-fill-passes") is { } fp) transportFillPasses = Math.Clamp(fp, 1, 8);
        transportCpuSunVis = args.Flag("transport-cpu-sunvis");
        transportSunVisCheck = args.Flag("transport-sunvis-check");
        transportCpuCouplings = args.Flag("transport-cpu-couplings");
        cookCpuTests = args.Flag("cook-cpu-tests");
        cookScreenOff = args.Flag("no-cook-screen");
        if (args.Float("transport-facing-cos") is { } fcos) transportFacingCos = Math.Clamp(fcos, -1f, 1f);
        if (args.Float("transport-facing-weight") is { } fw) transportFacingWeight = Math.Max(0f, fw);
        if (args.Float("transport-vis-lift") is { } vl) transportVisLift = Math.Clamp(vl, 0.0005f, 0.2f);
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
            LogMemoryInventory("the transport cook landed");
        }
        if (!transportGpu || transportCooked || !fullyLoaded || !RaySurfacesReady || postLoadFrames < 2) return;
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
        var stageKeys = TransportCacheKeys(scene, surfaces);
        var cacheKey = stageKeys.Combined;
        DecideBakeScene(cacheKey);
        // Each stage from its own cache; the couplings and the sun visibility only over the patches they were made for.
        var patchStage = LoadPatches(stageKeys.Patches);
        var couplingStage = patchStage is null ? null : LoadCouplings(stageKeys.Couplings);
        var sunVisStage = patchStage is null ? null : LoadSunVis(stageKeys.SunVis);
        var texelStage = LoadTexels(stageKeys.Texels);

        // ---- R1: patches ------------------------------------------------------------------------------------------
        var patchArea = transportSpacing * transportSpacing;
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var albedo = new List<Vector3>();
        if (patchStage is null)
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
            // On Metal when it is there (SponzaLoop.Contacts.cs, ContactTraceAll), else the CPU.
            var backs = new int[pos.Count];
            if (ContactTraceAll(scene, pos.Count * 16L, r =>
                {
                    var i = (int)(r / 16);
                    return (pos[i] + nrm[i] * 1e-3f, CosineHashed(nrm[i], RayHash(i, (int)(r % 16), 77u)), float.PositiveInfinity);
                }, (r, kind, _) => { if (kind == 2) Interlocked.Increment(ref backs[(int)(r / 16)]); }, 77u, "transport: patches, inside a solid? (Metal)"))
                for (var i = 0; i < pos.Count; i++) keep[i] = backs[i] * 2 < 16;
            else
            Parallel.For(0, pos.Count, i =>
            {
                var r = new Random(77 + i);
                var back = 0;
                const int tests = 16;
                for (var k = 0; k < tests; k++)
                {
                    var d = CosineHemisphereCpu(nrm[i], r);
                    if (ClosestThroughContacts(scene, new Ray(pos[i] + nrm[i] * 1e-3f, d), 0f, float.PositiveInfinity, (uint)r.Next()) is { } h && !h.FrontFace) back++;
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
            pos = patchStage.Pos.ToList(); nrm = patchStage.Nrm.ToList(); albedo = patchStage.Albedo.ToList();
        }
        var patches = pos.Count;
        // A hash grid over the patches: a ray's hit finds the nearest patch facing the way the hit surface faces.
        // Cells twice the spacing, so the 3x3x3 search reaches past any gap between neighbouring patches.
        var cell = 2f * transportSpacing;
        var grid = new Dictionary<(int, int, int), List<int>>();
        // The dense form of the grid (BuildDenseGrid), for the Metal path's candidate search.
        Vector3[] densePos = Array.Empty<Vector3>(), denseNrm = Array.Empty<Vector3>();
        int[] denseStart = Array.Empty<int>(), denseItems = Array.Empty<int>();
        int denseX = 0, denseY = 0, denseZ = 0, denseMinX = 0, denseMinY = 0, denseMinZ = 0;
        (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));
        for (var i = 0; i < patches; i++)
        {
            var key = Key(pos[i]);
            if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
            list.Add(i);
        }
        // The nearest patch a hit can SEE: the patches around it (the 27 cells), facing its way (or, with none, either way:
        // dropping the ray dropped its light, energy -4%), nearest first, each taken only when a ray from the hit reaches
        // it. Nearest by distance alone handed a hit at the foot of a wall the patch across it -- floors either side of a
        // wall are one plane: the thin wall's sealed room read the open room's sunlit floor at its corners (the truth
        // there is black), Sponza's atrium under the lavabo the courtyard's. None visible: none. Only the
        // transportCandidates nearest are tried: every patch in the 27 cells, sorted and each tried, took Sponza's cook
        // past 20 minutes (217 s before visibility) where none was visible.
        int NearestVisible(Vector3 p, Vector3 n, uint seed, bool anyFacing) => NearestVisibleOr(p, n, seed, anyFacing, out _);
        // As NearestVisible; nearest: the nearest candidate, seen or not (-1: none), for OwnFallback.
        var facingPenalty = transportSpacing * transportFacingWeight;
        int NearestVisibleOr(Vector3 p, Vector3 n, uint seed, bool anyFacing, out int nearest)
        {
            var (kx, ky, kz) = Key(p);
            var candidates = transportCandidates;
            Span<int> best = stackalloc int[candidates];
            Span<float> bestD = stackalloc float[candidates];
            best.Fill(-1);
            bestD.Fill(float.MaxValue);
            for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var list)) continue;
                foreach (var j in list)
                {
                    var facing = Vector3.Dot(nrm[j], n);
                    if (!anyFacing && facing < transportFacingCos) continue;
                    // Ranked by distance and angle: a patch turned away counts as farther, up to the spacing times
                    // the weight at 90 degrees (anyFacing: by distance alone).
                    var turn = anyFacing ? 0f : facingPenalty * (1f - facing);
                    var d2 = Vector3.DistanceSquared(pos[j], p) + turn * turn;
                    if (d2 >= bestD[candidates - 1]) continue;
                    var at = candidates - 1;
                    while (at > 0 && bestD[at - 1] > d2) { bestD[at] = bestD[at - 1]; best[at] = best[at - 1]; at--; }
                    bestD[at] = d2;
                    best[at] = j;
                }
            }
            var firstDistance2 = best[0] >= 0 ? Vector3.DistanceSquared(pos[best[0]], p) : float.MaxValue;
            nearest = firstDistance2 <= OwnFallbackReach * OwnFallbackReach ? best[0] : -1;
            // Nearest over transportFarCutoff away: likely a hidden cavity, where every candidate fails; past the first
            // BaseCandidates they cost most and find least there (Sponza, 8 candidates: 580 of a 956 s cook).
            var tried = firstDistance2 > transportFarCutoff * transportFarCutoff ? Math.Min(candidates, BaseCandidates) : candidates;
            var origin = p + n * transportVisLift;
            for (var rank = 0; rank < tried; rank++)
            {
                var j = best[rank];
                if (j < 0) break;
                var target = pos[j] + nrm[j] * transportVisLift;
                var d = target - origin;
                var length = d.Length();
                if (length < 1e-4f) { nearestHow = rank; return j; }
                // The rays past the BaseCandidates nearest, timed: what more candidates cost (--transport-miss-why).
                var late = transportMissWhy && rank >= BaseCandidates ? Stopwatch.GetTimestamp() : 0L;
                var seen = !scene.Any(new Ray(origin, d / length), 0f, length * 0.999f, seed);
                if (late != 0) Interlocked.Add(ref lateRankTicks, Stopwatch.GetTimestamp() - late);
                if (seen) { nearestHow = rank; return j; }
            }
            nearestHow = HowMiss;
            return -1;
        }
        // --transport-own-fallback: none visible, the nearest patch facing the hit's way within OwnFallbackReach, when
        // what blocks the line to it is the hit's own placement -- a door's carving, a curtain's fold: light from the
        // same piece a few centimetres on, which cannot have come through another piece's wall.
        // One closest-hit ray to the nearest candidate the search already found (within OwnFallbackReach).
        int OwnFallback(Vector3 p, Vector3 n, int own, int nearest, uint seed)
        {
            if (!transportOwnFallback || own < 0 || nearest < 0) return -1;
            var started = transportMissWhy ? Stopwatch.GetTimestamp() : 0L;
            var origin = p + n * transportVisLift;
            var to = pos[nearest] + nrm[nearest] * transportVisLift - origin;
            var length = to.Length();
            var taken = length < 1e-4f
                || scene.Closest(new Ray(origin, to / length), 0f, length * 0.999f, seed) is { } blocker && blocker.Instance == own;
            if (started != 0) Interlocked.Add(ref fallbackTicks, Stopwatch.GetTimestamp() - started);
            if (!taken) return -1;
            nearestHow = HowFallback;
            return nearest;
        }
        int Nearest(Vector3 p, Vector3 n, int own, uint seed = 0)
        {
            var j = NearestVisibleOr(p, n, seed, anyFacing: false, out var nearest);
            return j >= 0 ? j : OwnFallback(p, n, own, nearest, seed);
        }
        int NearestAny(Vector3 p, Vector3 n, int own, uint seed = 0)
        {
            var j = NearestVisibleOr(p, n, seed, anyFacing: false, out var nearest);
            if (j < 0)
            {
                j = NearestVisible(p, n, seed, anyFacing: true);
                if (j >= 0) nearestHow = HowAnyFacing;
            }
            return j >= 0 ? j : OwnFallback(p, n, own, nearest, seed);
        }
        Vector3 HitNormal(RayHit h, Vector3 dir)
        {
            var (a, b, c) = Tri(h.Instance, h.Triangle);
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            return Vector3.Dot(n, dir) > 0f ? -n : n;
        }
        for (var pass = 1; transportFill && patchStage is null && pass <= transportFillPasses; pass++)
            if (FillPatches(pass) == 0) break;
        var cookPatches = clock.Elapsed.TotalSeconds;

        // --transport-fill: patches where hits find none facing their way that they can see. A discovery pass of
        // transportFillRays rays a patch (the couplings take transportRays) finds front-face hits whose facing pass
        // failed -- the back of a curtain whose facing patches lie past its folds, a door's carving -- one a cell of
        // the spacing and coarse normal bin; each becomes a patch there (its hit's position, normal and albedo) when
        // open air sees it (a hidden cavity gets none: SponzaLoop.TransportDiag.cs) and it is not inside a solid (the
        // patches' rule). The couplings then run with them: hits there find a patch facing their way.
        // In passes (--transport-fill-passes, 2): one patch a cell can sit on the near side of a curtain's fold or a
        // carving's lip, and the hits past it still see none; the next pass's discovery finds them and adds theirs.
        // Returns the patches added.
        int FillPatches(int pass)
        {
            var fillClock = Stopwatch.StartNew();
            var found = new ConcurrentDictionary<(int, int, int, int), (Vector3 P, Vector3 N, Vector3 A, int Instance)>();
            var stage = $"transport: fill pass {pass}";
            ProgressBegin(stage + ", discovery", patches);
            Parallel.For(0, patches, i =>
            {
                ProgressAdd(stage + ", discovery");
                var r = new Random(5000 + i + pass * 7919);
                var origin = pos[i] + nrm[i] * 1e-3f;
                for (var k = 0; k < transportFillRays; k++)
                {
                    var d = CosineHemisphereCpu(nrm[i], r);
                    if (scene.Closest(new Ray(origin, d), 0f, float.PositiveInfinity, (uint)r.Next()) is not { FrontFace: true } h) continue;
                    if ((surfaces[data.RowOf(h.Instance, h.Triangle)] >> 24) < 255u) continue;
                    var hp = origin + d * h.T;
                    var hn = HitNormal(h, d);
                    if (NearestVisibleOr(hp, hn, (uint)r.Next(), anyFacing: false, out _) >= 0) continue;
                    var key = ((int)MathF.Floor(hp.X / transportSpacing), (int)MathF.Floor(hp.Y / transportSpacing), (int)MathF.Floor(hp.Z / transportSpacing), OctBin(hn, 4));
                    found.TryAdd(key, (hp, hn, Albedo(h.Instance, h.Triangle), h.Instance));
                }
            });
            ProgressEnd(stage + ", discovery");
            if (diagViews is null) BuildDiagViews(scene, pos, nrm);
            var spots = found.Values.ToArray();
            var take = new bool[spots.Length];
            int hidden = 0, inside = 0;
            ProgressBegin(stage + ", spots", spots.Length);
            Parallel.For(0, spots.Length, s =>
            {
                ProgressAdd(stage + ", spots");
                var (sp, sn, _, _) = spots[s];
                if (!Exposed(scene, sp, sn, (uint)s)) { Interlocked.Increment(ref hidden); return; }
                var r = new Random(77 + s);
                var back = 0;
                const int tests = 16;
                // A double-sided sheet's back is the far side of a curtain, not the inside of a solid (the lightmap's
                // rule): a spot behind a fold is not buried by it.
                for (var k = 0; k < tests; k++)
                    if (ClosestThroughContacts(scene, new Ray(sp + sn * 1e-3f, CosineHemisphereCpu(sn, r)), 0f, float.PositiveInfinity, (uint)r.Next()) is { FrontFace: false } b
                        && !rayPlacementDoubleSided[b.Instance])
                        back++;
                if (back * 2 >= tests) { Interlocked.Increment(ref inside); return; }
                take[s] = true;
            });
            ProgressEnd(stage + ", spots");
            var byName = new Dictionary<string, int>();
            for (var s = 0; s < spots.Length; s++)
            {
                if (!take[s]) continue;
                var (sp, sn, sa, inst) = spots[s];
                var at = pos.Count;
                pos.Add(sp); nrm.Add(sn); albedo.Add(sa);
                var key = Key(sp);
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
                list.Add(at);
                var name = rayPlacementNames[inst];
                byName[name] = byName.GetValueOrDefault(name) + 1;
            }
            var added = pos.Count - patches;
            patches = pos.Count;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport fill, pass {pass}: {spots.Length:N0} spots where hits found no patch facing their way ({transportFillRays} rays a patch); {added:N0} patches added, {hidden:N0} hidden from open air, {inside:N0} inside a solid; {fillClock.Elapsed.TotalSeconds:0.0} s."));
            foreach (var (name, c) in byName.OrderByDescending(x => x.Value).Take(12))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {c,7:N0}  {name}"));
            var families = byName.GroupBy(x => x.Key.Split('_')[0]).Select(g => (g.Key, g.Sum(x => x.Value))).OrderByDescending(x => x.Item2).Take(10);
            Console.WriteLine("    by family: " + string.Join(", ", families.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Key} {f.Item2:N0}"))));
            return added;
        }

        // ---- R2 couplings + R3 sky bins + R3 sun visibility -------------------------------------------------------
        var couplings = new (int To, float W)[patches][];
        var sky = new float[patches * TransportSkyRes * TransportSkyRes];
        var visWords = (transportVisRes * transportVisRes + 63) / 64;
        var sunVis = new ulong[patches * visWords];
        long dropped = 0;
        // Of those, the hits on a back face: rays travelling inside a solid (from a patch at a joint between two), whose
        // light is not real anyway -- nearly all of the Cornell box's 0.36% at 4 candidates, against a slab's far side.
        long droppedInside = 0;
        // --transport-miss-why: every 16th ray's front-face miss, sorted by why after the cook (MissWhy).
        var missSamples = transportMissWhy ? new ConcurrentBag<(Vector3 P, Vector3 N, int Instance, int From)>() : null;
        var recoveredSamples = transportMissWhy ? new ConcurrentBag<(Vector3 P, Vector3 N, int How)>() : null;
        // Every 16th front-face hit that read a patch facing another way, for why (EitherWhy).
        var eitherSamples = transportMissWhy ? new ConcurrentBag<(Vector3 P, Vector3 N, int Instance)>() : null;
        Array.Clear(nearestHows);
        lateRankTicks = fallbackTicks = 0;
        long foliageHits = 0;
        // Sunlets: where cooked rays hit architecture, a cell of transportSunlet metres AND a coarse normal bin (so a
        // corner's two walls, or a sheet's two sides, never share one) -- position, normal and albedo of its first hit.
        // A patch's first bounce of direct light is read through them; everything after through the patch couplings.
        var useSunlets = transportSunlet > 0f;
        var sunletPos = new List<Vector3>(); var sunletNrm = new List<Vector3>(); var sunletAlbedo = new List<Vector3>();
        var sunletCouplings = new (int To, float W)[patches][];
        // Made in SunletShards shards, each its own index, lists and lock (by a hash of the sunlet's key): one table
        // behind one lock serialized the couplings -- every hit asks for its sunlet, 7.7M of Sponza's are new, and the
        // profile had the cook's threads waiting on that lock (38%) or idle (44%), the candidate search 6% (2026-10-13).
        // A sunlet's id while the couplings are made is its index in its shard times SunletShards plus its shard;
        // FinalizeSunlets lays the shards end to end and renumbers the couplings to them.
        const int SunletShards = 64;
        // Lookups take no lock (most hits find their sunlet made): neighbouring patches' rays land together, and a
        // lock on every lookup still had threads queued on the same few shards (38% of their time, 2026-10-13).
        var sunletShardIndex = new ConcurrentDictionary<ulong, int>[SunletShards];
        var sunletShardData = new List<(Vector3 P, Vector3 N, Vector3 A)>[SunletShards];
        for (var sh = 0; sh < SunletShards; sh++) { sunletShardIndex[sh] = new(); sunletShardData[sh] = new(); }
        // A sunlet's key, packed: its cell (20 bits an axis, offset) and normal bin (4 bits); its shard from the key.
        ulong SunletKey(Vector3 hp, Vector3 hn)
        {
            ulong Axis(float v) => (ulong)((long)MathF.Floor(v / transportSunlet) + (1L << 19)) & 0xFFFFFUL;
            return Axis(hp.X) | (Axis(hp.Y) << 20) | (Axis(hp.Z) << 40) | ((ulong)OctBin(hn, 4) << 60);
        }
        static int SunletShard(ulong key) => (int)((key * 0x9E3779B97F4A7C15UL) >> 58);
        int SunletOf(Vector3 hp, Vector3 hn, Vector3 alb)
        {
            var key = SunletKey(hp, hn);
            var shard = SunletShard(key);
            var index = sunletShardIndex[shard];
            if (index.TryGetValue(key, out var found)) return found;
            var data = sunletShardData[shard];
            lock (data)
            {
                if (index.TryGetValue(key, out found)) return found;
                var id = data.Count * SunletShards + shard;
                data.Add((hp, hn, alb));
                index[key] = id;
                return id;
            }
        }
        void FinalizeSunlets()
        {
            var offsets = new int[SunletShards + 1];
            for (var sh = 0; sh < SunletShards; sh++) offsets[sh + 1] = offsets[sh] + sunletShardData[sh].Count;
            for (var sh = 0; sh < SunletShards; sh++)
                foreach (var (sp, sn, sa) in sunletShardData[sh]) { sunletPos.Add(sp); sunletNrm.Add(sn); sunletAlbedo.Add(sa); }
            Parallel.For(0, sunletCouplings.Length, i =>
            {
                var row = sunletCouplings[i];
                if (row is null) return;
                for (var k = 0; k < row.Length; k++)
                {
                    var id = row[k].To;
                    row[k] = (offsets[id % SunletShards] + id / SunletShards, row[k].W);
                }
            });
            for (var sh = 0; sh < SunletShards; sh++) { sunletShardIndex[sh] = new(); sunletShardData[sh] = new(); }
        }
        // A block's sunlets at once (the Metal path): its hits' keys partitioned by shard in parallel, then each shard's
        // hits by one task alone against its own table -- no locks, each task's lookups in one small table.
        void AssignSunlets(int total, ulong[] keys, int[] instOf, int[] triOf, Vector3[] hpOf, Vector3[] hnOf, int[] stateOf, int[] into,
            int[] order, int pendingState)
        {
            var ranges = Environment.ProcessorCount * 4;
            var span = (total + ranges - 1) / ranges;
            var counts = new int[ranges * SunletShards];
            Parallel.For(0, ranges, g =>
            {
                for (var at = g * span; at < Math.Min(total, (g + 1) * span); at++)
                    if (stateOf[at] == pendingState) counts[g * SunletShards + SunletShard(keys[at])]++;
            });
            var offsets = new int[ranges * SunletShards];
            var shardStart = new int[SunletShards + 1];
            var sum = 0;
            for (var sh = 0; sh < SunletShards; sh++)
            {
                shardStart[sh] = sum;
                for (var g = 0; g < ranges; g++) { offsets[g * SunletShards + sh] = sum; sum += counts[g * SunletShards + sh]; }
            }
            shardStart[SunletShards] = sum;
            Parallel.For(0, ranges, g =>
            {
                for (var at = g * span; at < Math.Min(total, (g + 1) * span); at++)
                    if (stateOf[at] == pendingState) order[offsets[g * SunletShards + SunletShard(keys[at])]++] = at;
            });
            Parallel.For(0, SunletShards, sh =>
            {
                var index = sunletShardIndex[sh];
                var data = sunletShardData[sh];
                lock (data)   // the other couplings worker may be in this shard too
                for (var q = shardStart[sh]; q < shardStart[sh + 1]; q++)
                {
                    var at = order[q];
                    if (!index.TryGetValue(keys[at], out var id))
                    {
                        id = data.Count * SunletShards + sh;
                        data.Add((hpOf[at], hnOf[at], Albedo(instOf[at], triOf[at])));
                        index[keys[at]] = id;
                    }
                    into[at] = id;
                }
            });
        }
        bool Cutout(RayHit h) => (surfaces[data.RowOf(h.Instance, h.Triangle)] >> 24) < 255u;
        if (sunVisStage is not null) sunVis = sunVisStage;
        var sunVisWithCouplings = false;   // the CPU couplings' loop made them, patch by patch
        var sunVisMetal = sunVisStage is not null || transportCpuSunVis ? null : WaitForBakeTracer();
        if (couplingStage is not null)
        {
            couplings = couplingStage.Couplings; sunletCouplings = couplingStage.SunletCouplings; sky = couplingStage.Sky;
            sunletPos.AddRange(couplingStage.SunletPos); sunletNrm.AddRange(couplingStage.SunletNrm); sunletAlbedo.AddRange(couplingStage.SunletAlbedo);
        }
        else
        {
        var couplingsMetal = transportCpuCouplings ? null : WaitForBakeTracer();
        if (couplingsMetal is not null) CouplingsOnMetal(couplingsMetal);
        else
        {
        ProgressBegin("transport: couplings", patches);
        Parallel.For(0, patches, i =>
        {
            ProgressAdd("transport: couplings");
            var r = new Random(CouplingSeed(i));
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
                    var j = useSunlets ? NearestAny(hp, hn, h.Instance, (uint)r.Next()) : Nearest(hp, hn, h.Instance, (uint)r.Next());
                    if (transportMissWhy && h.FrontFace)
                    {
                        Interlocked.Increment(ref nearestHows[Math.Min(nearestHow, nearestHows.Length - 1)]);
                        // Every 16th hit found past the BaseCandidates nearest, or by the fallback: exposure-tested after.
                        if (j >= 0 && (k & 15) == 0 && (nearestHow is >= BaseCandidates and < HowAnyFacing || nearestHow is HowFallback or HowAnyFacing))
                            recoveredSamples!.Add((hp, hn, nearestHow >= HowAnyFacing ? nearestHow : BaseCandidates));
                        if (j >= 0 && (k & 15) == 0 && nearestHow == HowAnyFacing) eitherSamples!.Add((hp, hn, h.Instance));
                    }
                    if (j < 0)
                    {
                        lost++;
                        if (!h.FrontFace) Interlocked.Increment(ref droppedInside);
                        else if (missSamples is not null && (k & 15) == 0) missSamples.Add((hp, hn, h.Instance, i));
                        continue;
                    }
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
            if (sunVisStage is null && sunVisMetal is null) SunVisCpu(i, sunVis);
        });
        ProgressEnd("transport: couplings");
        sunVisWithCouplings = sunVisStage is null && sunVisMetal is null;
        }
        }
        if (couplingStage is null && useSunlets) FinalizeSunlets();
        if (sunVisStage is null && !sunVisWithCouplings)
        {
            if (sunVisMetal is not null) SunVisOnMetal(sunVisMetal);
            else Parallel.For(0, patches, i => SunVisCpu(i, sunVis));
        }
        // Each stage cached as soon as it is made, under its own key: a run that closes early (--texel-dump) keeps them.
        if (transportGpu && couplingStage is null)
            SaveCouplings(stageKeys.Couplings, new CouplingStage
            {
                Couplings = couplings, SunletCouplings = sunletCouplings,
                SunletPos = sunletPos.ToArray(), SunletNrm = sunletNrm.ToArray(), SunletAlbedo = sunletAlbedo.ToArray(), Sky = sky,
            });
        if (transportGpu && sunVisStage is null) SaveSunVis(stageKeys.SunVis, sunVis);
        var cookSeconds = clock.Elapsed.TotalSeconds;

        // R2 + R3 sky bins on Metal, in blocks of CouplingBlock patches: the patches' rays (as the CPU path draws them)
        // traced for their closest hits; each hit on architecture its sunlet and its candidates, nearest first (as
        // NearestVisibleOr ranks them); then the visibility rays in rounds, one batch a rank -- the facing candidates,
        // the ones facing either way (with sunlets), then the own fallback's closest-hit ray -- each hit taking the first
        // its line reaches. The hits' front faces and normals are the CPU's (its world triangles), not the kernel's
        // (a region's mirrored triangles are stored with their winding swapped). The same accounting as the CPU path.
        void CouplingsOnMetal(Blix.Graphics.MetalHWX.MetalRayTracer metal)
        {
            var metalClock = Stopwatch.StartNew();
            BuildDenseGrid();
            // The candidate search on the GPU (blix_candidates): the patches and their dense grid up once.
            {
                var packed = new Vector4[densePos.Length * 2];
                for (var j = 0; j < densePos.Length; j++) { packed[2 * j] = new Vector4(densePos[j], 0f); packed[2 * j + 1] = new Vector4(denseNrm[j], 0f); }
                metal.CandidatesSetup(packed, MemoryMarshal.Cast<int, uint>(denseStart.AsSpan()), MemoryMarshal.Cast<int, uint>(denseItems.AsSpan()));
            }
            Blix.Graphics.MetalHWX.MetalRayTracer.CandidateParams CandidateParamsFor(bool anyFacing) => new()
            {
                C = (uint)transportCandidates, AnyFacing = anyFacing ? 1u : 0u, Cell = cell, FacingCos = transportFacingCos,
                Penalty = anyFacing ? 0f : facingPenalty, FarCutoff = transportFarCutoff, OwnReach = OwnFallbackReach,
                MinX = denseMinX, MinY = denseMinY, MinZ = denseMinZ, DimX = denseX, DimY = denseY, DimZ = denseZ,
            };
            double gpuMs = 0;
            long rayCount = 0;
            var gpuLock = new object();
            void AddGpu(double ms) { lock (gpuLock) gpuMs += ms; }
            var R = transportRays;
            var C = transportCandidates;
            // Where the time goes: each phase's wall time, and the collector's pauses.
            var phase = new double[6];   // rays, trace (waiting on Metal), candidates, gather, accumulate, sort
            var phaseNames = new[] { "rays", "Metal", "candidates", "gather", "accumulate", "sunlets" };
            // Two workers run blocks side by side, so a phase's figure is the workers' time in it summed (it can exceed
            // the wall time), each worker lapping its own clock.
            void LapOf(Stopwatch clock, int k) { lock (phase) phase[k] += clock.Elapsed.TotalSeconds; clock.Restart(); }
            var pauseBefore = GC.GetTotalPauseDuration();
            var gen2Before = GC.CollectionCount(2);
            // The block's arrays, made once and reused: each block made hundreds of MB anew, and the collector's full
            // passes over a heap holding the contact table, couplings and sunlets stalled every thread.
            var maxTotal = Math.Min(CouplingBlock, patches) * R;
            var workers = new System.Collections.Concurrent.ConcurrentBag<CouplingWorker>();
            for (var w = 0; w < CouplingWorkers; w++) workers.Add(new CouplingWorker(maxTotal, C));
            ProgressBegin("transport: couplings (Metal)", patches);
            var blockStarts = Enumerable.Range(0, (patches + CouplingBlock - 1) / CouplingBlock).Select(b => b * CouplingBlock).ToArray();
            Parallel.ForEach(blockStarts, new ParallelOptions { MaxDegreeOfParallelism = CouplingWorkers }, first =>
            {
                workers.TryTake(out var w);
                ProcessBlock(w!, first);
                workers.Add(w!);
            });
            void ProcessBlock(CouplingWorker w, int first)
            {
                var (rays, dirs, state, hp, hn, inst, front, how) = (w.Rays, w.Dirs, w.State, w.Hp, w.Hn, w.Inst, w.Front, w.How);
                var (cand, anyCand, nearestOf, tried, anyTried, sunletOf, sunKeys, triOf) = (w.Cand, w.AnyCand, w.NearestOf, w.Tried, w.AnyTried, w.SunletOf, w.SunKeys, w.TriOf);
                var (sunOrder, candOut, triedOut, visIds, visTargets, vis, visNone, hits, answers) = (w.SunOrder, w.CandOut, w.TriedOut, w.VisIds, w.VisTargets, w.Vis, w.VisNone, w.Hits, w.Answers);
                var clock = Stopwatch.StartNew();
                void Lap(int k) => LapOf(clock, k);
                var n = Math.Min(CouplingBlock, patches - first);
                var total = n * R;
                // 1. The patches' rays, closest hits.
                Parallel.For(0, n, ii =>
                {
                    var i = first + ii;
                    var r = new Random(CouplingSeed(i));
                    var origin = pos[i] + nrm[i] * 1e-3f;
                    for (var k = 0; k < R; k++)
                    {
                        var d = CosineHemisphereCpu(nrm[i], r);
                        r.Next();
                        var at = ii * R + k;
                        dirs[at] = d;
                        rays[2 * at] = new Vector4(origin, 0f);
                        rays[2 * at + 1] = new Vector4(d, float.PositiveInfinity);
                    }
                });
                Lap(0);
                metal.TraceFromHost(rays.AsSpan(0, total * 2), hits, (uint)(first * R), closestOnly: true, out var ms);
                AddGpu(ms);
                Interlocked.Add(ref rayCount, total);
                Lap(1);
                // 2. Each hit: sky, foliage, or a hit on architecture with its candidates.
                const int Sky = -3, Leaf = -4, Pending = -1;
                Parallel.For(0, total, at =>
                {
                    sunletOf[at] = -1;
                    if ((hits[at * 12 + 3] & 1u) == 0) { state[at] = Sky; return; }
                    var placement = (int)hits[at * 12 + 4];
                    var triangle = (int)hits[at * 12 + 5];
                    if ((surfaces[data.RowOf(placement, triangle)] >> 24) < 255u) { state[at] = Leaf; return; }
                    var t = BitConverter.UInt32BitsToSingle(hits[at * 12]);
                    var i = first + at / R;
                    var d = dirs[at];
                    var p = pos[i] + nrm[i] * 1e-3f + d * t;
                    var (a, b, c) = Tri(placement, triangle);
                    var faceN = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                    front[at] = Vector3.Dot(faceN, d) < 0f;
                    var nn = front[at] ? faceN : -faceN;
                    hp[at] = p; hn[at] = nn; inst[at] = placement; triOf[at] = triangle;
                    if (useSunlets) sunKeys[at] = SunletKey(p, nn);
                    state[at] = Pending;
                    how[at] = HowMiss;
                });
                if (useSunlets) AssignSunlets(total, sunKeys, inst, triOf, hp, hn, state, sunletOf, sunOrder, Pending);
                Lap(5);
                // The pending hits' candidates, on the GPU (as CollectCandidates), scattered back.
                void CandidatesOnGpu(bool anyFacing, int[] intoCand, byte[] intoTried, int[]? intoNearest)
                {
                    var count = Gather(_ => 0);
                    if (count == 0) return;
                    Parallel.For(0, count, q =>
                    {
                        var at = visIds[q];
                        vis[2 * q] = new Vector4(hp[at], 0f);
                        vis[2 * q + 1] = new Vector4(hn[at], 0f);
                    });
                    metal.Candidates(vis.AsSpan(0, count * 2), CandidateParamsFor(anyFacing), candOut.AsSpan(0, count * C), triedOut.AsSpan(0, count * 2), out var cms);
                    AddGpu(cms);
                    Parallel.For(0, count, q =>
                    {
                        var at = visIds[q];
                        Array.Copy(candOut, q * C, intoCand, at * C, C);
                        intoTried[at] = (byte)triedOut[2 * q];
                        if (intoNearest is not null) intoNearest[at] = (int)triedOut[2 * q + 1];
                    });
                }
                CandidatesOnGpu(false, cand, tried, nearestOf);
                Lap(2);
                // 3. The visibility rounds.
                // The pending hits whose candidate exists, gathered in parallel (per range, then concatenated in order).
                int Gather(Func<int, int> candidateOf)
                {
                    var ranges = Environment.ProcessorCount * 4;
                    var span = (total + ranges - 1) / ranges;
                    var counts = new int[ranges];
                    Parallel.For(0, ranges, g =>
                    {
                        var c = 0;
                        for (var at = g * span; at < Math.Min(total, (g + 1) * span); at++)
                            if (state[at] == Pending && candidateOf(at) >= 0) c++;
                        counts[g] = c;
                    });
                    var offsets = new int[ranges];
                    var sum = 0;
                    for (var g = 0; g < ranges; g++) { offsets[g] = sum; sum += counts[g]; }
                    Parallel.For(0, ranges, g =>
                    {
                        var o = offsets[g];
                        for (var at = g * span; at < Math.Min(total, (g + 1) * span); at++)
                        {
                            if (state[at] != Pending) continue;
                            var j = candidateOf(at);
                            if (j < 0) continue;
                            visIds[o] = at; visTargets[o] = j; o++;
                        }
                    });
                    return sum;
                }
                void Round(Func<int, int> candidateOf, int howIfSeen)
                {
                    var count = Gather(candidateOf);
                    if (count == 0) return;
                    Parallel.For(0, count, q =>
                    {
                        var at = visIds[q];
                        var j = visTargets[q];
                        var origin = hp[at] + hn[at] * transportVisLift;
                        var to = pos[j] + nrm[j] * transportVisLift - origin;
                        var len = to.Length();
                        visNone[q] = len < 1e-4f;
                        vis[2 * q] = new Vector4(origin, 0f);
                        vis[2 * q + 1] = visNone[q] ? new Vector4(0f, 1f, 0f, 0f) : new Vector4(to / len, len * 0.999f);
                    });
                    Lap(3);
                    var answer = answers;
                    metal.TraceFromHost(vis.AsSpan(0, count * 2), answer, (uint)(Interlocked.Add(ref rayCount, count) + first), closestOnly: true, out var vms);
                    AddGpu(vms);
                    Lap(1);
                    Parallel.For(0, count, q =>
                    {
                        var at = visIds[q];
                        if (visNone[q] || (answer[q * 12 + 3] & 1u) == 0) { state[at] = visTargets[q]; how[at] = howIfSeen; }
                    });
                    Lap(3);
                }
                // Ranks from `fromRank` up, every pending hit's at once: each hit takes the lowest rank its line reaches
                // (as rank by rank would).
                void RoundAll(int[] cands, byte[] counts, int fromRank, Func<int, int> howOf)
                {
                    var pendingCount = Gather(_ => 0);
                    if (pendingCount == 0) return;
                    // Each pending hit's rays: its candidates from fromRank to its count, laid out one after another.
                    var start = new int[pendingCount + 1];
                    for (var q = 0; q < pendingCount; q++) start[q + 1] = start[q] + Math.Max(0, counts[visIds[q]] - fromRank);
                    var rays = start[pendingCount];
                    if (rays == 0) return;
                    var hitsOf = (int[])visIds.Clone();
                    var batch = new Vector4[rays * 2];
                    var none = new bool[rays];
                    var target = new int[rays];
                    Parallel.For(0, pendingCount, q =>
                    {
                        var at = hitsOf[q];
                        for (var k = 0; k < start[q + 1] - start[q]; k++)
                        {
                            var r = start[q] + k;
                            var j = cands[at * C + fromRank + k];
                            target[r] = j;
                            var origin = hp[at] + hn[at] * transportVisLift;
                            var to = pos[j] + nrm[j] * transportVisLift - origin;
                            var len = to.Length();
                            none[r] = len < 1e-4f;
                            batch[2 * r] = new Vector4(origin, 0f);
                            batch[2 * r + 1] = none[r] ? new Vector4(0f, 1f, 0f, 0f) : new Vector4(to / len, len * 0.999f);
                        }
                    });
                    Lap(3);
                    var answer = rays * 12 <= answers.Length ? answers : new uint[rays * 12];
                    metal.TraceFromHost(batch, answer, (uint)(Interlocked.Add(ref rayCount, rays) + first), closestOnly: true, out var ams);
                    AddGpu(ams);
                    Lap(1);
                    Parallel.For(0, pendingCount, q =>
                    {
                        var at = hitsOf[q];
                        for (var k = 0; k < start[q + 1] - start[q]; k++)
                        {
                            var r = start[q] + k;
                            if (!none[r] && (answer[r * 12 + 3] & 1u) != 0) continue;
                            state[at] = target[r];
                            how[at] = howOf(fromRank + k);
                            break;
                        }
                    });
                    Lap(3);
                }
                // The first rank alone (it settles most hits), then every later rank in one batch: each round trip waits
                // behind the frames' GPU work (a cook runs while the demo renders), so fewer of them, a few more rays.
                Round(at => 0 < tried[at] ? cand[at * C] : -1, 0);
                RoundAll(cand, tried, 1, rk => rk);
                if (useSunlets)
                {
                    Array.Clear(anyTried, 0, total);
                    CandidatesOnGpu(true, anyCand, anyTried, null);
                    Lap(2);
                    RoundAll(anyCand, anyTried, 0, _ => HowAnyFacing);
                }
                if (transportOwnFallback)
                {
                    // One closest-hit ray to the nearest candidate: taken when what blocks it is the hit's own placement.
                    var count = Gather(at => nearestOf[at]);
                    if (count > 0)
                    {
                        Parallel.For(0, count, q =>
                        {
                            var at = visIds[q];
                            var j = visTargets[q];
                            var origin = hp[at] + hn[at] * transportVisLift;
                            var to = pos[j] + nrm[j] * transportVisLift - origin;
                            var len = to.Length();
                            visNone[q] = len < 1e-4f;
                            vis[2 * q] = new Vector4(origin, 0f);
                            vis[2 * q + 1] = visNone[q] ? new Vector4(0f, 1f, 0f, 0f) : new Vector4(to / len, len * 0.999f);
                        });
                        Lap(3);
                        var answer = answers;
                        metal.TraceFromHost(vis.AsSpan(0, count * 2), answer, (uint)(Interlocked.Add(ref rayCount, count) + first), closestOnly: true, out var fms);
                        AddGpu(fms);
                        Lap(1);
                        Parallel.For(0, count, q =>
                        {
                            var at = visIds[q];
                            var found = (answer[q * 12 + 3] & 1u) != 0;
                            if (visNone[q] || found && (int)answer[q * 12 + 4] == inst[at]) { state[at] = visTargets[q]; how[at] = HowFallback; }
                        });
                        Lap(3);
                    }
                }
                // 4. Per patch: its couplings, sunlets, sky bins and the accounting.
                Parallel.For(0, n, ii =>
                {
                    var i = first + ii;
                    // The patch's targets and sunlets, sorted and counted in runs (no dictionary a patch).
                    Span<int> targets = R <= 2048 ? stackalloc int[R] : new int[R];
                    Span<int> suns = R <= 2048 ? stackalloc int[R] : new int[R];
                    int nt = 0, ns = 0;
                    var lost = 0;
                    for (var k = 0; k < R; k++)
                    {
                        var at = ii * R + k;
                        switch (state[at])
                        {
                            case Sky:
                                sky[i * TransportSkyRes * TransportSkyRes + OctBin(dirs[at], TransportSkyRes)] += 1f / R;
                                continue;
                            case Leaf:
                                Interlocked.Increment(ref foliageHits);
                                continue;
                        }
                        if (sunletOf[at] >= 0) suns[ns++] = sunletOf[at];
                        var j = state[at];
                        if (transportMissWhy && front[at])
                        {
                            Interlocked.Increment(ref nearestHows[Math.Min(how[at], nearestHows.Length - 1)]);
                            if (j >= 0 && (k & 15) == 0 && (how[at] is >= BaseCandidates and < HowAnyFacing || how[at] is HowFallback or HowAnyFacing))
                                recoveredSamples!.Add((hp[at], hn[at], how[at] >= HowAnyFacing ? how[at] : BaseCandidates));
                            if (j >= 0 && (k & 15) == 0 && how[at] == HowAnyFacing) eitherSamples!.Add((hp[at], hn[at], inst[at]));
                        }
                        if (j < 0)
                        {
                            lost++;
                            if (!front[at]) Interlocked.Increment(ref droppedInside);
                            else if (missSamples is not null && (k & 15) == 0) missSamples.Add((hp[at], hn[at], inst[at], i));
                            continue;
                        }
                        targets[nt++] = j;
                    }
                    couplings[i] = Runs(targets[..nt], R);
                    sunletCouplings[i] = Runs(suns[..ns], R);
                    Interlocked.Add(ref dropped, lost);
                });
                Lap(4);
                ProgressAdd("transport: couplings (Metal)", n);
            }
            ProgressEnd("transport: couplings (Metal)");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport: couplings on Metal, {patches:N0} patches, {rayCount:N0} rays in {metalClock.Elapsed.TotalSeconds:0.0} s ({gpuMs / 1000:0.0} s GPU); by phase {string.Join(", ", phase.Select((v, k) => $"{phaseNames[k]} {v:0.0} s"))}; collector paused {(GC.GetTotalPauseDuration() - pauseBefore).TotalSeconds:0.0} s in {GC.CollectionCount(2) - gen2Before} full collections."));
        }

        // The patches as flat arrays and their grid as a dense index (cell -> a range of patch ids), for the Metal path's
        // candidate search: the dictionary of lists, looked up 27 times for each of Sponza's ~85M hits, was most of its
        // couplings' 200 s (2026-10-13). Built once the patches are final (after the fill).
        void BuildDenseGrid()
        {
            densePos = pos.ToArray();
            denseNrm = nrm.ToArray();
            int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
            foreach (var (x, y, z) in grid.Keys)
            {
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
            }
            if (grid.Count == 0) { minX = minY = minZ = 0; maxX = maxY = maxZ = 0; }
            denseMinX = minX; denseMinY = minY; denseMinZ = minZ;
            denseX = maxX - minX + 1; denseY = maxY - minY + 1; denseZ = maxZ - minZ + 1;
            var cells = denseX * denseY * denseZ;
            denseStart = new int[cells + 1];
            foreach (var ((x, y, z), list) in grid) denseStart[((z - minZ) * denseY + (y - minY)) * denseX + (x - minX) + 1] = list.Count;
            for (var c = 0; c < cells; c++) denseStart[c + 1] += denseStart[c];
            denseItems = new int[denseStart[cells]];
            foreach (var ((x, y, z), list) in grid)
                list.CopyTo(denseItems, denseStart[((z - minZ) * denseY + (y - minY)) * denseX + (x - minX)]);
        }

        // The candidates NearestVisibleOr would try, nearest first (by its ranking), into `into`; returns how many to try
        // (the far cutoff's limit); nearest: the nearest within OwnFallbackReach (-1: none). Reads the dense grid.
        int CollectCandidates(Vector3 p, Vector3 n, bool anyFacing, Span<int> into, out int nearest)
        {
            var (kx, ky, kz) = Key(p);
            var candidates = into.Length;
            Span<float> bestD = stackalloc float[candidates];
            into.Fill(-1);
            bestD.Fill(float.MaxValue);
            // The 27 cells nearest first (by the point's distance to each), and a cell no nearer than the current last
            // candidate skipped: the distance is a lower bound on every key in it (the angle penalty only adds).
            Span<float> cellD = stackalloc float[27];
            Span<int> order = stackalloc int[27];
            for (var c = 0; c < 27; c++)
            {
                int ox = c % 3 - 1, oy = c / 3 % 3 - 1, oz = c / 9 - 1;
                float Gap(float v, int k, int o) => o == 0 ? 0f : o < 0 ? v - k * cell : (k + 1) * cell - v;
                var gx = Gap(p.X, kx, ox); var gy = Gap(p.Y, ky, oy); var gz = Gap(p.Z, kz, oz);
                cellD[c] = gx * gx + gy * gy + gz * gz;
                order[c] = c;
            }
            for (var a = 1; a < 27; a++)
            {
                var key = order[a];
                var b = a - 1;
                while (b >= 0 && cellD[order[b]] > cellD[key]) { order[b + 1] = order[b]; b--; }
                order[b + 1] = key;
            }
            for (var oc = 0; oc < 27; oc++)
            {
                var c = order[oc];
                if (cellD[c] >= bestD[candidates - 1]) break;
                int gx2 = kx + c % 3 - 1 - denseMinX, gy2 = ky + c / 3 % 3 - 1 - denseMinY, gz2 = kz + c / 9 - 1 - denseMinZ;
                if ((uint)gx2 >= (uint)denseX || (uint)gy2 >= (uint)denseY || (uint)gz2 >= (uint)denseZ) continue;
                var ci = (gz2 * denseY + gy2) * denseX + gx2;
                for (var q = denseStart[ci]; q < denseStart[ci + 1]; q++)
                {
                    var j = denseItems[q];
                    var facing = Vector3.Dot(denseNrm[j], n);
                    if (!anyFacing && facing < transportFacingCos) continue;
                    var turn = anyFacing ? 0f : facingPenalty * (1f - facing);
                    var d2 = Vector3.DistanceSquared(densePos[j], p) + turn * turn;
                    if (d2 >= bestD[candidates - 1]) continue;
                    var at = candidates - 1;
                    while (at > 0 && bestD[at - 1] > d2) { bestD[at] = bestD[at - 1]; into[at] = into[at - 1]; at--; }
                    bestD[at] = d2;
                    into[at] = j;
                }
            }
            var firstDistance2 = into[0] >= 0 ? Vector3.DistanceSquared(densePos[into[0]], p) : float.MaxValue;
            nearest = firstDistance2 <= OwnFallbackReach * OwnFallbackReach ? into[0] : -1;
            var count = 0;
            while (count < candidates && into[count] >= 0) count++;
            return firstDistance2 > transportFarCutoff * transportFarCutoff ? Math.Min(count, BaseCandidates) : count;
        }

        // R3 sun visibility, one patch on the CPU: whether each direction bin it faces reaches the sky.
        void SunVisCpu(int i, ulong[] into)
        {
            var p = pos[i] + nrm[i] * 1e-3f;
            var n = nrm[i];
            for (var b = 0; b < transportVisRes * transportVisRes; b++)
            {
                var d = SunVisDirection(b);
                if (Vector3.Dot(d, n) <= 0f) continue;
                if (!scene.Any(new Ray(p, d), 0f, float.PositiveInfinity, (uint)(i * 7919 + b))) into[i * visWords + b / 64] |= 1UL << (b % 64);
            }
        }
        Vector3 SunVisDirection(int b)
        {
            var uv = new Vector2((b % transportVisRes + 0.5f) / transportVisRes, (b / transportVisRes + 0.5f) / transportVisRes) * 2f - Vector2.One;
            return OctDecode(uv);
        }
        // R3 on Metal (the lightlet bake's dense pass: the point faces the direction and nothing is in the way, from
        // 1 mm off, one coverage coin for every ray): about half the cook before the patches' visibility tests was
        // these rays on the CPU. --transport-sunvis-check: the first SunVisCheckPatches on the CPU too, the bits that
        // differ reported.
        void SunVisOnMetal(Blix.Graphics.MetalHWX.MetalRayTracer metal)
        {
            var sunClock = Stopwatch.StartNew();
            var bins = transportVisRes * transportVisRes;
            var words32 = (bins + 31) / 32;
            var points = new Vector4[patches * 2];
            for (var i = 0; i < patches; i++) { points[2 * i] = new Vector4(pos[i], 0f); points[2 * i + 1] = new Vector4(nrm[i], 0f); }
            var dirs = new Vector4[bins];
            for (var b = 0; b < bins; b++) dirs[b] = new Vector4(SunVisDirection(b), 0f);
            ProgressBegin("transport: sun visibility (Metal)", 0);
            var bits = metal.LightletVisibility(points, dirs, words32, 7919u, 1e-3f, float.PositiveInfinity, out var gpuMs);
            ProgressEnd("transport: sun visibility (Metal)");
            for (var i = 0; i < patches; i++)
                for (var w = 0; w < words32; w++)
                {
                    var word = bits[i * words32 + w];
                    if (word == 0) continue;
                    var b0 = w * 32;
                    sunVis[i * visWords + b0 / 64] |= (ulong)word << (b0 % 64);
                }
            var line = string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport: sun visibility on Metal, {patches:N0} patches x {bins} directions in {sunClock.Elapsed.TotalSeconds:0.0} s ({gpuMs:0} ms GPU)");
            if (transportSunVisCheck)
            {
                var check = Math.Min(patches, SunVisCheckPatches);
                var cpu = new ulong[check * visWords];
                Parallel.For(0, check, i => SunVisCpu(i, cpu));
                long differ = 0, set = 0;
                for (var k = 0; k < check * visWords; k++)
                {
                    differ += System.Numerics.BitOperations.PopCount(cpu[k] ^ sunVis[k]);
                    set += System.Numerics.BitOperations.PopCount(cpu[k]);
                }
                line += string.Create(CultureInfo.InvariantCulture, $"; against the CPU on {check:N0} patches: {differ:N0} bits differ of {set:N0} set on the CPU");
            }
            Console.WriteLine(line + ".");
        }
        // Why a front-face hit found no patch it could see: no patch facing its way within the 27 cells, or the nearest
        // ones blocked -- by the hit's own placement (a fold, a moulding: the patch behind a bump of the same piece) or
        // another; how far the nearest was; by the hit's placement.
        void MissWhy(ConcurrentBag<(Vector3 P, Vector3 N, int Instance, int From)> misses)
        {
            // How each front-face hit found its patch: by rank, by facing either way, by the fallback, or not at all;
            // the time the late ranks and the fallback took (summed over threads) against the cook's; and how many of
            // the hits the late ranks and the fallback recovered are where open air sees them (a sample).
            long all = nearestHows.Sum();
            var ranks = string.Join(", ", Enumerable.Range(0, transportCandidates).Select(r => string.Create(CultureInfo.InvariantCulture, $"{r + 1}: {nearestHows[r]:N0}")));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport hits, how their patch was found ({all:N0} front-face hits): by rank {ranks}; facing either way {nearestHows[HowAnyFacing]:N0}; own fallback {nearestHows[HowFallback]:N0}; none {nearestHows[HowMiss]:N0}."));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport hits: past rank {BaseCandidates}, {nearestHows.Take(transportCandidates).Skip(BaseCandidates).Sum() / (double)Math.Max(1, all) * 100:0.000}% of hits for {lateRankTicks / (double)Stopwatch.Frequency:0} thread-s; the fallback {nearestHows[HowFallback] / (double)Math.Max(1, all) * 100:0.000}% for {fallbackTicks / (double)Stopwatch.Frequency:0} thread-s; the cook {cookSeconds:0} s on {Environment.ProcessorCount} threads."));
            if (recoveredSamples is { } recovered)
            {
                var r = recovered.ToArray();
                ProgressBegin("diagnostics: recovered hits, exposed?", r.Length);
                int Seen((Vector3 P, Vector3 N, int How)[] of)
                {
                    var seen = 0;
                    Parallel.For(0, of.Length, i =>
                    {
                        if (Exposed(scene, of[i].P, of[i].N, (uint)i)) Interlocked.Increment(ref seen);
                        ProgressAdd("diagnostics: recovered hits, exposed?");
                    });
                    return seen;
                }
                var late = r.Where(x => x.How == BaseCandidates).ToArray();
                var fell = r.Where(x => x.How == HowFallback).ToArray();
                var either = r.Where(x => x.How == HowAnyFacing).ToArray();
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] transport hits recovered, exposed (a sample, every 16th ray): past rank {BaseCandidates} {Seen(late):N0} of {late.Length:N0}; by the fallback {Seen(fell):N0} of {fell.Length:N0}; facing either way {Seen(either):N0} of {either.Length:N0}."));
                ProgressEnd("diagnostics: recovered hits, exposed?");
            }
            var list = misses.ToArray();
            int none = 0, ownBlock = 0, otherBlock = 0;
            var near = new int[5];   // nearest candidate < 0.1, < 0.25, < 0.5, < 1, beyond
            var byHit = new Dictionary<string, int>();
            var byBlocker = new Dictionary<string, int>();
            var dumpLines = new List<string>();
            // Whether each hit is exposed (SponzaLoop.TransportDiag.cs): light lost where open air sees it is a real loss.
            var exposedMiss = new bool[list.Length];
            ProgressBegin("diagnostics: misses, exposed?", list.Length);
            Parallel.For(0, list.Length, m =>
            {
                exposedMiss[m] = Exposed(scene, list[m].P, list[m].N, (uint)m);
                ProgressAdd("diagnostics: misses, exposed?");
            });
            ProgressEnd("diagnostics: misses, exposed?");
            var exposedByHit = new Dictionary<string, int>();
            var exposedWhy = new Dictionary<string, int>();
            var at = -1;
            foreach (var (hp, hn, inst, from) in list)
            {
                at++;
                var hitName = rayPlacementNames[inst];
                byHit[hitName] = byHit.GetValueOrDefault(hitName) + 1;
                if (exposedMiss[at]) exposedByHit[hitName] = exposedByHit.GetValueOrDefault(hitName) + 1;
                var (kx, ky, kz) = Key(hp);
                var bestJ = -1;
                var bestD = float.MaxValue;
                for (var dz = -1; dz <= 1; dz++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var cellList)) continue;
                    foreach (var j in cellList)
                    {
                        if (Vector3.Dot(nrm[j], hn) < transportFacingCos) continue;
                        var d2 = Vector3.DistanceSquared(pos[j], hp);
                        if (d2 < bestD) { bestD = d2; bestJ = j; }
                    }
                }
                var why = "none facing";
                if (bestJ < 0) none++;
                else
                {
                    var dist = MathF.Sqrt(bestD);
                    near[dist < 0.1f ? 0 : dist < 0.25f ? 1 : dist < 0.5f ? 2 : dist < 1f ? 3 : 4]++;
                    var origin = hp + hn * transportVisLift;
                    var to = pos[bestJ] + nrm[bestJ] * transportVisLift - origin;
                    var len = to.Length();
                    if (scene.Closest(new Ray(origin, to / len), 0f, len * 0.999f, 1u) is { } b)
                    {
                        var own = b.Instance == inst;
                        if (own) ownBlock++; else otherBlock++;
                        var bn = (own ? "own: " : "") + rayPlacementNames[b.Instance] + (b.FrontFace ? "" : " (back)");
                        byBlocker[bn] = byBlocker.GetValueOrDefault(bn) + 1;
                        why = string.Create(CultureInfo.InvariantCulture, $"blocked {(own ? "own" : "other")} at {b.T / len:0.00} of {len:0.00} m");
                    }
                    else why = "nearest clear (a farther candidate order?)";
                }
                if (exposedMiss[at])
                {
                    var kind = why.StartsWith("blocked own", StringComparison.Ordinal) ? "blocked by its own placement"
                        : why.StartsWith("blocked other", StringComparison.Ordinal) ? "blocked by another" : why;
                    exposedWhy[kind] = exposedWhy.GetValueOrDefault(kind) + 1;
                }
                if (dumpLines.Count < 200_000)
                    dumpLines.Add(string.Create(CultureInfo.InvariantCulture, $"{hp.X} {hp.Y} {hp.Z} {hn.X} {hn.Y} {hn.Z} {hitName};{why};{(exposedMiss[at] ? "exposed" : "hidden")};from {pos[from].X} {pos[from].Y} {pos[from].Z} {nrm[from].X} {nrm[from].Y} {nrm[from].Z}"));
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport misses, why ({list.Length:N0} front-face misses sampled, every 16th ray): none facing within reach {none:N0}; nearest blocked by its own placement {ownBlock:N0}, by another {otherBlock:N0}; nearest at < 0.1 m {near[0]:N0}, < 0.25 {near[1]:N0}, < 0.5 {near[2]:N0}, < 1 {near[3]:N0}, beyond {near[4]:N0}."));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport misses, exposed (open air sees the hit): {exposedMiss.Count(e => e):N0} of {list.Length:N0}; why: {string.Join(", ", exposedWhy.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value:N0}"))}."));
            Console.WriteLine("    exposed, by the hit's placement:");
            foreach (var (name, c) in exposedByHit.OrderByDescending(x => x.Value).Take(12))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {c,7:N0}  {name}"));
            Console.WriteLine("    by the hit's placement:");
            foreach (var (name, c) in byHit.OrderByDescending(x => x.Value).Take(10))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {c,7:N0}  {name}"));
            Console.WriteLine("    by what blocked the nearest:");
            foreach (var (name, c) in byBlocker.OrderByDescending(x => x.Value).Take(10))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {c,7:N0}  {name}"));
            if (Environment.GetEnvironmentVariable("BLIX_DIAG_DUMP") is { } dump) File.WriteAllLines(dump + ".misses", dumpLines);
        }
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
            + $"{dropped / (double)Math.Max(1, (long)patches * transportRays) * 100:0.00}% of rays found no patch ({(dropped - droppedInside) / (double)Math.Max(1, (long)patches * transportRays) * 100:0.00}% on a front face), {foliageHits / (double)Math.Max(1, (long)patches * transportRays) * 100:0.00}% met foliage (absorbed); {bytes / 1048576.0:0.0} MB "
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
        // A sun's own terms: its direct light at the patches (the sun map) or at the sunlets and their first bounce to
        // the patches (an exact ray each).
        (Vector3[] Direct, Vector3[] First, Vector3[] SOut, string RaysOn) SunTerms(Vector3 toSunNew, Vector3 sunIrrNew)
        {
            var direct = new Vector3[patches];
            var first = new Vector3[patches];
            var sOut = new Vector3[useSunlets ? sunlets : 0];
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
            return (direct, first, sOut, raysOn);
        }
        transportRelight = (toSunNew, sunIrrNew) =>
        {
            var relightClock = Stopwatch.StartNew();
            var (direct, first, sOut, raysOn) = SunTerms(toSunNew, sunIrrNew);
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
        // Many suns at once (SponzaLoop.SunNodes.cs): each sun's own terms as above, then the bounces for a group of them
        // together -- a sweep of the couplings serves every sun in the group, where one sun at a time read all 26 M of
        // them (Sponza) 48 times over for each. No guides: they steer one sun's gather, and the nodes' gather is every
        // sun's. Warm-started from the last answer, as the one-sun relight is.
        // The same solve's inputs for the GPU (SponzaLoop.SunNodesSolve.cs): the sun nodes' patch light for many suns.
        // References, not copies: the relight closures keep them alive anyway.
        if (useSunlets)
            transportSolve = new TransportSolveData(patches, couplings, sunletCouplings, sunletPos, sunletNrm, sunletAlbedo,
                albedo, skyIn, () => lastIncident, iterations);
        // The guides from given light (SponzaLoop.SunNodesSolve.cs: the nodes' band averages).
        transportBuildGuides = (outg, sOut) => BuildGuides(outg, sOut);
        transportRelightMany = (toSuns, sunIrrNew) =>
        {
            const int group = 32;
            var result = new Vector3[toSuns.Length][];
            double termsMs = 0, bounceMs = 0;
            for (var g0 = 0; g0 < toSuns.Length; g0 += group)
            {
                var n = Math.Min(group, toSuns.Length - g0);
                var clock = Stopwatch.StartNew();
                var terms = new (Vector3[] Direct, Vector3[] First, Vector3[] SOut, string RaysOn)[n];
                for (var k = 0; k < n; k++) terms[k] = SunTerms(toSuns[g0 + k], sunIrrNew);
                termsMs += clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                // [patch][sun in the group]
                var inc = new Vector3[patches * n];
                var outg = new Vector3[patches * n];
                var fixedIn = new Vector3[patches * n];
                var direct = new Vector3[patches * n];
                for (var i = 0; i < patches; i++)
                for (var k = 0; k < n; k++)
                {
                    inc[i * n + k] = lastIncident[i];
                    fixedIn[i * n + k] = skyIn[i] + terms[k].First[i];
                    direct[i * n + k] = terms[k].Direct[i];
                }
                for (var it = 0; it < iterations; it++)
                {
                    Parallel.For(0, patches, i =>
                    {
                        var a = albedo[i] / MathF.PI;
                        for (var k = 0; k < n; k++) outg[i * n + k] = a * (inc[i * n + k] + direct[i * n + k]);
                    });
                    Parallel.For(0, patches, () => new Vector3[group], (i, _, h) =>
                    {
                        Array.Clear(h, 0, n);
                        foreach (var (to, w) in couplings[i])
                        {
                            var row = to * n;
                            for (var k = 0; k < n; k++) h[k] += outg[row + k] * w;
                        }
                        for (var k = 0; k < n; k++) inc[i * n + k] = fixedIn[i * n + k] + h[k] * MathF.PI;
                        return h;
                    }, _ => { });
                }
                for (var k = 0; k < n; k++)
                {
                    var one = new Vector3[patches];
                    for (var i = 0; i < patches; i++) one[i] = inc[i * n + k];
                    result[g0 + k] = one;
                }
                bounceMs += clock.Elapsed.TotalMilliseconds;
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport relight, {toSuns.Length} suns at once: their own terms in {termsMs / 1000:0.0} s, {iterations} bounces in groups of {group} in {bounceMs / 1000:0.0} s."));
            return result;
        };
        transportRelightSun = (toSun, sunIrr);
        // Each patch's openness: the fraction of its cosine rays that meet nothing within TexelOpenReach -- what a texel's
        // own openness is held against (texel_gather.comp): the patches' light, scaled by how much more or less open
        // the texel is than the patches around it.
        var openness = new float[patches];
        var opennessFree = new int[patches];
        if (patchStage is not null) openness = patchStage.Openness;
        else if (transportGpu && ContactTraceAll(scene, patches * 256L, r =>
                 {
                     var i = (int)(r / 256);
                     return (pos[i] + nrm[i] * 0.01f, CosineHashed(nrm[i], RayHash(i, (int)(r % 256), 6151u)), TexelOpenReach);
                 }, (r, kind, _) => { if (kind == 0) Interlocked.Increment(ref opennessFree[(int)(r / 256)]); }, 6151u, "transport: openness (Metal)"))
            for (var i = 0; i < patches; i++) openness[i] = opennessFree[i] / 256f;
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
        if (transportGpu && patchStage is null)
            SavePatches(stageKeys.Patches, new PatchStage { Pos = pos.ToArray(), Nrm = nrm.ToArray(), Albedo = albedo.ToArray(), Openness = openness });
        var guides = BuildGuides(outgoing, sunletOut);
        OnMain(() =>
        {
            UploadCookedPatches(pos, nrm, incident, openness, transportSpacing);
            UploadCookedGuides(guides);
        });
        // --lightmap: the lightmap atlas's texels are the finest level of the surfaces in the atlas (SponzaLoop.Lightmap.cs),
        // the hashed cells their coarser levels and every level of everything else (the foliage the cook leaves out). Null
        // when the atlas asks for more than the budget: the hashed texels then serve every surface at every level.
        if (lightmapBuriedWhy || transportMissWhy) BuildDiagViews(scene, pos, nrm);
        if (missSamples is not null) MissWhy(missSamples);
        if (eitherSamples is not null) EitherWhy(eitherSamples);
        // Why a hit read a patch facing another way: the best-turned patch within OwnFallbackReach (none at all; past
        // transportFacingCos; within it, then all blocked), the nearest within transportFacingCos and how far, whether
        // open air sees the hit, and by its placement (the exposed ones).
        void EitherWhy(ConcurrentBag<(Vector3 P, Vector3 N, int Instance)> samples)
        {
            var list = samples.ToArray();
            var bins = new long[4];   // no patch within reach; best turned past the facing cut; within 60-75 / the cut; within 60
            var exposedBins = new long[4];
            var near = new long[5];   // nearest facing: < 0.1, < 0.25, < 0.5, < 1, none within the cells
            var byName = new ConcurrentDictionary<string, int>();
            var exposedCount = 0;
            var blockAt = new long[5];   // the blocker's distance from the hit: < 1, < 2.5, < 5, < 10 cm, farther
            long blockBack = 0, blockOwn = 0, liftCrosses = 0;
            var seenAtLift = new long[Lifts.Length];
            var blockers = new ConcurrentDictionary<string, int>();
            ProgressBegin("diagnostics: facing either way, why", list.Length);
            Parallel.For(0, list.Length, m =>
            {
                ProgressAdd("diagnostics: facing either way, why");
                var (hp, hn, inst) = list[m];
                var (kx, ky, kz) = Key(hp);
                var bestCos = -2f;
                var nearestFacing = float.MaxValue;
                var nearestJ = -1;
                for (var dz = -1; dz <= 1; dz++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var cellList)) continue;
                    foreach (var j in cellList)
                    {
                        var c = Vector3.Dot(nrm[j], hn);
                        var d2 = Vector3.DistanceSquared(pos[j], hp);
                        if (d2 <= OwnFallbackReach * OwnFallbackReach) bestCos = MathF.Max(bestCos, c);
                        if (c >= transportFacingCos && d2 < nearestFacing) { nearestFacing = d2; nearestJ = j; }
                    }
                }
                var bin = bestCos < -1.5f ? 0 : bestCos < transportFacingCos ? 1 : bestCos < 0.5f ? 2 : 3;
                // The line to the nearest facing patch: what blocks it, how far from the hit, which side; whether the
                // lift itself crosses a surface; and whether another lift would see the patch.
                var exposedHere = false;
                if (nearestJ >= 0)
                {
                    var origin = hp + hn * transportVisLift;
                    var to = pos[nearestJ] + nrm[nearestJ] * transportVisLift - origin;
                    var len = to.Length();
                    if (len > 1e-4f && scene.Closest(new Ray(origin, to / len), 0f, len * 0.999f, 1u) is { } b)
                    {
                        var at = b.T;
                        Interlocked.Increment(ref blockAt[at < 0.01f ? 0 : at < 0.025f ? 1 : at < 0.05f ? 2 : at < 0.1f ? 3 : 4]);
                        if (!b.FrontFace) Interlocked.Increment(ref blockBack);
                        if (b.Instance == inst) Interlocked.Increment(ref blockOwn);
                        exposedHere = Exposed(scene, hp, hn, (uint)m);
                        if (exposedHere) blockers.AddOrUpdate((b.Instance == inst ? "own: " : "") + rayPlacementNames[b.Instance] + (b.FrontFace ? "" : " (back)"), 1, (_, v) => v + 1);
                    }
                    if (scene.Any(new Ray(hp + hn * 1e-3f, hn), 0f, transportVisLift, 1u)) Interlocked.Increment(ref liftCrosses);
                    for (var l = 0; l < Lifts.Length; l++)
                    {
                        var o2 = hp + hn * Lifts[l];
                        var t2 = pos[nearestJ] + nrm[nearestJ] * Lifts[l] - o2;
                        var l2 = t2.Length();
                        if (l2 < 1e-4f || !scene.Any(new Ray(o2, t2 / l2), 0f, l2 * 0.999f, 1u)) Interlocked.Increment(ref seenAtLift[l]);
                    }
                }
                Interlocked.Increment(ref bins[bin]);
                var dist = MathF.Sqrt(nearestFacing);
                Interlocked.Increment(ref near[dist < 0.1f ? 0 : dist < 0.25f ? 1 : dist < 0.5f ? 2 : dist < 1f ? 3 : 4]);
                if (nearestJ < 0 ? Exposed(scene, hp, hn, (uint)m) : exposedHere || Exposed(scene, hp, hn, (uint)m))
                {
                    Interlocked.Increment(ref exposedCount);
                    Interlocked.Increment(ref exposedBins[bin]);
                    byName.AddOrUpdate(rayPlacementNames[inst], 1, (_, v) => v + 1);
                }
            });
            ProgressEnd("diagnostics: facing either way, why");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport hits facing either way, why ({list.Length:N0} sampled, every 16th ray; {exposedCount:N0} exposed): best-turned patch within {OwnFallbackReach} m -- none {bins[0]:N0} ({exposedBins[0]:N0} exposed), past the facing cut {bins[1]:N0} ({exposedBins[1]:N0}), 60 degrees to the cut {bins[2]:N0} ({exposedBins[2]:N0}), within 60 but none seen {bins[3]:N0} ({exposedBins[3]:N0}); nearest facing at < 0.1 m {near[0]:N0}, < 0.25 {near[1]:N0}, < 0.5 {near[2]:N0}, < 1 {near[3]:N0}, farther or none {near[4]:N0}."));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport hits facing either way, the line to the nearest facing patch: blocked at < 1 cm {blockAt[0]:N0}, < 2.5 {blockAt[1]:N0}, < 5 {blockAt[2]:N0}, < 10 {blockAt[3]:N0}, farther {blockAt[4]:N0}; by a back face {blockBack:N0}, by the hit's own placement {blockOwn:N0}; the {transportVisLift} m lift crosses a surface {liftCrosses:N0}; seen with the lift at {string.Join(", ", Lifts.Select((l, i) => string.Create(CultureInfo.InvariantCulture, $"{l * 100:0.#} cm {seenAtLift[i]:N0}")))} (of {list.Length:N0})."));
            Console.WriteLine("    exposed, by the hit's placement:");
            foreach (var (name, c) in byName.OrderByDescending(x => x.Value).Take(12))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {c,7:N0}  {name}"));
            var eitherFamilies = byName.GroupBy(x => x.Key.Split('_')[0]).Select(g => (g.Key, g.Sum(x => x.Value))).OrderByDescending(x => x.Item2).Take(10);
            Console.WriteLine("    exposed, by family: " + string.Join(", ", eitherFamilies.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Key} {f.Item2:N0}"))));
            Console.WriteLine("    exposed, by what blocks the line:");
            foreach (var (name, c) in blockers.OrderByDescending(x => x.Value).Take(12))
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    {c,7:N0}  {name}"));
        }
        var lightmapBake = lightmapEnabled && transportTexels && lightmapSources.Count > 0 ? BakeLightmapTexels(scene, cacheKey) : null;
        TexelBake? texelBake;
        if (texelStage is { } cachedTexels)
        {
            texelBake = cachedTexels;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] texels: {cachedTexels.Count:N0} from the cache."));
        }
        else texelBake = CookTexels(scene, Tri, (i, t) => (surfaces[data.RowOf(i, t)] >> 24) < 255u,
            (i, t) => lightmapBake is not null && rayPlacementLightmapped[(int)data.RowPlacements[data.RowOf(i, t)]] ? 1 : 0);
        if (transportGpu && texelStage is null && transportTexels && texelBake is not null) SaveTexels(stageKeys.Texels, texelBake);
        if (texelBake is { } hashed)
        {
            // The baked light (SponzaLoop.LightBake.cs), read here off the frame thread; without it, the bake begins
            // once the texels are up, toward the same file.
            var texelTotal = hashed.Count + (lightmapBake?.Count ?? 0);
            var lightPath = lightBakeOff ? null : LightBakePathFor(cacheKey, transportRelightSun);
            lightBakeKey = cacheKey;
            // The resting form first (8 B a texel), else the full state (an older run's), else nothing: the bake begins.
            // Under --split-light the parts' caches answer (BeginSplitLight), not one light's.
            var resolved = lightPath is null || !TexelsMayRest || splitLight ? null : ReadResolved(ResolvedPathFor(lightPath), texelTotal);
            var light = lightPath is null || resolved is not null || splitLight ? null : ReadLightBake(lightPath, texelTotal);
            // Its own copy of the atlas: the method's locals are dropped at its end (below), before this may run.
            var atlasForUpload = lightmapBake;
            OnMain(() =>
            {
                if (atlasForUpload is { } atlas) UploadLightmapTexels(hashed, atlas, light, lightPath, resolved);
                else UploadTexels(hashed, light, lightPath, resolved: resolved);
                // Uploaded: let the CPU's texels go (the relight closure shares this scope and lives on).
                hashed = null!;
                atlasForUpload = null;
                light = null;
                resolved = null;
            });
        }
        // transportRelight (above) closes over this method's locals, and a closure keeps every local it shares a scope
        // with: drop the big ones it never reads (the stages as read, the texels and the atlas's records, both uploaded).
        patchStage = null;
        couplingStage = null;
        sunVisStage = null;
        texelStage = null;
        ReleaseCookMemory();
        texelBake = null;
        lightmapBake = null;
    }
}
