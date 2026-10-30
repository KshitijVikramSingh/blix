using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// RTGI's world volume (on by default; --rtgi-no-volume and the RTGI panel's switch off): a sparse volume of probes (rtgi_volume.glsl) whose cooked geometry -- not light -- sizes the cache's cells
// and gives the cells' traced visibility (rtgi_volume_vis.comp) its probes to reach. The probes once learnt light too, for
// young cells to lean on; the readout leaked and blotched with it, and nothing reads it now.
//
// Laid out for a streamed world (Jungle at full scale: 8.65 M foliage instances, terrain to 12 km, streaming, LOD,
// billboards): probes in bricks of 4 x 4 x 4 at levels 0.5, 1, 2, 4, 8 m (--rtgi-volume-spacing the 2 m), bricks where the
// geometry is and only there, found through a hash of (brick, level) the CPU owns. The 2 m and 8 m levels are placed from
// the ray scene's triangles (a probe within 1.5 spacings of a triangle's plane, inside its bounds); a streamed chunk would
// bring and take its own bricks, and a brick leaving the GPU would keep its probes in RAM by its key (persistence) -- not
// built yet: one load, every brick resident.
//
// Everything about it but the light is the geometry's, cooked at load (rtgi_volume_cook.comp), in chunks through the same
// Metal batch, a frame each: every probe's 256 rays to move it out of what it sits inside, then 512 for its 8 x 8 depth map
// and its harmonic mean distance. Then the refinement, --rtgi-volume-refine rounds (2): where the cook found the geometry
// tighter than a probe's spacing (inside a thin wall, moved far, a gap narrower than a spacing), the next level's probes
// around it (2 m -> 1 m -> 0.5 m), and the cook again. The harmonic mean distances size the cache's cells
// (rtgi_volume.glsl's volCellScale, --rtgi-volume-cells). The volume is read, and sizes the cells, only once all that is
// done. The camera never decides how big a point's cell is (the user, 2026-10-29: the cache's cells sized by the camera's
// distance crawled as it moved; leaks through Crytek's walls).
//
// Each cook frame the CPU lists a chunk of probes; rtgi_volume_rays.comp casts their rays, the RTGI Metal batch traces
// them, rtgi_volume_cook.comp folds them in.
internal sealed partial class SponzaLoop
{
    // Levels: level l's probes VolFinest x 2^l apart; the 2 m and 8 m placed from the triangles, the finer two refined.
    private const int VolLevels = 5, VolPlacedFine = 2, VolPlacedCoarse = 4;
    private bool rtgiVolume = true, rtgiVolumeBuilt;
    private float rtgiVolumeSpacing = 2f;
    private int rtgiVolumeMost = 4_000_000;
    // A probe whose rays meet back faces past this share is inside something (left out of every lookup).
    private float rtgiVolumeBackShare = 0.3f;
    // --rtgi-volume-refine N: rounds of refinement; --rtgi-volume-refine-move / -gap: the move and the gap (spacings) that ask
    // for it.
    private int rtgiVolumeRefine = 2;
    private float rtgiVolumeRefineMove = 0.2f, rtgiVolumeRefineGap = 1f;
    // --rtgi-volume-cells A: the cache's cells A x the harmonic mean distance (0: by the camera's distance, as before), at most
    // --rtgi-volume-cell-max metres; --rtgi-volume-far-grow G: the camera's floor, G per metre of distance (0 none).
    private float rtgiVolumeCells = 0.1f, rtgiVolumeCellMax = 1f, rtgiVolumeFarGrow;

    // The probes, per level their global coordinates (VolKey); per probe in layout order its batch entry (id, level,
    // coordinate bits) and its place.
    private HashSet<long>[] rtgiVolSets = Array.Empty<HashSet<long>>();
    private uint[] rtgiVolEntries = Array.Empty<uint>();
    private Vector3[] rtgiVolPositions = Array.Empty<Vector3>();
    private int rtgiVolBricks, rtgiVolTableSlots;
    private Task<HashSet<long>[]>? rtgiVolBuild;
    private double rtgiVolPlaceMs;

