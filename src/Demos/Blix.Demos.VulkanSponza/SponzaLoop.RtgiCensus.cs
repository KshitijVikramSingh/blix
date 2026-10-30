using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;

namespace Blix.Demos.VulkanSponza;

// --rtgi-census [--rtgi-census-out DIR]: a measurement, not GI. The cache is to be cooked per world chunk -- its cells'
// layout from the geometry alone, the light real-time -- and what to ship, what to rebuild at load and what to work out
// at run time, and the chunks' size, are to be chosen from numbers (2026-10-30). Once the ray scene is up the census:
//
//   geometry  the static instances' triangles, their world area and bounds (on a worker)
//   coarse    a surface sample every ~25 cm (CensusSpacing; one a triangle at least), each tracing CensusRays cosine rays
//             through the Metal batch (rtgi_census.comp, a chunk a frame): its harmonic hit distance (escapes adding
//             nothing to the sum, as the near cook's sizes do), its back-face share (over half: buried, inside something),
//             the share met within 25 cm, and from them the cache's cell size, clamp(0.1 x harmonic, 5 cm, 1 m)
//   cells     every triangle sampled again at half its local cell size (the smallest of the nearest coarse samples
//             at a 25 cm lattice over it, by a hash grid), each point's cell at its own level (its lattice point's
//             sample's size; none where that sample is buried) and every level above it -- the cells a cooked cache
//             would hold, by level, and those that are some surface's own (finest) level (on a worker)
//   pricing   bytes and rays by the per-item costs below, the times at the measured trace rate, whole and per chunk of
//             16, 32 and 64 m
//
// written to DIR/<scene>.json and .txt, a line to the log, and the demo closes. The cache is left untrained meanwhile
// (RecordRtgi), the world volume and the near cook off (ParseRtgiCensus): the trace's time is the census's own. Nothing
// here runs without the flag. Approximations: cells keyed by coordinates and side alone (no regions); the cell grid's
// origin as rtgi_cache.glsl's; the ray scene's meshes (its LOD) are the geometry; past --rtgi-census-most samples (12 M)
// the coarse samples are a random share of the full set, each standing for the rest's area.
internal sealed partial class SponzaLoop
{
    private bool rtgiCensus;
    private string rtgiCensusOut = "census";
    // Coarse samples a frame (64 rays each: 1 M rays at 16,384, the near cook's chunk) and the most traced at all.
    private int rtgiCensusChunk = 16384;
    private long rtgiCensusMost = 12_000_000;
    private const float CensusSpacing = 0.25f, CensusReach = 64f, CensusNear = 0.25f, CensusLift = 0.01f;
    private const int CensusRays = 64;
    // The slice of a 60 Hz frame the cook and the settling would be given: 2 ms of 16.7.
    private const double CensusSlice = 2.0 / 16.7;
    // The per-item costs priced (bytes, rays), stated in the report.
    private const int CensusShipSizeField = 1;       // B a coarse sample: its cell size
    private const int CensusShipCell = 2;            // B a cell: the chosen candidate + buried, its neighbour visibility (8 bits)
    private const int CensusNearCompact = 24;        // B a level-0 cell: the near transfer, compact
    private const int CensusNearToday = 256;         // B a level-0 cell: today's 32 per-ray links (8 B each)
    private const int CensusInsideRays = 16 * 8;     // rays a level-0/1 cell: the inside test, 16 candidates x 8 rays
    private const int CensusNeighbourRays = 8;       // rays a cell: its neighbours' visibility
    private const int CensusNearRays = 32;           // rays a level-0 cell: the near transfer (optional)
    // The cache's GPU bytes a slot today (SponzaLoop.Rtgi.cs RecordRtgi, SponzaLoop.RtgiVolume.cs): keys 4, accum 64
    // (RTGI_ACC 16 words), direction 16, confidence 16, hit-inv 4, record 32, seq 4, stands 4, resolved 32, moments 48,
    // stamps 8, cells 16, filtered 16, the volume's cell visibility 8.
    private static readonly (string Name, int Bytes)[] CensusSlotBytes =
    {
        ("keys", 4), ("accum", 64), ("direction", 16), ("confidence", 16), ("hit-inv", 4), ("record", 32), ("seq", 4),
        ("stands", 4), ("resolved", 32), ("moments", 48), ("stamps", 8), ("cells", 16), ("filtered", 16), ("volume-cell-vis", 8),
    };

    private int rtgiCensusDynamic;
    private RayQueryScene.Instance[] rtgiCensusInstances = Array.Empty<RayQueryScene.Instance>();
    private Task<CensusLayout>? rtgiCensusBuild;
    private CensusLayout? rtgiCensusLayout;
    private Task<string>? rtgiCensusReport;
    // 0 waiting for the samples, 1 tracing, 2 draining, 3 counting the cells, 4 done.
    private int rtgiCensusStage, rtgiCensusCursor, rtgiCensusDrain, rtgiCensusFrames;
    private uint rtgiCensusParity;
    private GpuBufferHandle rtgiCensusSamples, rtgiCensusResults;
    private GpuBufferHandle[] rtgiCensusRayBufs = Array.Empty<GpuBufferHandle>(), rtgiCensusHitBufs = Array.Empty<GpuBufferHandle>();
    private ShaderInterface rtgiCensusInterface = null!;
    private PassHandle rtgiCensusRaysPassHandle, rtgiCensusReducePassHandle;
    private PipelineHandle rtgiCensusPipeline;
    private readonly System.Diagnostics.Stopwatch rtgiCensusClock = new();
    private MetalShadowStats rtgiCensusTraceFrom;

    private void ParseRtgiCensus(AppArgs args)
    {
        rtgiCensus = args.Flag("rtgi-census");
        if (args.String("rtgi-census-out") is { Length: > 0 } dir) rtgiCensusOut = dir;
        if (args.Int("rtgi-census-chunk") is { } chunk) rtgiCensusChunk = Math.Clamp(chunk, 1024, 65536);
        if (args.Long("rtgi-census-most") is { } most) rtgiCensusMost = Math.Max(65536, most);
        if (!rtgiCensus) return;
        // Alone on the GPU: the volume's and the near cook's rays would share the trace's time.
        rtgiVolume = false;
        rtgiNearCook = false;
    }

