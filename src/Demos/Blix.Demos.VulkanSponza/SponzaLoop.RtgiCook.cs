using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;
using ImGuiNET;

namespace Blix.Demos.VulkanSponza;

// RTGI's cook (step 1a of the 2026-10-25 redesign): the cache's cells cooked from the geometry alone, by the census's rules
// (SponzaLoop.RtgiCensus.cs, measured on 8 scenes), as a job -- per world chunk, for now one chunk the whole scene's
// bounds -- that runs at load behind the cook screen ("Settling the light", the GPU its own: the cache sits out until
// it is done) or, with --no-rtgi-settle, beside the frame under a budget (--rtgi-cook-ms, the GPU's trace time a frame,
// or --rtgi-cook-rays). It replaces the world volume and the volume-fed near cook (2026-10-30). Its stages, each resumable
// a frame at a time:
//
//   geometry    the static instances' triangles, which are alpha-tested, the per-triangle bits the GPU reads (worker)
//   size field  by area, coarse first (2 m -> 0.5 m -> 0.25 m -> 0.125 m: refined where buried or the size is under 2 x the
//               pass's spacing, the last only where a 0.25 m sample's side is dropped -- a wall's foot, a narrow gap;
//               CensusPassSpacing), both sides of every surface: per sample 64 cosine rays lifted 1 cm, reach 64 m, its harmonic
//               distance, back-face share, downward escapes, nearest hit; a side kept where its back share <= 0.3 (an
//               alpha-tested triangle's back faces as fronts) and its void share <= 0.5 (alpha-tested: both kept); size
//               clamp(0.1 x harmonic, 5 cm, the top level's 51.2 m) (rtgi_cook.comp kinds 0, 1; the Metal batch)
//   cells       the size field smoothed along the surfaces (3 passes of each sample's log size averaged over its pass's
//               same-side samples on its plane within 1.5 spacings); every triangle, each live side, in pieces, its
//               points a fraction of the local size apart, each point's size interpolated over its piece's lattice of
//               smoothed sizes (its own side's samples on its own plane, the census's rule): first the size table (by side:
//               the mean log size of the side's points per 5 cm cube under CookFineBelow, per CookCoarseCube cube
//               everywhere), then each point's cell at the level the table gives it -- the cache's own lookup
//               (rtgi_cook.glsl's cookScaleAt), so the cooked cells are the cache's -- and every level above; per
//               own-level cell one candidate per occupied 4 x 4 sub-square of the cell across its surface (nearest the
//               sub-square's middle), the one nearest its middle, its normal, whether it is a junction (its sample's
//               nearest hit under min(1.5 x the cell size, 0.375 m)) (worker)
//   inside      every own-level cell's candidates, 16 rays each to 64 m: the one meeting the fewest back faces
//               (alpha-tested ones fronts), nearest the middle, the representative; only those meeting none kept as
//               training points (none clean: the best alone); buried where even it met more than 0.3 of them
//   finalize    each cell's representative point (the chosen candidate, or the one nearest its middle; an ancestor's
//               its children's nearest its middle -- the neighbour rays' and the near field's), its training points
//               (CookPick: clamp(ceil(occupied sub-squares / 2), 4, 16), at most the occupied, in a Bayer order; an
//               ancestor's from its children's, one per sub-square of its own grid), the cell table, the size table (worker)
//   near field  the level-0 junction cells' 32 near links (rtgi_cook.comp kinds 6, 7): their near light, relit each frame
//               (rtgi_nearcook_update.comp), is the readout's crease detail over the cache's far light
//   neighbours  each cell's 8 neighbours across its surface at its level, a ray between the points (kinds 4, 5): the
//               readout's neighbour test and the blend's reach, decided once for every pixel
//   regions     the label propagation within cubes over the level-0 cells (kinds 8-10): rtgi_cache.glsl's rtgiRegion
//
// Nothing is cached between runs yet (step 1b): CookVersion keys what will be. --rtgi-census reports from the same cook
// (its cells stage) and closes.
internal sealed partial class SponzaLoop
{
    // The cooked data's version: bumped with any change to what a stage produces, once stages are cached (1b).
    private const int CookVersion = 1;
    // The size table's tiers: 5 cm cubes (the finest cell) where the size is under CookFineBelow, CookCoarseCube cubes
    // everywhere (rtgi_cook.glsl's cookScaleAt); the points a third of a cube apart at most, so every cube a surface
    // crosses is met.
    private const float CookFineBelow = 0.2f, CookCoarseCube = 0.2f;
    // The inside test: candidates a cell, rays a candidate, the share of a candidate's rays on back faces past which it is
    // buried (the census's side rule).
    private const int CookCandidates = 16, CookChooseRays = 16;
    // --rtgi-cook-keep-backs N: a junction cell's candidate kept as a training point only where at most N of its inside
    // test's rays met a back face (0: clean only; none clean, the cell's best alone). At the buried share (4 of 16) creases
    // darkened: points at a wall's foot started a fifth of their rays inside the wall (the user, crevices, 2026-10-31).
    private int rtgiCookKeepBacks;
    // The training's tally (rtgi_train.comp cookTally): a cooked point out of its cell's turn once more than
    // CookDropBacks of its last CookDropWindow samples met back faces, and its back-face samples dropped; --rtgi-cook-no-drop
    // off (they train black, as a screen point's do).
    private bool rtgiCookDrop = true;
    private const int CookDropWindow = 32, CookDropBacks = 2;
    private const int CookBestOnlyBacks = 1;
    private const float CookBuriedShare = 0.3f;
    // The size field's smoothing: passes, and their reach in the pass's sample spacing (CookField.Smooth).
    private const int CookSmoothPasses = 3;
    // A point whose nearest sample is dead is kept within this of a live one (m).
    private const float CookLiveReach = 0.15f;
    private const float CookSmoothReach = 1.5f;
    private const int CookSizeRays = 64, CookNeighbourRays = 8, CookNearRays = 32;
    // The cook's rays a frame at most (its ray and hit buffers: 80 MB a parity, freed when the cook is done).
    private const int CookRayRoom = 1 << 20;
    // The regions' label passes (all at most; a frame's), as the near cook had them.
    private const int CookLabelPasses = 96, CookLabelPassesAFrame = 8;

    private bool rtgiCookReady;
    // --rtgi-cook-ms M: in the frame (not behind the cook screen), the cook's rays a frame held to M ms of the trace's GPU
    // time at the rate it has measured (4); --rtgi-cook-rays N: N rays a frame instead.
    private float rtgiCookMs = 4f;
    private int rtgiCookRays;
    // The size field's samples traced a pass at most (--rtgi-cook-most, the census's --rtgi-census-most): past it a random
    // share, each standing for the rest's area.
    private long rtgiCookMost = 12_000_000;
    // --rtgi-cook-log: each stage's line to the log (on); the cooked cells near a point (BLIX_RTGI_NEAR_PROBE="x,y,z").
    private bool rtgiCookProbePending;
    private uint cookReadyFrame;