    private GpuBufferHandle rtgiVolTableBuf, rtgiVolProbesBuf, rtgiVolDepthBuf, rtgiVolRefineBuf;
    // The cook (rtgi_volume_cook.comp): 0 moving the probes, 1 their depth maps, 2 done; the next probe to list; the frame
    // it started. This frame's kind of rays (rtgi_volume_rays.comp: 1 and 2, the cook's passes) and rays a probe.
    private int rtgiVolCookStage, rtgiVolCookCursor;
    private uint rtgiVolCookFrom;
    private readonly System.Diagnostics.Stopwatch rtgiVolCookClock = new();
    // The refinement: rounds done; the frame its flags may be read (the cook's last chunk done on the GPU); all done.
    private int rtgiVolRefineRound;
    private uint rtgiVolRefineAt = uint.MaxValue;
    private bool rtgiVolReady;
    private int rtgiVolKindNow, rtgiVolRaysNow;
    private const int RtgiVolMoveRays = 256, RtgiVolDepthRays = 512;
    // The cook's rays a frame at most (the ray buffers' room): 2,048 probes' first pass, 1,024 probes' second.
    private const int RtgiVolRayRoom = 524_288;
    private ShaderInterface rtgiVolCookInterface = null!;
    private PassHandle rtgiVolCookPassHandle;
    private PipelineHandle rtgiVolCookPipeline;
    private GpuBufferHandle[] rtgiVolBatchBufs = Array.Empty<GpuBufferHandle>(), rtgiVolRayBufs = Array.Empty<GpuBufferHandle>(),
        rtgiVolHitBufs = Array.Empty<GpuBufferHandle>();
    private ShaderInterface rtgiVolRaysInterface = null!;
    private PassHandle rtgiVolRaysPassHandle;
    private PipelineHandle rtgiVolRaysPipeline;
    private int rtgiVolListedNow;
    private int rtgiVolBatchSlot;
    private const int RtgiVolBatchMost = RtgiVolRayRoom / RtgiVolMoveRays;
    // Cells' traced visibility (rtgi_volume_vis.comp): per cache slot its key and mask; the readout's requests, two in turn
    // (the readout writes one, the next frame's passes trace and empty it); 8 rays a request and their hits.
    private const int RtgiVolVisRoom = 8192;
    // Rays a request: 8 to its probes, 12 to its neighbours across the surface (rtgi_volume_vis.comp's VIS_RAYS).
    private const int RtgiVolVisRays = 20;
    private GpuBufferHandle rtgiVolCellVis;
    private GpuBufferHandle[] rtgiVolVisRequests = Array.Empty<GpuBufferHandle>(), rtgiVolVisRays = Array.Empty<GpuBufferHandle>(),
        rtgiVolVisHits = Array.Empty<GpuBufferHandle>();
    private ShaderInterface rtgiVolVisInterface = null!;
    private PassHandle rtgiVolVisRaysPassHandle, rtgiVolVisPassHandle, rtgiVolVisClearPassHandle;
    private PipelineHandle rtgiVolVisPipeline;
    private bool rtgiVolVisNow;

    private float VolFinest => rtgiVolumeSpacing / (1 << VolPlacedFine);
    private float VolSpacingOf(int level) => VolFinest * (1 << level);
    private bool RtgiVolOn => rtgiVolume && rtgiVolumeBuilt && rtgiVolReady;
    // rtgi_volume.glsl's VOL_PARAMS: x level 0's spacing, y the levels, z the table's slots - 1, w 1 on.
    private Vector4 RtgiVolumeParams => new(VolFinest, VolLevels, Math.Max(rtgiVolTableSlots, 1) - 1, RtgiVolOn ? 1f : 0f);
    // Its VOL_GEOM: the cache's cells sized by the geometry (x 0: by the camera).
    private Vector4 RtgiVolumeGeom => new(RtgiVolOn ? rtgiVolumeCells : 0f, rtgiVolumeCellMax, rtgiVolumeFarGrow, VolPlacedFine);
    // The panel's A/B switch: the traced neighbour test.
    private bool rtgiVolumeNeighbours = true;
    private Vector4 RtgiVolumeReadout => new(0f, RtgiVolVisRoom, rtgiVolumeNeighbours ? 1f : 0f, 0f);
    private GpuBufferHandle RtgiVolCellVisOrDummy => rtgiVolumeBuilt ? rtgiVolCellVis : rtgiVolDummy;
    // The requests the readout writes this frame (the next frame's RecordRtgiVolumeVis traces them).
    private GpuBufferHandle RtgiVolVisRequestsNow => rtgiVolumeBuilt ? rtgiVolVisRequests[(int)(rtgiFrameCount & 1u)] : rtgiVolDummy;
    private GpuBufferHandle RtgiVolTableOrDummy => rtgiVolumeBuilt ? rtgiVolTableBuf : rtgiVolDummy;
    private GpuBufferHandle RtgiVolProbesOrDummy => rtgiVolumeBuilt ? rtgiVolProbesBuf : rtgiVolDummy;
    private GpuBufferHandle RtgiVolDepthOrDummy => rtgiVolumeBuilt ? rtgiVolDepthBuf : rtgiVolDummy;
    private GpuBufferHandle rtgiVolDummy;