    // Setup, beside the cache's placement: the rays' kind, before the trace.
    private void DeclareRtgiCensusRaysPass(Func<string, ShaderInterface> reflect)
    {
        if (!rtgiCensus) return;
        rtgiCensusInterface = reflect("rtgi_census.comp");
        rtgiCensusRaysPassHandle = graph.ComputePass("rtgi-census-rays").AfterAsync().Shader(rtgiCensusInterface).Handle;
    }

    // Setup, with the cache's update: the hits' reduction, after the trace.
    private void DeclareRtgiCensusReducePass()
    {
        if (!rtgiCensus) return;
        rtgiCensusReducePassHandle = graph.ComputePass("rtgi-census-reduce").Async(rtgiAsync).Shader(rtgiCensusInterface).Handle;
    }

    private void CreateRtgiCensusPipeline(Func<string, ShaderInterface, PipelineHandle> compute)
    {
        if (rtgiCensus) rtgiCensusPipeline = compute("rtgi_census", rtgiCensusInterface);
    }

    // FinishRayScene: the static instances, their samples placed on a worker.
    private void StartRtgiCensus(RayQueryScene scene)
    {
        if (!rtgiCensus || rtgiCensusBuild is not null) return;
        var instances = scene.Instances.Where(i => !i.Dynamic).ToArray();
        rtgiCensusDynamic = scene.Instances.Count - instances.Length;
        rtgiCensusInstances = instances;
        var most = rtgiCensusMost;
        DebugProgress.Begin("rtgi census: samples", 0);
        rtgiCensusBuild = Task.Run(() => BuildCensusLayout(instances, most));
    }

    // The coarse samples: xyz and the packed normal for the GPU, and what the CPU's count of cells reads.
    private sealed record CensusLayout(
        Vector4[] Gpu, Vector3[] Pos, byte[] Side, float[] Area, long Triangles, long Degenerate, double SurfaceArea,
        Vector3 Min, Vector3 Max, long FullSamples, double Keep, double Ms);

    // The triangles in runs of at most `run`, the workers' items (one mesh may be most of a scene).
    private static (int Inst, int First, int Count)[] CensusWork(RayQueryScene.Instance[] instances, int run = 65536)
    {
        var work = new List<(int, int, int)>();
        for (var i = 0; i < instances.Length; i++)
        {
            var tris = instances[i].Mesh.TriangleCount;
            for (var f = 0; f < tris; f += run) work.Add((i, f, Math.Min(run, tris - f)));
        }
        return work.ToArray();
    }

    private static (Vector3 A, Vector3 B, Vector3 C) CensusTriangle(in RayQueryScene.Instance inst, int t)
    {
        var pos = inst.Mesh.Positions;
        var idx = inst.Mesh.Indices;
        return (Vector3.Transform(pos[idx[3 * t]], inst.World), Vector3.Transform(pos[idx[3 * t + 1]], inst.World),
            Vector3.Transform(pos[idx[3 * t + 2]], inst.World));
    }

    private static int CensusSamplesOf(float area) => Math.Max(1, (int)MathF.Round(area / (CensusSpacing * CensusSpacing)));

    private static uint CensusOctPack(Vector3 n)
    {
        var s = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
        float x = n.X / s, y = n.Y / s;
        if (n.Z < 0f)
        {
            var ox = (1f - MathF.Abs(y)) * (x >= 0f ? 1f : -1f);
            var oy = (1f - MathF.Abs(x)) * (y >= 0f ? 1f : -1f);
            x = ox; y = oy;
        }
        var qx = (uint)(int)MathF.Round(Math.Clamp(x, -1f, 1f) * 32767f) & 0xFFFFu;
        var qy = (uint)(int)MathF.Round(Math.Clamp(y, -1f, 1f) * 32767f) & 0xFFFFu;
        return qx | (qy << 16);
    }

