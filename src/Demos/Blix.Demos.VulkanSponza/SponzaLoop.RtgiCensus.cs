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
//   coarse    surface samples by area, one per CensusSpacing^2 (25 x 25 cm) -- a running sum of area along each
//             run of triangles drops one at a random point of the triangle where it crosses the next 1/16 m2, so tiny
//             triangles share them; a double-sided triangle's area counts once a side, its samples one a side --
//             each tracing CensusRays cosine rays through the Metal batch (rtgi_census.comp, a chunk a frame): its
//             harmonic hit distance (escapes adding nothing to the sum, as the near cook's sizes do), its back-face
//             share (over half: buried, inside something; a double-sided placement's back face counts as a front, the
//             far side of a sheet with no inside), its nearest hit, and from them the cache's cell size,
//             clamp(0.1 x harmonic, 5 cm, the cache's top level) -- open ground gets cells of metres
//   cells     every triangle (each side of a double-sided one) sampled again at half its local cell size (the smallest
//             of the nearest same-side coarse samples at a 25 cm lattice over it, by a hash grid, searched outward where
//             none is near), each point's cell at its own level (its lattice point's sample's size; none where that
//             sample is buried) and every level above it -- the cells a cooked cache would hold, by level, those that
//             are some surface's own (finest) level, and the junction cells among them: another surface within 1.5 cell
//             sizes of the point (its sample's nearest hit), the only ones the cook's inside test is for (on a worker)
//   pricing   bytes and rays by the per-item costs below, the times at the measured trace rate, whole and per chunk of
//             16, 32 and 64 m; the runtime bytes at today's slot, at a proposed compact resident record (all cells, and
//             the chunks within 32 / 64 m of the start camera) plus a transient training pool
//
// written to DIR/<scene>.json and .txt, a line to the log, and the demo closes. The cache is left untrained meanwhile
// (RecordRtgi), the world volume and the near cook off (ParseRtgiCensus): the trace's time is the census's own. Nothing
// here runs without the flag. Approximations: cells keyed by coordinates and side alone (no regions); the cell grid's
// origin as rtgi_cache.glsl's; the ray scene's meshes (its LOD) are the geometry; double-sidedness is the placement's
// material's (rayPlacementDoubleSided, the raster's unculled pipelines); past --rtgi-census-most samples (12 M) the
// coarse samples are a random share of the full set, each standing for the rest's area.
internal sealed partial class SponzaLoop
{
    private bool rtgiCensus;
    private string rtgiCensusOut = "census";
    // Coarse samples a frame (64 rays each: 1 M rays at 16,384, the near cook's chunk) and the most traced at all.
    private int rtgiCensusChunk = 16384;
    private long rtgiCensusMost = 12_000_000;
    // --rtgi-census-cell-share F: the cells counted over a random share of the triangle runs and scaled up by 1 / F
    // (1: all). The jungle's count, whole, outgrew a 16 GB machine.
    private float rtgiCensusCellShare = 1f;
    private const float CensusSpacing = 0.25f, CensusReach = 64f, CensusNear = 0.25f, CensusLift = 0.01f;
    private const int CensusRays = 64;
    // The slice of a 60 Hz frame the cook and the settling would be given: 2 ms of 16.7.
    private const double CensusSlice = 2.0 / 16.7;
    // The per-item costs priced (bytes, rays), stated in the report.
    private const int CensusShipSizeField = 1;       // B a coarse sample: its cell size
    private const int CensusShipCell = 2;            // B a cell: the chosen candidate + buried, its neighbour visibility (8 bits)
    private const int CensusNearCompact = 24;        // B a level-0 cell: the near transfer, compact
    private const int CensusNearToday = 256;         // B a level-0 cell: today's 32 per-ray links (8 B each)
    private const int CensusInsideRays = 16 * 8;     // rays a junction cell: the inside test, 16 candidates x 8 rays
    // A junction cell: its sample's nearest hit within this many of the cell's size.
    private const float CensusJunctionReach = 1.5f;
    // The cell count's pieces: big triangles split until no edge is longer than this many lattice steps.
    private const int CensusPieceLattices = 8;
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
    // The proposed compact resident record (bits): what the readout, the hit lookups and the blend read every frame,
    // and the history that must outlive a training frame. The training's scratch -- the frame's accumulator (64 B), the
    // full moments (48 B), the dirty-list stamps -- in a transient pool of CensusPoolSlots slots, a cell taking one
    // when trained and giving it back at its resolve (the slot index in the resident record's flags word).
    private static readonly (string Name, int Bits)[] CensusCompactBits =
    {
        ("key: coords at level 3 x 18, level 4, side 3, spare 3", 64),
        ("shown light rgb (the blend's), fp16 x 3", 48), ("sky visibility, fp16", 16),
        ("resolved irradiance rgb (the running mean), fp16 x 3", 48), ("sun visibility, fp16", 16),
        ("fast mean rgb (change test), fp16 x 3", 48), ("luminance second moment, fp16", 16),
        ("irradiance direction, octahedral 2 x 8", 16), ("direction length, unorm 8", 8), ("near visibility, unorm 8", 8),
        ("inverse hit distance, fp16", 16), ("relative error, fp16", 16),
        ("samples held, fp16", 16), ("sun samples held, fp16", 16),
        ("parent weight (stands), fp16", 16), ("frames since trained, u16", 16),
        ("last trained frame, u24 + sun epoch u8", 32),
        ("far light's share, unorm 8", 8), ("done + flags, 8", 8), ("blue-noise sequence, u8", 8), ("cooked: candidate + buried, neighbour visibility", 16),
        ("transient pool slot (or none), u20 + spare 4", 24), ("spare", 32),
    };
    private static int CensusCompactBytes => CensusCompactBits.Sum(f => f.Bits) / 8;
    private static readonly (string Name, int Bytes)[] CensusPoolBytes =
    {
        ("accum (the frame's samples, RTGI_ACC 16 words)", 64), ("moments, full", 48), ("dirty stamps", 8), ("owner cell (back-pointer)", 4), ("spare", 4),
    };
    private const int CensusPoolSlots = 262_144;