    private void ParseRtgiVolume(Blix.Core.AppArgs args)
    {
        // --rtgi-no-volume: RTGI as it ran before the volume, its cells sized by the camera's distance (the A/B).
        if (args.Flag("rtgi-no-volume")) rtgiVolume = false;
        if (args.Float("rtgi-volume-spacing") is { } sp) rtgiVolumeSpacing = MathF.Max(0.25f, sp);
        if (args.Int("rtgi-volume-most") is { } most) rtgiVolumeMost = Math.Max(1024, most);
        if (args.Float("rtgi-volume-back") is { } bk) rtgiVolumeBackShare = Math.Clamp(bk, 0.01f, 1f);
        if (args.Int("rtgi-volume-refine") is { } rf) rtgiVolumeRefine = Math.Clamp(rf, 0, VolPlacedFine);
        if (args.Float("rtgi-volume-refine-move") is { } rm) rtgiVolumeRefineMove = MathF.Max(0f, rm);
        if (args.Float("rtgi-volume-refine-gap") is { } rg) rtgiVolumeRefineGap = MathF.Max(0f, rg);
        if (args.Float("rtgi-volume-cells") is { } vc) rtgiVolumeCells = MathF.Max(0f, vc);
        if (args.Float("rtgi-volume-cell-max") is { } cm) rtgiVolumeCellMax = MathF.Max(0.05f, cm);
        if (args.Float("rtgi-volume-far-grow") is { } fg) rtgiVolumeFarGrow = MathF.Max(0f, fg);
        ParseRtgiNearCook(args);
        if (!rtgiVolume) rtgiNearCook = false;
    }

    // The ray scene's triangles into the placed levels' probes, on a worker (FinishRayScene); taken up when done.
    private void StartRtgiVolumeBuild(RayQueryScene scene)
    {
        if (!rtgiVolume || rtgiVolBuild is not null) return;
        var instances = scene.Instances.Where(i => !i.Dynamic).ToArray();
        // Kept for the near field's cook (SponzaLoop.RtgiNearCook.cs), which places its cells once the volume is refined.
        if (rtgiNearCook) rtgiVolInstances = instances;
        var levels = new[] { VolPlacedFine, VolPlacedCoarse };
        var spacings = levels.Select(VolSpacingOf).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        rtgiVolBuild = Task.Run(() =>
        {
            var placed = PlaceVolumeProbes(instances, spacings);
            var sets = Enumerable.Range(0, VolLevels).Select(_ => new HashSet<long>()).ToArray();
            for (var k = 0; k < levels.Length; k++) sets[levels[k]] = placed[k];
            rtgiVolPlaceMs = clock.Elapsed.TotalMilliseconds;
            return sets;
        });
    }

    private static readonly Vector3 VolJitter = new(0.037f, 0.051f, 0.029f);

    private static Vector3 Floor3(Vector3 v) => new(MathF.Floor(v.X), MathF.Floor(v.Y), MathF.Floor(v.Z));

    private static long VolKey(int x, int y, int z) =>
        ((long)(x + (1 << 20)) << 42) | ((long)(y + (1 << 20)) << 21) | (long)(z + (1 << 20));

    private static (int X, int Y, int Z) VolUnkey(long k) =>
        ((int)((k >> 42) & 0x1FFFFF) - (1 << 20), (int)((k >> 21) & 0x1FFFFF) - (1 << 20), (int)(k & 0x1FFFFF) - (1 << 20));