    private static CensusLayout BuildCensusLayout(RayQueryScene.Instance[] instances, long most)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var work = CensusWork(instances);
        // First the totals: the triangles, their area and bounds, the samples the full set would have.
        long triangles = 0, degenerate = 0, full = 0;
        double area = 0;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var gate = new object();
        Parallel.ForEach(work, item =>
        {
            long tris = 0, degen = 0, samples = 0;
            double a = 0;
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            var inst = instances[item.Inst];
            for (var t = item.First; t < item.First + item.Count; t++)
            {
                var (pa, pb, pc) = CensusTriangle(inst, t);
                tris++;
                lo = Vector3.Min(lo, Vector3.Min(pa, Vector3.Min(pb, pc)));
                hi = Vector3.Max(hi, Vector3.Max(pa, Vector3.Max(pb, pc)));
                var len = Vector3.Cross(pb - pa, pc - pa).Length();
                if (!(len > 1e-12f)) { degen++; continue; }
                a += 0.5 * len;
                samples += CensusSamplesOf(0.5f * len);
            }
            lock (gate)
            {
                triangles += tris; degenerate += degen; full += samples; area += a;
                min = Vector3.Min(min, lo); max = Vector3.Max(max, hi);
            }
        });
        // Then the samples, all or (past `most`) a random share of them, each standing for its share's area.
        var keep = full > most ? most / (double)full : 1.0;
        var keepBits = keep >= 1.0 ? uint.MaxValue : (uint)(keep * uint.MaxValue);
        var lists = new List<(Vector3 P, Vector3 N, byte Side, float Area)>[work.Length];
        Parallel.For(0, work.Length, w =>
        {
            var item = work[w];
            var inst = instances[item.Inst];
            var list = new List<(Vector3, Vector3, byte, float)>();
            for (var t = item.First; t < item.First + item.Count; t++)
            {
                var (pa, pb, pc) = CensusTriangle(inst, t);
                var nrm = Vector3.Cross(pb - pa, pc - pa);
                var len = nrm.Length();
                if (!(len > 1e-12f)) continue;
                nrm /= len;
                var n = CensusSamplesOf(0.5f * len);
                var each = (float)(0.5 * len / n / keep);
                var side = (byte)NearSide(nrm);
                for (var j = 0; j < n; j++)
                {
                    if (keepBits != uint.MaxValue
                        && NearHash((uint)item.Inst * 0x9E3779B1u ^ NearHash((uint)t * 0x85EBCA77u ^ NearHash((uint)j + 0x7F4A7C15u))) > keepBits) continue;
                    double u, v;
                    if (n == 1) u = v = 1.0 / 3.0;
                    else
                    {
                        // The R2 sequence over the parallelogram, folded into the triangle.
                        u = (0.5 + j * 0.7548776662466927) % 1.0;
                        v = (0.5 + j * 0.5698402909980532) % 1.0;
                        if (u + v > 1.0) { u = 1.0 - u; v = 1.0 - v; }
                    }
                    list.Add((pa + (pb - pa) * (float)u + (pc - pa) * (float)v, nrm, side, each));
                }
            }
            lists[w] = list;
        });
        var count = lists.Sum(l => l.Count);
        var gpu = new Vector4[count];
        var positions = new Vector3[count];
        var sides = new byte[count];
        var areas = new float[count];
        var at = 0;
        foreach (var list in lists)
            foreach (var (p, nrm, side, a) in list)
            {
                gpu[at] = new Vector4(p, BitConverter.UInt32BitsToSingle(CensusOctPack(nrm)));
                positions[at] = p; sides[at] = side; areas[at] = a;
                at++;
            }
        return new CensusLayout(gpu, positions, sides, areas, triangles, degenerate, area, min, max, full, keep, clock.Elapsed.TotalMilliseconds);
    }

    private DispatchCommand CensusDispatch(int cur, int kind, int first, int count, int threads) => new(rtgiCensusPipeline, (threads + 63) / 64, 1, 1,
        new ShaderUniform[]
        {
            new("uChunk", new Vector4Uniform(new Vector4(first, count, CensusReach, kind))),
            new("uNear", new Vector4Uniform(new Vector4(CensusNear, CensusLift, 0f, 0f))),
        },
        Array.Empty<ShaderTextureBinding>(),
        Buffers: new[]
        {
            new ShaderBufferBinding("RtgiCensusSamples", rtgiCensusSamples),
            new ShaderBufferBinding("RtgiCensusRays", rtgiCensusRayBufs[cur]),
            new ShaderBufferBinding("RtgiCensusHits", rtgiCensusHitBufs[cur]),
            new ShaderBufferBinding("RtgiCensusResults", rtgiCensusResults),
        });

    // RecordRtgi, in place of the cache's frame: the census's stage this frame.
    private void RecordRtgiCensus(MetalRayTracer tracer)
    {
        if (rtgiCensusStage == 0)
        {
            if (rtgiCensusBuild is not { IsCompleted: true } build) return;
            DebugProgress.End("rtgi census: samples");
            var layout = rtgiCensusLayout = build.Result;
            rtgiCensusBuild = Task.FromResult(layout with { Gpu = Array.Empty<Vector4>() });
            var samples = Math.Max(layout.Pos.Length, 1);
            rtgiCensusSamples = Own(device.CreateGpuBuffer(samples * 16,
                layout.Pos.Length > 0 ? MemoryMarshal.AsBytes(layout.Gpu.AsSpan()).ToArray() : new byte[16], "sponza.rtgi.census-samples"));
            rtgiCensusResults = Own(device.CreateGpuBuffer(samples * 8, new byte[samples * 8], "sponza.rtgi.census-results"));
            var rays = rtgiCensusChunk * CensusRays;
            rtgiCensusRayBufs = Enumerable.Range(0, 2).Select(k => Own(device.CreateGpuBuffer(rays * 32, name: $"sponza.rtgi.census-rays{k}"))).ToArray();
            rtgiCensusHitBufs = Enumerable.Range(0, 2).Select(k => Own(device.CreateGpuBuffer(rays * 48, name: $"sponza.rtgi.census-hits{k}"))).ToArray();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] rtgi census: {layout.Triangles:N0} triangles ({rtgiCensusDynamic} dynamic instances left out), {layout.SurfaceArea:N0} m2; {layout.FullSamples:N0} coarse samples, {layout.Pos.Length:N0} traced (share {layout.Keep:0.####}), placed in {layout.Ms:0} ms."));
            DebugProgress.Begin("rtgi census: coarse rays", layout.Pos.Length);
            rtgiCensusTraceFrom = tracer.TraceStats;
            rtgiCensusClock.Restart();
            rtgiCensusStage = 1;
            return;
        }
        var census = rtgiCensusLayout!;
        if (rtgiCensusStage == 1)
        {
            var cur = (int)(rtgiCensusParity++ & 1u);
            var first = rtgiCensusCursor;
            var count = Math.Min(rtgiCensusChunk, census.Pos.Length - first);
            if (count > 0)
            {
                var rays = count * CensusRays;
                graph.Dispatch(rtgiCensusRaysPassHandle, CensusDispatch(cur, 0, first, count, rays));
                var rayBuf = rtgiCensusRayBufs[cur];
                var hitBuf = rtgiCensusHitBufs[cur];
                var seed = unchecked((uint)first * 0x9E3779B9u + 0x2545F491u);
                void EncodeAll(ExternalWorkSync sync, MetalRayTracer t) => t.EncodeTrace(sync, rayBuf, 0, hitBuf, 0, rays, seed);
                // As the cache's own trace goes (RecordRtgi).
                if (rtgiAsync && (device.Features & GraphicsFeatures.AsyncCompute) != 0)
                    graph.External(rtgiTracePassHandle, sync => tracer.EncodeGroup(sync, () => EncodeAll(sync, tracer)));
                else if (!TracerGroupsOn) graph.External(rtgiInlineTracePassHandle, sync => tracer.EncodeGroup(sync, () => EncodeAll(sync, tracer)));
                else DeferTracerBatch(EncodeAll);
                graph.Dispatch(rtgiCensusReducePassHandle, CensusDispatch(cur, 1, first, count, count));
                rtgiCensusCursor += count;
                rtgiCensusFrames++;
                DebugProgress.Set("rtgi census: coarse rays", rtgiCensusCursor, census.Pos.Length);
            }
            if (rtgiCensusCursor >= census.Pos.Length) rtgiCensusStage = 2;
            return;
        }
        if (rtgiCensusStage == 2)
        {
            if (++rtgiCensusDrain < 3) return;
            device.WaitIdle();
            var wallMs = rtgiCensusClock.Elapsed.TotalMilliseconds;
            var stats = tracer.TraceStats;
            DebugProgress.End("rtgi census: coarse rays");
            var n = census.Pos.Length;
            var results = new uint[2 * n];
            const int piece = 1 << 26;
            for (long off = 0; off < 8L * n; off += piece)
            {
                var len = (int)Math.Min(piece, 8L * n - off);
                MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtgiCensusResults, (int)off, len).AsSpan()).CopyTo(results.AsSpan((int)(off / 4)));
            }
            var timing = new CensusTiming((long)n * CensusRays, stats.TotalGpuMs - rtgiCensusTraceFrom.TotalGpuMs, wallMs, rtgiCensusFrames,
                stats.Dispatches - rtgiCensusTraceFrom.Dispatches, stats.Failures - rtgiCensusTraceFrom.Failures);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] rtgi census: {timing.Rays / 1e6:0.0} M coarse rays in {rtgiCensusFrames} frames, {wallMs / 1000:0.0} s wall, {timing.GpuMs:0} ms the trace's GPU time; counting the cells."));
            var instances = rtgiCensusInstances;
            rtgiCensusInstances = Array.Empty<RayQueryScene.Instance>();
            var name = scene.Name;
            var dir = rtgiCensusOut;
            var dynamic = rtgiCensusDynamic;
            var cell = rtgiCell;
            DebugProgress.Begin("rtgi census: cells", 0);
            rtgiCensusReport = Task.Run(() => WriteCensusReport(name, dir, census, results, instances, dynamic, timing, cell));
            rtgiCensusStage = 3;
            return;
        }
        if (rtgiCensusStage == 3 && rtgiCensusReport is { IsCompleted: true } report)
        {
            DebugProgress.End("rtgi census: cells");
            rtgiCensusStage = 4;
            Console.WriteLine(report.IsFaulted ? $"[VulkanSponza] rtgi census: failed: {report.Exception}" : report.Result);
            host.RequestClose();
        }
    }

    private sealed record CensusTiming(long Rays, double GpuMs, double WallMs, int Frames, long Dispatches, long Failures);

    // The coarse samples' grids for the nearest: a fine one sized to their density (~4 a cell where they are densest --
    // Intel Sponza's 13 M samples over 12,000 m2 put ~60 in each 25 cm cell, and its count of cells ran for minutes) and
    // the coarse one where the fine finds none near enough to be sure of.
    private sealed class CensusGrid
    {
        private readonly CensusGridLevel fine, coarse;
        public CensusGrid(Vector3[] pos, byte[] side, float fineSize, float coarseSize)
        {
            coarse = new CensusGridLevel(pos, side, coarseSize);
            fine = fineSize < 0.75f * coarseSize ? new CensusGridLevel(pos, side, fineSize) : coarse;
        }

        // The nearest sample facing the same side, else the nearest of any (-1: none within two coarse cells). The fine
        // grid's answer stands when it lies within one fine cell: nothing outside its 27 cells can be nearer.
        public int Nearest(Vector3 q, uint wantSide)
        {
            if (!ReferenceEquals(fine, coarse))
            {
                var (j, d2) = fine.Nearest(q, wantSide, 1, true);
                if (j >= 0 && d2 <= fine.Size * fine.Size) return j;
            }
            for (var reach = 1; reach <= 2; reach++)
            {
                var (same, _) = coarse.Nearest(q, wantSide, reach, true);
                if (same >= 0) return same;
                var (any, _) = coarse.Nearest(q, wantSide, reach, false);
                if (any >= 0) return any;
            }
            return -1;
        }
    }

    private sealed class CensusGridLevel
    {
        public readonly float Size;
        private readonly Vector3[] pos;
        private readonly byte[] side;
        private readonly int[] order;
        private readonly Dictionary<long, (int Start, int Count)> cells = new();

        public CensusGridLevel(Vector3[] pos, byte[] side, float size)
        {
            this.pos = pos; this.side = side; Size = size;
            var keys = new long[pos.Length];
            order = new int[pos.Length];
            for (var i = 0; i < pos.Length; i++) { keys[i] = KeyOf(pos[i]); order[i] = i; }
            Array.Sort(keys, order);
            for (var i = 0; i < keys.Length;)
            {
                var j = i;
                while (j < keys.Length && keys[j] == keys[i]) j++;
                cells[keys[i]] = (i, j - i);
                i = j;
            }
        }

        private long KeyOf(Vector3 p) => Key((int)MathF.Floor(p.X / Size), (int)MathF.Floor(p.Y / Size), (int)MathF.Floor(p.Z / Size));
        private static long Key(int x, int y, int z) => ((long)(x + (1 << 20)) << 42) | ((long)(y + (1 << 20)) << 21) | (long)(z + (1 << 20));

        // The nearest within `reach` cells (facing `wantSide` only, or any) and its squared distance.
        public (int Index, float D2) Nearest(Vector3 q, uint wantSide, int reach, bool sameOnly)
        {
            int cx = (int)MathF.Floor(q.X / Size), cy = (int)MathF.Floor(q.Y / Size), cz = (int)MathF.Floor(q.Z / Size);
            var best = -1;
            var bestD2 = float.MaxValue;
            for (var dz = -reach; dz <= reach; dz++)
            for (var dy = -reach; dy <= reach; dy++)
            for (var dx = -reach; dx <= reach; dx++)
            {
                if (!cells.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var run)) continue;
                for (var k = run.Start; k < run.Start + run.Count; k++)
                {
                    var i = order[k];
                    if (sameOnly && side[i] != wantSide) continue;
                    var d2 = Vector3.DistanceSquared(pos[i], q);
                    if (d2 < bestD2) { bestD2 = d2; best = i; }
                }
            }
            return (best, bestD2);
        }
    }

    // A cell's key: its coordinates at its level (20 bits each) and its side.
    private static long CensusCellKey(int x, int y, int z, uint side) =>
        (((long)(x + (1 << 19)) & 0xFFFFF) << 43) | (((long)(y + (1 << 19)) & 0xFFFFF) << 23) | (((long)(z + (1 << 19)) & 0xFFFFF) << 3) | side;

    private static (int X, int Y, int Z, uint Side) CensusCellUnkey(long k) =>
        ((int)((k >> 43) & 0xFFFFF) - (1 << 19), (int)((k >> 23) & 0xFFFFF) - (1 << 19), (int)((k >> 3) & 0xFFFFF) - (1 << 19), (uint)(k & 7));

    private static long CensusParent(long k)
    {
        var (x, y, z, side) = CensusCellUnkey(k);
        return CensusCellKey(x >> 1, y >> 1, z >> 1, side);
    }

    private sealed class CensusChunk
    {
        public long Cells, Own, Fine, Level0;
        public double Coarse;
    }

    private static string WriteCensusReport(string sceneName, string dir, CensusLayout census, uint[] results,
        RayQueryScene.Instance[] instances, int dynamicCount, CensusTiming timing, float finest)
    {
        var levels = RtgiTopLevel + 1;
        var n = census.Pos.Length;
        int LevelOf(float size) => Math.Clamp((int)MathF.Floor(MathF.Log2(size / finest) + 1e-4f), 0, RtgiTopLevel);

        // The size field: each sample's harmonic distance, its size and level, buried or not.
        var size = new float[n];
        var buried = new bool[n];
        double areaBuried = 0, raysMet = 0, raysBack = 0, raysNear = 0, sampleWeight = 0;
        var areaAtLevel = new double[levels];
        var samplesAtLevel = new long[levels];
        var harmonicBands = new[] { 0.25, 0.5, 1, 2, 4, 8, 16, double.MaxValue };
        var areaInBand = new double[harmonicBands.Length];
        for (var i = 0; i < n; i++)
        {
            var invSum = BitConverter.UInt32BitsToSingle(results[2 * i]);
            var counts = results[2 * i + 1];
            int met = (int)(counts & 0xFF), back = (int)((counts >> 8) & 0xFF), near = (int)((counts >> 16) & 0xFF);
            var harmonic = invSum > 0f ? CensusRays / invSum : float.PositiveInfinity;
            size[i] = Math.Clamp(0.1f * harmonic, 0.05f, 1.0f);
            buried[i] = back * 2 > CensusRays;
            var a = census.Area[i];
            raysMet += met * (double)a; raysBack += back * (double)a; raysNear += near * (double)a; sampleWeight += a;
            if (buried[i]) { areaBuried += a; continue; }
            var l = LevelOf(size[i]);
            areaAtLevel[l] += a;
            samplesAtLevel[l]++;
            var b = 0;
            while (b < harmonicBands.Length - 1 && harmonic > harmonicBands[b]) b++;
            areaInBand[b] += a;
        }

        // The cells: every triangle at half its local size, each point's cell at its own level (the nearest coarse
        // sample's size); the levels above from those by halving their coordinates.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var coarseGrid = CensusSpacing / (float)Math.Sqrt(Math.Min(1.0, census.Keep));
        var density = n / Math.Max(census.SurfaceArea, 1e-6);
        var grid = new CensusGrid(census.Pos, census.Side, Math.Clamp(2f / (float)Math.Sqrt(density), 0.02f, coarseGrid), coarseGrid);
        var own = Enumerable.Range(0, levels).Select(_ => new HashSet<long>()).ToArray();
        long points = 0, pointsBuried = 0;
        var gate = new object();
        // Short runs: a run of big triangles at 2.5 cm is minutes of one worker's.
        var work = CensusWork(instances, 1024);
        var done = 0;
        Parallel.ForEach(work, () => (Sets: Enumerable.Range(0, levels).Select(_ => new HashSet<long>()).ToArray(), Points: 0L, Buried: 0L), (item, _, local) =>
        {
            var inst = instances[item.Inst];
            var sets = local.Sets;
            long pts = 0, bur = 0;
            for (var t = item.First; t < item.First + item.Count; t++)
            {
                var (pa, pb, pc) = CensusTriangle(inst, t);
                var nrm = Vector3.Cross(pb - pa, pc - pa);
                var len = nrm.Length();
                if (!(len > 1e-12f)) continue;
                var side = NearSide(nrm / len);
                var origin = NearGridOrigin(side);
                // Rows parallel to the longest edge, `h` apart, points `h` apart along each (the corners included).
                Vector3 ea = pa, eb = pb, apex = pc;
                float ab = (pb - pa).LengthSquared(), bc = (pc - pb).LengthSquared(), ca = (pa - pc).LengthSquared();
                if (bc >= ab && bc >= ca) { ea = pb; eb = pc; apex = pa; }
                else if (ca >= ab && ca >= bc) { ea = pc; eb = pa; apex = pb; }
                var baseLen = (eb - ea).Length();
                var height = len / MathF.Max(baseLen, 1e-9f);
                // The nearest coarse sample at each point of a 25 cm lattice over it, and the local size the smallest
                // live one's. Each fine point takes its lattice point's sample: a grid search per fine point -- San
                // Miguel's 50 M+ of them, big triangles at 2.5 cm -- ran the count past 25 minutes.
                var rows0 = Math.Max(1, (int)MathF.Ceiling(height / CensusSpacing));
                var lattice = new int[rows0 + 1][];
                var least = 1.0f;
                for (var r = 0; r <= rows0; r++)
                {
                    var f = r / (float)rows0;
                    var p0 = ea + (apex - ea) * f;
                    var p1 = eb + (apex - eb) * f;
                    var m = (int)MathF.Ceiling((1f - f) * baseLen / CensusSpacing);
                    var row = lattice[r] = new int[m + 1];
                    for (var k = 0; k <= m; k++)
                    {
                        var j = row[k] = grid.Nearest(m == 0 ? p0 : p0 + (p1 - p0) * (k / (float)m), side);
                        if (j >= 0 && !buried[j]) least = MathF.Min(least, size[j]);
                    }
                }
                // Then the points half the local size apart: rows parallel to the longest edge, the corners included.
                var h = 0.5f * least;
                var rows = Math.Max(1, (int)MathF.Ceiling(height / h));
                for (var r = 0; r <= rows; r++)
                {
                    var f = r / (float)rows;
                    var p0 = ea + (apex - ea) * f;
                    var p1 = eb + (apex - eb) * f;
                    var m = (int)MathF.Ceiling((1f - f) * baseLen / h);
                    var row = lattice[(int)MathF.Round(f * rows0)];
                    for (var k = 0; k <= m; k++)
                    {
                        var along = m == 0 ? 0f : k / (float)m;
                        var j = row[(int)MathF.Round(along * (row.Length - 1))];
                        if (j >= 0 && buried[j]) { bur++; continue; }
                        var l = LevelOf(j >= 0 ? size[j] : 1.0f);
                        var cs = finest * MathF.Pow(2f, l);
                        var g = (p0 + (p1 - p0) * along - origin) / cs;
                        sets[l].Add(CensusCellKey((int)MathF.Floor(g.X), (int)MathF.Floor(g.Y), (int)MathF.Floor(g.Z), side));
                        pts++;
                    }
                }
            }
            var finished = Interlocked.Increment(ref done);
            DebugProgress.Set("rtgi census: cells", finished, work.Length);
            return (sets, local.Points + pts, local.Buried + bur);
        }, local =>
        {
            lock (gate)
            {
                for (var l = 0; l < levels; l++) own[l].UnionWith(local.Sets[l]);
                points += local.Points;
                pointsBuried += local.Buried;
            }
        });
        var all = new HashSet<long>[levels];
        all[0] = own[0];
        for (var l = 1; l < levels; l++)
        {
            all[l] = new HashSet<long>(own[l]);
            foreach (var k in all[l - 1]) all[l].Add(CensusParent(k));
        }
        var enumerateMs = clock.Elapsed.TotalMilliseconds;

        // The totals priced.
        var cellsAt = all.Select(s => (long)s.Count).ToArray();
        var ownAt = own.Select(s => (long)s.Count).ToArray();
        long cells = cellsAt.Sum(), ownCells = ownAt.Sum(), level0 = cellsAt[0], fine = cellsAt[0] + cellsAt[1];
        var coarse = census.FullSamples;
        var slotBytes = CensusSlotBytes.Sum(s => s.Bytes);
        var rate = timing.GpuMs > 0 ? timing.Rays / (timing.GpuMs / 1000.0) : timing.Rays / (timing.WallMs / 1000.0);
        var wallRate = timing.Rays / (timing.WallMs / 1000.0);
        double Full(double rays) => rays / rate;
        double Sliced(double rays) => rays / (rate * CensusSlice);
        long shipped = coarse * CensusShipSizeField + cells * CensusShipCell;
        long nearCompact = level0 * CensusNearCompact, nearToday = level0 * CensusNearToday;
        long runtime = cells * slotBytes;
        long cookSize = coarse * CensusRays, cookInside = fine * CensusInsideRays, cookNeighbours = cells * CensusNeighbourRays;
        long cookRays = cookSize + cookInside + cookNeighbours, nearRays = level0 * CensusNearRays;
        long settle8 = ownCells * 8, settle64 = ownCells * 64;

        // The chunks: each cell by its centre, each coarse sample by its point.
        var chunkSizes = new[] { 16f, 32f, 64f };
        var chunkStats = new Dictionary<string, object>[chunkSizes.Length];
        Parallel.For(0, chunkSizes.Length, c =>
        {
            var cs = chunkSizes[c];
            var chunks = new Dictionary<(int, int, int), CensusChunk>();
            CensusChunk At(Vector3 p)
            {
                var key = ((int)MathF.Floor(p.X / cs), (int)MathF.Floor(p.Y / cs), (int)MathF.Floor(p.Z / cs));
                if (!chunks.TryGetValue(key, out var e)) chunks[key] = e = new CensusChunk();
                return e;
            }
            // Cells bigger than the chunk (parents only, a few) belong to no one chunk: counted apart.
            long global = 0;
            for (var l = 0; l < levels; l++)
            {
                var cellSize = finest * MathF.Pow(2f, l);
                if (cellSize > cs) { global += all[l].Count; continue; }
                foreach (var k in all[l])
                {
                    var (x, y, z, side) = CensusCellUnkey(k);
                    var e = At((new Vector3(x, y, z) + new Vector3(0.5f)) * cellSize + NearGridOrigin(side));
                    e.Cells++;
                    if (l <= 1) e.Fine++;
                    if (l == 0) e.Level0++;
                    if (own[l].Contains(k)) e.Own++;
                }
            }
            var perSample = census.Keep > 0 ? 1.0 / census.Keep : 1.0;
            foreach (var p in census.Pos) At(p).Coarse += perSample;
            var list = chunks.Values.ToArray();
            double Mean(Func<CensusChunk, double> f) => list.Length > 0 ? list.Average(f) : 0;
            double Max(Func<CensusChunk, double> f) => list.Length > 0 ? list.Max(f) : 0;
            double Ship(CensusChunk e) => e.Coarse * CensusShipSizeField + e.Cells * CensusShipCell;
            double Cook(CensusChunk e) => e.Coarse * CensusRays + e.Fine * CensusInsideRays + e.Cells * CensusNeighbourRays;
            chunkStats[c] = new Dictionary<string, object>
            {
                ["chunk_m"] = cs,
                ["chunks"] = list.Length, ["cells_bigger_than_chunk"] = global,
                ["cells_mean"] = Mean(e => e.Cells), ["cells_max"] = Max(e => e.Cells),
                ["own_cells_mean"] = Mean(e => e.Own), ["own_cells_max"] = Max(e => e.Own),
                ["shipped_kb_mean"] = Mean(Ship) / 1024, ["shipped_kb_max"] = Max(Ship) / 1024,
                ["runtime_mb_mean"] = Mean(e => e.Cells) * slotBytes / 1048576.0, ["runtime_mb_max"] = Max(e => e.Cells) * slotBytes / 1048576.0,
                ["cook_ms_slice_mean"] = Mean(e => Sliced(Cook(e))) * 1000, ["cook_ms_slice_max"] = Max(e => Sliced(Cook(e))) * 1000,
                ["settle8_ms_slice_mean"] = Mean(e => Sliced(e.Own * 8.0)) * 1000, ["settle8_ms_slice_max"] = Max(e => Sliced(e.Own * 8.0)) * 1000,
                ["settle64_ms_slice_mean"] = Mean(e => Sliced(e.Own * 64.0)) * 1000, ["settle64_ms_slice_max"] = Max(e => Sliced(e.Own * 64.0)) * 1000,
            };
        });

        var extent = census.Max - census.Min;
        var report = new Dictionary<string, object>
        {
            ["scene"] = sceneName,
            ["geometry"] = new Dictionary<string, object>
            {
                ["instances"] = instances.Length, ["dynamic_instances_left_out"] = dynamicCount,
                ["triangles"] = census.Triangles, ["degenerate_triangles"] = census.Degenerate,
                ["area_m2"] = census.SurfaceArea,
                ["bounds_min"] = new[] { census.Min.X, census.Min.Y, census.Min.Z }, ["bounds_max"] = new[] { census.Max.X, census.Max.Y, census.Max.Z },
                ["extent_m"] = new[] { extent.X, extent.Y, extent.Z },
            },
            ["coarse"] = new Dictionary<string, object>
            {
                ["spacing_m"] = CensusSpacing, ["rays_per_sample"] = CensusRays, ["reach_m"] = CensusReach, ["lift_m"] = CensusLift,
                ["samples"] = coarse, ["samples_traced"] = n, ["traced_share"] = census.Keep, ["place_ms"] = census.Ms,
                ["rays_traced"] = timing.Rays, ["trace_gpu_ms"] = timing.GpuMs, ["trace_wall_ms"] = timing.WallMs, ["trace_frames"] = timing.Frames,
                ["trace_dispatches"] = timing.Dispatches, ["trace_failures"] = timing.Failures,
                ["rate_rays_per_s_gpu"] = rate, ["rate_rays_per_s_wall"] = wallRate,
                ["area_buried_share"] = areaBuried / Math.Max(sampleWeight, 1e-9),
                ["ray_met_share"] = raysMet / CensusRays / Math.Max(sampleWeight, 1e-9),
                ["ray_back_share"] = raysBack / CensusRays / Math.Max(sampleWeight, 1e-9),
                ["ray_within_0_25m_share"] = raysNear / CensusRays / Math.Max(sampleWeight, 1e-9),
                ["area_m2_by_level"] = areaAtLevel, ["traced_samples_by_level"] = samplesAtLevel,
                ["harmonic_band_upper_m"] = harmonicBands.Select(b => b == double.MaxValue ? -1 : b).ToArray(), ["area_m2_by_harmonic_band"] = areaInBand,
            },
            ["cells"] = new Dictionary<string, object>
            {
                ["finest_m"] = finest, ["top_level"] = RtgiTopLevel,
                ["by_level"] = cellsAt, ["own_by_level"] = ownAt, ["total"] = cells, ["own_total"] = ownCells,
                ["junction_level0_1"] = fine, ["points"] = points, ["points_buried_skipped"] = pointsBuried, ["enumerate_ms"] = enumerateMs,
            },
            ["costs"] = new Dictionary<string, object>
            {
                ["ship_size_field_b_per_coarse_sample"] = CensusShipSizeField, ["ship_b_per_cell"] = CensusShipCell,
                ["near_compact_b_per_level0_cell"] = CensusNearCompact, ["near_today_b_per_level0_cell"] = CensusNearToday,
                ["runtime_b_per_slot"] = slotBytes, ["runtime_slot_breakdown"] = CensusSlotBytes.ToDictionary(s => s.Name, s => (object)s.Bytes),
                ["cook_rays_per_coarse_sample"] = CensusRays, ["inside_rays_per_level0_1_cell"] = CensusInsideRays,
                ["neighbour_rays_per_cell"] = CensusNeighbourRays, ["near_rays_per_level0_cell"] = CensusNearRays,
                ["settle_rays_per_sample"] = 1, ["slice_share"] = CensusSlice,
            },
            ["pricing"] = new Dictionary<string, object>
            {
                ["shipped_mb"] = shipped / 1048576.0,
                ["near_transfer_compact_mb"] = nearCompact / 1048576.0, ["near_transfer_today_mb"] = nearToday / 1048576.0,
                ["runtime_gpu_mb"] = runtime / 1048576.0,
                ["cook_rays"] = cookRays, ["cook_rays_size_field"] = cookSize, ["cook_rays_inside"] = cookInside, ["cook_rays_neighbours"] = cookNeighbours,
                ["cook_s_full"] = Full(cookRays), ["cook_s_slice"] = Sliced(cookRays),
                ["near_rays"] = nearRays, ["near_s_full"] = Full(nearRays), ["near_s_slice"] = Sliced(nearRays),
                ["settle8_rays"] = settle8, ["settle8_s_full"] = Full(settle8), ["settle8_s_slice"] = Sliced(settle8),
                ["settle64_rays"] = settle64, ["settle64_s_full"] = Full(settle64), ["settle64_s_slice"] = Sliced(settle64),
            },
            ["chunks"] = chunkStats,
        };

        Directory.CreateDirectory(dir);
        var basePath = Path.Combine(dir, sceneName);
        using (var stream = File.Create(basePath + ".json"))
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            CensusJson(json, report);

        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        void Line(FormattableString s) => text.AppendLine(s.ToString(inv));
        Line($"RTGI census: {sceneName} ({DateTime.Now:yyyy-MM-dd HH:mm})");
        Line($"");
        Line($"Geometry: {instances.Length:N0} static instances ({dynamicCount} dynamic left out), {census.Triangles:N0} triangles ({census.Degenerate:N0} degenerate), {census.SurfaceArea:N0} m2");
        Line($"  bounds ({census.Min.X:0.0}, {census.Min.Y:0.0}, {census.Min.Z:0.0}) .. ({census.Max.X:0.0}, {census.Max.Y:0.0}, {census.Max.Z:0.0}), extent {extent.X:0.0} x {extent.Y:0.0} x {extent.Z:0.0} m");
        Line($"");
        Line($"Coarse pass: {coarse:N0} samples at ~{CensusSpacing * 100:0} cm (>= 1 a triangle), {n:N0} traced (share {census.Keep:0.####}), {CensusRays} cosine rays each to {CensusReach:0} m, lifted {CensusLift * 100:0} cm");
        Line($"  {timing.Rays / 1e6:0.0} M rays: GPU {timing.GpuMs:0} ms ({rate / 1e6:0} M rays/s), wall {timing.WallMs / 1000:0.0} s over {timing.Frames} frames ({wallRate / 1e6:0} M rays/s); {timing.Failures} failed dispatches");
        Line($"  by area: buried {100 * areaBuried / Math.Max(sampleWeight, 1e-9):0.0}%; rays met {100 * raysMet / CensusRays / Math.Max(sampleWeight, 1e-9):0.0}%, back faces {100 * raysBack / CensusRays / Math.Max(sampleWeight, 1e-9):0.0}%, within 25 cm {100 * raysNear / CensusRays / Math.Max(sampleWeight, 1e-9):0.0}%");
        text.Append("  live area by size level (m2):");
        for (var l = 0; l <= 4; l++) text.Append(inv, $" L{l} ({finest * MathF.Pow(2, l) * 100:0} cm) {areaAtLevel[l]:N0}");
        text.AppendLine();
        text.Append("  live area by harmonic distance (m2):");
        for (var b = 0; b < harmonicBands.Length; b++) text.Append(inv, $" {(b == harmonicBands.Length - 1 ? ">16" : "<=" + harmonicBands[b].ToString(inv))} {areaInBand[b]:N0}");
        text.AppendLine();
        Line($"");
        Line($"Cells (enumerated in {enumerateMs / 1000:0.0} s, {points:N0} points, {pointsBuried:N0} buried skipped):");
        for (var l = 0; l < levels; l++)
            Line($"  L{l,-2} {finest * MathF.Pow(2, l),7:0.00} m  {cellsAt[l],14:N0} cells  {ownAt[l],14:N0} own-level");
        Line($"  total {cells:N0}, own-level {ownCells:N0}, junction (L0+L1) {fine:N0}");
        Line($"");
        Line($"Costs: ship {CensusShipSizeField} B/coarse sample + {CensusShipCell} B/cell; near transfer {CensusNearCompact} B (compact) / {CensusNearToday} B (today) per L0 cell; runtime {slotBytes} B/slot");
        Line($"  rays: {CensusRays}/coarse sample, {CensusInsideRays}/L0-1 cell (inside test), {CensusNeighbourRays}/cell (neighbours), {CensusNearRays}/L0 cell (near, optional); settle 1 ray/sample on own-level cells");
        Line($"  slot: {string.Join(", ", CensusSlotBytes.Select(s => $"{s.Name} {s.Bytes}"))}");
        Line($"");
        Line($"Pricing (rate {rate / 1e6:0} M rays/s; slice {CensusSlice * 100:0.0}%):");
        Line($"  shipped {shipped / 1048576.0:0.00} MB; near transfer {nearCompact / 1048576.0:0.00} MB compact / {nearToday / 1048576.0:0.00} MB today; runtime GPU {runtime / 1048576.0:0.0} MB");
        Line($"  cook {cookRays / 1e6:0.0} M rays (size {cookSize / 1e6:0.0} M, inside {cookInside / 1e6:0.0} M, neighbours {cookNeighbours / 1e6:0.0} M): {Full(cookRays):0.00} s full, {Sliced(cookRays):0.0} s at 2 ms/frame");
        Line($"  near transfer {nearRays / 1e6:0.0} M rays: {Full(nearRays):0.00} s full, {Sliced(nearRays):0.0} s sliced");
        Line($"  settle 8: {settle8 / 1e6:0.0} M rays, {Full(settle8):0.00} s full, {Sliced(settle8):0.0} s sliced; settle 64: {settle64 / 1e6:0.0} M rays, {Full(settle64):0.00} s full, {Sliced(settle64):0.0} s sliced");
        Line($"");
        Line($"Chunks (by cell centre; times at the 2 ms slice):");
        foreach (var c in chunkStats)
            Line($"  {c["chunk_m"],2} m: {c["chunks"],6} chunks ({c["cells_bigger_than_chunk"]} bigger cells apart); cells mean {c["cells_mean"]:N0} max {c["cells_max"]:N0}; shipped KB mean {c["shipped_kb_mean"]:0.0} max {c["shipped_kb_max"]:0.0}; cook ms mean {c["cook_ms_slice_mean"]:0} max {c["cook_ms_slice_max"]:0}; settle8 ms mean {c["settle8_ms_slice_mean"]:0.0} max {c["settle8_ms_slice_max"]:0.0}; settle64 ms mean {c["settle64_ms_slice_mean"]:0} max {c["settle64_ms_slice_max"]:0}");
        File.WriteAllText(basePath + ".txt", text.ToString());

        return string.Create(inv,
            $"[VulkanSponza] rtgi census {sceneName}: {census.Triangles:N0} tris, {census.SurfaceArea:N0} m2, {coarse:N0} coarse, cells {cells:N0} (L0 {cellsAt[0]:N0}, L1 {cellsAt[1]:N0}, own {ownCells:N0}), shipped {shipped / 1048576.0:0.0} MB, runtime {runtime / 1048576.0:0} MB, cook {Full(cookRays):0.0} s / {Sliced(cookRays):0} s sliced, settle64 {Full(settle64):0.0} s / {Sliced(settle64):0} s sliced, {rate / 1e6:0} M rays/s; written to {basePath}.json/.txt.");
    }

    private static void CensusJson(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case float f: w.WriteNumberValue(float.IsFinite(f) ? f : 0f); break;
            case double d: w.WriteNumberValue(double.IsFinite(d) ? d : 0.0); break;
            case System.Collections.IDictionary dict:
                w.WriteStartObject();
                foreach (System.Collections.DictionaryEntry e in dict) { w.WritePropertyName((string)e.Key); CensusJson(w, e.Value); }
                w.WriteEndObject();
                break;
            case System.Collections.IEnumerable list:
                w.WriteStartArray();
                foreach (var e in list) CensusJson(w, e);
                w.WriteEndArray();
                break;
            default: w.WriteStringValue(value.ToString()); break;
        }
    }
}
