using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --rtgi-near-cook (on with --rtgi-volume; --rtgi-no-near-cook off): the near field cooked from the geometry
// (rtgi_nearcook.glsl). Where the volume found the geometry tight (its refined 1 m and 0.5 m probes: creases, gaps,
// contacts -- elsewhere the near field is the far light), the triangles are sampled into --rtgi-near-cell (5 cm) surface
// cells by side; each traces 16 fixed cosine rays to the contact radius once, in chunks through the RTGI Metal batch, and
// keeps its escape share and what each ray met. Each frame (a share of the cells) its near light is those surfaces' cache
// light (rtgi_nearcook_update.comp); the readout puts it over the cache's far light. The hybrid near field
// before it (its own near rays per pixel, since removed) kept a history on the screen, cleared as the camera moved, a crease seconds coming back; the user asked
// for it worked ahead, with the chunks, as the volume is (2026-10-29). Streamed worlds: a chunk's cells and their links
// would come and go with it -- one load here, every cell resident.
internal sealed partial class SponzaLoop
{
    private bool rtgiNearCook = true, rtgiNearCookBuilt, rtgiNearCookReady;
    // The cache's visible points moved onto their cell's cooked surface (rtgi_rays.comp surfacePoint); the panel's switch,
    // --rtgi-no-surface-train off.
    private bool rtgiSurfaceTrain = true;
    private float rtgiNearCell = 0.05f;
    private int rtgiNearCookMost = 4_000_000;
    // Cells relit a frame at most, and the blend's weight of a frame.
    private int rtgiNearUpdateMost = 262_144;
    private float rtgiNearBlend = 0.25f;
    private RayQueryScene.Instance[] rtgiVolInstances = Array.Empty<RayQueryScene.Instance>();
    private Task<NearCookLayout>? rtgiNearBuild;
    private int rtgiNearCells, rtgiNearTableSlots, rtgiNearCookCursor, rtgiNearUpdateCursor;
    private int rtgiNearCookFirstNow, rtgiNearCookCountNow, rtgiNearCookRaysNow;
    private uint rtgiNearCookFrom;
    private readonly System.Diagnostics.Stopwatch rtgiNearCookClock = new();
    private GpuBufferHandle rtgiNearTable, rtgiNearPoints, rtgiNearLinks, rtgiNearLightBuf;
    private GpuBufferHandle[] rtgiNearCookRayBufs = Array.Empty<GpuBufferHandle>(), rtgiNearCookHitBufs = Array.Empty<GpuBufferHandle>();
    private ShaderInterface rtgiNearCookInterface = null!, rtgiNearUpdateInterface = null!;
    private PassHandle rtgiNearCookRaysPassHandle, rtgiNearCookPassHandle, rtgiNearUpdatePassHandle;
    private PipelineHandle rtgiNearCookPipeline, rtgiNearUpdatePipeline;
    private const int RtgiNearCookChunk = 32768;
    private const int NearRays = 32;
    // rtgi_cache.glsl's rtgiOrigin: the round coordinates along a surface, RTGI_KEY_INSET back along its normal.
    private static Vector3 NearGridOrigin(uint side)
    {
        const float inset = 0.0213f;
        var v = (side & 1) == 0 ? inset : -inset;
        return (side >> 1) switch { 0 => new Vector3(v, 0f, 0f), 1 => new Vector3(0f, v, 0f), _ => new Vector3(0f, 0f, v) };
    }