    // Per spacing, the probes within 1.5 spacings of a triangle's plane, inside its bounds.
    private static HashSet<long>[] PlaceVolumeProbes(RayQueryScene.Instance[] instances, float[] spacings)
    {
        var levels = spacings.Length;
        var sets = Enumerable.Range(0, levels).Select(_ => new ConcurrentDictionary<long, byte>()).ToArray();
        Parallel.ForEach(instances, () => Enumerable.Range(0, levels).Select(_ => new HashSet<long>()).ToArray(), (inst, _, local) =>
        {
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
                var lo = Vector3.Min(a, Vector3.Min(b, c));
                var hi = Vector3.Max(a, Vector3.Max(b, c));
                for (var l = 0; l < levels; l++)
                {
                    var s = spacings[l];
                    var pad = 1.5f * s;
                    // Probe coordinate c stands at (c + 0.5 + jitter) x s.
                    var c0 = Floor3((lo - new Vector3(pad)) / s - new Vector3(0.5f) - VolJitter) + Vector3.One;
                    var c1 = Floor3((hi + new Vector3(pad)) / s - new Vector3(0.5f) - VolJitter);
                    for (var z = (int)c0.Z; z <= (int)c1.Z; z++)
                    for (var y = (int)c0.Y; y <= (int)c1.Y; y++)
                    for (var x = (int)c0.X; x <= (int)c1.X; x++)
                    {
                        var p = (new Vector3(x, y, z) + new Vector3(0.5f) + VolJitter) * s;
                        if (MathF.Abs(Vector3.Dot(nrm, p - a)) > pad) continue;
                        local[l].Add(VolKey(x, y, z));
                    }
                }
            }
            return local;
        }, local =>
        {
            for (var l = 0; l < levels; l++)
                foreach (var k in local[l]) sets[l].TryAdd(k, 0);
        });
        return sets.Select(d => new HashSet<long>(d.Keys)).ToArray();
    }

    private static uint VolHash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352du;
        x ^= x >> 15; x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    // rtgi_volume.glsl's volBrickHash.
    private static (uint Start, uint Key) BrickHash(int bx, int by, int bz, uint level, uint mask)
    {
        unchecked
        {
            var a = VolHash((uint)bx * 0x9E3779B1u ^ VolHash((uint)by * 0x85EBCA77u ^ VolHash((uint)bz * 0xC2B2AE3Du ^ (level + 0x51u))));
            var k = VolHash(a ^ 0x68E31DA4u ^ (uint)bx * 0x1B873593u ^ (uint)by * 0xCC9E2D51u ^ (uint)bz * 0x27D4EB2Fu) | 1u;
            return (a & mask, k);
        }
    }

    private void DeclareRtgiVolumeRaysPass(Func<string, ShaderInterface> reflect)
    {
        rtgiVolRaysInterface = reflect("rtgi_volume_rays.comp");
        rtgiVolRaysPassHandle = graph.ComputePass("rtgi-volume-rays").AfterAsync().Shader(rtgiVolRaysInterface).Handle;
        DeclareRtgiNearCookPasses(reflect);
        rtgiVolVisInterface = reflect("rtgi_volume_vis.comp");
        rtgiVolVisRaysPassHandle = graph.ComputePass("rtgi-volume-vis-rays").AfterAsync().Shader(rtgiVolVisInterface).Handle;
    }

    private void DeclareRtgiVolumeAsyncPasses(Func<string, ShaderInterface> reflect)
    {
        rtgiVolCookInterface = reflect("rtgi_volume_cook.comp");
        rtgiVolCookPassHandle = graph.ComputePass("rtgi-volume-cook").Async(rtgiAsync).Shader(rtgiVolCookInterface).Handle;
        rtgiVolVisPassHandle = graph.ComputePass("rtgi-volume-vis").Async(rtgiAsync).Shader(rtgiVolVisInterface).Handle;
        rtgiVolVisClearPassHandle = graph.ComputePass("rtgi-volume-vis-clear").Async(rtgiAsync).Shader(rtgiVolVisInterface).Handle;
        DeclareRtgiNearCookAsyncPasses(reflect);
    }

    private void CreateRtgiVolumePipelines(Func<string, ShaderInterface, ComputePipelineOptions, PipelineHandle> compute, ComputePipelineOptions full32)
    {
        rtgiVolRaysPipeline = compute("rtgi_volume_rays", rtgiVolRaysInterface, ComputePipelineOptions.None);
        rtgiVolCookPipeline = compute("rtgi_volume_cook", rtgiVolCookInterface, full32);
        rtgiVolVisPipeline = compute("rtgi_volume_vis", rtgiVolVisInterface, ComputePipelineOptions.None);
        CreateRtgiNearCookPipelines(compute);
    }

    // The buffers the cache's shaders bind whether or not the volume is on (a dummy until it is built).
    private void CreateRtgiVolumeDummy()
    {
        if (rtgiVolDummy.Equals(default(GpuBufferHandle))) rtgiVolDummy = Own(device.CreateGpuBuffer(256, new byte[256], "sponza.rtgi.volume-dummy"));
    }

    // Takes the placement up once done: its layout made.
    private void TakeRtgiVolumePlacement()
    {
        if (rtgiVolumeBuilt || rtgiVolBuild is not { IsCompleted: true } task) return;
        rtgiVolSets = task.Result;
        rtgiVolBuild = Task.FromResult(Array.Empty<HashSet<long>>());
        var rays = RtgiVolRayRoom;
        rtgiVolBatchBufs = Enumerable.Range(0, 3).Select(k => Own(device.CreateReadableGpuBuffer(RtgiVolBatchMost * 16, $"sponza.rtgi.volume-batch{k}"))).ToArray();
        rtgiVolRayBufs = Enumerable.Range(0, 2).Select(k => Own(device.CreateGpuBuffer(rays * 32, name: $"sponza.rtgi.volume-rays{k}"))).ToArray();
        rtgiVolHitBufs = Enumerable.Range(0, 2).Select(k => Own(device.CreateGpuBuffer(rays * 48, name: $"sponza.rtgi.volume-hits{k}"))).ToArray();
        rtgiVolCellVis = Own(device.CreateGpuBuffer(RtgiSlots * 8, new byte[RtgiSlots * 8], "sponza.rtgi.volume-cell-vis"));
        var reqBytes = 16 + RtgiVolVisRoom * 32;
        rtgiVolVisRequests = Enumerable.Range(0, 2).Select(k => Own(device.CreateGpuBuffer(reqBytes, new byte[reqBytes], $"sponza.rtgi.volume-vis-requests{k}"))).ToArray();
        rtgiVolVisRays = Enumerable.Range(0, 2).Select(k => Own(device.CreateGpuBuffer(RtgiVolVisRoom * RtgiVolVisRays * 32, name: $"sponza.rtgi.volume-vis-rays{k}"))).ToArray();
        rtgiVolVisHits = Enumerable.Range(0, 2).Select(k => Own(device.CreateGpuBuffer(RtgiVolVisRoom * RtgiVolVisRays * 48, name: $"sponza.rtgi.volume-vis-hits{k}"))).ToArray();
        MakeRtgiVolumeLayout($"placed from the triangles in {rtgiVolPlaceMs:0} ms");
        rtgiVolumeBuilt = true;
    }

    // The probes' layout from the sets: bricks, the table, the probe buffers (new, empty: the cook runs again over all).
    private void MakeRtgiVolumeLayout(string how)
    {
        var entries = new List<uint>();
        var positions = new List<Vector3>();
        var perLevel = new int[VolLevels];
        var brickIndex = new Dictionary<(int, int, int, int), int>();
        for (var l = 0; l < VolLevels; l++)
        {
            var s = VolSpacingOf(l);
            var probes = rtgiVolSets[l].Select(VolUnkey).ToArray();
            if (probes.Length > rtgiVolumeMost)
            {
                var focus = cameraPosition;
                probes = probes.OrderBy(c => Vector3.DistanceSquared((new Vector3(c.X, c.Y, c.Z) + new Vector3(0.5f)) * s, focus)).Take(rtgiVolumeMost).ToArray();
            }
            perLevel[l] = probes.Length;
            foreach (var (x, y, z) in probes)
            {
                var bk = (x >> 2, y >> 2, z >> 2, l);
                if (!brickIndex.TryGetValue(bk, out var brick)) { brick = brickIndex.Count; brickIndex.Add(bk, brick); }
                var id = (uint)(brick * 64 + (x & 3) + 4 * (y & 3) + 16 * (z & 3));
                entries.Add(id); entries.Add((uint)l);
                entries.Add((uint)(x + 32768) | ((uint)(y + 32768) << 16)); entries.Add((uint)(z + 32768));
                positions.Add((new Vector3(x, y, z) + new Vector3(0.5f) + VolJitter) * s);
            }
        }
        // The table: (key, brick) pairs, linear probing within rtgi_volume.glsl's VOL_PROBE_STEPS; grown until every brick fits.
        var slots = 1024;
        while (slots < brickIndex.Count * 2) slots <<= 1;
        uint[] table;
        while (true)
        {
            table = new uint[slots * 2];
            var fits = true;
            foreach (var ((bx, by, bz, l), brick) in brickIndex)
            {
                var (start, key) = BrickHash(bx, by, bz, (uint)l, (uint)(slots - 1));
                var placed = false;
                for (uint k = 0; k < 8 && !placed; k++)
                {
                    var at = (int)((start + k) & (uint)(slots - 1));
                    if (table[2 * at] != 0) continue;
                    table[2 * at] = key; table[2 * at + 1] = (uint)brick; placed = true;
                }
                if (!placed) { fits = false; break; }
            }
            if (fits) break;
            slots <<= 1;
        }
        // The last layout's buffers go (a load-time step: the device waited on first).
        if (rtgiVolumeBuilt)
        {
            device.WaitIdle();
            Disown(rtgiVolTableBuf); Disown(rtgiVolProbesBuf); Disown(rtgiVolDepthBuf); Disown(rtgiVolRefineBuf);
        }
        var probesN = positions.Count;
        rtgiVolEntries = entries.ToArray();
        rtgiVolPositions = positions.ToArray();
        rtgiVolBricks = brickIndex.Count;
        rtgiVolTableSlots = slots;
        var probeBytes = (long)rtgiVolBricks * 64 * 24 * 4;
        var depthBytes = (long)rtgiVolBricks * 64 * 64 * 4;
        rtgiVolTableBuf = Own(device.CreateGpuBuffer(table.Length * 4, MemoryMarshal.AsBytes(table.AsSpan()).ToArray(), "sponza.rtgi.volume-table"));
        rtgiVolProbesBuf = Own(device.CreateGpuBuffer((int)probeBytes, new byte[probeBytes], "sponza.rtgi.volume-probes"));
        rtgiVolDepthBuf = Own(device.CreateGpuBuffer((int)depthBytes, new byte[depthBytes], "sponza.rtgi.volume-depth"));
        rtgiVolRefineBuf = Own(device.CreateReadableGpuBuffer(rtgiVolBricks * 64 * 4, "sponza.rtgi.volume-refine"));
        rtgiVolCookStage = 0;
        rtgiVolCookCursor = 0;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] rtgi volume: {string.Join(", ", perLevel.Select((c, l) => (c, l)).Where(e => e.c > 0).Select(e => $"{e.c:N0} probes at {VolSpacingOf(e.l):0.##} m"))} in {rtgiVolBricks:N0} bricks ({(probeBytes + depthBytes) / 1048576.0:0} MB with their depth maps, {rtgiVolBricks * 64.0 / Math.Max(probesN, 1):0.0}x the probes' room), table {slots:N0} slots; {how}."));
    }

    // After a cook: the probes it flagged (rtgi_volume_cook.comp) at the level being refined bring the next level's probes
    // around them -- the 4 x 4 x 4 finer ones over their cell and its neighbours' halves -- and the layout is made again
    // (cooked again from the start). False when nothing more is to be refined.
    private bool RefineRtgiVolume()
    {
        var level = VolPlacedFine - rtgiVolRefineRound;
        if (rtgiVolRefineRound >= rtgiVolumeRefine || level <= 0) return false;
        rtgiVolRefineRound++;
        var flags = new byte[rtgiVolBricks * 64 * 4];
        device.ReadMappedGpuBuffer(rtgiVolRefineBuf, 0, flags);
        var flag = MemoryMarshal.Cast<byte, uint>(flags);
        var finer = rtgiVolSets[level - 1];
        var before = finer.Count;
        var flagged = 0;
        for (var i = 0; i < rtgiVolPositions.Length; i++)
        {
            if (rtgiVolEntries[i * 4 + 1] != (uint)level || flag[(int)rtgiVolEntries[i * 4]] == 0) continue;
            flagged++;
            var e = rtgiVolEntries[i * 4 + 2];
            var (x, y, z) = ((int)(e & 0xFFFF) - 32768, (int)(e >> 16) - 32768, (int)rtgiVolEntries[i * 4 + 3] - 32768);
            for (var dz = -1; dz <= 2; dz++)
            for (var dy = -1; dy <= 2; dy++)
            for (var dx = -1; dx <= 2; dx++)
                finer.Add(VolKey(2 * x + dx, 2 * y + dy, 2 * z + dz));
        }
        if (finer.Count == before) return false;
        MakeRtgiVolumeLayout(string.Create(CultureInfo.InvariantCulture,
            $"refined: {flagged:N0} {VolSpacingOf(level):0.##} m probes found the geometry tighter than their spacing, {finer.Count - before:N0} {VolSpacingOf(level - 1):0.##} m probes added"));
        return true;
    }

    // The cook's next chunk: as many probes as the ray buffers hold at this pass's rays a probe, in order.
    private int ListRtgiVolumeCook(int probes)
    {
        if (rtgiVolCookStage == 0 && rtgiVolCookCursor == 0) { rtgiVolCookFrom = rtgiFrameCount; rtgiVolCookClock.Restart(); }
        rtgiVolKindNow = rtgiVolCookStage + 1;
        rtgiVolRaysNow = rtgiVolCookStage == 0 ? RtgiVolMoveRays : RtgiVolDepthRays;
        var chunk = Math.Min(RtgiVolRayRoom / rtgiVolRaysNow, probes - rtgiVolCookCursor);
        var list = new uint[chunk * 4];
        Array.Copy(rtgiVolEntries, rtgiVolCookCursor * 4, list, 0, chunk * 4);
        rtgiVolBatchSlot = (rtgiVolBatchSlot + 1) % 3;
        device.WriteMappedGpuBuffer(rtgiVolBatchBufs[rtgiVolBatchSlot], 0, MemoryMarshal.AsBytes(list.AsSpan()));
        rtgiVolCookCursor += chunk;
        if (rtgiVolCookCursor >= probes)
        {
            rtgiVolCookCursor = 0;
            rtgiVolCookStage++;
            if (rtgiVolCookStage == 2)
            {
                // Its flags readable once this frame's work is done on the GPU. Four frames on, the second round's count
                // came out 641, 1,024 and 1,502 on three boots of Crytek Sponza (the cook's last chunks still in flight on
                // the async queue): thirty.
                rtgiVolRefineAt = rtgiFrameCount + 30;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] rtgi volume: {probes:N0} probes moved clear of the geometry and their depth maps traced ({probes * (long)(RtgiVolMoveRays + RtgiVolDepthRays) / 1e6:0.0} M rays) in {rtgiFrameCount - rtgiVolCookFrom + 1} frames, {rtgiVolCookClock.Elapsed.TotalSeconds:0.0} s."));
            }
        }
        return chunk;
    }

    // Placement time, while the volume cooks: the chunk's probes listed and their rays cast (the Metal batch traces them).
    private void RecordRtgiVolumeRays(int cur)
    {
        rtgiVolListedNow = 0;
        if (!rtgiVolume) return;
        TakeRtgiVolumePlacement();
        if (!rtgiVolumeBuilt) return;
        // The cook done and its flags readable: refine, or the volume is ready.
        if (!rtgiVolReady && rtgiVolCookStage >= 2 && rtgiFrameCount >= rtgiVolRefineAt)
        {
            rtgiVolRefineAt = uint.MaxValue;
            if (!RefineRtgiVolume())
            {
                rtgiVolReady = true;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] rtgi volume: ready -- {rtgiVolPositions.Length:N0} probes; the cache's cells sized by the geometry ({rtgiVolumeCells:0.###} x the harmonic mean distance, at most {rtgiVolumeCellMax:0.##} m{(rtgiVolumeCells > 0f ? "" : ": off, by the camera's distance")})."));
            }
        }
        if (rtgiVolReady || rtgiVolCookStage >= 2 || rtgiVolPositions.Length == 0) return;
        rtgiVolListedNow = ListRtgiVolumeCook(rtgiVolPositions.Length);
        if (rtgiVolListedNow == 0) return;
        var n = rtgiVolListedNow * rtgiVolRaysNow;
        graph.Dispatch(rtgiVolRaysPassHandle, new DispatchCommand(rtgiVolRaysPipeline, (n + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uVolume", new Vector4Uniform(RtgiVolumeParams)),
                new("uVol", new Vector4Uniform(new Vector4(rtgiVolListedNow, rtgiVolRaysNow, rayReach, 0f))),
                new("uVol2", new Vector4Uniform(new Vector4(rtgiVolKindNow, 0f, 0f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiVolumeBatch", rtgiVolBatchBufs[rtgiVolBatchSlot]),
                new ShaderBufferBinding("RtgiVolumeRayBuffer", rtgiVolRayBufs[cur]),
                new ShaderBufferBinding("RtgiVolumeProbes", rtgiVolProbesBuf),
            }));
    }

    // Placement time: last frame's readout's visibility requests, 8 rays each (traced in the Metal batch), then with the
    // training their masks into the cells and the list emptied.
    private void RecordRtgiVolumeVis(int cur)
    {
        rtgiVolVisNow = RtgiVolOn;
        if (!rtgiVolVisNow) return;
        var requests = rtgiVolVisRequests[cur];
        DispatchCommand Vis(int kind, int threads) => new(rtgiVolVisPipeline, (threads + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uVolume", new Vector4Uniform(RtgiVolumeParams)),
                new("uVis", new Vector4Uniform(new Vector4(RtgiVolVisRoom, rtgiCell, 0f, kind))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiVolumeVisRequests", requests),
                new ShaderBufferBinding("RtgiVolumeVisRays", rtgiVolVisRays[cur]),
                new ShaderBufferBinding("RtgiVolumeVisHits", rtgiVolVisHits[cur]),
                new ShaderBufferBinding("RtgiVolumeCellVis", rtgiVolCellVis),
                new ShaderBufferBinding("RtgiVolumeTable", rtgiVolTableBuf),
                new ShaderBufferBinding("RtgiVolumeProbes", rtgiVolProbesBuf),
            });
        graph.Dispatch(rtgiVolVisRaysPassHandle, Vis(0, RtgiVolVisRoom * RtgiVolVisRays));
        graph.Dispatch(rtgiVolVisPassHandle, Vis(1, RtgiVolVisRoom));
        graph.Dispatch(rtgiVolVisClearPassHandle, Vis(2, 1));
    }

    // With the training (async): the cook's chunk's hits folded into its probes.
    private void RecordRtgiVolumeCook(int cur)
    {
        if (!rtgiVolumeBuilt || rtgiVolListedNow == 0) return;
        graph.Dispatch(rtgiVolCookPassHandle, new DispatchCommand(rtgiVolCookPipeline, rtgiVolListedNow, 1, 1,
            new ShaderUniform[]
            {
                new("uVolume", new Vector4Uniform(RtgiVolumeParams with { X = VolFinest })),
                new("uVol", new Vector4Uniform(new Vector4(rtgiVolListedNow, rtgiVolRaysNow, rtgiVolumeBackShare, rtgiVolKindNow))),
                new("uVol2", new Vector4Uniform(new Vector4(rtgiVolumeRefineMove, rtgiVolumeRefineGap, 1f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiVolumeBatch", rtgiVolBatchBufs[rtgiVolBatchSlot]),
                new ShaderBufferBinding("RtgiVolumeRayBuffer", rtgiVolRayBufs[cur]),
                new ShaderBufferBinding("RtgiVolumeHits", rtgiVolHitBufs[cur]),
                new ShaderBufferBinding("RtgiVolumeProbes", rtgiVolProbesBuf),
                new ShaderBufferBinding("RtgiVolumeDepth", rtgiVolDepthBuf),
                new ShaderBufferBinding("RtgiVolumeRefine", rtgiVolRefineBuf),
            }));
    }
}