    private enum CookStage { Waiting, Geometry, SizePlace, SizeTrace, SizeDrain, Cells, InsideTrace, InsideDrain, Finalize, NearLinks, Neighbours, Regions, Ready }
    private CookStage cookStage = CookStage.Waiting;
    private readonly Stopwatch cookClock = new();
    // Per stage, as the cook screen and the log show it.
    private sealed class CookStageInfo
    {
        public string Name = "";
        public long Items, Done, Total, Rays;
        public double Ms;
        public bool Started, Finished;
        public readonly Stopwatch Clock = new();
    }
    private readonly Dictionary<string, CookStageInfo> cookStages = new();
    private static readonly string[] CookStageOrder = { "geometry", "size field", "cells", "inside test", "finalize", "near field", "neighbours", "regions" };
    private CookStageInfo CookInfo(string name)
    {
        if (!cookStages.TryGetValue(name, out var s)) cookStages[name] = s = new CookStageInfo { Name = name };
        return s;
    }
    private void CookBegin(string name, long total)
    {
        var s = CookInfo(name);
        if (s.Started) return;
        s.Started = true;
        s.Total = total;
        s.Clock.Restart();
        DebugProgress.Begin("rtgi cook: " + name, total);
    }
    private void CookProgress(string name, long done, long total, long rays = 0)
    {
        var s = CookInfo(name);
        s.Done = done;
        s.Total = total;
        s.Rays += rays;
        DebugProgress.Set("rtgi cook: " + name, done, total);
    }
    private void CookEnd(string name, long items, string detail, double? ms = null)
    {
        var s = CookInfo(name);
        if (s.Finished) return;
        s.Finished = true;
        s.Items = items;
        s.Clock.Stop();
        s.Ms = ms ?? s.Clock.Elapsed.TotalMilliseconds;
        DebugProgress.End("rtgi cook: " + name);
        var rate = s.Rays > 0 && s.Ms > 0 ? $", {s.Rays / 1e6:0.0} M rays, {s.Rays / (s.Ms / 1000.0) / 1e6:0} M rays/s" : "";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] rtgi cook: {name}: {detail}{rate}; {s.Ms:0} ms."));
    }

    // The geometry and the size field's passes (the census's types).
    private Task<CensusGeometry>? cookGeometryBuild;
    private CensusGeometry? cookGeometry;
    private Task<CensusPass?>? cookPassBuild;
    private CensusPass? cookPass;
    private readonly List<CensusPass> cookPasses = new();
    private int cookCursor, cookDrain, cookDynamic, cookPlacements;
    private uint cookParity;
    private int cookSampleCapacity;
    private MetalShadowStats cookTraceFrom;
    private readonly Stopwatch cookPassClock = new();
    // The cells (worker) and what the GPU's stages need of them.
    private Task<CookCells>? cookCellsBuild;
    private CookCells? cookCells;
    private Task<CookLayout>? cookFinalizeBuild;
    private CookLayout? cookLayout;
    private int cookInsideItems;
    private int cookLabelPass, cookRegionStep;
    // The rays this frame (their kind's dispatches recorded): how many, from which parity's buffers.
    private int cookRaysNow, cookRaysParity;
    // The trace's measured rate (rays a GPU second), from the frames the cook traced alone.
    private long cookAloneRays;
    private double cookAloneGpuMs;
    private long cookRaysTotal;

    private GpuBufferHandle cookDummy, cookSided, cookSamples, cookResults, cookCandidates, cookChosen;
    private GpuBufferHandle[] cookRayBufs = Array.Empty<GpuBufferHandle>(), cookHitBufs = Array.Empty<GpuBufferHandle>();
    // The cooked data (rtgi_cook.glsl): the cell table, the cells' points, the size table, the level-0 cells' regions, the
    // near field's links and light, the near-train list; the regions' scratch.
    private GpuBufferHandle cookCellPoints, cookProbe, cookScan;
    // BLIX_RTGI_OUTLIERS=N: N frames after the cook (1200; "1" too), the outlier scan (ScanRtgiOutliers); its read a few
    // frames after its dispatch.
    private int cookScanAt = -1;
    private uint cookScanReadAt = uint.MaxValue;
    private GpuBufferHandle CookSidedOrDummy => CookOr(cookSided);
    private GpuBufferHandle CookProbeOrDummy => CookOr(cookProbe);
    // rtgi_train.comp's uCookTrain (x the tally and the drop on; y the probe's counts; z the back-face samples a window
    // allows; w the window) and uCookTrain2 (x the placements).
    private Vector4 CookTrainUniform => new(rtgiCookReady && rtgiCookDrop ? 1f : 0f, rtgiCookReady && !cookProbe.Equals(default(GpuBufferHandle)) ? 1f : 0f, CookDropBacks, CookDropWindow);
    private Vector4 CookTrain2Uniform => new(cookPlacements, 0f, 0f, 0f);
    private GpuBufferHandle CookCellPointsOrDummy => CookOr(cookCellPoints);
    private GpuBufferHandle cookTable, cookPoints, cookScale, cookRegions, cookLabels, cookCubes, nearLinks, nearHits, nearLightBuf, nearList;
    private int cookCubeSlots;
    private long cookGpuBytes;
    private ShaderInterface cookInterface = null!, nearUpdateInterface = null!;
    private PassHandle cookRaysPassHandle, cookReducePassHandle, nearUpdatePassHandle;
    private PipelineHandle cookPipeline, nearUpdatePipeline;

    // The near field each frame: its cells relit a frame at most, the blend's weight of a frame; the near-train list.
    // 131,072: Crytek Sponza's 471 k near-field cells each relit every 3.6 frames (the near cook's 2.8 M every 11).
    private int rtgiNearUpdateMost = 131_072, rtgiNearUpdateCursor;
    private float rtgiNearBlend = 0.25f;
    private bool rtgiNearCook = true;
    // The cache's visible points moved onto their cell's cooked point (rtgi_rays.comp surfacePoint); --rtgi-no-surface-train.
    private bool rtgiSurfaceTrain = true;
    // The readout's neighbour test from the cooked masks (rtgi_readout.frag, rtgi_temporal.frag); the panel's switch.
    private bool rtgiCookNeighbours = true;
    // --rtgi-near-train N (8192; 0 off): N of the hit points a frame are cooked cells' points within --rtgi-near-reach m (6)
    // of the camera, in turn (rtgi_rays.comp), the list remade every 30 frames (rtgi_cook.comp kinds 14, 15).
    private int rtgiNearTrain = 8192, rtgiNearTrainCursor;
    private float rtgiNearReach = 6f;

    // rtgi_cook.glsl's COOK_PARAMS (w 1: the runtime reads it) and COOK_COUNTS.
    private int CookCellCount => cookLayout?.Cells ?? 0;
    private Vector4 CookParams => new(rtgiCell, Math.Max(cookLayout?.TableSlots ?? 1, 1) - 1, CookCellCount, rtgiCookReady ? 1f : 0f);
    private Vector4 CookParamsCooking => CookParams with { W = cookLayout is null ? 0f : 1f };
    private Vector4 CookCounts => new(cookLayout?.Level0 ?? 0, rtgiNearCook ? cookLayout?.NearCells ?? 0 : 0, Math.Max(cookLayout?.ScaleSlots ?? 1, 1) - 1, CookCoarseCube);
    private GpuBufferHandle CookOr(GpuBufferHandle h) => cookLayout is not null && !h.Equals(default(GpuBufferHandle)) ? h : cookDummy;
    private GpuBufferHandle CookTableOrDummy => CookOr(cookTable);
    private GpuBufferHandle CookPointsOrDummy => CookOr(cookPoints);
    private GpuBufferHandle CookScaleOrDummy => CookOr(cookScale);
    private GpuBufferHandle CookRegionsOrDummy => CookOr(cookRegions);
    private GpuBufferHandle NearLightOrDummy => CookOr(nearLightBuf);
    private GpuBufferHandle NearListOrDummy => CookOr(nearList);
    private bool RtgiCookSettling => !rtgiCookReady;

    private void ParseRtgiCook(Blix.Core.AppArgs args)
    {
        if (args.Float("rtgi-cook-ms") is { } ms) rtgiCookMs = MathF.Max(0.1f, ms);
        if (args.Int("rtgi-cook-rays") is { } rays) rtgiCookRays = Math.Clamp(rays, 0, CookRayRoom);
        if (args.Long("rtgi-cook-most") is { } most) rtgiCookMost = Math.Max(65536, most);
        if (args.Int("rtgi-cook-keep-backs") is { } kb) rtgiCookKeepBacks = Math.Clamp(kb, 0, CookChooseRays);
        if (args.Flag("rtgi-cook-no-drop")) rtgiCookDrop = false;
        if (args.Flag("rtgi-no-near-cook")) rtgiNearCook = false;
        if (args.Flag("rtgi-no-surface-train")) rtgiSurfaceTrain = false;
        if (args.Float("rtgi-near-blend") is { } nb) rtgiNearBlend = Math.Clamp(nb, 0.01f, 1f);
        if (args.Int("rtgi-near-train") is { } nt) rtgiNearTrain = Math.Max(0, nt);
        if (args.Float("rtgi-near-reach") is { } nr) rtgiNearReach = MathF.Max(0.5f, nr);
    }

    private void DeclareRtgiCookRaysPass(Func<string, ShaderInterface> reflect)
    {
        cookInterface = reflect("rtgi_cook.comp");
        cookRaysPassHandle = graph.ComputePass("rtgi-cook-rays").AfterAsync().Shader(cookInterface).Handle;
    }

    private void DeclareRtgiCookAsyncPasses(Func<string, ShaderInterface> reflect)
    {
        cookReducePassHandle = graph.ComputePass("rtgi-cook-reduce").Async(rtgiAsync).Shader(cookInterface).Handle;
        nearUpdateInterface = reflect("rtgi_nearcook_update.comp");
        nearUpdatePassHandle = graph.ComputePass("rtgi-near-update").Async(rtgiAsync).Shader(nearUpdateInterface).Handle;
    }

    private void CreateRtgiCookPipelines(Func<string, ShaderInterface, ComputePipelineOptions, PipelineHandle> compute)
    {
        cookPipeline = compute("rtgi_cook", cookInterface, ComputePipelineOptions.None);
        nearUpdatePipeline = compute("rtgi_nearcook_update", nearUpdateInterface, ComputePipelineOptions.None);
    }

    // The buffers the cache's shaders bind whether or not the cook is done.
    private void CreateRtgiCookDummy()
    {
        if (cookDummy.Equals(default(GpuBufferHandle))) cookDummy = Own(device.CreateGpuBuffer(256, new byte[256], "sponza.rtgi.cook-dummy"));
    }

    private static uint NearHash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352du;
        x ^= x >> 15; x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    // rtgi_cook.glsl's cookCellHash.
    private static (uint Start, uint Key) CookCellHash(int x, int y, int z, uint level, uint side, uint mask)
    {
        unchecked
        {
            var a = NearHash((uint)x * 0x9E3779B1u ^ NearHash((uint)y * 0x85EBCA77u ^ NearHash((uint)z * 0xC2B2AE3Du ^ (level * 8u + side + 0x3A1u))));
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

    // rtgi_cache.glsl's rtgiOrigin: the round coordinates along a surface, RTGI_KEY_INSET back along its normal.
    private static Vector3 NearGridOrigin(uint side)
    {
        const float inset = 0.0213f;
        var v = (side & 1) == 0 ? inset : -inset;
        return (side >> 1) switch { 0 => new Vector3(v, 0f, 0f), 1 => new Vector3(0f, v, 0f), _ => new Vector3(0f, 0f, v) };
    }

    // rtgi_cook.glsl's NEAR_SCALE_ORIGIN: the size table's cubes off the round coordinates on every axis.
    private static readonly Vector3 CookScaleOrigin = new(0.0137f, 0.0271f, 0.0419f);
    private static Vector3 Floor3(Vector3 v) => new(MathF.Floor(v.X), MathF.Floor(v.Y), MathF.Floor(v.Z));
    private static long CubeKey(Vector3 g) =>
        ((long)((int)g.X + (1 << 20)) << 42) | ((long)((int)g.Y + (1 << 20)) << 21) | (long)((int)g.Z + (1 << 20));
    private static (int X, int Y, int Z) CubeUnkey(long k) =>
        ((int)((k >> 42) & 0x1FFFFF) - (1 << 20), (int)((k >> 21) & 0x1FFFFF) - (1 << 20), (int)(k & 0x1FFFFF) - (1 << 20));

    // FinishRayScene: the static instances and every placement's per-triangle bits, on a worker (the cook's first stage).
    private void StartRtgiCook(RayQueryScene scene)
    {
        if (cookGeometryBuild is not null) return;
        // The scene's instances are its placements, in rayPlacementMaterials' order (the hits' placement index).
        var placements = scene.Instances.ToArray();
        var kept = Enumerable.Range(0, placements.Length).Where(i => !placements[i].Dynamic).ToArray();
        var alpha = Enumerable.Range(0, placements.Length).Select(i => i < rayPlacementMaterials.Count && rayPlacementMaterials[i].AlphaCutoff > 0f).ToArray();
        var flagged = Enumerable.Range(0, placements.Length).Select(i => i < rayPlacementDoubleSided.Count && rayPlacementDoubleSided[i]).ToArray();
        cookDynamic = placements.Length - kept.Length;
        cookPlacements = placements.Length;
        cookClock.Restart();
        CookBegin("geometry", 0);
        cookStage = CookStage.Geometry;
        cookGeometryBuild = Task.Run(() => BuildCensusGeometry(placements, kept, alpha, flagged));
    }

    // ---- The cells (worker): the size field's samples as one list, and the walk over every surface point. ----

    // Every pass's samples as one list: each one's size, whether its side is dropped, its nearest hit, its pass; the
    // finest pass's sample near a point.
    private sealed class CookField
    {
        public readonly CensusPass[] Passes;
        public readonly int[] Offsets;
        public readonly float[] Size, Nearest;
        // Each sample's log2 size, smoothed along its surface (Smooth).
        public float[] LogSize;
        // Per dead sample: a live one of its side lies within CookLiveReach on its plane (the points near it kept).
        public bool[] DeadNearLive = Array.Empty<bool>();
        public readonly bool[] Dead;
        public readonly byte[] PassOf;
        public readonly float Finest, Top;
        public readonly int LastPass;

        public CookField(CensusPass[] passes, float finest, float top)
        {
            Passes = passes; Finest = finest; Top = top; LastPass = passes.Length - 1;
            Offsets = new int[passes.Length + 1];
            for (var p = 0; p < passes.Length; p++) Offsets[p + 1] = Offsets[p] + passes[p].Pos.Length;
            var total = Offsets[^1];
            Size = new float[total]; Nearest = new float[total]; Dead = new bool[total]; PassOf = new byte[total];
            for (var p = 0; p < passes.Length; p++)
                for (var i = 0; i < passes[p].Pos.Length; i++)
                {
                    var g = Offsets[p] + i;
                    Size[g] = passes[p].Size[i]; Dead[g] = passes[p].Dead[i]; Nearest[g] = passes[p].Nearest[i]; PassOf[g] = (byte)p;
                }
            LogSize = Size.Select(v => MathF.Log2(v)).ToArray();
        }

        // The size field smoothed along the surfaces: CookSmoothPasses passes, each sample's log size the weighted mean of
        // its pass's live samples facing its side on its plane within CookSmoothReach of the pass's spacing (itself
        // included) -- as the near cook averaged its sizes over its linked neighbours, four passes. Each sample's own 64
        // rays' size jumped from sample to sample, and the levels stepped along the size table's cubes.
        public void Smooth()
        {
            DeadNearLive = new bool[Size.Length];
            for (var p = 0; p < Passes.Length; p++)
            {
                var pass = Passes[p];
                var off = Offsets[p];
                Parallel.For(0, pass.Pos.Length, i =>
                {
                    if (Dead[off + i]) DeadNearLive[off + i] = LiveWithin(pass.Pos[i], pass.Side[i], pass.Normal[i], CookLiveReach);
                });
            }
            for (var p = 0; p < Passes.Length; p++)
            {
                var pass = Passes[p];
                var off = Offsets[p];
                var radius = CookSmoothReach * pass.Effective;
                for (var k = 0; k < CookSmoothPasses; k++)
                {
                    var next = (float[])LogSize.Clone();
                    var from = LogSize;
                    Parallel.For(0, pass.Pos.Length, i =>
                    {
                        if (Dead[off + i]) return;
                        var (sum, w) = pass.Grid!.Average(pass.Pos[i], pass.Side[i], pass.Normal[i], radius, from, Dead, off, from[off + i]);
                        if (w > 0) next[off + i] = (float)(sum / w);
                    });
                    LogSize = next;
                }
            }
        }

        // Whether a live sample facing side lies within radius of q on its plane (any pass).
        public bool LiveWithin(Vector3 q, uint side, Vector3 normal, float radius)
        {
            for (var p = LastPass; p >= 0; p--)
                if (Passes[p].Grid!.Average(q, side, normal, radius, LogSize, Dead, Offsets[p]).Weight > 0) return true;
            return false;
        }

        // The smoothed log size at q on a side (facing normal): the finest pass with live samples near on its plane,
        // their weighted mean within the pass's spacing; NaN none.
        public float Kernel(Vector3 q, uint side, Vector3 normal)
        {
            for (var p = LastPass; p >= 0; p--)
            {
                // Edge-stopping about the finest size near (Grid.Average): a point by a wall stays as fine as its finest
                // neighbour on the plane, not the mean of the two sides of the wall.
                var least = Passes[p].Grid!.Least(q, side, normal, Passes[p].Effective, LogSize, Dead, Offsets[p]);
                if (float.IsNaN(least)) continue;
                var (sum, w) = Passes[p].Grid!.Average(q, side, normal, Passes[p].Effective, LogSize, Dead, Offsets[p], least);
                if (w > 0) return (float)(sum / w);
            }
            return float.NaN;
        }

        // The finest pass's sample near a point: one facing its side within 1.5 of a pass's traced spacing, else the
        // first pass's nearest facing it (searched outward to 8 cells). A side takes its size, and whether it is kept,
        // from its own samples: another side's first doubled the crevices testbed's cells (the floor's points took the
        // wall's).
        public int FinestNear(Vector3 q, uint side, Vector3 normal)
        {
            for (var p = LastPass; p > 0; p--)
            {
                var (j, d2) = Passes[p].Grid!.Nearest(q, side, 1, sameOnly: true, normal: normal);
                var reach = 1.5f * Passes[p].Effective;
                if (j >= 0 && d2 <= reach * reach) return Offsets[p] + j;
            }
            var (j0, _) = Passes[0].Grid!.Nearest(q, side, 8, sameOnly: true, normal: normal);
            return j0 >= 0 ? j0 : -1;
        }

        public int LevelOf(float size) => Math.Clamp((int)MathF.Floor(MathF.Log2(size / Finest)), 0, RtgiTopLevel);
        public float SizeOfLevel(int l) => Finest * MathF.Pow(2f, l);
    }

    // The walk's counts: points, those on dropped sides skipped, lattice points filled outward, pieces without a sample.
    private struct CookWalkCounts { public long Points, Buried, Filled, None; }

    // A point of a surface side: where, its side, the side's normal, its finest sample (-1 none), its smoothed size (m).
    private delegate void CookPointSink(Vector3 p, uint side, Vector3 normal, int sample, float size);

    // One run of triangles walked, each side in pieces no edge longer than CensusPieceLattices lattice steps of the pass at
    // hand (whole, one 1 km2 terrain triangle of the jungle's was one worker's for most of an hour; a piece whose lattice
    // finds a finer pass's samples taken again at that pass's spacing), each piece's lattice its points' finest samples,
    // then points CookSpacing(the piece's smallest live size) apart, each with its lattice point's sample; the points on
    // dropped sides counted and left out.
    private static void CookWalk(CookField field, RayQueryScene.Instance inst, int firstTri, int triCount, CookPointSink sink, ref CookWalkCounts counts)
    {
        var passes = field.Passes;
        var stack = new Stack<(Vector3 A, Vector3 B, Vector3 C, int Level)>();
        for (var t = firstTri; t < firstTri + triCount; t++)
        {
            var (ta, tb, tc) = CensusTriangle(inst, t);
            var tcross = Vector3.Cross(tb - ta, tc - ta);
            var tlen = tcross.Length();
            if (!(tlen > 1e-12f)) continue;
            var nrm = tcross / tlen;
            for (var face = 0; face < 2; face++)
            {
                var faceNormal = face == 0 ? nrm : -nrm;
                var side = NearSide(faceNormal);
                stack.Push((ta, tb, tc, 0));
                while (stack.TryPop(out var piece))
                {
                    var (pa, pb, pc, level) = piece;
                    var lat = passes[level].Effective;
                    if (CensusLongest(pa, pb, pc) > CensusPieceLattices * lat)
                    {
                        var (h0, h1) = CensusBisect(pa, pb, pc);
                        stack.Push((h0.Item1, h0.Item2, h0.Item3, level));
                        stack.Push((h1.Item1, h1.Item2, h1.Item3, level));
                        continue;
                    }
                    var len = Vector3.Cross(pb - pa, pc - pa).Length();
                    if (!(len > 1e-12f)) continue;
                    // Rows parallel to the longest edge, points along each (the corners included).
                    Vector3 ea = pa, eb = pb, apex = pc;
                    float ab = (pb - pa).LengthSquared(), bc = (pc - pb).LengthSquared(), ca = (pa - pc).LengthSquared();
                    if (bc >= ab && bc >= ca) { ea = pb; eb = pc; apex = pa; }
                    else if (ca >= ab && ca >= bc) { ea = pc; eb = pa; apex = pb; }
                    var baseLen = (eb - ea).Length();
                    var height = len / MathF.Max(baseLen, 1e-9f);
                    // The lattice: its points' finest samples, and the local size the smallest live one's. A lattice
                    // point with none near takes its row's nearest that has one, or the nearest row's.
                    var rows0 = Math.Max(1, (int)MathF.Ceiling(height / lat));
                    var lattice = new int[rows0 + 1][];
                    var least = field.Top;
                    var anyRow = -1;
                    var finer = false;
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
                            var j = row[k] = field.FinestNear(m == 0 ? p0 : p0 + (p1 - p0) * (k / (float)m), side, faceNormal);
                            if (j < 0) continue;
                            rowHas = true;
                            if (field.PassOf[j] > level) finer = true;
                            if (!field.Dead[j]) least = MathF.Min(least, field.Size[j]);
                        }
                        if (rowHas) anyRow = r;
                    }
                    if (finer && level < field.LastPass)
                    {
                        stack.Push((pa, pb, pc, level + 1));
                        continue;
                    }
                    if (anyRow < 0) counts.None++;
                    else
                        for (var r = 0; r <= rows0; r++)
                        {
                            var row = lattice[r];
                            if (row.Any(j => j >= 0))
                            {
                                for (var k = 0; k < row.Length; k++)
                                {
                                    if (row[k] >= 0) continue;
                                    for (var d = 1; d < row.Length; d++)
                                    {
                                        if (k - d >= 0 && row[k - d] >= 0) { row[k] = row[k - d]; break; }
                                        if (k + d < row.Length && row[k + d] >= 0) { row[k] = row[k + d]; break; }
                                    }
                                    counts.Filled++;
                                }
                                continue;
                            }
                            var from = lattice[anyRow];
                            for (var d = 1; d <= rows0; d++)
                            {
                                if (r - d >= 0 && lattice[r - d].Any(j => j >= 0)) { from = lattice[r - d]; break; }
                                if (r + d <= rows0 && lattice[r + d].Any(j => j >= 0)) { from = lattice[r + d]; break; }
                            }
                            for (var k = 0; k < row.Length; k++) row[k] = from[(int)MathF.Round(k / (float)Math.Max(1, row.Length - 1) * (from.Length - 1))];
                            counts.Filled += row.Length;
                        }
                    // The smoothed size field at the lattice points (CookField.Kernel), each point's size interpolated between
                    // them -- smooth contours, not the samples' cells.
                    var vals = new float[rows0 + 1][];
                    // Per lattice point whose sample is dead: whether a live one lies within CookLiveReach (the points
                    // near it kept, below).
                    var liveNear = new bool[rows0 + 1][];
                    var leastSmooth = float.MaxValue;
                    for (var r = 0; r <= rows0; r++)
                    {
                        var f = r / (float)rows0;
                        var p0 = ea + (apex - ea) * f;
                        var p1 = eb + (apex - eb) * f;
                        var m = lattice[r].Length - 1;
                        var row = vals[r] = new float[m + 1];
                        var near = liveNear[r] = new bool[m + 1];
                        for (var k = 0; k <= m; k++)
                        {
                            var lq = m == 0 ? p0 : p0 + (p1 - p0) * (k / (float)m);
                            var lj = lattice[r][k];
                            near[k] = lj >= 0 && field.Dead[lj] && field.DeadNearLive[lj];
                            row[k] = field.Kernel(lq, side, faceNormal);
                            if (!float.IsNaN(row[k])) leastSmooth = MathF.Min(leastSmooth, MathF.Pow(2f, row[k]));
                        }
                    }
                    if (leastSmooth < float.MaxValue) least = Math.Clamp(leastSmooth, field.Finest, field.Top);
                    var h = CookSpacing(least);
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
                            // Its nearest sample dead: dropped unless a live sample of its side lies within CookLiveReach on
                            // its plane. By the nearest alone, a floor's points beside a wall's foot went with the samples
                            // under the wall a few centimetres off -- the crease's cells not cooked, the light there read
                            // from cells the screen trained, dashes along it (crevices, 2026-10-31). The points kept under a
                            // wall's edge are buried by the inside test; kept within any live sample's reach (the smoothed
                            // field's), Crytek Sponza's thick walls' insides cooked 1.4 M buried cells, 47 s.
                            var lv = CookInterp(vals, rows0, f, along);
                            if (j >= 0 && field.Dead[j] && !CookLiveCorner(liveNear, rows0, f, along)) { counts.Buried++; continue; }
                            counts.Points++;
                            var size = float.IsNaN(lv) ? (j >= 0 ? field.Size[j] : field.Top) : Math.Clamp(MathF.Pow(2f, lv), field.Finest, field.Top);
                            sink(p0 + (p1 - p0) * along, side, faceNormal, j, size);
                        }
                    }
                }
            }
        }
    }

    // Whether any of the lattice points around (f, along) has a live sample near (CookWalk's liveNear).
    private static bool CookLiveCorner(bool[][] near, int rows0, float f, float along)
    {
        var fr = f * rows0;
        var r0 = Math.Min((int)MathF.Floor(fr), rows0);
        var r1 = Math.Min(r0 + 1, rows0);
        return Row(near[r0]) || Row(near[r1]);
        bool Row(bool[] row)
        {
            var x = along * (row.Length - 1);
            var k0 = Math.Min((int)MathF.Floor(x), row.Length - 1);
            return row[k0] || row[Math.Min(k0 + 1, row.Length - 1)];
        }
    }

    // The lattice's values at row fraction f, along the row: linear between rows and along each, NaN entries left out.
    private static float CookInterp(float[][] vals, int rows0, float f, float along)
    {
        var fr = f * rows0;
        var r0 = Math.Min((int)MathF.Floor(fr), rows0);
        var r1 = Math.Min(r0 + 1, rows0);
        var a = RowAt(vals[r0], along);
        var b = RowAt(vals[r1], along);
        return Mix(a, b, fr - r0);

        static float RowAt(float[] row, float along)
        {
            var x = along * (row.Length - 1);
            var k0 = Math.Min((int)MathF.Floor(x), row.Length - 1);
            var k1 = Math.Min(k0 + 1, row.Length - 1);
            return Mix(row[k0], row[k1], x - k0);
        }
        static float Mix(float a, float b, float t) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : a + (b - a) * t;
    }

    // Points a quarter of the local size apart (a cell's 4 x 4 sub-squares each met), and a third of the size table's
    // cube at most (every cube a surface crosses met).
    private static float CookSpacing(float least) => MathF.Min(0.25f * least, (least < CookFineBelow ? CookFineCubeForSpacing : CookCoarseCube) / 3f);
    private const float CookFineCubeForSpacing = 0.05f;

    // An own-level cell's accumulation: the point nearest its middle (along its surface), its candidates (one per 4 x 4
    // sub-square of the cell across its surface, levels 0 and 1), its normal, whether a junction.
    private sealed class CookAcc
    {
        public Vector3 Best, N;
        public float BestD = float.MaxValue;
        public bool Junction;
        public Vector3[]? Sub;
        public float[]? SubD;
        public int SubMask;
        public void Add(CookAcc o)
        {
            N += o.N;
            Junction |= o.Junction;
            if (o.BestD < BestD) { BestD = o.BestD; Best = o.Best; }
            if (o.Sub is null) return;
            Sub ??= new Vector3[CookCandidates];
            SubD ??= new float[CookCandidates];
            for (var k = 0; k < CookCandidates; k++)
                if ((o.SubMask & (1 << k)) != 0 && ((SubMask & (1 << k)) == 0 || o.SubD![k] < SubD[k])) { Sub[k] = o.Sub[k]; SubD[k] = o.SubD![k]; SubMask |= 1 << k; }
        }
    }

    // The cells stage's result: the size table, the own-level cells' accumulations by level (keyed as the census keys
    // them), every level's cells (the census's sorted keys: own-level flag; own: junction flag), the counts.
    private sealed class CookCells
    {
        public Dictionary<(long, byte), float> Fine = new(), Coarse = new();
        public Dictionary<long, CookAcc>[] Acc = Array.Empty<Dictionary<long, CookAcc>>();
        public long[][] All = Array.Empty<long[]>(), Own = Array.Empty<long[]>();
        public CookWalkCounts Counts;
        public double TableMs, EnumerateMs;
        public float Share = 1f, FineCube = 0.05f;
        public double SmoothMs;
        public CookField Field = null!;
    }

    // The size table's lookup, as rtgi_cook.glsl's cookScaleAt: the side's fine cube, else its coarse; -1 none.
    private static float CookTableSize(CookCells cells, Vector3 p, uint side)
    {
        if (cells.Fine.TryGetValue((CubeKey(Floor3((p - CookScaleOrigin) / cells.FineCube)), (byte)side), out var f)) return f;
        if (cells.Coarse.TryGetValue((CubeKey(Floor3((p - CookScaleOrigin) / CookCoarseCube)), (byte)side), out var c)) return c;
        return -1f;
    }

    private static CookCells BuildCookCells(CensusGeometry geom, CensusPass[] passes, float finest, float top, float share)
    {
        var field = new CookField(passes, finest, top);
        var cells = new CookCells { Field = field, Share = share, FineCube = finest };
        var instances = geom.Instances;
        // Short runs: a run of big triangles at fine spacing is minutes of one worker's.
        var work = CensusWork(instances, 1024);
        if (share < 1f)
        {
            var shareBits = (uint)(share * uint.MaxValue);
            work = work.Where((_, w) => NearHash((uint)w * 0x27D4EB2Fu + 0x165667B1u) <= shareBits).ToArray();
        }
        var clock = Stopwatch.StartNew();
        var gate = new object();
        // The size field smoothed along the surfaces first (CookField.Smooth).
        field.Smooth();
        cells.SmoothMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        // The table, by side: the mean log size of the side's points in each fine cube (points under CookFineBelow) and
        // each coarse cube (points from CookFineBelow up where the cube has any, else all) -- each point's size its own
        // side's samples on its own plane, smoothed (the census's rule); the cells are then enumerated from the table, so
        // the runtime's lookup gives the cells the cook made.
        var fineAcc = new Dictionary<(long, byte), (double Sum, int N)>();
        var bigAcc = new Dictionary<(long, byte), (double Sum, int N)>();
        var allAcc = new Dictionary<(long, byte), (double Sum, int N)>();
        var done = 0;
        DebugProgress.Begin("rtgi cook: cells (table)", work.Length);
        Parallel.ForEach(work, () => (Fine: new Dictionary<(long, byte), (double Sum, int N)>(), Big: new Dictionary<(long, byte), (double Sum, int N)>(), All: new Dictionary<(long, byte), (double Sum, int N)>()), (item, _, local) =>
        {
            var counts = new CookWalkCounts();
            CookWalk(field, instances[item.Inst], item.First, item.Count, (p, side, normal, j, s) =>
            {
                var ls = MathF.Log2(s);
                if (s < CookFineBelow) Add(local.Fine, (CubeKey(Floor3((p - CookScaleOrigin) / finest)), (byte)side), ls);
                var ck = (CubeKey(Floor3((p - CookScaleOrigin) / CookCoarseCube)), (byte)side);
                if (s >= CookFineBelow) Add(local.Big, ck, ls);
                Add(local.All, ck, ls);
            }, ref counts);
            DebugProgress.Set("rtgi cook: cells (table)", Interlocked.Increment(ref done), work.Length);
            return local;
        }, local =>
        {
            lock (gate)
            {
                foreach (var (k, v) in local.Fine) Merge(fineAcc, k, v);
                foreach (var (k, v) in local.Big) Merge(bigAcc, k, v);
                foreach (var (k, v) in local.All) Merge(allAcc, k, v);
            }
        });
        foreach (var (k, v) in fineAcc) cells.Fine[k] = MathF.Pow(2f, (float)(v.Sum / v.N));
        foreach (var (k, v) in allAcc) cells.Coarse[k] = MathF.Pow(2f, (float)(bigAcc.TryGetValue(k, out var big) ? big.Sum / big.N : v.Sum / v.N));
        DebugProgress.End("rtgi cook: cells (table)");
        cells.TableMs = clock.Elapsed.TotalMilliseconds;

        // The cells: each point's at the level the table gives it, keyed as the census keys them.
        clock.Restart();
        var levels = RtgiTopLevel + 1;
        var acc = Enumerable.Range(0, levels).Select(_ => new Dictionary<long, CookAcc>()).ToArray();
        var totals = new CookWalkCounts();
        done = 0;
        DebugProgress.Begin("rtgi cook: cells", work.Length);
        Parallel.ForEach(work, () => (Acc: Enumerable.Range(0, levels).Select(_ => new Dictionary<long, CookAcc>()).ToArray(), Counts: new CookWalkCounts()), (item, _, local) =>
        {
            var counts = local.Counts;
            var mine = local.Acc;
            CookWalk(field, instances[item.Inst], item.First, item.Count, (p, side, normal, j, own) =>
            {
                var s = CookTableSize(cells, p, side);
                if (!(s > 0f)) s = own;
                var l = field.LevelOf(s);
                var cs = field.SizeOfLevel(l);
                var g = (p - NearGridOrigin(side)) / cs;
                int cx = (int)MathF.Floor(g.X), cy = (int)MathF.Floor(g.Y), cz = (int)MathF.Floor(g.Z);
                var key = CensusCellKey(cx, cy, cz, side);
                if (!mine[l].TryGetValue(key, out var e)) mine[l][key] = e = new CookAcc();
                e.N += normal;
                if (j >= 0 && field.Nearest[j] < MathF.Min(CensusJunctionReach * cs, CensusJunctionMost)) e.Junction = true;
                int axis = (int)(side >> 1), a0 = (axis + 1) % 3, a1 = (axis + 2) % 3;
                float f0 = a0 == 0 ? g.X : a0 == 1 ? g.Y : g.Z, f1 = a1 == 0 ? g.X : a1 == 1 ? g.Y : g.Z;
                float u0 = f0 - MathF.Floor(f0), u1 = f1 - MathF.Floor(f1);
                var d = (u0 - 0.5f) * (u0 - 0.5f) + (u1 - 0.5f) * (u1 - 0.5f);
                if (d < e.BestD) { e.BestD = d; e.Best = p; }
                // Its sub-square of the cell's 4 x 4 grid across its surface: the cell's candidates (the inside test's at
                // junctions) and training points, one each, the point nearest the sub-square's middle.
                e.Sub ??= new Vector3[CookCandidates];
                e.SubD ??= new float[CookCandidates];
                float s0 = u0 * 4f, s1 = u1 * 4f;
                var q = Math.Min((int)s0, 3) + 4 * Math.Min((int)s1, 3);
                var dq = (s0 - MathF.Floor(s0) - 0.5f) * (s0 - MathF.Floor(s0) - 0.5f) + (s1 - MathF.Floor(s1) - 0.5f) * (s1 - MathF.Floor(s1) - 0.5f);
                if ((e.SubMask & (1 << q)) == 0 || dq < e.SubD[q]) { e.Sub[q] = p; e.SubD[q] = dq; e.SubMask |= 1 << q; }
            }, ref counts);
            DebugProgress.Set("rtgi cook: cells", Interlocked.Increment(ref done), work.Length);
            return (mine, counts);
        }, local =>
        {
            lock (gate)
            {
                totals.Points += local.Counts.Points; totals.Buried += local.Counts.Buried;
                totals.Filled += local.Counts.Filled; totals.None += local.Counts.None;
                for (var l = 0; l < levels; l++)
                    foreach (var (k, v) in local.Acc[l])
                    {
                        if (acc[l].TryGetValue(k, out var e)) e.Add(v);
                        else acc[l][k] = v;
                    }
            }
        });
        DebugProgress.End("rtgi cook: cells");
        cells.Acc = acc;
        cells.Counts = totals;
        // Each level's own cells (flag: a junction), then every level's cells (flag: own-level), its parents from the level
        // below's (the census's lists).
        cells.Own = new long[levels][];
        for (var l = 0; l < levels; l++)
        {
            var keys = acc[l].Select(kv => kv.Key | (kv.Value.Junction ? 1L : 0L)).ToArray();
            cells.Own[l] = CensusSortUnique(keys, keys.Length);
        }
        cells.All = new long[levels][];
        for (var l = 0; l < levels; l++)
        {
            var below = l > 0 ? cells.All[l - 1].Length : 0;
            var keys = new long[below + cells.Own[l].Length];
            for (var i = 0; i < below; i++) keys[i] = CensusParent(cells.All[l - 1][i]);
            for (var i = 0; i < cells.Own[l].Length; i++) keys[below + i] = (cells.Own[l][i] & ~1L) | 1L;
            cells.All[l] = CensusSortUnique(keys, keys.Length);
        }
        cells.EnumerateMs = clock.Elapsed.TotalMilliseconds;
        return cells;

        static void Add(Dictionary<(long, byte), (double Sum, int N)> d, (long, byte) k, float v)
        {
            d.TryGetValue(k, out var o);
            d[k] = (o.Sum + v, o.N + 1);
        }
        static void Merge(Dictionary<(long, byte), (double Sum, int N)> d, (long, byte) k, (double Sum, int N) v)
        {
            d.TryGetValue(k, out var o);
            d[k] = (o.Sum + v.Sum, o.N + v.N);
        }
    }

    // The inside test's cells: every own-level cell (its candidates are its training points). The junction cells of levels
    // 0 and 1 alone were tested until 2026-10-31: a floor's cell under a wall standing on it, its sample a quarter metre
    // off and clear, was no junction, and all its points sat inside the wall -- 91% of their rays on back faces, a black
    // patch at the dead end's crease (crevices, the probe).
    private static (int Level, long Key)[] CookInsideCells(CookCells cells) =>
        Enumerable.Range(0, cells.Acc.Length).SelectMany(l => cells.Acc[l].Where(kv => kv.Value.Sub is not null).Select(kv => (l, kv.Key))).ToArray();

    private static uint CookOctPack(Vector3 n) => CensusOctPack(n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY);

    private static Vector3 CookCellMiddle(long key, int level, float finest)
    {
        var (x, y, z, side) = CensusCellUnkey(key);
        return (new Vector3(x, y, z) + new Vector3(0.5f)) * (finest * MathF.Pow(2f, level)) + NearGridOrigin(side);
    }

    // The finalized layout: the cells in order (level 0's junction cells, its others, level 1 up), their GPU points, the
    // cell table, the size table, the counts.
    private sealed class CookLayout
    {
        public int Cells, Level0, NearCells, TableSlots, ScaleSlots;
        public Vector4[] Points = Array.Empty<Vector4>();
        public uint[] Table = Array.Empty<uint>(), Scale = Array.Empty<uint>(), CellPoints = Array.Empty<uint>();
        public long[] PointsPerCell = new long[CookCandidates + 1];
        public int ScaleEntries;
        // The inside test's back-face rays of each training point (255: not inside-tested), by its slot past the cells';
        // the inside-tested cells' points under the rule before and now, and those left their best candidate alone.
        public byte[] PointBacks = Array.Empty<byte>();
        // Per cell: 1 its best candidate alone (none clean), 2 inside-tested.
        public byte[] CellFlags = Array.Empty<byte>();
        public long InsidePointsBefore, InsidePointsNow, BestOnly;
        public long[] ByLevel = new long[RtgiTopLevel + 1], OwnByLevel = new long[RtgiTopLevel + 1], JunctionByLevel = new long[RtgiTopLevel + 1];
        public long Buried, InsideBuried;
        public double Ms;
    }

    private static CookLayout FinalizeCook(CookCells cells, (int Level, long Key)[] inside, uint[] chosen, float finest, int keepBacks)
    {
        var clock = Stopwatch.StartNew();
        var levels = RtgiTopLevel + 1;
        var layout = new CookLayout();
        // The inside test's choices by cell.
        var choice = new Dictionary<(int, long), int>(inside.Length);
        var bestOnly = new HashSet<(int, long)>();
        for (var i = 0; i < inside.Length; i++) choice[inside[i]] = i;
        // Per level, every cell's representative point, normal, bits, and its training points; ancestors' from their
        // children's.
        var cellsAt = new Dictionary<long, (Vector3 P, Vector3 N, uint Bits, Vector3[] Pts, byte[] Backs)>[levels];
        for (var l = 0; l < levels; l++)
        {
            var mine = cellsAt[l] = new Dictionary<long, (Vector3, Vector3, uint, Vector3[], byte[])>(cells.All[l].Length);
            var cs = finest * MathF.Pow(2f, l);
            foreach (var (key, a) in cells.Acc[l])
            {
                var (_, _, _, side) = CensusCellUnkey(key);
                var p = a.Best;
                var bits = side | 64u | ((uint)l << 8) | (a.Junction ? 32u : 0u);
                var mask = a.SubMask;
                var backs = new byte[CookCandidates];
                Array.Fill(backs, (byte)255);
                if (choice.TryGetValue((l, key), out var ci))
                {
                    var c = chosen[5 * ci];
                    var bestK = (int)(c & 0xFF);
                    if (a.Sub is not null && (a.SubMask & (1 << bestK)) != 0) p = a.Sub[bestK];
                    var clean = 0;
                    for (var k = 0; k < CookCandidates; k++)
                    {
                        backs[k] = (byte)(chosen[5 * ci + 1 + k / 4] >> (8 * (k % 4)));
                        if ((a.SubMask & (1 << k)) != 0 && backs[k] <= keepBacks) clean |= 1 << k;
                    }
                    // The rule before (bd877111): a level-0/1 junction cell's candidates under the buried share; any other
                    // cell's every candidate, untested.
                    var tested = a.Junction && l <= 1;
                    var before = tested ? a.SubMask & (int)(c >> 16) : a.SubMask;
                    if ((c & 256u) != 0) { bits |= 16u; layout.InsideBuried++; mask = 0; if (tested) before = 0; }
                    // None clean: its best alone if that met at most CookBestOnlyBacks of its rays on back faces, else the
                    // cell is buried -- its one point half inside a wall trained it dark or speckled (0.7% of crevices'
                    // best-only cells under half or over twice their neighbours' light, none of the others; 2026-10-31).
                    else if (clean == 0 && backs[bestK] <= CookBestOnlyBacks) { mask = 1 << bestK; layout.BestOnly++; bestOnly.Add((l, key)); }
                    else if (clean == 0) { bits |= 16u; layout.InsideBuried++; mask = 0; }
                    else mask = clean;
                    layout.InsidePointsBefore += CookPick(a.Sub, before).Pts.Length;
                    layout.InsidePointsNow += CookPick(a.Sub, mask).Pts.Length;
                }
                var n = a.N.LengthSquared() > 1e-12f ? Vector3.Normalize(a.N) : Vector3.UnitY;
                var pick = CookPick(a.Sub, mask);
                mine[key] = (p, n, bits, pick.Pts, pick.Subs.Select(k => backs[k]).ToArray());
            }
            if (l == 0) continue;
            // The ancestors: from the level below's live cells -- the representative the child's nearest the middle, the
            // training points the children's, one per sub-square of the ancestor's own 4 x 4 grid (nearest its middle).
            var best = new Dictionary<long, (Vector3 P, Vector3 N, float D, bool Live, Vector3[] Sub, float[] SubD, int Mask)>();
            foreach (var (childKey, child) in cellsAt[l - 1])
            {
                var parent = CensusParent(childKey);
                if (mine.ContainsKey(parent)) continue;
                var live = (child.Bits & 16u) == 0;
                var mid = CookCellMiddle(parent, l, finest);
                var d = Vector3.DistanceSquared(child.P, mid);
                if (!best.TryGetValue(parent, out var b)) b = (child.P, child.N, float.MaxValue, false, new Vector3[CookCandidates], new float[CookCandidates], 0);
                if ((live && !b.Live) || (live == b.Live && d < b.D)) { b.P = child.P; b.N = child.N; b.D = d; b.Live = live; }
                if (live)
                {
                    var (_, _, _, side) = CensusCellUnkey(parent);
                    foreach (var q in child.Pts.Length > 0 ? child.Pts : new[] { child.P })
                    {
                        var (k, dq) = CookSubSquare(q, side, cs);
                        if ((b.Mask & (1 << k)) == 0 || dq < b.SubD[k]) { b.Sub[k] = q; b.SubD[k] = dq; b.Mask |= 1 << k; }
                    }
                }
                best[parent] = b;
            }
            foreach (var (key, b) in best)
            {
                var (_, _, _, side) = CensusCellUnkey(key);
                var pts = b.Live ? CookPick(b.Sub, b.Mask).Pts : Array.Empty<Vector3>();
                mine[key] = (b.P, b.N, side | ((uint)l << 8) | (b.Live ? 0u : 16u), pts, pts.Select(_ => (byte)255).ToArray());
            }
        }
        // The order: level 0's live junction cells (the near field's), its others, level 1 up.
        var order = new List<(int Level, long Key)>();
        order.AddRange(cellsAt[0].Where(kv => (kv.Value.Bits & 48u) == 32u).Select(kv => (0, kv.Key)));
        layout.NearCells = order.Count;
        order.AddRange(cellsAt[0].Where(kv => (kv.Value.Bits & 48u) != 32u).Select(kv => (0, kv.Key)));
        layout.Level0 = order.Count;
        for (var l = 1; l < levels; l++) order.AddRange(cellsAt[l].Keys.Select(k => (l, k)));
        layout.Cells = order.Count;
        layout.Points = new Vector4[order.Count * 2];
        var totalPts = order.Sum(o => (long)cellsAt[o.Level][o.Key].Pts.Length);
        // The training points after the cells' index words (rtgi_cook.glsl's COOK_CELL_POINTS).
        layout.CellPoints = new uint[2 * (order.Count + totalPts)];
        layout.PointBacks = new byte[totalPts];
        layout.CellFlags = new byte[order.Count];
        var at = order.Count;
        for (var i = 0; i < order.Count; i++)
        {
            var (l, key) = order[i];
            var c = cellsAt[l][key];
            layout.Points[2 * i] = new Vector4(c.P, BitConverter.UInt32BitsToSingle(c.Bits));
            layout.Points[2 * i + 1] = new Vector4(c.N, 0f);
            var (x, y, z, side) = CensusCellUnkey(key);
            var cs = finest * MathF.Pow(2f, l);
            var corner = new Vector3(x, y, z);
            layout.CellPoints[2 * i] = (uint)at | ((uint)c.Pts.Length << 27);
            for (var pi = 0; pi < c.Pts.Length; pi++)
            {
                var q = c.Pts[pi];
                layout.PointBacks[at - order.Count] = c.Backs[pi];
                var u = (q - NearGridOrigin(side)) / cs - corner;
                uint Q(float v) => (uint)Math.Clamp((int)(v * 65536f), 0, 65535);
                layout.CellPoints[2 * at] = Q(u.X) | (Q(u.Y) << 16);
                layout.CellPoints[2 * at + 1] = Q(u.Z);
                at++;
            }
            layout.PointsPerCell[c.Pts.Length]++;
            layout.CellFlags[i] = (byte)((bestOnly.Contains((l, key)) ? 1 : 0) | (choice.ContainsKey((l, key)) ? 2 : 0));
            layout.ByLevel[l]++;
            if ((c.Bits & 64u) != 0) layout.OwnByLevel[l]++;
            if ((c.Bits & 32u) != 0) layout.JunctionByLevel[l]++;
            if ((c.Bits & 16u) != 0) layout.Buried++;
        }
        // The cell table: (key, cell), linear probing within rtgi_cook.glsl's COOK_PROBE_STEPS; grown until every cell fits.
        layout.Table = CookHashTable(order.Count, i =>
        {
            var (l, key) = order[i];
            var (x, y, z, side) = CensusCellUnkey(key);
            return (x, y, z, (uint)l, side, (uint)i);
        }, out layout.TableSlots);
        // The size table: the fine cubes (level 15), the coarse (14), by side, each its size's bits.
        var scale = cells.Fine.Select(kv => (Key: kv.Key, Tier: 15u, Size: kv.Value)).Concat(cells.Coarse.Select(kv => (Key: kv.Key, Tier: 14u, Size: kv.Value))).ToArray();
        layout.ScaleEntries = scale.Length;
        layout.Scale = CookHashTable(scale.Length, i =>
        {
            var (x, y, z) = CubeUnkey(scale[i].Key.Item1);
            return (x, y, z, scale[i].Tier, scale[i].Key.Item2, BitConverter.SingleToUInt32Bits(scale[i].Size));
        }, out layout.ScaleSlots);
        layout.Ms = clock.Elapsed.TotalMilliseconds;
        return layout;
    }

    // rtgi_cook.glsl's COOK_PROBE_STEPS: linear probing's longest run at half full. At 8 Crytek Sponza's 2 M cells did not
    // fit until the table was 16x their count (268 MB); a lookup stops at the first empty slot, so a long cap costs nothing
    // where the runs are short.
    private const int CookProbeSteps = 64;
    // Slots per entry at least: at 2 (half full) the near field's relight walked long runs, 9.6 ms a frame at Crytek
    // Sponza; at 4, a quarter full.
    private const int CookTableRoom = 4;
    // A cell's training points from its candidates (one per occupied sub-square of its 4 x 4 grid, mask): as many as
    // clamp(ceil(occupied / 2), 4, 16), at most the occupied -- one per (cell size / 2)^2 of its surface, a full cell's 8,
    // a sliver's all -- taken in a 4 x 4 Bayer order, so any count is spread over the cell.
    private static readonly int[] CookBayer = { 0, 10, 2, 8, 5, 15, 7, 13, 1, 11, 3, 9, 4, 14, 6, 12 };
    private static (Vector3[] Pts, int[] Subs) CookPick(Vector3[]? sub, int mask)
    {
        if (sub is null || mask == 0) return (Array.Empty<Vector3>(), Array.Empty<int>());
        var occupied = BitOperations.PopCount((uint)mask);
        var want = Math.Min(occupied, Math.Clamp((occupied + 1) / 2, 4, 16));
        var picked = new Vector3[want];
        var subs = new int[want];
        var n = 0;
        foreach (var k in CookBayer)
            if ((mask & (1 << k)) != 0 && n < want) { subs[n] = k; picked[n++] = sub[k]; }
        return (picked, subs);
    }

    // A point's sub-square of a cell's 4 x 4 grid across its surface (side), and its squared distance from the
    // sub-square's middle (in sub-squares).
    private static (int K, float D) CookSubSquare(Vector3 p, uint side, float cs)
    {
        var g = (p - NearGridOrigin(side)) / cs;
        int axis = (int)(side >> 1), a0 = (axis + 1) % 3, a1 = (axis + 2) % 3;
        float f0 = a0 == 0 ? g.X : a0 == 1 ? g.Y : g.Z, f1 = a1 == 0 ? g.X : a1 == 1 ? g.Y : g.Z;
        float s0 = (f0 - MathF.Floor(f0)) * 4f, s1 = (f1 - MathF.Floor(f1)) * 4f;
        var k = Math.Min((int)s0, 3) + 4 * Math.Min((int)s1, 3);
        var d = (s0 - MathF.Floor(s0) - 0.5f) * (s0 - MathF.Floor(s0) - 0.5f) + (s1 - MathF.Floor(s1) - 0.5f) * (s1 - MathF.Floor(s1) - 0.5f);
        return (k, d);
    }

    private static uint[] CookHashTable(int count, Func<int, (int X, int Y, int Z, uint Level, uint Side, uint Value)> entry, out int slots)
    {
        slots = 1024;
        while (slots < count * CookTableRoom) slots <<= 1;
        while (true)
        {
            var table = new uint[slots * 2];
            var fits = true;
            for (var i = 0; i < count && fits; i++)
            {
                var (x, y, z, level, side, value) = entry(i);
                var (start, key) = CookCellHash(x, y, z, level, side, (uint)(slots - 1));
                var placed = false;
                for (uint k = 0; k < CookProbeSteps && !placed; k++)
                {
                    var at = (int)((start + k) & (uint)(slots - 1));
                    if (table[2 * at] != 0) continue;
                    table[2 * at] = key; table[2 * at + 1] = value; placed = true;
                }
                fits = placed;
            }
            if (fits) return table;
            slots <<= 1;
        }
    }

    // ---- The GPU's stages, a frame at a time. ----

    // This frame's ray budget: the full room behind the cook screen, else --rtgi-cook-rays or --rtgi-cook-ms at the rate
    // measured.
    private int CookRaysAllowed()
    {
        if (RtgiCookAlone) return CookRayRoom;
        if (rtgiCookRays > 0) return rtgiCookRays;
        var rate = cookAloneGpuMs > 1.0 ? cookAloneRays / cookAloneGpuMs * 1000.0 : 2e8;
        return (int)Math.Clamp(rate * rtgiCookMs / 1000.0, 65536, CookRayRoom);
    }

    // Behind the cook screen (the first settle, or the census): the cook alone on the GPU, the cache sitting out.
    private bool RtgiCookAlone => !rtgiCookReady && (rtgiCensus || (rtgiSettleWanted && !rtgiSettleDone));

    private DispatchCommand CookDispatch(int parity, int kind, int first, int count, int threads, float reach = 64f, bool firstLabels = false) =>
        new(cookPipeline, Math.Max(1, (threads + 63) / 64), 1, 1,
            new ShaderUniform[]
            {
                new("uCook", new Vector4Uniform(CookParamsCooking)),
                new("uCounts", new Vector4Uniform(CookCounts)),
                new("uChunk", new Vector4Uniform(new Vector4(first, count, reach, kind))),
                new("uMore", new Vector4Uniform(new Vector4(CensusLift, cookPlacements, CensusVoidDown, kind <= 1 ? CensusNear : rtgiContactRadius))),
                new("uConn", new Vector4Uniform(new Vector4(firstLabels ? 1f : 0f, Math.Max(cookCubeSlots, 1) - 1, CookBuriedShare, 0f))),
                new("uNear", new Vector4Uniform(new Vector4(cameraPosition, rtgiNearReach))),
                new("uCache", new Vector4Uniform(RtgiParams)),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiCookSamples", CookBuf(cookSamples)),
                new ShaderBufferBinding("RtgiCookResults", CookBuf(cookResults)),
                new ShaderBufferBinding("RtgiCookRays", cookRayBufs.Length > 0 ? cookRayBufs[parity] : cookDummy),
                new ShaderBufferBinding("RtgiCookHits", cookHitBufs.Length > 0 ? cookHitBufs[parity] : cookDummy),
                new ShaderBufferBinding("RtgiCookSided", CookBuf(cookSided)),
                new ShaderBufferBinding("RtgiCookCandidates", CookBuf(cookCandidates)),
                new ShaderBufferBinding("RtgiCookChosen", CookBuf(cookChosen)),
                new ShaderBufferBinding("RtgiCookTable", CookBuf(cookTable)),
                new ShaderBufferBinding("RtgiCookPoints", CookBuf(cookPoints)),
                new ShaderBufferBinding("RtgiNearLinks", CookBuf(nearLinks)),
                new ShaderBufferBinding("RtgiNearLight", CookBuf(nearLightBuf)),
                new ShaderBufferBinding("RtgiCookLabels", CookBuf(cookLabels)),
                new ShaderBufferBinding("RtgiCookRegions", CookBuf(cookRegions)),
                new ShaderBufferBinding("RtgiCookCubes", CookBuf(cookCubes)),
                new ShaderBufferBinding("RtgiNearList", CookBuf(nearList)),
                new ShaderBufferBinding("RtgiNearHits", CookBuf(nearHits)),
                new ShaderBufferBinding("RtgiCookScale", CookBuf(cookScale)),
                new ShaderBufferBinding("RtgiKeys", CookBuf(rtgiKeys)),
                new ShaderBufferBinding("RtgiResolved", CookBuf(rtgiResolved)),
                new ShaderBufferBinding("RtgiRecord", CookBuf(rtgiRecord)),
                new ShaderBufferBinding("RtgiCookScan", CookBuf(cookScan)),
            });
    private GpuBufferHandle CookBuf(GpuBufferHandle h) => h.Equals(default(GpuBufferHandle)) ? cookDummy : h;

    // A chunk of a ray stage: its rays (placement time, before the trace) and their reduction (after it).
    private void CookRayChunk(int raysKind, int first, int count, int raysEach, float reach = 64f)
    {
        var parity = (int)(cookParity++ & 1u);
        cookRaysNow = count * raysEach;
        cookRaysParity = parity;
        cookRaysTotal += cookRaysNow;
        graph.Dispatch(cookRaysPassHandle, CookDispatch(parity, raysKind, first, count, cookRaysNow, reach));
        graph.Dispatch(cookReducePassHandle, CookDispatch(parity, raysKind + 1, first, count, count, reach));
    }

    // RecordRtgi, before the cache's frame: the cook's stage this frame. Its rays (cookRaysNow) traced with the cache's,
    // or alone (EncodeRtgiCookAlone) when the cache sits out.
    private void RecordRtgiCook(MetalRayTracer tracer)
    {
        cookRaysNow = 0;
        if (cookStage == CookStage.Waiting) return;
        if (cookStage == CookStage.Ready) { RecordRtgiNearField(); return; }
        // The measured rate: last frame's alone rays over the trace's GPU time since.
        var stats = tracer.TraceStats;
        if (cookPrevAloneRays > 0) { cookAloneRays += cookPrevAloneRays; cookAloneGpuMs += stats.TotalGpuMs - cookPrevGpuMs; }
        cookPrevGpuMs = stats.TotalGpuMs;
        cookPrevAloneRays = 0;
        var budget = CookRaysAllowed();
        switch (cookStage)
        {
            case CookStage.Geometry:
            {
                if (cookGeometryBuild is not { IsCompleted: true } build) return;
                var geom = cookGeometry = build.Result;
                // Its time the worker's own: the stage also waited for the frame's tracer (its first rays need it).
                var waited = CookInfo("geometry").Clock.Elapsed.TotalMilliseconds;
                CookEnd("geometry", geom.Triangles, string.Create(CultureInfo.InvariantCulture,
                    $"{geom.Triangles:N0} triangles ({cookDynamic} dynamic instances left out), {geom.Area:N0} m2 a side; alpha-tested {geom.ByAlpha.Tris:N0} triangles, {geom.ByAlpha.Area:N0} m2 (the double-sided flag's {geom.ByFlag.Area:N0} m2); {waited:0} ms to the tracer"), geom.Ms);
                cookSided = Own(device.CreateGpuBuffer(geom.SidedBits.Length * 4, MemoryMarshal.AsBytes(geom.SidedBits.AsSpan()).ToArray(), "sponza.rtgi.cook-sided"));
                cookRayBufs = Enumerable.Range(0, 2).Select(k => Own(device.CreateGpuBuffer(CookRayRoom * 32, name: $"sponza.rtgi.cook-rays{k}"))).ToArray();
                cookHitBufs = Enumerable.Range(0, 2).Select(k => Own(device.CreateGpuBuffer(CookRayRoom * 48, name: $"sponza.rtgi.cook-hits{k}"))).ToArray();
                var most = rtgiCookMost;
                cookPassBuild = Task.Run<CensusPass?>(() => BuildCensusPass(geom, 0, Array.Empty<CensusPass>(), most));
                CookBegin("size field", 0);
                cookStage = CookStage.SizePlace;
                return;
            }
            case CookStage.SizePlace:
            {
                if (cookPassBuild is not { IsCompleted: true } build) return;
                var pass = build.Result;
                if (pass is null)
                {
                    var all = cookPasses.ToArray();
                    CookEnd("size field", all.Sum(p => p.Full), string.Create(CultureInfo.InvariantCulture,
                        $"{string.Join(", ", all.Select(p => $"{p.Full:N0} samples at {p.Spacing:0.##} m ({p.Pos.Length:N0} traced, {100.0 * p.Refine.Count(r => r) / Math.Max(1, p.Refine.Length):0.0}% refined)"))}; GPU {all.Sum(p => p.GpuMs):0} ms of trace"));
                    var geom = cookGeometry!;
                    var finest = rtgiCell;
                    var top = rtgiCell * MathF.Pow(2f, RtgiTopLevel);
                    var share = rtgiCensus ? rtgiCensusCellShare : 1f;
                    CookBegin("cells", 0);
                    cookCellsBuild = Task.Run(() => BuildCookCells(geom, all, finest, top, share));
                    cookStage = CookStage.Cells;
                    return;
                }
                cookPass = pass;
                var n = Math.Max(pass.Pos.Length, 1);
                var bytes = MemoryMarshal.AsBytes(pass.Gpu.AsSpan());
                if (n <= cookSampleCapacity) { if (bytes.Length > 0) device.WriteGpuBuffer(cookSamples, 0, bytes); }
                else
                {
                    if (cookSampleCapacity > 0) { device.WaitIdle(); Disown(cookSamples); Disown(cookResults); }
                    cookSampleCapacity = n;
                    cookSamples = Own(device.CreateGpuBuffer(n * 16, bytes.Length > 0 ? bytes.ToArray() : new byte[16], "sponza.rtgi.cook-samples"));
                    cookResults = Own(device.CreateGpuBuffer(n * 16, new byte[n * 16], "sponza.rtgi.cook-results"));
                }
                pass.Gpu = Array.Empty<Vector4>();
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] rtgi cook: size pass {pass.Index} at {pass.Spacing:0.##} m: {pass.Full:N0} samples, {pass.Pos.Length:N0} traced (share {pass.Keep:0.####}), placed in {pass.Ms:0} ms."));
                cookCursor = 0;
                cookDrain = 0;
                cookTraceFrom = tracer.TraceStats;
                cookPassClock.Restart();
                cookStage = CookStage.SizeTrace;
                return;
            }
            case CookStage.SizeTrace:
            {
                var pass = cookPass!;
                var count = Math.Min(Math.Max(1, budget / CookSizeRays), pass.Pos.Length - cookCursor);
                if (count > 0)
                {
                    CookRayChunk(0, cookCursor, count, CookSizeRays, CensusReach);
                    cookCursor += count;
                    pass.Frames++;
                }
                var traced = cookPasses.Sum(p => (long)p.Pos.Length) + cookCursor;
                CookProgress("size field", traced, cookPasses.Sum(p => (long)p.Pos.Length) + pass.Pos.Length, (long)count * CookSizeRays);
                if (cookCursor >= pass.Pos.Length) cookStage = CookStage.SizeDrain;
                return;
            }
            case CookStage.SizeDrain:
            {
                if (++cookDrain < 3) return;
                device.WaitIdle();
                var current = cookPass!;
                current.WallMs = cookPassClock.Elapsed.TotalMilliseconds;
                current.GpuMs = tracer.TraceStats.TotalGpuMs - cookTraceFrom.TotalGpuMs;
                var n = current.Pos.Length;
                var results = new uint[4 * n];
                const int piece = 1 << 26;
                for (long off = 0; off < 16L * n; off += piece)
                {
                    var len = (int)Math.Min(piece, 16L * n - off);
                    MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(cookResults, (int)off, len).AsSpan()).CopyTo(results.AsSpan((int)(off / 4)));
                }
                current.Results = results;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] rtgi cook: size pass {current.Index}: {(long)n * CookSizeRays / 1e6:0.0} M rays in {current.Frames} frames, {current.WallMs / 1000:0.0} s wall, {current.GpuMs:0} ms the trace's GPU time."));
                cookPasses.Add(current);
                // On a worker: this pass's results taken up, the next pass's samples placed from them (or none left).
                var done = cookPasses.ToArray();
                var most = rtgiCookMost;
                var finest = rtgiCell;
                var topSize = rtgiCell * MathF.Pow(2f, RtgiTopLevel);
                var geom = cookGeometry!;
                cookPassBuild = Task.Run<CensusPass?>(() =>
                {
                    TakeCensusResults(current, finest, topSize);
                    return done.Length < CensusPassSpacing.Length ? BuildCensusPass(geom, done.Length, done, most) : null;
                });
                cookStage = CookStage.SizePlace;
                return;
            }
            case CookStage.Cells:
            {
                if (cookCellsBuild is not { IsCompleted: true } build) return;
                var cells = cookCells = build.Result;
                var own = cells.Own.Sum(o => (long)o.Length);
                CookEnd("cells", cells.All.Sum(a => (long)a.Length), string.Create(CultureInfo.InvariantCulture,
                    $"{cells.All.Sum(a => (long)a.Length):N0} cells ({own:N0} own-level, {cells.Own.Sum(o => o.LongCount(k => (k & 1) != 0)):N0} junction) from {cells.Counts.Points:N0} surface points ({cells.Counts.Buried:N0} on dropped sides skipped); size table {cells.Fine.Count:N0} fine cubes and {cells.Coarse.Count:N0} coarse in {cells.TableMs:0} ms, cells in {cells.EnumerateMs:0} ms (the size field smoothed in {cells.SmoothMs:0} ms)"));
                if (rtgiCensus)
                {
                    // --rtgi-census: the report from this cook's cells, and the demo closes.
                    StartCensusReport(cells);
                    cookStage = CookStage.Waiting;
                    return;
                }
                var inside = cookInsideList = CookInsideCells(cells);
                cookInsideItems = inside.Length;
                var items = new Vector4[Math.Max(1, inside.Length) * (CookCandidates + 1)];
                for (var i = 0; i < inside.Length; i++)
                {
                    var (l, key) = inside[i];
                    var a = cells.Acc[l][key];
                    var mid = CookCellMiddle(key, l, rtgiCell);
                    items[(CookCandidates + 1) * i] = new Vector4(mid, BitConverter.UInt32BitsToSingle(CookOctPack(a.N)));
                    for (var c = 0; c < CookCandidates; c++)
                        items[(CookCandidates + 1) * i + 1 + c] = (a.SubMask & (1 << c)) != 0 ? new Vector4(a.Sub![c], 1f) : Vector4.Zero;
                }
                cookCandidates = Own(device.CreateGpuBuffer(items.Length * 16, MemoryMarshal.AsBytes(items.AsSpan()).ToArray(), "sponza.rtgi.cook-candidates"));
                cookChosen = Own(device.CreateGpuBuffer(Math.Max(1, inside.Length) * 20, new byte[Math.Max(1, inside.Length) * 20], "sponza.rtgi.cook-chosen"));
                cookCursor = 0;
                cookDrain = 0;
                CookBegin("inside test", inside.Length);
                cookStage = CookStage.InsideTrace;
                return;
            }
            case CookStage.InsideTrace:
            {
                const int each = CookCandidates * CookChooseRays;
                var count = Math.Min(Math.Max(1, budget / each), cookInsideItems - cookCursor);
                if (count > 0)
                {
                    CookRayChunk(2, cookCursor, count, each);
                    cookCursor += count;
                }
                CookProgress("inside test", cookCursor, cookInsideItems, (long)Math.Max(count, 0) * each);
                if (cookCursor >= cookInsideItems) cookStage = CookStage.InsideDrain;
                return;
            }
            case CookStage.InsideDrain:
            {
                if (++cookDrain < 3) return;
                device.WaitIdle();
                var chosen = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(cookChosen, 0, Math.Max(1, cookInsideItems) * 20).AsSpan()).ToArray();
                var buried = Enumerable.Range(0, cookInsideItems).Count(i => (chosen[5 * i] & 256u) != 0);
                CookEnd("inside test", cookInsideItems, string.Create(CultureInfo.InvariantCulture,
                    $"{cookInsideItems:N0} own-level cells, up to {CookCandidates} candidates x {CookChooseRays} rays each; {buried:N0} buried"));
                Disown(cookCandidates); Disown(cookChosen);
                cookCandidates = default; cookChosen = default;
                var cells = cookCells!;
                var inside = cookInsideList;
                var finest = rtgiCell;
                CookBegin("finalize", 0);
                var keepBacks = rtgiCookKeepBacks;
                cookFinalizeBuild = Task.Run(() => FinalizeCook(cells, inside, chosen, finest, keepBacks));
                cookStage = CookStage.Finalize;
                return;
            }
            case CookStage.Finalize:
            {
                if (cookFinalizeBuild is not { IsCompleted: true } build) return;
                var layout = build.Result;
                UploadCookLayout(layout);
                cookLayout = layout;
                // The size field's samples and the census's lists are no longer needed (only the census's report reads them).
                cookCells = null;
                cookInsideList = Array.Empty<(int, long)>();
                cookPasses.Clear();
                cookPass = null;
                if (cookSampleCapacity > 0) { Disown(cookSamples); Disown(cookResults); cookSamples = default; cookResults = default; cookSampleCapacity = 0; }
                CookEnd("finalize", layout.Cells, string.Create(CultureInfo.InvariantCulture,
                    $"{layout.Cells:N0} cells by level {string.Join("/", layout.ByLevel)} (own-level {string.Join("/", layout.OwnByLevel)}; junction {string.Join("/", layout.JunctionByLevel)}), {layout.Buried:N0} buried; the inside-tested cells' training points {layout.InsidePointsNow:N0} (at most {rtgiCookKeepBacks} of {CookChooseRays} rays on back faces; {layout.InsidePointsBefore:N0} under the buried share, the rule before; {layout.BestOnly:N0} cells their best candidate alone); training points {layout.PointsPerCell.Select((c, k) => (long)c * k).Sum():N0}, cells by points 0-16 {string.Join("/", layout.PointsPerCell)} ({(layout.CellPoints.Length * 4L) / 1048576.0:0.0} MB); cell table {layout.TableSlots:N0} slots ({layout.Table.Length * 4L / 1048576.0:0.0} MB), size table {layout.ScaleEntries:N0} cubes by side in {layout.ScaleSlots:N0} slots ({layout.Scale.Length * 4L / 1048576.0:0.0} MB); {cookGpuBytes / 1048576.0:0.0} MB on the GPU"));
                cookCursor = 0;
                CookBegin("near field", layout.NearCells);
                cookStage = CookStage.NearLinks;
                return;
            }
            case CookStage.NearLinks:
            {
                var cells = rtgiNearCook ? cookLayout!.NearCells : 0;
                var count = Math.Min(Math.Max(1, budget / CookNearRays), cells - cookCursor);
                if (count > 0)
                {
                    CookRayChunk(6, cookCursor, count, CookNearRays);
                    cookCursor += count;
                }
                CookProgress("near field", cookCursor, cells, (long)Math.Max(count, 0) * CookNearRays);
                if (cookCursor < cells) return;
                CookEnd("near field", cells, string.Create(CultureInfo.InvariantCulture, $"{cells:N0} level-0 junction cells, {CookNearRays} links each to {rtgiContactRadius:0.##} m"));
                cookCursor = 0;
                CookBegin("neighbours", cookLayout!.Cells);
                cookStage = CookStage.Neighbours;
                return;
            }
            case CookStage.Neighbours:
            {
                var cells = cookLayout!.Cells;
                var count = Math.Min(Math.Max(1, budget / CookNeighbourRays), cells - cookCursor);
                if (count > 0)
                {
                    CookRayChunk(4, cookCursor, count, CookNeighbourRays);
                    cookCursor += count;
                }
                CookProgress("neighbours", cookCursor, cells, (long)Math.Max(count, 0) * CookNeighbourRays);
                if (cookCursor < cells) return;
                CookEnd("neighbours", cells, string.Create(CultureInfo.InvariantCulture, $"{cells:N0} cells, {CookNeighbourRays} rays each"));
                cookLabelPass = 0;
                cookRegionStep = 0;
                CookBegin("regions", CookLabelPasses + 2);
                cookStage = CookStage.Regions;
                return;
            }
            case CookStage.Regions:
            {
                var n0 = cookLayout!.Level0;
                if (n0 > 0)
                {
                    if (cookLabelPass < CookLabelPasses)
                    {
                        for (var k = 0; k < CookLabelPassesAFrame && cookLabelPass < CookLabelPasses; k++, cookLabelPass++)
                            graph.Dispatch(cookReducePassHandle, CookDispatch(0, 8, 0, n0, n0, firstLabels: cookLabelPass == 0));
                        CookProgress("regions", cookLabelPass, CookLabelPasses + 2);
                        return;
                    }
                    if (cookRegionStep == 0) { graph.Dispatch(cookReducePassHandle, CookDispatch(0, 9, 0, n0, n0)); cookRegionStep = 1; CookProgress("regions", CookLabelPasses + 1, CookLabelPasses + 2); return; }
                    graph.Dispatch(cookReducePassHandle, CookDispatch(0, 10, 0, n0, n0));
                }
                CookProgress("regions", CookLabelPasses + 2, CookLabelPasses + 2);
                CookEnd("regions", n0, string.Create(CultureInfo.InvariantCulture, $"{n0:N0} level-0 cells, {CookLabelPasses} label passes, cubes of {CookRegionLevels} levels"));
                FinishRtgiCook();
                return;
            }
        }
    }
    private (int Level, long Key)[] cookInsideList = Array.Empty<(int, long)>();
    private long cookPrevAloneRays;
    private double cookPrevGpuMs;
    private const int CookRegionLevels = 6;

    // The cook's rays this frame traced alone (the cache sitting out: behind the cook screen, or the census).
    private void EncodeRtgiCookAlone(MetalRayTracer tracer)
    {
        if (cookRaysNow <= 0) return;
        var rays = cookRaysNow;
        var rayBuf = cookRayBufs[cookRaysParity];
        var hitBuf = cookHitBufs[cookRaysParity];
        var seed = unchecked((uint)cookRaysTotal * 0x9E3779B9u + 0x2545F491u);
        cookPrevAloneRays = rays;
        void EncodeAll(ExternalWorkSync sync, MetalRayTracer t) => t.EncodeTrace(sync, rayBuf, 0, hitBuf, 0, rays, seed);
        if (rtgiAsync && (device.Features & GraphicsFeatures.AsyncCompute) != 0)
            graph.External(rtgiTracePassHandle, sync => tracer.EncodeGroup(sync, () => EncodeAll(sync, tracer)));
        else if (!TracerGroupsOn) graph.External(rtgiInlineTracePassHandle, sync => tracer.EncodeGroup(sync, () => EncodeAll(sync, tracer)));
        else DeferTracerBatch(EncodeAll);
    }

    // The cook's rays this frame inside the cache's own batch (RecordRtgi's Encode).
    private void EncodeRtgiCookWith(ExternalWorkSync sync, MetalRayTracer t, int rays, int parity, uint seed)
    {
        if (rays <= 0) return;
        t.EncodeTrace(sync, cookRayBufs[parity], 0, cookHitBufs[parity], 0, rays, seed ^ 0x7F4A7C15u);
    }

    // The finalized layout onto the GPU: the cooked data, and the regions' scratch.
    private void UploadCookLayout(CookLayout layout)
    {
        var cells = Math.Max(layout.Cells, 1);
        var n0 = Math.Max(layout.Level0, 1);
        var nj = Math.Max(layout.NearCells, 1);
        cookTable = Own(device.CreateGpuBuffer(layout.Table.Length * 4, MemoryMarshal.AsBytes(layout.Table.AsSpan()).ToArray(), "sponza.rtgi.cook-table"));
        if (Environment.GetEnvironmentVariable("BLIX_RTGI_NEAR_PROBE") is { Length: > 0 })
        {
            // By slot, as the training indexes it (the cells' index words first).
            var slots = Math.Max(layout.CellPoints.Length / 2, 1);
            cookProbe = Own(device.CreateGpuBuffer(slots * 16, new byte[slots * 16], "sponza.rtgi.cook-probe"));
        }
        cookCellPoints = Own(device.CreateGpuBuffer(Math.Max(layout.CellPoints.Length, 2) * 4, layout.CellPoints.Length > 0 ? MemoryMarshal.AsBytes(layout.CellPoints.AsSpan()).ToArray() : new byte[8], "sponza.rtgi.cook-cell-points"));
        cookPoints = Own(device.CreateGpuBuffer(cells * 32, layout.Cells > 0 ? MemoryMarshal.AsBytes(layout.Points.AsSpan()).ToArray() : new byte[32], "sponza.rtgi.cook-points"));
        cookScale = Own(device.CreateGpuBuffer(layout.Scale.Length * 4, MemoryMarshal.AsBytes(layout.Scale.AsSpan()).ToArray(), "sponza.rtgi.cook-scale"));
        cookRegions = Own(device.CreateGpuBuffer(n0 * CookRegionLevels * 4, new byte[n0 * CookRegionLevels * 4], "sponza.rtgi.cook-regions"));
        cookLabels = Own(device.CreateGpuBuffer(n0 * CookRegionLevels * 4, new byte[n0 * CookRegionLevels * 4], "sponza.rtgi.cook-labels"));
        cookCubeSlots = 1024;
        while (cookCubeSlots < n0 * CookRegionLevels * 2) cookCubeSlots <<= 1;
        // Per slot: the cube's key, its lowest label, its highest (rtgi_cook.comp kinds 9, 10), a pad.
        var cubes = new uint[cookCubeSlots * 4];
        for (var k = 1; k < cubes.Length; k += 4) cubes[k] = 0xFFFFFFFFu;
        cookCubes = Own(device.CreateGpuBuffer(cubes.Length * 4, MemoryMarshal.AsBytes(cubes.AsSpan()).ToArray(), "sponza.rtgi.cook-cubes"));
        nearLinks = Own(device.CreateGpuBuffer(nj * CookNearRays * 8, new byte[nj * CookNearRays * 8], "sponza.rtgi.near-links"));
        nearHits = Own(device.CreateGpuBuffer(nj * CookNearRays * 4, new byte[nj * CookNearRays * 4], "sponza.rtgi.near-hits"));
        nearLightBuf = Own(device.CreateGpuBuffer(nj * 16, new byte[nj * 16], "sponza.rtgi.near-light"));
        nearList = Own(device.CreateGpuBuffer(16 + cells * 4, new byte[16 + cells * 4], "sponza.rtgi.near-list"));
        cookGpuBytes = layout.CellPoints.Length * 4L + layout.Table.Length * 4L + cells * 32L + layout.Scale.Length * 4L + n0 * CookRegionLevels * 4L + nj * (CookNearRays * 12L + 16L) + 16L + cells * 4L;
    }

    // The cook done: the cache started over on the cooked cells, the totals logged; its transient buffers freed a few
    // frames on (this frame's dispatches still bind them).
    private void FinishRtgiCook()
    {
        cookFreeLater.AddRange(cookRayBufs.Concat(cookHitBufs).Append(cookLabels).Append(cookCubes).Where(b => !b.Equals(default(GpuBufferHandle))));
        cookFreeIn = 4;
        cookRayBufs = Array.Empty<GpuBufferHandle>();
        cookHitBufs = Array.Empty<GpuBufferHandle>();
        // The alpha-tested bits stay: the training's tally reads them (rtgi_train.comp hitAlphaTested).
        cookLabels = default; cookCubes = default;
        rtgiCookReady = true;
        cookStage = CookStage.Ready;
        cookClock.Stop();
        // The cells the cache held until now were sized by the camera: emptied, its cells the cooked ones from here.
        rtgiResetPending = true;
        rtgiCookProbePending = true;
        cookReadyFrame = rtgiFrameCount;
        var l = cookLayout!;
        var stageMs = string.Join(", ", CookStageOrder.Where(cookStages.ContainsKey).Select(s => $"{s} {cookStages[s].Ms:0}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] rtgi cook: done in {cookClock.Elapsed.TotalSeconds:0.0} s ({stageMs} ms; {cookRaysTotal / 1e6:0.0} M rays{(cookAloneGpuMs > 1 ? $", {cookAloneRays / cookAloneGpuMs * 1000.0 / 1e6:0} M rays/s the trace's GPU time alone" : "")}): {l.Cells:N0} cells (L0 {l.Level0:N0}, of them {l.NearCells:N0} junction with a near field), {l.Buried:N0} buried; cooked data {cookGpuBytes / 1048576.0:0.0} MB on the GPU (version {CookVersion})."));
    }

    private readonly List<GpuBufferHandle> cookFreeLater = new();
    private int cookFreeIn;

    // Once cooked, each frame: the near-train list now and then, the near field's light relit.
    private void RecordRtgiNearField()
    {
        if (cookFreeLater.Count > 0 && --cookFreeIn <= 0)
        {
            device.WaitIdle();
            foreach (var b in cookFreeLater) Disown(b);
            cookFreeLater.Clear();
        }
        if (cookLayout is not { } layout || layout.Cells == 0) return;
        // The outlier scan (BLIX_RTGI_OUTLIERS): kind 16 over every cell, read a few frames on.
        if (cookScanAt < 0 && Environment.GetEnvironmentVariable("BLIX_RTGI_OUTLIERS") is { Length: > 0 } scanVar)
            cookScanAt = int.TryParse(scanVar, out var sv) && sv > 1 ? sv : 1200;
        if (cookScanAt > 0 && rtgiFrameCount - cookReadyFrame == (uint)cookScanAt)
        {
            if (cookScan.Equals(default(GpuBufferHandle))) cookScan = Own(device.CreateGpuBuffer(layout.Cells * 32, new byte[layout.Cells * 32], "sponza.rtgi.cook-scan"));
            graph.Dispatch(cookReducePassHandle, CookDispatch(0, 16, 0, layout.Cells, layout.Cells));
            cookScanReadAt = rtgiFrameCount + 4;
        }
        if (rtgiFrameCount == cookScanReadAt) { cookScanReadAt = uint.MaxValue; ScanRtgiOutliers(); }
        // The probe (and the tally's count of dropped points), BLIX_RTGI_NEAR_PROBE_FRAMES (900) frames on.
        var probeAt = int.TryParse(Environment.GetEnvironmentVariable("BLIX_RTGI_NEAR_PROBE_FRAMES"), out var pf) ? pf : 900;
        if (rtgiCookProbePending && rtgiFrameCount - cookReadyFrame >= probeAt) { rtgiCookProbePending = false; ProbeRtgiCookCells(); }
        if (rtgiNearTrain > 0 && rtgiFrameCount % 30 == 0)
        {
            graph.Dispatch(cookReducePassHandle, CookDispatch(0, 14, 0, 1, 1));
            graph.Dispatch(cookReducePassHandle, CookDispatch(0, 15, 0, layout.Cells, layout.Cells));
        }
        var cells = rtgiNearCook ? layout.NearCells : 0;
        if (cells == 0) return;
        var count = Math.Min(cells, rtgiNearUpdateMost);
        var first = rtgiNearUpdateCursor % cells;
        rtgiNearUpdateCursor = (first + count) % cells;
        var towardSun = -Vector3.Normalize(sunDirection);
        graph.Dispatch(nearUpdatePassHandle, new DispatchCommand(nearUpdatePipeline, (count + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uParams", new Vector4Uniform(RtgiParams)),
                new("uCamera", new Vector4Uniform(new Vector4(cameraPosition, 0f))),
                new("uSun", new Vector4Uniform(new Vector4(towardSun, 0f))),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
                new("uCook", new Vector4Uniform(CookParams)),
                new("uCookCounts", new Vector4Uniform(CookCounts)),
                new("uUpdate", new Vector4Uniform(new Vector4(first, count, rtgiNearBlend, rtgiContactRadius))),
                new("uNearField", new Vector4Uniform(new Vector4(rtgiNearField, 0f, 0f, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("RtgiCookTable", cookTable),
                new ShaderBufferBinding("RtgiCookPoints", cookPoints),
                new ShaderBufferBinding("RtgiCookRegions", cookRegions),
                new ShaderBufferBinding("RtgiCookScale", cookScale),
                new ShaderBufferBinding("RtgiNearLinks", nearLinks),
                new ShaderBufferBinding("RtgiNearHits", nearHits),
                new ShaderBufferBinding("RtgiNearLight", nearLightBuf),
                new ShaderBufferBinding("RtgiKeys", rtgiKeys),
                new ShaderBufferBinding("RtgiRecord", rtgiRecord),
                new ShaderBufferBinding("RtgiResolved", rtgiResolved),
            }));
    }

    private Vector4 RtgiNearTrainUniform(int hits)
    {
        var surface = rtgiSurfaceTrain ? 1f : 0f;
        if (!rtgiCookReady || rtgiNearTrain <= 0) return new Vector4(rtgiNearField, 0f, 0f, surface);
        var n = Math.Min(rtgiNearTrain, hits);
        var at = rtgiNearTrainCursor;
        rtgiNearTrainCursor = (rtgiNearTrainCursor + n) & 0x3FFFFFFF;
        return new Vector4(rtgiNearField, n, at, surface);
    }

    // BLIX_RTGI_NEAR_PROBE="x,y,z": once cooked, the cooked cells within 6 cm of the point written to the log -- their
    // point, level, side and flags, their neighbour mask, the near light of a near-field cell.
    private void ProbeRtgiCookCells()
    {
        var layout = cookLayout!;
        device.WaitIdle();
        var pts = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(cookPoints, 0, layout.Cells * 32)).ToArray();
        var cp = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(cookCellPoints, 0, layout.CellPoints.Length * 4)).ToArray();
        var regs = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(cookRegions, 0, Math.Max(layout.Level0, 1) * CookRegionLevels * 4)).ToArray();
        var probe = cookProbe.Equals(default(GpuBufferHandle)) ? Array.Empty<uint>()
            : MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(cookProbe, 0, Math.Max(layout.CellPoints.Length / 2, 1) * 16)).ToArray();
        // The scene: training points dropped from their cells' turns (the tally), and the cooked samples' back-face share.
        long dead = 0, deadTested = 0;
        for (var k = 0; k < layout.PointBacks.Length; k++)
            if ((cp[2 * (layout.Cells + k) + 1] & 0x80000000u) != 0) { dead++; if (layout.PointBacks[k] != 255) deadTested++; }
        long samples = 0, backs = 0;
        for (var k = 0; k + 3 < probe.Length; k += 4) { samples += probe[k]; backs += probe[k + 1]; }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] rtgi cook probe at {rtgiFrameCount - cookReadyFrame} frames: {dead:N0} of {layout.PointBacks.Length:N0} training points dropped by the tally ({deadTested:N0} of them inside-tested){(probe.Length > 0 ? $"; cooked samples {samples:N0}, {100.0 * backs / Math.Max(1, samples):0.00}% on back faces" : "")}."));
        if (Environment.GetEnvironmentVariable("BLIX_RTGI_NEAR_PROBE") is not { Length: > 0 } where) return;
        var at = where.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var q = new Vector3(at[0], at[1], at[2]);
        var radius = float.TryParse(Environment.GetEnvironmentVariable("BLIX_RTGI_NEAR_PROBE_R"), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0.15f;
        long nearSamples = 0, nearBacks = 0, nearPts = 0, nearDead = 0;
        double nearLum = 0;
        for (var i = 0; i < layout.Cells; i++)
        {
            var p = new Vector3(pts[2 * i].X, pts[2 * i].Y, pts[2 * i].Z);
            if (Vector3.Distance(p, q) > radius) continue;
            var w = BitConverter.SingleToUInt32Bits(pts[2 * i].W);
            var level = (int)((w >> 8) & 15);
            var side = w & 7;
            var cs = rtgiCell * MathF.Pow(2f, level);
            var corner = Floor3((p - NearGridOrigin(side)) / cs);
            var first = (int)(cp[2 * i] & 0x07FFFFFFu);
            var count = (int)(cp[2 * i] >> 27);
            long cs0 = 0, cb0 = 0; double cl0 = 0;
            var lines = new List<string>();
            for (var k = 0; k < count; k++)
            {
                var slot = first + k;
                if (slot < layout.Cells || 2 * slot + 1 >= cp.Length) continue;
                var x = cp[2 * slot]; var y = cp[2 * slot + 1];
                var f = new Vector3(x & 0xFFFF, x >> 16, y & 0xFFFF) / 65536f;
                var pp = NearGridOrigin(side) + (corner + f) * cs;
                var ib = layout.PointBacks[slot - layout.Cells];
                var life = probe.Length > 0 ? (probe[4 * slot], probe[4 * slot + 1], probe[4 * slot + 2]) : (0u, 0u, 0u);
                cs0 += life.Item1; cb0 += life.Item2; cl0 += life.Item3 / 1024.0;
                if ((y & 0x80000000u) != 0) nearDead++;
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"    point ({pp.X:0.000},{pp.Y:0.000},{pp.Z:0.000}) inside-test backs {(ib == 255 ? "-" : $"{ib}/{CookChooseRays}")}{((y & 0x80000000u) != 0 ? " DEAD" : "")}; samples {life.Item1}, back {100.0 * life.Item2 / Math.Max(1u, life.Item1):0.0}%, mean luminance {life.Item3 / 1024.0 / Math.Max(1u, life.Item1):0.0000}"));
            }
            nearSamples += cs0; nearBacks += cb0; nearLum += cl0; nearPts += count;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] cooked cell {i}: rep ({p.X:0.000},{p.Y:0.000},{p.Z:0.000}) level {level} side {side} buried {(w & 16) != 0} junction {(w & 32) != 0} own {(w & 64) != 0} adj 0x{BitConverter.SingleToUInt32Bits(pts[2 * i + 1].W):x}{(i < layout.Level0 ? $" regions {string.Join("/", Enumerable.Range(0, 4).Select(l => regs[i * CookRegionLevels + l]))}" : "")}; {count} points: samples {cs0}, back {100.0 * cb0 / Math.Max(1, cs0):0.0}%, mean luminance {cl0 / Math.Max(1, cs0):0.0000}"));
            foreach (var line in lines) Console.WriteLine(line);
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] rtgi cook probe within {radius:0.##} m of ({q.X},{q.Y},{q.Z}): {nearPts} points ({nearDead} dropped), samples {nearSamples:N0}, back {100.0 * nearBacks / Math.Max(1, nearSamples):0.0}%, mean sample luminance {nearLum / Math.Max(1, nearSamples):0.0000}."));
    }

    // BLIX_RTGI_OUTLIERS: every live level-0 and level-1 cooked cell's cache light against the mean of its linked neighbours'
    // (the cooked neighbour bits, same level and side): those under 0.5x or over 2x it listed with what might explain them,
    // counted by category; and the 8 cm gap between crevices' front thin walls (x 1..3, z 2.6..2.68) by side and height.
    private void ScanRtgiOutliers()
    {
        var layout = cookLayout!;
        device.WaitIdle();
        var n = layout.Cells;
        var pts = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(cookPoints, 0, n * 32)).ToArray();
        var scan = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(cookScan, 0, n * 32)).ToArray();
        var cp = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(cookCellPoints, 0, layout.CellPoints.Length * 4)).ToArray();
        var nj = layout.NearCells;
        var links = nj > 0 ? MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(nearLinks, 0, nj * CookNearRays * 8)).ToArray() : Array.Empty<uint>();
        var nlight = nj > 0 ? MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(nearLightBuf, 0, nj * 16)).ToArray() : Array.Empty<Vector4>();
        static float Lum(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        var index = new Dictionary<(int, int, int, int, uint), int>(n);
        (int X, int Y, int Z) CoordsOf(int i, out int level, out uint side)
        {
            var w = BitConverter.SingleToUInt32Bits(pts[2 * i].W);
            level = (int)((w >> 8) & 15); side = w & 7;
            var g = Floor3((new Vector3(pts[2 * i].X, pts[2 * i].Y, pts[2 * i].Z) - NearGridOrigin(side)) / (rtgiCell * MathF.Pow(2f, level)));
            return ((int)g.X, (int)g.Y, (int)g.Z);
        }
        for (var i = 0; i < n; i++) { var c = CoordsOf(i, out var l, out var sd); index[(c.X, c.Y, c.Z, l, sd)] = i; }
        bool Live(int i) => (BitConverter.SingleToUInt32Bits(pts[2 * i].W) & 16u) == 0;
        var ring = new (int, int)[] { (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1) };
        var rows = new List<(double Score, string Line, string Cat)>();
        var cats = new SortedDictionary<string, (int Out, int All)>();
        var scanned = 0;
        string Describe(int i, float lum, float mean, int nb)
        {
            var c = CoordsOf(i, out var l, out var side);
            var w = BitConverter.SingleToUInt32Bits(pts[2 * i].W);
            var first = (int)(cp[2 * i] & 0x07FFFFFFu); var count = (int)(cp[2 * i] >> 27);
            int dead = 0, tested = 0, dirty = 0;
            for (var k = 0; k < count; k++)
            {
                if ((cp[2 * (first + k) + 1] & 0x80000000u) != 0) dead++;
                var b = layout.PointBacks[first + k - n];
                if (b != 255) { tested++; if (b > 0) dirty++; }
            }
            var near = "";
            if (i < nj)
            {
                int met = 0, back = 0, close = 0;
                for (var k = 0; k < CookNearRays; k++)
                {
                    var lx = links[2 * (i * CookNearRays + k)]; var ly = links[2 * (i * CookNearRays + k) + 1];
                    if ((lx & 0x80000000u) == 0) continue;
                    met++;
                    if (ly == 0) back++;
                    if (((lx >> 16) & 0x7FFF) / 32767f * rtgiContactRadius < 0.01f) close++;
                }
                var nl = nlight[i];
                near = string.Create(CultureInfo.InvariantCulture, $" near: {met}/32 met, {back} albedo-0 (back faces or black), {close} within 1 cm; near light {Lum(new Vector3(nl.X, nl.Y, nl.Z)):0.0000} w {nl.W:0.00}");
            }
            var r0 = scan[2 * i]; var r1 = scan[2 * i + 1];
            return string.Create(CultureInfo.InvariantCulture,
                $"cell {i} L{l} side {side} at ({pts[2 * i].X:0.000},{pts[2 * i].Y:0.000},{pts[2 * i].Z:0.000}) region {r1.W:0} junction {(w & 32) != 0} flags {layout.CellFlags[i]}: resolved {lum:0.0000} vs {nb} neighbours' {mean:0.0000} (x{lum / Math.Max(mean, 1e-6f):0.00}), blended {Lum(new Vector3(r1.X, r1.Y, r1.Z)):0.0000}, samples {r0.W:0}; points {count} ({dead} dropped, {tested} tested, {dirty} with back faces){near}");
        }
        for (var i = 0; i < n; i++)
        {
            if (!Live(i)) continue;
            var c = CoordsOf(i, out var l, out var side);
            if (l > 1 || scan[2 * i].W < 1f) continue;
            var adj = BitConverter.SingleToUInt32Bits(pts[2 * i + 1].W);
            int axis = (int)(side >> 1), a0 = (axis + 1) % 3, a1 = (axis + 2) % 3;
            double sum = 0; var nb = 0;
            for (var k = 0; k < 8; k++)
            {
                if ((adj & (1u << k)) == 0) continue;
                var cc = new[] { c.X, c.Y, c.Z };
                cc[a0] += ring[k].Item1; cc[a1] += ring[k].Item2;
                if (!index.TryGetValue((cc[0], cc[1], cc[2], l, side), out var j) || !Live(j) || scan[2 * j].W < 1f) continue;
                sum += Lum(new Vector3(scan[2 * j].X, scan[2 * j].Y, scan[2 * j].Z)); nb++;
            }
            if (nb < 2) continue;
            scanned++;
            var mean = (float)(sum / nb);
            var lum = Lum(new Vector3(scan[2 * i].X, scan[2 * i].Y, scan[2 * i].Z));
            var w = BitConverter.SingleToUInt32Bits(pts[2 * i].W);
            var cat = $"L{l} {((w & 32) != 0 ? "junction" : "plain")}{(i < nj ? " near" : "")}{(scan[2 * i + 1].W > 0.5f ? " split-region" : "")}{((layout.CellFlags[i] & 1) != 0 ? " best-only" : "")}{(scan[2 * i].W < 64 ? " few-samples" : "")}";
            var isOut = mean > 0.02f && (lum < 0.5f * mean || lum > 2f * mean);
            cats.TryGetValue(cat, out var e);
            cats[cat] = (e.Out + (isOut ? 1 : 0), e.All + 1);
            if (isOut) rows.Add((Math.Abs(Math.Log(Math.Max(lum, 1e-6) / mean)), Describe(i, lum, mean, nb), cat));
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] rtgi outliers: {rows.Count:N0} of {scanned:N0} level-0/1 cells under 0.5x or over 2x their linked neighbours' resolved light (dark {rows.Count(r => r.Line.Contains("(x0."))}):"));
        foreach (var (cat, e) in cats) Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza]   {cat}: {e.Out} of {e.All} ({100.0 * e.Out / Math.Max(1, e.All):0.0}%)"));
        foreach (var r in rows.OrderByDescending(r => r.Score).Take(30)) Console.WriteLine("[VulkanSponza]   " + r.Line);
        // BLIX_RTGI_SCAN_BOX="x0,x1,y0,y1,z0,z1": every cooked cell of levels 0-2 whose representative lies in the box, in
        // full (its neighbour bits too).
        if (Environment.GetEnvironmentVariable("BLIX_RTGI_SCAN_BOX") is { Length: > 0 } boxVar)
        {
            var b = boxVar.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            for (var i = 0; i < n; i++)
            {
                var p = pts[2 * i];
                if (p.X < b[0] || p.X > b[1] || p.Y < b[2] || p.Y > b[3] || p.Z < b[4] || p.Z > b[5]) continue;
                var c = CoordsOf(i, out var l, out var side);
                if (l > 2) continue;
                var w = BitConverter.SingleToUInt32Bits(p.W);
                var adj = BitConverter.SingleToUInt32Bits(pts[2 * i + 1].W);
                var first = (int)(cp[2 * i] & 0x07FFFFFFu); var count = (int)(cp[2 * i] >> 27);
                var ptsTxt = string.Join(" ", Enumerable.Range(0, count).Select(k =>
                {
                    var x = cp[2 * (first + k)]; var y = cp[2 * (first + k) + 1];
                    var f = new Vector3(x & 0xFFFF, x >> 16, y & 0xFFFF) / 65536f;
                    var pp = NearGridOrigin(side) + (new Vector3(c.X, c.Y, c.Z) + f) * (rtgiCell * MathF.Pow(2f, l));
                    return string.Create(CultureInfo.InvariantCulture, $"({pp.X:0.000},{pp.Y:0.000},{pp.Z:0.000}){((y & 0x80000000u) != 0 ? "x" : "")}");
                }));
                var r0 = scan[2 * i]; var r1 = scan[2 * i + 1];
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza]   box cell {i} L{l} side {side} c ({c.X},{c.Y},{c.Z}) rep ({p.X:0.000},{p.Y:0.000},{p.Z:0.000}) buried {(w & 16) != 0} junction {(w & 32) != 0} own {(w & 64) != 0} flags {layout.CellFlags[i]} adj {Convert.ToString(adj & 0x1FF, 2).PadLeft(9, '0')} region {r1.W:0}: resolved {Lum(new Vector3(r0.X, r0.Y, r0.Z)):0.0000} samples {r0.W:0}, blended {Lum(new Vector3(r1.X, r1.Y, r1.Z)):0.0000}{(i < nj ? string.Create(CultureInfo.InvariantCulture, $", near light {Lum(new Vector3(nlight[i].X, nlight[i].Y, nlight[i].Z)):0.0000} w {nlight[i].W:0.00}") : "")}; points {ptsTxt}"));
            }
        }
        // The gap: by facing side and height, the cells' resolved light, blended light and near light.
        var gap = new SortedDictionary<(uint, int), (double R, double B, double Nl, int N, int Nn)>();
        for (var i = 0; i < n; i++)
        {
            var p = pts[2 * i];
            if (p.X < 1f || p.X > 3f || p.Z < 2.6f || p.Z > 2.68f || !Live(i)) continue;
            CoordsOf(i, out var l, out var side);
            if (l > 1 || side >> 1 != 2) continue;
            var key = (side, (int)MathF.Floor(p.Y / 0.25f));
            gap.TryGetValue(key, out var g);
            g.N++;
            if (scan[2 * i].W >= 1f) { g.R += Lum(new Vector3(scan[2 * i].X, scan[2 * i].Y, scan[2 * i].Z)); g.B += Lum(new Vector3(scan[2 * i + 1].X, scan[2 * i + 1].Y, scan[2 * i + 1].Z)); }
            if (i < nj && nlight[i].W > 0.5f) { g.Nl += Lum(new Vector3(nlight[i].X, nlight[i].Y, nlight[i].Z)); g.Nn++; }
            gap[key] = g;
        }
        foreach (var ((side, y), g) in gap)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza]   gap side {side} y {y * 0.25:0.00}-{y * 0.25 + 0.25:0.00}: {g.N} cells, resolved {g.R / Math.Max(1, g.N):0.0000}, blended {g.B / Math.Max(1, g.N):0.0000}, near light {(g.Nn > 0 ? (g.Nl / g.Nn).ToString("0.0000", CultureInfo.InvariantCulture) : "-")} ({g.Nn} near cells)"));
    }

    // The cook window's section (DrawRtgiSettle): each stage, its progress, its rays and rate.
    private void DrawRtgiCook()
    {
        if (rtgiCookReady) return;
        ImGui.TextUnformatted(string.Create(CultureInfo.InvariantCulture,
            $"Cooking the GI cells from the geometry ({cookClock.Elapsed.TotalSeconds:0.0} s; the light settles after):"));
        foreach (var name in CookStageOrder)
        {
            var s = cookStages.GetValueOrDefault(name);
            if (s is null || !s.Started) { ImGui.TextDisabled("  " + name); continue; }
            var secs = s.Finished ? s.Ms / 1000.0 : s.Clock.Elapsed.TotalSeconds;
            var rays = s.Rays > 0 ? string.Create(CultureInfo.InvariantCulture, $", {s.Rays / 1e6:0.0} M rays, {s.Rays / Math.Max(secs, 1e-3) / 1e6:0} M rays/s") : "";
            var frac = s.Finished ? 1f : s.Total > 0 ? (float)Math.Clamp(s.Done / (double)s.Total, 0, 1) : 0f;
            var label = string.Create(CultureInfo.InvariantCulture,
                $"{name}: {(s.Finished ? "done" : s.Total > 0 ? $"{s.Done:N0} of {s.Total:N0}" : "working")}, {secs:0.0} s{rays}");
            ImGui.ProgressBar(frac, new Vector2(-1f, 0f), label);
        }
    }
}