    private sealed record NearCookLayout(Vector4[] Points, Vector4[] Candidates, uint[] Table, int Slots, double Ms);
    // A cell's samples summed, and one sample kept per 4 x 4 sub-square of it across its surface: its candidate points
    // (rtgi_nearcook_cook.comp's choice). Averaged into one point, a cell a wall stood on most of put its point under the
    // wall; four quarters' means did no better where the open strip was a centimetre wide -- a line down every junction
    // (the user's cooked-state view, 2026-10-30).
    private sealed class NearAcc
    {
        public Vector3 P, N;
        public int Count;
        public readonly Vector3[] Sub = new Vector3[16];
        public int SubMask;
        public void Add(Vector3 p, Vector3 n, int q) { P += p; N += n; Count++; if ((SubMask & (1 << q)) == 0) { Sub[q] = p; SubMask |= 1 << q; } }
        public void Add(NearAcc o)
        {
            P += o.P; N += o.N; Count += o.Count;
            for (var k = 0; k < 16; k++) if ((o.SubMask & (1 << k)) != 0 && (SubMask & (1 << k)) == 0) { Sub[k] = o.Sub[k]; SubMask |= 1 << k; }
        }
    }
    private const int NearCandidates = 16, NearChooseRays = 8;
    private bool rtgiNearChoosePass = true;
    private bool rtgiNearConnecting;
    // A cook dispatch over every cell (the regions' passes: no rays).
    private DispatchCommand NearCookDispatchAll(int cur, int kind, int cells, bool firstLabels)
    {
        rtgiNearCookFirstNow = 0;
        rtgiNearCookCountNow = cells;
        var d = NearCookDispatch(cur, kind, cells, firstLabels);
        rtgiNearCookCountNow = 0;
        return d;
    }
    private GpuBufferHandle rtgiNearCandidates;
    // The regions by connectivity (rtgi_nearcook_cook.comp kinds 6-10): each cell's links, its label and region at each of
    // NearLevels cube sizes, the cube hash. The stage: 0 the links, 1 the labels' passes, 2 the cubes' lowest, 3 the
    // regions, 4 done.
    private const int NearLevels = 6, NearLabelPasses = 96, NearLabelPassesAFrame = 8;
    private GpuBufferHandle rtgiNearAdj, rtgiNearLabels, rtgiNearRegions, rtgiNearCubes;
    private int rtgiNearCubeSlots, rtgiNearConnStage, rtgiNearLabelPass;
    private GpuBufferHandle RtgiNearRegionsOrDummy => rtgiNearCookBuilt ? rtgiNearRegions : rtgiVolDummy;
    // The cache's cell sizes the cooked cells ask for (rtgi_nearcook_cook.comp kinds 11-13: stages 4-6 after the regions):
    // each cell's mean inverse distance (two halves), averaged NearScalePasses times over its linked neighbours, then by
    // 5 cm cube into a table of the near table's slots (rtgi_nearcook.glsl's nearScaleAt).
    private const int NearScalePasses = 4;
    // --rtgi-near-train N (8192; 0 off): N of the hit points a frame are cooked cells' points within --rtgi-near-reach m (6)
    // of the camera, in turn (rtgi_rays.comp), the list remade every 30 frames (rtgi_nearcook_cook.comp kinds 14, 15).
    private int rtgiNearTrain = 8192, rtgiNearTrainCursor;
    private float rtgiNearReach = 6f;
    private GpuBufferHandle rtgiNearList;
    private GpuBufferHandle RtgiNearListOrDummy => rtgiNearCookBuilt ? rtgiNearList : rtgiVolDummy;
    private Vector4 RtgiNearTrainUniform(int hits)
    {
        if (!RtgiNearCookOn || rtgiNearTrain <= 0) return new Vector4(rtgiNearField, 0f, 0f, 0f);
        var n = Math.Min(rtgiNearTrain, hits);
        var at = rtgiNearTrainCursor;
        rtgiNearTrainCursor = (rtgiNearTrainCursor + n) & 0x3FFFFFFF;
        return new Vector4(rtgiNearField, n, at, 0f);
    }
    // BLIX_RTGI_NEAR_PROBE="x,y,z": once cooked, the cooked cells within 6 cm of the point written to the log -- their point,
    // side and flags, candidates, light (the crevices testbed's black strips, 2026-10-30).
    private bool rtgiNearProbePending;
    private void ProbeRtgiNearCells()
    {
        var at = Environment.GetEnvironmentVariable("BLIX_RTGI_NEAR_PROBE")!.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var q = new Vector3(at[0], at[1], at[2]);
        device.WaitIdle();
        var pts = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtgiNearPoints, 0, rtgiNearCells * 32)).ToArray();
        var cands = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtgiNearCandidates, 0, rtgiNearCells * NearCandidates * 16)).ToArray();
        var light = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(rtgiNearLightBuf, 0, rtgiNearCells * 16)).ToArray();
        for (var i = 0; i < rtgiNearCells; i++)
        {
            var p = new Vector3(pts[2 * i].X, pts[2 * i].Y, pts[2 * i].Z);
            if (Vector3.Distance(p, q) > 0.06f) continue;
            var w = BitConverter.SingleToUInt32Bits(pts[2 * i].W);
            var cs = string.Join(" ", Enumerable.Range(0, NearCandidates).Select(c => cands[NearCandidates * i + c]).Where(c => c.W > 0.5f).Select(c => $"({c.X:0.0000},{c.Y:0.0000},{c.Z:0.0000})"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] near cell {i}: p=({p.X:0.0000},{p.Y:0.0000},{p.Z:0.0000}) side {w & 7} buried {(w & 16) != 0} n=({pts[2 * i + 1].X:0.00},{pts[2 * i + 1].Y:0.00},{pts[2 * i + 1].Z:0.00}) light w {light[i].W:0.00}; candidates {cs}"));
        }
    }
    private GpuBufferHandle rtgiNearScale, rtgiNearScaleTable;
    private int rtgiNearScalePass;
    private GpuBufferHandle RtgiNearScaleTableOrDummy => rtgiNearCookBuilt ? rtgiNearScaleTable : rtgiVolDummy;
    private GpuBufferHandle RtgiNearAdjOrDummy => rtgiNearCookBuilt ? rtgiNearAdj : rtgiVolDummy;

    private bool RtgiNearCookOn => rtgiNearCook && rtgiNearCookReady;
    // rtgi_nearcook.glsl's NEAR_PARAMS.
    private Vector4 RtgiNearParams => new(rtgiNearCell, Math.Max(rtgiNearTableSlots, 1) - 1, rtgiNearCells, RtgiNearCookOn ? 1f : 0f);
    private GpuBufferHandle RtgiNearTableOrDummy => rtgiNearCookBuilt ? rtgiNearTable : rtgiVolDummy;
    private GpuBufferHandle RtgiNearLightOrDummy => rtgiNearCookBuilt ? rtgiNearLightBuf : rtgiVolDummy;
    private GpuBufferHandle RtgiNearPointsOrDummy => rtgiNearCookBuilt ? rtgiNearPoints : rtgiVolDummy;

    private void ParseRtgiNearCook(Blix.Core.AppArgs args)
    {
        if (args.Flag("rtgi-no-near-cook")) rtgiNearCook = false;
        if (args.Flag("rtgi-no-surface-train")) rtgiSurfaceTrain = false;
        if (args.Float("rtgi-near-cell") is { } nc) rtgiNearCell = MathF.Max(0.01f, nc);
        if (args.Int("rtgi-near-cook-most") is { } most) rtgiNearCookMost = Math.Max(1024, most);
        if (args.Float("rtgi-near-blend") is { } nb) rtgiNearBlend = Math.Clamp(nb, 0.01f, 1f);
        if (args.Int("rtgi-near-train") is { } nt) rtgiNearTrain = Math.Max(0, nt);
        if (args.Float("rtgi-near-reach") is { } nr) rtgiNearReach = MathF.Max(0.5f, nr);
    }

    private void DeclareRtgiNearCookPasses(Func<string, ShaderInterface> reflect)
    {
        rtgiNearCookInterface = reflect("rtgi_nearcook_cook.comp");
        rtgiNearCookRaysPassHandle = graph.ComputePass("rtgi-near-cook-rays").AfterAsync().Shader(rtgiNearCookInterface).Handle;
    }

    private void DeclareRtgiNearCookAsyncPasses(Func<string, ShaderInterface> reflect)
    {
        rtgiNearCookPassHandle = graph.ComputePass("rtgi-near-cook").Async(rtgiAsync).Shader(rtgiNearCookInterface).Handle;
        rtgiNearUpdateInterface = reflect("rtgi_nearcook_update.comp");
        rtgiNearUpdatePassHandle = graph.ComputePass("rtgi-near-update").Async(rtgiAsync).Shader(rtgiNearUpdateInterface).Handle;
    }

    private void CreateRtgiNearCookPipelines(Func<string, ShaderInterface, ComputePipelineOptions, PipelineHandle> compute)
    {
        rtgiNearCookPipeline = compute("rtgi_nearcook_cook", rtgiNearCookInterface, ComputePipelineOptions.None);
        rtgiNearUpdatePipeline = compute("rtgi_nearcook_update", rtgiNearUpdateInterface, ComputePipelineOptions.None);
    }

    private static uint NearHash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352du;
        x ^= x >> 15; x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    // rtgi_nearcook.glsl's nearCellHash.
    private static (uint Start, uint Key) NearCellHash(int x, int y, int z, uint side, uint mask)
    {
        unchecked
        {
            var a = NearHash((uint)x * 0x9E3779B1u ^ NearHash((uint)y * 0x85EBCA77u ^ NearHash((uint)z * 0xC2B2AE3Du ^ (side + 0x3A1u))));
            var k = NearHash(a ^ 0x2545F491u ^ (uint)x * 0x1B873593u ^ (uint)y * 0xCC9E2D51u ^ (uint)z * 0x27D4EB2Fu) | 1u;
            return (a & mask, k);
        }
    }

    private static uint NearSide(Vector3 n)
    {
        var a = Vector3.Abs(n);
        if (a.X >= a.Y && a.X >= a.Z) return n.X >= 0f ? 0u : 1u;
        if (a.Y >= a.Z) return n.Y >= 0f ? 2u : 3u;
        return n.Z >= 0f ? 4u : 5u;
    }

    // Once the volume is ready: the cells placed from the triangles inside its refined regions, on a worker.
    private void StartRtgiNearCookBuild()
    {
        if (!rtgiNearCook || rtgiNearBuild is not null || !rtgiVolReady || rtgiVolInstances.Length == 0) return;
        var instances = rtgiVolInstances;
        rtgiVolInstances = Array.Empty<RayQueryScene.Instance>();
        var fineSets = new[] { (Set: new HashSet<long>(rtgiVolSets[0]), S: VolSpacingOf(0)), (Set: new HashSet<long>(rtgiVolSets[1]), S: VolSpacingOf(1)) };
        var cell = rtgiNearCell;
        var most = rtgiNearCookMost;
        rtgiNearBuild = Task.Run(() => BuildNearCookLayout(instances, fineSets, cell, most));
    }

    private static NearCookLayout BuildNearCookLayout(RayQueryScene.Instance[] instances, (HashSet<long> Set, float S)[] fine, float cell, int most)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // Inside the refined regions: a point whose probe cell, at either fine level, has one of its 8 probes placed.
        bool Inside(Vector3 p)
        {
            foreach (var (set, s) in fine)
            {
                if (set.Count == 0) continue;
                var g = p / s - new Vector3(0.5f) - VolJitter;
                int x = (int)MathF.Floor(g.X), y = (int)MathF.Floor(g.Y), z = (int)MathF.Floor(g.Z);
                for (var k = 0; k < 8; k++)
                    if (set.Contains(VolKey(x + (k & 1), y + ((k >> 1) & 1), z + (k >> 2)))) return true;
            }
            return false;
        }
        // BLIX_RTGI_LOW_VERTS: the traced triangles' distinct vertex heights near the ground (0.1 mm), by the side the
        // triangle faces -- whether walls sit on the floor as the source has them (the user, crevices, 2026-10-30).
        if (Environment.GetEnvironmentVariable("BLIX_RTGI_LOW_VERTS") is { Length: > 0 })
        {
            var low = new SortedDictionary<(double, string), int>();
            foreach (var inst in instances)
            {
                var pos = inst.Mesh.Positions; var idx = inst.Mesh.Indices;
                for (var t = 0; t + 2 < idx.Length; t += 3)
                {
                    var a = Vector3.Transform(pos[idx[t]], inst.World); var b = Vector3.Transform(pos[idx[t + 1]], inst.World); var c = Vector3.Transform(pos[idx[t + 2]], inst.World);
                    var nn = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                    var face = MathF.Abs(nn.Y) > 0.9f ? (nn.Y > 0 ? "+y" : "-y") : "side";
                    foreach (var v in new[] { a, b, c })
                        if (MathF.Abs(v.Y) < 0.1f) { var key = (Math.Round(v.Y, 4), face); low[key] = low.GetValueOrDefault(key) + 1; }
                }
            }
            Console.WriteLine($"[VulkanSponza] rtgi low verts ({instances.Length} instances): " + string.Join(", ", low.Select(kv => $"y={kv.Key.Item1:0.0000} {kv.Key.Item2} x{kv.Value}")));
        }
        var cells = new ConcurrentDictionary<(int, int, int, uint), NearAcc>();
        var h = cell * 0.25f;
        Parallel.ForEach(instances, inst =>
        {
            var local = new Dictionary<(int, int, int, uint), NearAcc>();
            var pos = inst.Mesh.Positions;
            var idx = inst.Mesh.Indices;
            for (var t = 0; t + 2 < idx.Length; t += 3)
            {
                var a = Vector3.Transform(pos[idx[t]], inst.World);
                var b = Vector3.Transform(pos[idx[t + 1]], inst.World);
                var c = Vector3.Transform(pos[idx[t + 2]], inst.World);
                var nrm = Vector3.Cross(b - a, c - a);
                var len = nrm.Length();
                if (!(len > 1e-12f)) continue;
                nrm /= len;
                // Quick reject: none of its corners nor its centre inside, and small enough that none of it is.
                var big = MathF.Max((b - a).Length(), MathF.Max((c - a).Length(), (c - b).Length()));
                if (big < 0.5f && !Inside(a) && !Inside(b) && !Inside(c) && !Inside((a + b + c) / 3f)) continue;
                var side = NearSide(nrm);
                var nu = Math.Max(1, (int)MathF.Ceiling((b - a).Length() / h));
                var nv = Math.Max(1, (int)MathF.Ceiling((c - a).Length() / h));
                for (var i = 0; i <= nu; i++)
                for (var j = 0; j <= nv; j++)
                {
                    float u = i / (float)nu, v = j / (float)nv;
                    if (u + v > 1f) continue;
                    var p = a + (b - a) * u + (c - a) * v;
                    if (!Inside(p)) continue;
                    // rtgi_cache.glsl's rtgiOrigin.
                    var g = (p - NearGridOrigin(side)) / cell;
                    var key = ((int)MathF.Floor(g.X), (int)MathF.Floor(g.Y), (int)MathF.Floor(g.Z), side);
                    if (!local.TryGetValue(key, out var e)) local[key] = e = new NearAcc();
                    // Its 4 x 4 sub-square of the cell across its surface (the side's two other axes).
                    int axis = (int)(side >> 1), a0 = (axis + 1) % 3, a1 = (axis + 2) % 3;
                    float f0 = a0 == 0 ? g.X : a0 == 1 ? g.Y : g.Z, f1 = a1 == 0 ? g.X : a1 == 1 ? g.Y : g.Z;
                    var q = Math.Min((int)((f0 - MathF.Floor(f0)) * 4f), 3) + 4 * Math.Min((int)((f1 - MathF.Floor(f1)) * 4f), 3);
                    e.Add(p, nrm, q);
                }
            }
            foreach (var (k, v) in local)
                cells.AddOrUpdate(k, v, (_, e) => { lock (e) e.Add(v); return e; });
        });
        var list = cells.Take(most).ToArray();
        var points = new Vector4[list.Length * 2];
        var candidates = new Vector4[list.Length * NearCandidates];
        for (var i = 0; i < list.Length; i++)
        {
            var ((_, _, _, side), acc) = list[i];
            var (p, n, count) = (acc.P, acc.N, acc.Count);
            for (var q = 0; q < NearCandidates; q++)
                candidates[NearCandidates * i + q] = (acc.SubMask & (1 << q)) != 0 ? new Vector4(acc.Sub[q], 1f) : Vector4.Zero;
            points[2 * i] = new Vector4(p / count, BitConverter.UInt32BitsToSingle(side));
            var nn = n.Length() > 1e-6f ? Vector3.Normalize(n) : Vector3.UnitY;
            points[2 * i + 1] = new Vector4(nn, 0f);
        }
        var slots = 1024;
        while (slots < list.Length * 2) slots <<= 1;
        uint[] table;
        while (true)
        {
            table = new uint[slots * 2];
            var fits = true;
            for (var i = 0; i < list.Length && fits; i++)
            {
                var ((x, y, z, side), _) = list[i];
                var (start, key) = NearCellHash(x, y, z, side, (uint)(slots - 1));
                var placed = false;
                for (uint k = 0; k < 8 && !placed; k++)
                {
                    var at = (int)((start + k) & (uint)(slots - 1));
                    if (table[2 * at] != 0) continue;
                    table[2 * at] = key; table[2 * at + 1] = (uint)i; placed = true;
                }
                fits = placed;
            }
            if (fits) break;
            slots <<= 1;
        }
        return new NearCookLayout(points, candidates, table, slots, clock.Elapsed.TotalMilliseconds);
    }

    private void TakeRtgiNearCookLayout()
    {
        if (rtgiNearCookBuilt || rtgiNearBuild is not { IsCompleted: true } task) return;
        var layout = task.Result;
        rtgiNearBuild = Task.FromResult(layout with { Points = Array.Empty<Vector4>(), Candidates = Array.Empty<Vector4>(), Table = Array.Empty<uint>() });
        rtgiNearCells = layout.Points.Length / 2;
        rtgiNearTableSlots = layout.Slots;
        var cells = Math.Max(rtgiNearCells, 1);
        rtgiNearTable = Own(device.CreateGpuBuffer(layout.Table.Length * 4, MemoryMarshal.AsBytes(layout.Table.AsSpan()).ToArray(), "sponza.rtgi.near-table"));
        rtgiNearPoints = Own(device.CreateGpuBuffer(cells * 32, rtgiNearCells > 0 ? MemoryMarshal.AsBytes(layout.Points.AsSpan()).ToArray() : new byte[32], "sponza.rtgi.near-points"));
        rtgiNearAdj = Own(device.CreateGpuBuffer(cells * 4, new byte[cells * 4], "sponza.rtgi.near-adj"));
        rtgiNearLabels = Own(device.CreateGpuBuffer(cells * NearLevels * 4, new byte[cells * NearLevels * 4], "sponza.rtgi.near-labels"));
        rtgiNearRegions = Own(device.CreateGpuBuffer(cells * NearLevels * 4, new byte[cells * NearLevels * 4], "sponza.rtgi.near-regions"));
        rtgiNearCubeSlots = 1024;
        while (rtgiNearCubeSlots < cells * NearLevels * 2) rtgiNearCubeSlots <<= 1;
        var cubes = new uint[rtgiNearCubeSlots * 2];
        for (var k = 1; k < cubes.Length; k += 2) cubes[k] = 0xFFFFFFFFu;
        rtgiNearCubes = Own(device.CreateGpuBuffer(cubes.Length * 4, MemoryMarshal.AsBytes(cubes.AsSpan()).ToArray(), "sponza.rtgi.near-cubes"));
        rtgiNearList = Own(device.CreateGpuBuffer(16 + cells * 4, new byte[16 + cells * 4], "sponza.rtgi.near-list"));
        rtgiNearScale = Own(device.CreateGpuBuffer(cells * 8, new byte[cells * 8], "sponza.rtgi.near-scale"));
        var scaleTable = new uint[layout.Slots * 2];
        for (var k = 1; k < scaleTable.Length; k += 2) scaleTable[k] = 0xFFFFFFFFu;
        rtgiNearScaleTable = Own(device.CreateGpuBuffer(scaleTable.Length * 4, MemoryMarshal.AsBytes(scaleTable.AsSpan()).ToArray(), "sponza.rtgi.near-scale-table"));
        rtgiNearCandidates = Own(device.CreateGpuBuffer(cells * NearCandidates * 16, rtgiNearCells > 0 ? MemoryMarshal.AsBytes(layout.Candidates.AsSpan()).ToArray() : new byte[NearCandidates * 16], "sponza.rtgi.near-candidates"));
        rtgiNearLinks = Own(device.CreateGpuBuffer(cells * NearRays * 8, new byte[cells * NearRays * 8], "sponza.rtgi.near-links"));
        rtgiNearLightBuf = Own(device.CreateGpuBuffer(cells * 16, new byte[cells * 16], "sponza.rtgi.near-light"));
        rtgiNearCookRayBufs = Enumerable.Range(0, 2).Select(k => Own(device.CreateGpuBuffer(RtgiNearCookChunk * NearRays * 32, name: $"sponza.rtgi.near-cook-rays{k}"))).ToArray();
        rtgiNearCookHitBufs = Enumerable.Range(0, 2).Select(k => Own(device.CreateGpuBuffer(RtgiNearCookChunk * NearRays * 48, name: $"sponza.rtgi.near-cook-hits{k}"))).ToArray();
        rtgiNearCookBuilt = true;
        rtgiNearCookFrom = rtgiFrameCount;
        rtgiNearCookClock.Restart();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] rtgi near cook: {rtgiNearCells:N0} cells of {rtgiNearCell * 100:0.#} cm in the volume's refined regions ({(cells * (32L + NearRays * 8 + 16) + layout.Table.Length * 4L) / 1048576.0:0} MB), table {layout.Slots:N0} slots; placed from the triangles in {layout.Ms:0} ms."));
        if (rtgiNearCells == 0) rtgiNearCookReady = true;
    }

    private DispatchCommand NearCookDispatch(int cur, int kind, int threads, bool firstLabels = false) => new(rtgiNearCookPipeline, (threads + 63) / 64, 1, 1,
        new ShaderUniform[]
        {
            new("uNearCook", new Vector4Uniform(RtgiNearParams)),
            new("uCook", new Vector4Uniform(new Vector4(rtgiNearCookFirstNow, rtgiNearCookCountNow, rtgiContactRadius, kind))),
            new("uVolume", new Vector4Uniform(RtgiVolumeParams)),
            new("uConn", new Vector4Uniform(new Vector4(firstLabels ? 1f : 0f, Math.Max(rtgiNearCubeSlots, 1) - 1, rtgiNearScalePass & 1, 0f))),
            new("uGeom", new Vector4Uniform(RtgiVolumeGeom)),
            new("uNear", new Vector4Uniform(new Vector4(cameraPosition, rtgiNearReach))),
        },
        Array.Empty<ShaderTextureBinding>(),
        Buffers: new[]
        {
            new ShaderBufferBinding("RtgiNearPoints", rtgiNearPoints),
            new ShaderBufferBinding("RtgiNearCookRays", rtgiNearCookRayBufs[cur]),
            new ShaderBufferBinding("RtgiNearCookHits", rtgiNearCookHitBufs[cur]),
            new ShaderBufferBinding("RtgiNearLinks", rtgiNearLinks),
            new ShaderBufferBinding("RtgiNearLight", rtgiNearLightBuf),
            new ShaderBufferBinding("RtgiVolumeTable", RtgiVolTableOrDummy),
            new ShaderBufferBinding("RtgiVolumeProbes", RtgiVolProbesOrDummy),
            new ShaderBufferBinding("RtgiNearCandidates", rtgiNearCandidates),
            new ShaderBufferBinding("RtgiNearAdj", rtgiNearAdj),
            new ShaderBufferBinding("RtgiNearLabels", rtgiNearLabels),
            new ShaderBufferBinding("RtgiNearRegions", rtgiNearRegions),
            new ShaderBufferBinding("RtgiNearCubes", rtgiNearCubes),
            new ShaderBufferBinding("RtgiNearTable", rtgiNearTable),
            new ShaderBufferBinding("RtgiNearScale", rtgiNearScale),
            new ShaderBufferBinding("RtgiNearScaleTable", rtgiNearScaleTable),
            new ShaderBufferBinding("RtgiNearList", rtgiNearList),
        });

    // Placement time: the next chunk's rays (traced in the Metal batch: RtgiNearCookRaysNow); with the training their
    // links. Then, cooked, each frame's relight.
    private void RecordRtgiNearCook(int cur, Vector3 towardSun)
    {
        rtgiNearCookCountNow = 0;
        if (!rtgiNearCook) return;
        StartRtgiNearCookBuild();
        TakeRtgiNearCookLayout();
        if (!rtgiNearCookBuilt || rtgiNearCells == 0) return;
        if (!rtgiNearCookReady && !rtgiNearConnecting)
        {
            rtgiNearCookFirstNow = rtgiNearCookCursor;
            // First each cell's point chosen among its candidates (8 test rays each), then its links, then its region.
            if (rtgiNearChoosePass)
            {
                var chooseChunk = RtgiNearCookChunk * NearRays / (NearCandidates * NearChooseRays);
                rtgiNearCookCountNow = Math.Min(chooseChunk, rtgiNearCells - rtgiNearCookCursor);
                rtgiNearCookRaysNow = rtgiNearCookCountNow * NearCandidates * NearChooseRays;
                graph.Dispatch(rtgiNearCookRaysPassHandle, NearCookDispatch(cur, 4, rtgiNearCookRaysNow));
                graph.Dispatch(rtgiNearCookPassHandle, NearCookDispatch(cur, 5, rtgiNearCookCountNow));
                rtgiNearCookCursor += rtgiNearCookCountNow;
                if (rtgiNearCookCursor >= rtgiNearCells) { rtgiNearChoosePass = false; rtgiNearCookCursor = 0; }
                return;
            }
            rtgiNearCookCountNow = Math.Min(RtgiNearCookChunk, rtgiNearCells - rtgiNearCookCursor);
            rtgiNearCookRaysNow = rtgiNearCookCountNow * NearRays;
            graph.Dispatch(rtgiNearCookRaysPassHandle, NearCookDispatch(cur, 0, rtgiNearCookRaysNow));
            graph.Dispatch(rtgiNearCookPassHandle, NearCookDispatch(cur, 1, rtgiNearCookCountNow));
            rtgiNearCookCursor += rtgiNearCookCountNow;
            if (rtgiNearCookCursor >= rtgiNearCells)
            {
                rtgiNearConnStage = 0;
                rtgiNearCookCursor = 0;
                rtgiNearConnecting = true;
            }
            return;
        }
        if (!rtgiNearCookReady && rtgiNearConnecting)
        {
            // The regions by connectivity: the links (8 rays a cell, chunked), the labels' passes (all cells, several a
            // frame), each cube's lowest, the regions.
            if (rtgiNearConnStage == 0)
            {
                rtgiNearCookFirstNow = rtgiNearCookCursor;
                rtgiNearCookCountNow = Math.Min(RtgiNearCookChunk, rtgiNearCells - rtgiNearCookCursor);
                rtgiNearCookRaysNow = rtgiNearCookCountNow * 8;
                graph.Dispatch(rtgiNearCookRaysPassHandle, NearCookDispatch(cur, 6, rtgiNearCookRaysNow));
                graph.Dispatch(rtgiNearCookPassHandle, NearCookDispatch(cur, 7, rtgiNearCookCountNow));
                rtgiNearCookCursor += rtgiNearCookCountNow;
                if (rtgiNearCookCursor >= rtgiNearCells) { rtgiNearConnStage = 1; rtgiNearCookCursor = 0; }
                return;
            }
            rtgiNearCookFirstNow = 0;
            rtgiNearCookCountNow = 0;
            var all = rtgiNearCells;
            if (rtgiNearConnStage == 1)
            {
                for (var k = 0; k < NearLabelPassesAFrame && rtgiNearLabelPass < NearLabelPasses; k++, rtgiNearLabelPass++)
                    graph.Dispatch(rtgiNearCookPassHandle, NearCookDispatchAll(cur, 8, all, rtgiNearLabelPass == 0));
                if (rtgiNearLabelPass >= NearLabelPasses) rtgiNearConnStage = 2;
                return;
            }
            if (rtgiNearConnStage == 2) { graph.Dispatch(rtgiNearCookPassHandle, NearCookDispatchAll(cur, 9, all, false)); rtgiNearConnStage = 3; return; }
            if (rtgiNearConnStage == 3) { graph.Dispatch(rtgiNearCookPassHandle, NearCookDispatchAll(cur, 10, all, false)); rtgiNearConnStage = 4; return; }
            // The cell sizes: the raw, the passes (one a frame: each reads the other half), the table.
            if (rtgiNearConnStage == 4) { rtgiNearScalePass = 0; graph.Dispatch(rtgiNearCookPassHandle, NearCookDispatchAll(cur, 11, all, false)); rtgiNearConnStage = 5; return; }
            if (rtgiNearConnStage == 5)
            {
                graph.Dispatch(rtgiNearCookPassHandle, NearCookDispatchAll(cur, 12, all, false));
                if (++rtgiNearScalePass >= NearScalePasses) rtgiNearConnStage = 6;
                return;
            }
            graph.Dispatch(rtgiNearCookPassHandle, NearCookDispatchAll(cur, 13, all, false));
            rtgiNearConnecting = false;
            {
                rtgiNearCookReady = true;
                rtgiNearProbePending = Environment.GetEnvironmentVariable("BLIX_RTGI_NEAR_PROBE") is { Length: > 0 };
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] rtgi near cook: {rtgiNearCells:N0} cells traced ({rtgiNearCells * (long)NearRays / 1e6:0.0} M rays) in {rtgiFrameCount - rtgiNearCookFrom + 1} frames, {rtgiNearCookClock.Elapsed.TotalSeconds:0.0} s."));
            }
            return;
        }
        if (rtgiNearProbePending && rtgiFrameCount > rtgiNearCookFrom + 4) { rtgiNearProbePending = false; ProbeRtgiNearCells(); }
        // The cells near the camera listed again (--rtgi-near-train).
        if (rtgiNearTrain > 0 && rtgiFrameCount % 30 == 0)
        {
            graph.Dispatch(rtgiNearCookPassHandle, NearCookDispatchAll(cur, 14, 1, false));
            graph.Dispatch(rtgiNearCookPassHandle, NearCookDispatchAll(cur, 15, rtgiNearCells, false));
        }
        var count = Math.Min(rtgiNearCells, rtgiNearUpdateMost);
        var first = rtgiNearUpdateCursor;
        rtgiNearUpdateCursor = (rtgiNearUpdateCursor + count) % rtgiNearCells;
        graph.Dispatch(rtgiNearUpdatePassHandle, new DispatchCommand(rtgiNearUpdatePipeline, (count + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uParams", new Vector4Uniform(RtgiParams)),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                new("uSun", new Vector4Uniform(new Vector4(towardSun, 0f))),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
                new("uNearCook", new Vector4Uniform(RtgiNearParams)),
                new("uUpdate", new Vector4Uniform(new Vector4(first, count, rtgiNearBlend, rtgiContactRadius))),
                new("uVolume", new Vector4Uniform(RtgiVolumeParams)),
                new("uVolumeGeom", new Vector4Uniform(RtgiVolumeGeom)),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiNearPoints", rtgiNearPoints),
                new ShaderBufferBinding("RtgiNearLinks", rtgiNearLinks),
                new ShaderBufferBinding("RtgiNearLight", rtgiNearLightBuf),
                new ShaderBufferBinding("RtgiNearRegions", rtgiNearRegions),
                new ShaderBufferBinding("RtgiNearAdj", rtgiNearAdj),
                new ShaderBufferBinding("RtgiNearScaleTable", rtgiNearScaleTable),
                new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                new ShaderBufferBinding("RtgiRecord", rtgiRecord),
                new ShaderBufferBinding("RtgiResolved", rtgiResolved),
                new ShaderBufferBinding("RtgiVolumeTable", RtgiVolTableOrDummy),
                new ShaderBufferBinding("RtgiVolumeProbes", RtgiVolProbesOrDummy),
                new ShaderBufferBinding("RtgiNearTable", RtgiNearTableOrDummy),
            }));
    }
}