    private int rtgiCensusDynamic;
    private RayQueryScene.Instance[] rtgiCensusInstances = Array.Empty<RayQueryScene.Instance>();
    // Per census instance, whether its material is double-sided; and per placement (the hit's), for the reduction.
    private bool[] rtgiCensusSided = Array.Empty<bool>();
    private GpuBufferHandle rtgiCensusSidedBuf;
    private int rtgiCensusPlacements;
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
        if (args.Float("rtgi-census-cell-share") is { } cellShare) rtgiCensusCellShare = Math.Clamp(cellShare, 0.001f, 1f);
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
        // The scene's instances are its placements, in rayPlacementDoubleSided's order (the hits' placement index).
        var kept = Enumerable.Range(0, scene.Instances.Count).Where(i => !scene.Instances[i].Dynamic).ToArray();
        var instances = kept.Select(i => scene.Instances[i]).ToArray();
        var sided = kept.Select(i => i < rayPlacementDoubleSided.Count && rayPlacementDoubleSided[i]).ToArray();
        rtgiCensusDynamic = scene.Instances.Count - instances.Length;
        rtgiCensusInstances = instances;
        rtgiCensusSided = sided;
        rtgiCensusPlacements = rayPlacementDoubleSided.Count;
        var flags = rayPlacementDoubleSided.Select(d => d ? 1u : 0u).DefaultIfEmpty(0u).ToArray();
        rtgiCensusSidedBuf = Own(device.CreateGpuBuffer(flags.Length * 4, MemoryMarshal.AsBytes(flags.AsSpan()).ToArray(), "sponza.rtgi.census-sided"));
        var most = rtgiCensusMost;
        DebugProgress.Begin("rtgi census: samples", 0);
        rtgiCensusBuild = Task.Run(() => BuildCensusLayout(instances, sided, most));
    }

    // The coarse samples: xyz and the packed normal for the GPU, and what the CPU's count of cells reads.
    private sealed record CensusLayout(
        Vector4[] Gpu, Vector3[] Pos, byte[] Side, float[] Area, long Triangles, long Degenerate, double SurfaceArea,
        long SidedTriangles, double SidedArea, Vector3 Min, Vector3 Max, long FullSamples, double Keep, double Ms);

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

    // The area sampling's walk over a run of triangles: `area` added (a side's), the samples it drops -- each where the
    // running sum crosses the next CensusSpacing^2, from an offset of the run's own.
    private struct CensusAreaWalk
    {
        private double sum, next;
        public CensusAreaWalk(uint seed) { sum = 0; next = (NearHash(seed) / 4294967296.0) * CensusSpacing * CensusSpacing; }
        public int Add(double area)
        {
            sum += area;
            var n = 0;
            while (sum >= next) { n++; next += CensusSpacing * CensusSpacing; }
            return n;
        }
    }

    // A uniform random point of the triangle from two hashes.
    private static Vector3 CensusPointIn(Vector3 a, Vector3 b, Vector3 c, uint h)
    {
        var r1 = NearHash(h) / 4294967296f;
        var r2 = NearHash(h ^ 0x68E31DA4u) / 4294967296f;
        var s = MathF.Sqrt(r1);
        return a * (1f - s) + b * (s * (1f - r2)) + c * (s * r2);
    }

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

    private static CensusLayout BuildCensusLayout(RayQueryScene.Instance[] instances, bool[] sided, long most)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var work = CensusWork(instances);
        // First the totals: the triangles, their area and bounds, the samples the area walk drops.
        long triangles = 0, degenerate = 0, full = 0, sidedTris = 0;
        double area = 0, sidedArea = 0;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var gate = new object();
        Parallel.For(0, work.Length, w =>
        {
            var item = work[w];
            long tris = 0, degen = 0, samples = 0, sTris = 0;
            double a = 0, sa = 0;
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            var inst = instances[item.Inst];
            var twoSided = sided[item.Inst];
            var walk = new CensusAreaWalk((uint)w * 0x9E3779B1u + 0x51u);
            for (var t = item.First; t < item.First + item.Count; t++)
            {
                var (pa, pb, pc) = CensusTriangle(inst, t);
                tris++;
                lo = Vector3.Min(lo, Vector3.Min(pa, Vector3.Min(pb, pc)));
                hi = Vector3.Max(hi, Vector3.Max(pa, Vector3.Max(pb, pc)));
                var len = Vector3.Cross(pb - pa, pc - pa).Length();
                if (!(len > 1e-12f)) { degen++; continue; }
                a += 0.5 * len;
                samples += walk.Add(0.5 * len);
                if (!twoSided) continue;
                sTris++; sa += 0.5 * len;
                samples += walk.Add(0.5 * len);
            }
            lock (gate)
            {
                triangles += tris; degenerate += degen; full += samples; area += a; sidedTris += sTris; sidedArea += sa;
                min = Vector3.Min(min, lo); max = Vector3.Max(max, hi);
            }
        });
        // Then the samples, all or (past `most`) a random share of them, each standing for its share's area.
        var keep = full > most ? most / (double)full : 1.0;
        var keepBits = keep >= 1.0 ? uint.MaxValue : (uint)(keep * uint.MaxValue);
        var each = (float)(CensusSpacing * CensusSpacing / keep);
        var lists = new List<(Vector3 P, Vector3 N, byte Side)>[work.Length];
        Parallel.For(0, work.Length, w =>
        {
            var item = work[w];
            var inst = instances[item.Inst];
            var twoSided = sided[item.Inst];
            var list = new List<(Vector3, Vector3, byte)>();
            var walk = new CensusAreaWalk((uint)w * 0x9E3779B1u + 0x51u);
            var dropped = 0u;
            void Drop(int count, Vector3 pa, Vector3 pb, Vector3 pc, Vector3 nrm)
            {
                for (var j = 0; j < count; j++, dropped++)
                {
                    var h = NearHash((uint)w * 0x85EBCA77u ^ NearHash(dropped + 0x7F4A7C15u));
                    if (keepBits != uint.MaxValue && NearHash(h ^ 0x2545F491u) > keepBits) continue;
                    list.Add((CensusPointIn(pa, pb, pc, h), nrm, (byte)NearSide(nrm)));
                }
            }
            for (var t = item.First; t < item.First + item.Count; t++)
            {
                var (pa, pb, pc) = CensusTriangle(inst, t);
                var nrm = Vector3.Cross(pb - pa, pc - pa);
                var len = nrm.Length();
                if (!(len > 1e-12f)) continue;
                nrm /= len;
                Drop(walk.Add(0.5 * len), pa, pb, pc, nrm);
                if (twoSided) Drop(walk.Add(0.5 * len), pa, pb, pc, -nrm);
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
            foreach (var (p, nrm, side) in list)
            {
                gpu[at] = new Vector4(p, BitConverter.UInt32BitsToSingle(CensusOctPack(nrm)));
                positions[at] = p; sides[at] = side; areas[at] = each;
                at++;
            }
        return new CensusLayout(gpu, positions, sides, areas, triangles, degenerate, area, sidedTris, sidedArea, min, max, full, keep,
            clock.Elapsed.TotalMilliseconds);
    }

    private DispatchCommand CensusDispatch(int cur, int kind, int first, int count, int threads) => new(rtgiCensusPipeline, (threads + 63) / 64, 1, 1,
        new ShaderUniform[]
        {
            new("uChunk", new Vector4Uniform(new Vector4(first, count, CensusReach, kind))),
            new("uNear", new Vector4Uniform(new Vector4(CensusNear, CensusLift, rtgiCensusPlacements, 0f))),
        },
        Array.Empty<ShaderTextureBinding>(),
        Buffers: new[]
        {
            new ShaderBufferBinding("RtgiCensusSamples", rtgiCensusSamples),
            new ShaderBufferBinding("RtgiCensusRays", rtgiCensusRayBufs[cur]),
            new ShaderBufferBinding("RtgiCensusHits", rtgiCensusHitBufs[cur]),
            new ShaderBufferBinding("RtgiCensusResults", rtgiCensusResults),
            new ShaderBufferBinding("RtgiCensusSided", rtgiCensusSidedBuf),
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
            rtgiCensusResults = Own(device.CreateGpuBuffer(samples * 16, new byte[samples * 16], "sponza.rtgi.census-results"));
            var rays = rtgiCensusChunk * CensusRays;
            rtgiCensusRayBufs = Enumerable.Range(0, 2).Select(k => Own(device.CreateGpuBuffer(rays * 32, name: $"sponza.rtgi.census-rays{k}"))).ToArray();
            rtgiCensusHitBufs = Enumerable.Range(0, 2).Select(k => Own(device.CreateGpuBuffer(rays * 48, name: $"sponza.rtgi.census-hits{k}"))).ToArray();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] rtgi census: {layout.Triangles:N0} triangles ({rtgiCensusDynamic} dynamic instances left out, {layout.SidedTriangles:N0} double-sided), {layout.SurfaceArea:N0} m2; {layout.FullSamples:N0} coarse samples, {layout.Pos.Length:N0} traced (share {layout.Keep:0.####}), placed in {layout.Ms:0} ms."));
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
            var results = new uint[4 * n];
            const int piece = 1 << 26;
            for (long off = 0; off < 16L * n; off += piece)
            {
                var len = (int)Math.Min(piece, 16L * n - off);
                MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(rtgiCensusResults, (int)off, len).AsSpan()).CopyTo(results.AsSpan((int)(off / 4)));
            }
            var timing = new CensusTiming((long)n * CensusRays, stats.TotalGpuMs - rtgiCensusTraceFrom.TotalGpuMs, wallMs, rtgiCensusFrames,
                stats.Dispatches - rtgiCensusTraceFrom.Dispatches, stats.Failures - rtgiCensusTraceFrom.Failures);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] rtgi census: {timing.Rays / 1e6:0.0} M coarse rays in {rtgiCensusFrames} frames, {wallMs / 1000:0.0} s wall, {timing.GpuMs:0} ms the trace's GPU time; counting the cells."));
            var instances = rtgiCensusInstances;
            var sided = rtgiCensusSided;
            rtgiCensusInstances = Array.Empty<RayQueryScene.Instance>();
            var name = scene.Name;
            var start = scene.StartPosition;
            var cellShare = rtgiCensusCellShare;
            var dir = rtgiCensusOut;
            var dynamic = rtgiCensusDynamic;
            var cell = rtgiCell;
            DebugProgress.Begin("rtgi census: cells", 0);
            rtgiCensusReport = Task.Run(() => WriteCensusReport(name, dir, census, results, instances, sided, dynamic, timing, cell, start, cellShare));
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

        // The nearest sample facing the same side, else the nearest of any (-1: none within eight coarse cells, searched
        // outward). The fine grid's answer stands when it lies within one fine cell: nothing outside its 27 can be nearer.
        public int Nearest(Vector3 q, uint wantSide)
        {
            if (!ReferenceEquals(fine, coarse))
            {
                var (j, d2) = fine.Nearest(q, wantSide, 1, true);
                if (j >= 0 && d2 <= fine.Size * fine.Size) return j;
            }
            foreach (var reach in CensusReaches)
            {
                var (same, _) = coarse.Nearest(q, wantSide, reach, true);
                if (same >= 0) return same;
                var (any, _) = coarse.Nearest(q, wantSide, reach, false);
                if (any >= 0) return any;
            }
            return -1;
        }
    }

    private static readonly int[] CensusReaches = { 1, 2, 4, 8 };

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

    // A cell's key: its coordinates at its level (x 19 bits, y and z 20), its side, and bit 0 a flag (a junction in the
    // own-level lists, own-level in the all-levels ones): sorted, a cell's two flag values lie side by side.
    private static long CensusCellKey(int x, int y, int z, uint side) =>
        (((long)(x + (1 << 18)) & 0x7FFFF) << 44) | (((long)(y + (1 << 19)) & 0xFFFFF) << 24) | (((long)(z + (1 << 19)) & 0xFFFFF) << 4) | ((long)side << 1);

    private static (int X, int Y, int Z, uint Side) CensusCellUnkey(long k) =>
        ((int)((k >> 44) & 0x7FFFF) - (1 << 18), (int)((k >> 24) & 0xFFFFF) - (1 << 19), (int)((k >> 4) & 0xFFFFF) - (1 << 19), (uint)((k >> 1) & 7));

    private static long CensusParent(long k)
    {
        var (x, y, z, side) = CensusCellUnkey(k);
        return CensusCellKey(x >> 1, y >> 1, z >> 1, side);
    }

    // Sorted, each cell once, its flag the OR of its copies'.
    private static long[] CensusSortUnique(long[] keys, int length)
    {
        Array.Sort(keys, 0, length);
        var at = 0;
        for (var i = 0; i < length; i++)
        {
            if (at > 0 && (keys[at - 1] >> 1) == (keys[i] >> 1)) keys[at - 1] |= keys[i] & 1;
            else keys[at++] = keys[i];
        }
        Array.Resize(ref keys, at);
        return keys;
    }

    // A worker's list of a level's keys compacted in place once long (duplicates across its runs' edges).
    private static void CensusCompact(List<long> list)
    {
        var keys = CensusSortUnique(list.ToArray(), list.Count);
        list.Clear();
        list.AddRange(keys);
    }

    private sealed class CensusChunk
    {
        public double Cells, Own, Fine, Level0, Junction, Coarse;
    }

    private static string WriteCensusReport(string sceneName, string dir, CensusLayout census, uint[] results,
        RayQueryScene.Instance[] instances, bool[] sided, int dynamicCount, CensusTiming timing, float finest, Vector3 start, float cellShare)
    {
        var levels = RtgiTopLevel + 1;
        var n = census.Pos.Length;
        var topSize = finest * MathF.Pow(2f, RtgiTopLevel);
        int LevelOf(float size) => Math.Clamp((int)MathF.Floor(MathF.Log2(size / finest) + 1e-4f), 0, RtgiTopLevel);
        float SizeOfLevel(int l) => finest * MathF.Pow(2f, l);

        // The size field: each sample's harmonic distance, its size and level (to the cache's top), buried or not, its
        // nearest hit.
        var size = new float[n];
        var buried = new bool[n];
        var nearest = new float[n];
        double areaBuried = 0, areaBuriedIgnoringSided = 0, raysMet = 0, raysBack = 0, raysSidedBack = 0, raysNear = 0, sampleWeight = 0;
        var areaAtLevel = new double[levels];
        var samplesAtLevel = new long[levels];
        var harmonicBands = new[] { 0.25, 0.5, 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, double.MaxValue };
        var areaInBand = new double[harmonicBands.Length];
        for (var i = 0; i < n; i++)
        {
            var invSum = BitConverter.UInt32BitsToSingle(results[4 * i]);
            var counts = results[4 * i + 1];
            int met = (int)(counts & 0xFF), back = (int)((counts >> 8) & 0xFF), near = (int)((counts >> 16) & 0xFF), sidedBack = (int)(counts >> 24);
            nearest[i] = met > 0 ? BitConverter.UInt32BitsToSingle(results[4 * i + 2]) : float.PositiveInfinity;
            var harmonic = invSum > 0f ? CensusRays / invSum : float.PositiveInfinity;
            size[i] = Math.Clamp(0.1f * harmonic, finest, topSize);
            buried[i] = back * 2 > CensusRays;
            var a = census.Area[i];
            // Were double-sidedness ignored (the census before it): a double-sided back face a back face too.
            if ((back + sidedBack) * 2 > CensusRays) areaBuriedIgnoringSided += a;
            raysMet += met * (double)a; raysBack += back * (double)a; raysSidedBack += sidedBack * (double)a; raysNear += near * (double)a; sampleWeight += a;
            if (buried[i]) { areaBuried += a; continue; }
            var l = LevelOf(size[i]);
            areaAtLevel[l] += a;
            samplesAtLevel[l]++;
            var b = 0;
            while (b < harmonicBands.Length - 1 && harmonic > harmonicBands[b]) b++;
            areaInBand[b] += a;
        }

        // The cells: every triangle (each side of a double-sided one) at half its local size, each point's cell at its
        // own level (its lattice point's coarse sample's size), a junction cell where that sample's nearest hit is within
        // CensusJunctionReach of the cell's size; the levels above from those by halving their coordinates.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var coarseGrid = CensusSpacing / (float)Math.Sqrt(Math.Min(1.0, census.Keep));
        var density = n / Math.Max(census.SurfaceArea + census.SidedArea, 1e-6);
        var grid = new CensusGrid(census.Pos, census.Side, Math.Clamp(2f / (float)Math.Sqrt(density), 0.02f, coarseGrid), coarseGrid);
        // The cells as sorted arrays of keys, not sets: the jungle's hundreds of millions in HashSets (24+ B each, and a
        // merge's copy) took 22 GB of a 16 GB machine and the count never finished. Each run's points de-duplicated in
        // a small set, the worker's list compacted as it grows, the lists sorted together at the end.
        var parts = Enumerable.Range(0, levels).Select(_ => new List<List<long>>()).ToArray();
        long points = 0, pointsBuried = 0, latticeFilled = 0, latticeNone = 0;
        var gate = new object();
        // Short runs: a run of big triangles at 2.5 cm is minutes of one worker's.
        var work = CensusWork(instances, 1024);
        // Under --rtgi-census-cell-share, a random share of the runs (each count below scaled by 1 / the share).
        if (cellShare < 1f)
        {
            var shareBits = (uint)(cellShare * uint.MaxValue);
            work = work.Where((_, w) => NearHash((uint)w * 0x27D4EB2Fu + 0x165667B1u) <= shareBits).ToArray();
        }
        var scale = 1.0 / cellShare;
        // The lattice's step (the traced samples' spacing) and the longest edge of a piece.
        var lat = coarseGrid;
        var pieceMost = CensusPieceLattices * lat;
        var done = 0;
        Parallel.ForEach(work, () => (Sets: Enumerable.Range(0, levels).Select(_ => new HashSet<long>()).ToArray(),
            Lists: Enumerable.Range(0, levels).Select(_ => new List<long>()).ToArray(), Compacted: new int[levels], Points: 0L, Buried: 0L, Filled: 0L, None: 0L), (item, _, local) =>
        {
            var inst = instances[item.Inst];
            var sets = local.Sets;
            long pts = 0, bur = 0, filled = 0, none = 0;
            var faces = sided[item.Inst] ? 2 : 1;
            var pieces = new Stack<(Vector3, Vector3, Vector3)>();
            for (var t = item.First; t < item.First + item.Count; t++)
            {
                var (ta, tb, tc) = CensusTriangle(inst, t);
                var tcross = Vector3.Cross(tb - ta, tc - ta);
                var tlen = tcross.Length();
                if (!(tlen > 1e-12f)) continue;
                var nrm = tcross / tlen;
                // Big triangles in pieces no edge longer than CensusPieceLattices lattice steps, each with its own local
                // size: whole, one 1 km2 terrain triangle of the jungle's was one worker's for most of an hour (its
                // lattice millions of points, its fine walk at the smallest size anywhere on it).
                pieces.Clear();
                pieces.Push((ta, tb, tc));
                while (pieces.TryPop(out var piece))
                {
                    var (pa, pb, pc) = piece;
                    float ab = (pb - pa).LengthSquared(), bc = (pc - pb).LengthSquared(), ca = (pa - pc).LengthSquared();
                    if (MathF.Max(ab, MathF.Max(bc, ca)) > pieceMost * pieceMost)
                    {
                        if (ab >= bc && ab >= ca) { var m = (pa + pb) * 0.5f; pieces.Push((pa, m, pc)); pieces.Push((m, pb, pc)); }
                        else if (bc >= ca) { var m = (pb + pc) * 0.5f; pieces.Push((pa, pb, m)); pieces.Push((pa, m, pc)); }
                        else { var m = (pc + pa) * 0.5f; pieces.Push((pa, pb, m)); pieces.Push((m, pb, pc)); }
                        continue;
                    }
                    var len = Vector3.Cross(pb - pa, pc - pa).Length();
                    if (!(len > 1e-12f)) continue;
                    // Rows parallel to the longest edge, `h` apart, points `h` apart along each (the corners included).
                    Vector3 ea = pa, eb = pb, apex = pc;
                    if (bc >= ab && bc >= ca) { ea = pb; eb = pc; apex = pa; }
                    else if (ca >= ab && ca >= bc) { ea = pc; eb = pa; apex = pb; }
                    var baseLen = (eb - ea).Length();
                    var height = len / MathF.Max(baseLen, 1e-9f);
                    for (var face = 0; face < faces; face++)
                    {
                        var side = NearSide(face == 0 ? nrm : -nrm);
                        var origin = NearGridOrigin(side);
                        // The nearest coarse sample at each point of a lattice over it at the traced samples' spacing (25
                        // cm, wider where they are a share), and the local size the smallest
                        // live one's. Each fine point takes its lattice point's sample: a grid search per fine point -- San
                        // Miguel's 50 M+ of them, big triangles at 2.5 cm -- ran the count past 25 minutes. Area sampling
                        // leaves tiny triangles without a sample of their own: a lattice point with none near takes its
                        // row's nearest that has one, or the nearest row's.
                        var rows0 = Math.Max(1, (int)MathF.Ceiling(height / lat));
                        var lattice = new int[rows0 + 1][];
                        var least = topSize;
                        var anyRow = -1;
                        for (var r = 0; r <= rows0; r++)
                        {
                            var f = r / (float)rows0;
                            var p0 = ea + (apex - ea) * f;
                            var p1 = eb + (apex - eb) * f;
                            var m = (int)MathF.Ceiling((1f - f) * baseLen / lat);
                            var row = lattice[r] = new int[m + 1];
                            var rowHas = false;
                            for (var k = 0; k <= m; k++)
                            {
                                var j = row[k] = grid.Nearest(m == 0 ? p0 : p0 + (p1 - p0) * (k / (float)m), side);
                                if (j < 0) continue;
                                rowHas = true;
                                if (!buried[j]) least = MathF.Min(least, size[j]);
                            }
                            if (rowHas) anyRow = r;
                            else continue;
                            for (var k = 0; k <= m; k++)
                            {
                                if (row[k] >= 0) continue;
                                for (var d = 1; d <= m; d++)
                                {
                                    if (k - d >= 0 && row[k - d] >= 0) { row[k] = row[k - d]; break; }
                                    if (k + d <= m && row[k + d] >= 0) { row[k] = row[k + d]; break; }
                                }
                                filled++;
                            }
                        }
                        if (anyRow < 0) none++;
                        else
                            for (var r = 0; r <= rows0; r++)
                            {
                                if (lattice[r].Any(j => j >= 0)) continue;
                                var from = lattice[anyRow];
                                for (var d = 1; d <= rows0; d++)
                                {
                                    if (r - d >= 0 && lattice[r - d].Any(j => j >= 0)) { from = lattice[r - d]; break; }
                                    if (r + d <= rows0 && lattice[r + d].Any(j => j >= 0)) { from = lattice[r + d]; break; }
                                }
                                var row = lattice[r];
                                for (var k = 0; k < row.Length; k++) row[k] = from[(int)MathF.Round(k / (float)Math.Max(1, row.Length - 1) * (from.Length - 1))];
                                filled += row.Length;
                            }
                        // Then the points half the local size apart.
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
                                // No sample anywhere near (none within 8 coarse cells): open, the top.
                                var l = LevelOf(j >= 0 ? size[j] : topSize);
                                var cs = SizeOfLevel(l);
                                var g = (p0 + (p1 - p0) * along - origin) / cs;
                                var junctionHere = j >= 0 && nearest[j] < CensusJunctionReach * cs;
                                sets[l].Add(CensusCellKey((int)MathF.Floor(g.X), (int)MathF.Floor(g.Y), (int)MathF.Floor(g.Z), side) | (junctionHere ? 1L : 0L));
                                pts++;
                            }
                        }
                    }
                }
            }
            for (var l = 0; l < levels; l++)
            {
                local.Lists[l].AddRange(sets[l]);
                sets[l].Clear();
                // Compacted when it has doubled since last (at least 2 M keys): near its unique size, at a log's cost.
                if (local.Lists[l].Count > Math.Max(1 << 21, 2 * local.Compacted[l])) { CensusCompact(local.Lists[l]); local.Compacted[l] = local.Lists[l].Count; }
            }
            var finished = Interlocked.Increment(ref done);
            DebugProgress.Set("rtgi census: cells", finished, work.Length);
            return (sets, local.Lists, local.Compacted, local.Points + pts, local.Buried + bur, local.Filled + filled, local.None + none);
        }, local =>
        {
            lock (gate)
            {
                for (var l = 0; l < levels; l++) parts[l].Add(local.Lists[l]);
                points += local.Points;
                pointsBuried += local.Buried;
                latticeFilled += local.Filled;
                latticeNone += local.None;
            }
        });
        // Each level's own cells (flag: a junction), then every level's cells (flag: own-level), its parents from the
        // level below's.
        var own = new long[levels][];
        for (var l = 0; l < levels; l++)
        {
            var keys = new long[parts[l].Sum(q => (long)q.Count)];
            var at = 0;
            foreach (var q in parts[l]) { q.CopyTo(keys, at); at += q.Count; q.Clear(); q.TrimExcess(); }
            own[l] = CensusSortUnique(keys, keys.Length);
        }
        var all = new long[levels][];
        for (var l = 0; l < levels; l++)
        {
            var below = l > 0 ? all[l - 1].Length : 0;
            var keys = new long[below + own[l].Length];
            for (var i = 0; i < below; i++) keys[i] = CensusParent(all[l - 1][i]);
            for (var i = 0; i < own[l].Length; i++) keys[below + i] = (own[l][i] & ~1L) | 1L;
            all[l] = CensusSortUnique(keys, keys.Length);
        }
        var enumerateMs = clock.Elapsed.TotalMilliseconds;

        // The totals priced.
        var cellsAt = all.Select(s => (long)(s.Length * scale)).ToArray();
        var ownAt = own.Select(s => (long)(s.Length * scale)).ToArray();
        var junctionAt = own.Select(s => (long)(s.LongCount(k => (k & 1) != 0) * scale)).ToArray();
        long cells = cellsAt.Sum(), ownCells = ownAt.Sum(), level0 = cellsAt[0], fine = cellsAt[0] + cellsAt[1], junctions = junctionAt.Sum();
        var coarse = census.FullSamples;
        var slotBytes = CensusSlotBytes.Sum(s => s.Bytes);
        var compactBytes = CensusCompactBytes;
        var poolBytes = CensusPoolBytes.Sum(s => s.Bytes);
        long pool = (long)CensusPoolSlots * poolBytes;
        var rate = timing.GpuMs > 0 ? timing.Rays / (timing.GpuMs / 1000.0) : timing.Rays / (timing.WallMs / 1000.0);
        var wallRate = timing.Rays / (timing.WallMs / 1000.0);
        double Full(double rays) => rays / rate;
        double Sliced(double rays) => rays / (rate * CensusSlice);
        const double Mb = 1048576.0;
        long shipped = coarse * CensusShipSizeField + cells * CensusShipCell;
        long nearCompact = level0 * CensusNearCompact, nearToday = level0 * CensusNearToday;
        long runtime = cells * slotBytes, runtimeCompact = cells * compactBytes;
        long cookSize = coarse * CensusRays, cookInside = junctions * CensusInsideRays, cookNeighbours = cells * CensusNeighbourRays;
        long cookRays = cookSize + cookInside + cookNeighbours, nearRays = level0 * CensusNearRays;
        long settle8 = ownCells * 8, settle64 = ownCells * 64;

        // The chunks: each cell by its centre, each coarse sample by its point; resident, those whose box lies within
        // R of the start camera (cells bigger than the chunk always).
        var chunkSizes = new[] { 16f, 32f, 64f };
        var reaches = new[] { 32f, 64f };
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
                var cellSize = SizeOfLevel(l);
                if (cellSize > cs) { global += (long)(all[l].Length * scale); continue; }
                // The level's own cells walked beside its cells (both sorted) for their junction flags.
                var ownKeys = own[l];
                var o = 0;
                foreach (var k in all[l])
                {
                    var (x, y, z, side) = CensusCellUnkey(k);
                    while (o < ownKeys.Length && (ownKeys[o] >> 1) < (k >> 1)) o++;
                    var isJunction = o < ownKeys.Length && (ownKeys[o] >> 1) == (k >> 1) && (ownKeys[o] & 1) != 0;
                    var e = At((new Vector3(x, y, z) + new Vector3(0.5f)) * cellSize + NearGridOrigin(side));
                    e.Cells += scale;
                    if (l <= 1) e.Fine += scale;
                    if (l == 0) e.Level0 += scale;
                    if ((k & 1) != 0) e.Own += scale;
                    if (isJunction) e.Junction += scale;
                }
            }
            var perSample = census.Keep > 0 ? 1.0 / census.Keep : 1.0;
            foreach (var p in census.Pos) At(p).Coarse += perSample;
            var list = chunks.Values.ToArray();
            double Mean(Func<CensusChunk, double> f) => list.Length > 0 ? list.Average(f) : 0;
            double Max(Func<CensusChunk, double> f) => list.Length > 0 ? list.Max(f) : 0;
            double Ship(CensusChunk e) => e.Coarse * CensusShipSizeField + e.Cells * CensusShipCell;
            double Cook(CensusChunk e) => e.Coarse * CensusRays + e.Junction * CensusInsideRays + e.Cells * CensusNeighbourRays;
            var stats = new Dictionary<string, object>
            {
                ["chunk_m"] = cs,
                ["chunks"] = list.Length, ["cells_bigger_than_chunk"] = global,
                ["cells_mean"] = Mean(e => e.Cells), ["cells_max"] = Max(e => e.Cells),
                ["own_cells_mean"] = Mean(e => e.Own), ["own_cells_max"] = Max(e => e.Own),
                ["junction_cells_mean"] = Mean(e => e.Junction), ["junction_cells_max"] = Max(e => e.Junction),
                ["shipped_kb_mean"] = Mean(Ship) / 1024, ["shipped_kb_max"] = Max(Ship) / 1024,
                ["runtime_today_mb_mean"] = Mean(e => e.Cells) * slotBytes / Mb, ["runtime_today_mb_max"] = Max(e => e.Cells) * slotBytes / Mb,
                ["runtime_compact_mb_mean"] = Mean(e => e.Cells) * compactBytes / Mb, ["runtime_compact_mb_max"] = Max(e => e.Cells) * compactBytes / Mb,
                ["cook_ms_slice_mean"] = Mean(e => Sliced(Cook(e))) * 1000, ["cook_ms_slice_max"] = Max(e => Sliced(Cook(e))) * 1000,
                ["settle8_ms_slice_mean"] = Mean(e => Sliced(e.Own * 8.0)) * 1000, ["settle8_ms_slice_max"] = Max(e => Sliced(e.Own * 8.0)) * 1000,
                ["settle64_ms_slice_mean"] = Mean(e => Sliced(e.Own * 64.0)) * 1000, ["settle64_ms_slice_max"] = Max(e => Sliced(e.Own * 64.0)) * 1000,
            };
            foreach (var reach in reaches)
            {
                long resident = global, residentChunks = 0;
                foreach (var ((x, y, z), e) in chunks)
                {
                    var lo = new Vector3(x, y, z) * cs;
                    var d = Vector3.Distance(Vector3.Clamp(start, lo, lo + new Vector3(cs)), start);
                    if (d > reach) continue;
                    resident += (long)e.Cells;
                    residentChunks++;
                }
                stats[$"resident_r{reach:0}_chunks"] = residentChunks;
                stats[$"resident_r{reach:0}_cells"] = resident;
                stats[$"resident_r{reach:0}_compact_plus_pool_mb"] = (resident * compactBytes + pool) / Mb;
            }
            chunkStats[c] = stats;
        });

        var extent = census.Max - census.Min;
        var share = (double v) => v / Math.Max(sampleWeight, 1e-9);
        var report = new Dictionary<string, object>
        {
            ["scene"] = sceneName,
            ["geometry"] = new Dictionary<string, object>
            {
                ["instances"] = instances.Length, ["dynamic_instances_left_out"] = dynamicCount,
                ["double_sided_instances"] = sided.Count(d => d),
                ["triangles"] = census.Triangles, ["degenerate_triangles"] = census.Degenerate,
                ["double_sided_triangles"] = census.SidedTriangles,
                ["area_m2"] = census.SurfaceArea, ["double_sided_area_m2"] = census.SidedArea,
                ["sampled_area_m2"] = census.SurfaceArea + census.SidedArea,
                ["bounds_min"] = new[] { census.Min.X, census.Min.Y, census.Min.Z }, ["bounds_max"] = new[] { census.Max.X, census.Max.Y, census.Max.Z },
                ["extent_m"] = new[] { extent.X, extent.Y, extent.Z },
                ["start_camera"] = new[] { start.X, start.Y, start.Z },
            },
            ["coarse"] = new Dictionary<string, object>
            {
                ["sampling"] = "by area", ["spacing_m"] = CensusSpacing, ["rays_per_sample"] = CensusRays, ["reach_m"] = CensusReach, ["lift_m"] = CensusLift,
                ["size_cap_m"] = topSize,
                ["samples"] = coarse, ["samples_traced"] = n, ["traced_share"] = census.Keep, ["place_ms"] = census.Ms,
                ["size_field_rays"] = cookSize,
                ["rays_traced"] = timing.Rays, ["trace_gpu_ms"] = timing.GpuMs, ["trace_wall_ms"] = timing.WallMs, ["trace_frames"] = timing.Frames,
                ["trace_dispatches"] = timing.Dispatches, ["trace_failures"] = timing.Failures,
                ["rate_rays_per_s_gpu"] = rate, ["rate_rays_per_s_wall"] = wallRate,
                ["area_buried_share"] = share(areaBuried),
                ["area_buried_share_ignoring_double_sided"] = share(areaBuriedIgnoringSided),
                ["ray_met_share"] = share(raysMet) / CensusRays,
                ["ray_back_share"] = share(raysBack) / CensusRays,
                ["ray_double_sided_back_share"] = share(raysSidedBack) / CensusRays,
                ["ray_within_0_25m_share"] = share(raysNear) / CensusRays,
                ["area_m2_by_level"] = areaAtLevel, ["traced_samples_by_level"] = samplesAtLevel,
                ["harmonic_band_upper_m"] = harmonicBands.Select(b => b == double.MaxValue ? -1 : b).ToArray(), ["area_m2_by_harmonic_band"] = areaInBand,
            },
            ["cells"] = new Dictionary<string, object>
            {
                ["finest_m"] = finest, ["top_level"] = RtgiTopLevel, ["top_m"] = topSize,
                ["by_level"] = cellsAt, ["own_by_level"] = ownAt, ["junction_by_level"] = junctionAt,
                ["total"] = cells, ["own_total"] = ownCells, ["junction_total"] = junctions, ["level0_1"] = fine,
                ["points"] = points, ["points_buried_skipped"] = pointsBuried,
                ["cell_share"] = cellShare,
                ["lattice_points_filled_outward"] = latticeFilled, ["triangle_sides_without_sample"] = latticeNone,
                ["enumerate_ms"] = enumerateMs,
            },
            ["costs"] = new Dictionary<string, object>
            {
                ["ship_size_field_b_per_coarse_sample"] = CensusShipSizeField, ["ship_b_per_cell"] = CensusShipCell,
                ["near_compact_b_per_level0_cell"] = CensusNearCompact, ["near_today_b_per_level0_cell"] = CensusNearToday,
                ["runtime_b_per_slot"] = slotBytes, ["runtime_slot_breakdown"] = CensusSlotBytes.ToDictionary(s => s.Name, s => (object)s.Bytes),
                ["compact_b_per_cell"] = compactBytes, ["compact_layout_bits"] = CensusCompactBits.ToDictionary(f => f.Name, f => (object)f.Bits),
                ["pool_slots"] = CensusPoolSlots, ["pool_b_per_slot"] = poolBytes, ["pool_layout"] = CensusPoolBytes.ToDictionary(f => f.Name, f => (object)f.Bytes),
                ["cook_rays_per_coarse_sample"] = CensusRays, ["inside_rays_per_junction_cell"] = CensusInsideRays,
                ["junction_rule"] = $"own-level cell whose point's coarse sample met a surface within {CensusJunctionReach} x the cell size",
                ["neighbour_rays_per_cell"] = CensusNeighbourRays, ["near_rays_per_level0_cell"] = CensusNearRays,
                ["settle_rays_per_sample"] = 1, ["slice_share"] = CensusSlice,
            },
            ["pricing"] = new Dictionary<string, object>
            {
                ["shipped_mb"] = shipped / Mb,
                ["near_transfer_compact_mb"] = nearCompact / Mb, ["near_transfer_today_mb"] = nearToday / Mb,
                ["runtime_gpu_mb"] = runtime / Mb, ["runtime_compact_all_mb"] = runtimeCompact / Mb, ["pool_mb"] = pool / Mb,
                ["cook_rays"] = cookRays, ["cook_rays_size_field"] = cookSize, ["cook_rays_inside"] = cookInside, ["cook_rays_neighbours"] = cookNeighbours,
                ["cook_rays_inside_if_all_level0_1"] = fine * CensusInsideRays,
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
        Line($"Geometry: {instances.Length:N0} static instances ({dynamicCount} dynamic left out, {sided.Count(d => d):N0} double-sided), {census.Triangles:N0} triangles ({census.Degenerate:N0} degenerate, {census.SidedTriangles:N0} double-sided), {census.SurfaceArea:N0} m2 (+{census.SidedArea:N0} m2 the double-sided's back sides)");
        Line($"  bounds ({census.Min.X:0.0}, {census.Min.Y:0.0}, {census.Min.Z:0.0}) .. ({census.Max.X:0.0}, {census.Max.Y:0.0}, {census.Max.Z:0.0}), extent {extent.X:0.0} x {extent.Y:0.0} x {extent.Z:0.0} m; start camera ({start.X:0.0}, {start.Y:0.0}, {start.Z:0.0})");
        Line($"");
        Line($"Coarse pass: {coarse:N0} samples by area (one per {CensusSpacing * 100:0} x {CensusSpacing * 100:0} cm), {n:N0} traced (share {census.Keep:0.####}), {CensusRays} cosine rays each to {CensusReach:0} m, lifted {CensusLift * 100:0} cm; size clamp(0.1 x harmonic, {finest * 100:0} cm, {topSize:0.0} m = level {RtgiTopLevel})");
        Line($"  {timing.Rays / 1e6:0.0} M rays: GPU {timing.GpuMs:0} ms ({rate / 1e6:0} M rays/s), wall {timing.WallMs / 1000:0.0} s over {timing.Frames} frames ({wallRate / 1e6:0} M rays/s); {timing.Failures} failed dispatches");
        Line($"  by area: buried {100 * share(areaBuried):0.0}% ({100 * share(areaBuriedIgnoringSided):0.0}% were double-sidedness ignored); rays met {100 * share(raysMet) / CensusRays:0.0}%, single-sided back faces {100 * share(raysBack) / CensusRays:0.0}%, double-sided back faces (as fronts) {100 * share(raysSidedBack) / CensusRays:0.0}%, within 25 cm {100 * share(raysNear) / CensusRays:0.0}%");
        text.Append("  live area by size level (m2):");
        for (var l = 0; l < levels; l++) text.Append(inv, $" L{l} {areaAtLevel[l]:N0}");
        text.AppendLine();
        text.Append("  live area by harmonic distance (m2):");
        for (var b = 0; b < harmonicBands.Length; b++) text.Append(inv, $" {(b == harmonicBands.Length - 1 ? ">512" : "<=" + harmonicBands[b].ToString(inv))} {areaInBand[b]:N0}");
        text.AppendLine();
        Line($"");
        if (cellShare < 1f) Line($"Cells counted over a random {cellShare:P0} of the triangle runs, scaled up by {scale:0.##} (the points and fills below are unscaled; chunk counts are per counted run, scaled).");
        Line($"Cells (enumerated in {enumerateMs / 1000:0.0} s, {points:N0} points, {pointsBuried:N0} buried skipped; {latticeFilled:N0} lattice points filled outward, {latticeNone:N0} triangle sides with no sample within 8 coarse cells):");
        for (var l = 0; l < levels; l++)
            Line($"  L{l,-2} {SizeOfLevel(l),7:0.00} m  {cellsAt[l],14:N0} cells  {ownAt[l],14:N0} own-level  {junctionAt[l],12:N0} junction");
        Line($"  total {cells:N0}, own-level {ownCells:N0}, junction {junctions:N0} (L0+L1 cells {fine:N0})");
        Line($"");
        Line($"Costs: ship {CensusShipSizeField} B/coarse sample + {CensusShipCell} B/cell; near transfer {CensusNearCompact} B (compact) / {CensusNearToday} B (today) per L0 cell; runtime {slotBytes} B/slot today, {compactBytes} B compact + a {CensusPoolSlots:N0}-slot pool of {poolBytes} B");
        Line($"  rays: {CensusRays}/coarse sample, {CensusInsideRays}/junction cell (inside test; junction: nearest hit < {CensusJunctionReach} x cell size), {CensusNeighbourRays}/cell (neighbours), {CensusNearRays}/L0 cell (near, optional); settle 1 ray/sample on own-level cells");
        Line($"  slot today: {string.Join(", ", CensusSlotBytes.Select(s => $"{s.Name} {s.Bytes}"))}");
        Line($"  compact record ({compactBytes} B): {string.Join("; ", CensusCompactBits.Select(f => $"{f.Name} [{f.Bits}]"))}");
        Line($"  pool slot ({poolBytes} B): {string.Join(", ", CensusPoolBytes.Select(f => $"{f.Name} {f.Bytes}"))}");
        Line($"");
        Line($"Pricing (rate {rate / 1e6:0} M rays/s; slice {CensusSlice * 100:0.0}%):");
        Line($"  shipped {shipped / Mb:0.00} MB; near transfer {nearCompact / Mb:0.00} MB compact / {nearToday / Mb:0.00} MB today");
        Line($"  runtime GPU: today's layout {runtime / Mb:0.0} MB; compact, all resident {runtimeCompact / Mb:0.0} MB (+ pool {pool / Mb:0} MB)");
        Line($"  cook {cookRays / 1e6:0.0} M rays (size {cookSize / 1e6:0.0} M, inside {cookInside / 1e6:0.0} M [all L0+L1: {fine * CensusInsideRays / 1e6:0.0} M], neighbours {cookNeighbours / 1e6:0.0} M): {Full(cookRays):0.00} s full, {Sliced(cookRays):0.0} s at 2 ms/frame");
        Line($"  near transfer {nearRays / 1e6:0.0} M rays: {Full(nearRays):0.00} s full, {Sliced(nearRays):0.0} s sliced");
        Line($"  settle 8: {settle8 / 1e6:0.0} M rays, {Full(settle8):0.00} s full, {Sliced(settle8):0.0} s sliced; settle 64: {settle64 / 1e6:0.0} M rays, {Full(settle64):0.00} s full, {Sliced(settle64):0.0} s sliced");
        Line($"");
        Line($"Chunks (by cell centre; times at the 2 ms slice; resident: chunks within R of the start camera, compact + pool):");
        foreach (var c in chunkStats)
        {
            Line($"  {c["chunk_m"],2} m: {c["chunks"],6} chunks ({c["cells_bigger_than_chunk"]} bigger cells apart); cells mean {c["cells_mean"]:N0} max {c["cells_max"]:N0}; junction mean {c["junction_cells_mean"]:N0} max {c["junction_cells_max"]:N0}; shipped KB mean {c["shipped_kb_mean"]:0.0} max {c["shipped_kb_max"]:0.0}; cook ms mean {c["cook_ms_slice_mean"]:0} max {c["cook_ms_slice_max"]:0}; settle8 ms mean {c["settle8_ms_slice_mean"]:0.0} max {c["settle8_ms_slice_max"]:0.0}; settle64 ms mean {c["settle64_ms_slice_mean"]:0} max {c["settle64_ms_slice_max"]:0}");
            Line($"        resident R 32 m: {c["resident_r32_chunks"]} chunks, {c["resident_r32_cells"]:N0} cells, {c["resident_r32_compact_plus_pool_mb"]:0.0} MB; R 64 m: {c["resident_r64_chunks"]} chunks, {c["resident_r64_cells"]:N0} cells, {c["resident_r64_compact_plus_pool_mb"]:0.0} MB");
        }
        File.WriteAllText(basePath + ".txt", text.ToString());

        return string.Create(inv,
            $"[VulkanSponza] rtgi census {sceneName}: {census.Triangles:N0} tris, {census.SurfaceArea:N0} m2, {coarse:N0} coarse, buried {100 * share(areaBuried):0.0}%, cells {cells:N0} (L0 {cellsAt[0]:N0}, own {ownCells:N0}, junction {junctions:N0}), shipped {shipped / Mb:0.0} MB, runtime {runtime / Mb:0} MB today / {runtimeCompact / Mb:0} MB compact, cook {Full(cookRays):0.0} s / {Sliced(cookRays):0} s sliced, settle64 {Full(settle64):0.0} s / {Sliced(settle64):0} s sliced, {rate / 1e6:0} M rays/s; written to {basePath}.json/.txt.");
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
