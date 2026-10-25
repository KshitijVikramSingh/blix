using System.Globalization;
using System.Numerics;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --sweep-in-frame: a light change's gather, once behind the frame (GatherInBackground: after the visible and changed),
// recorded in the frame graph -- a pass of a small slice a frame (its rays, the CLOSEST trace with each hit's own sun ray,
// the shade; at the slice's last pass its far finish, census and GPU resolve), with no CPU round trip inside the frame.
// The host path (RunSunNodePassesNow's) waits for the GPU three times a pass: at ~10 ms of work a 1 M-ray pass, under
// the cook screen a sun change was ~25 s at ~4 fps on Crytek Sponza, ~15 min at Intel Sponza's boot (2026-10-25).
// What stays on the host for now: a round's end (its count and histogram read back, a frame after its last resolve was
// recorded), the sweep's setup and the fine hand-off. The compose runs at a round's end, not after every slice (its
// filter alone is 74 ms a run on Intel Sponza).
internal sealed partial class SponzaLoop
{
    private bool sweepInFrame;
    // The texels of a frame's slice: sweepSlice x 32 rays a pass, one pass a frame (--sweep-slice N; 8,192: ~260 K rays).
    private int sweepSlice = 8192;
    // A slice begun in the frame stays there to its last pass (the slice's size and its sums' slot are the frame's).
    private bool sweepSliceInFrame;
    private int sweepSliceNow;

    // The budget: the sweep's slice passes (rays, trace, shade) a fixed slot of the frame's GPU time (--sweep-gpu-ms, 3),
    // the slice sized by their measured cost a texel-pass over windows of SweepBudgetFrames (the host's pass totals: the
    // compute passes are timed; the render's are not, so the slot is fixed rather than the frame's rest). A controller on
    // the frames' pace shrank the slice to its floor: frames missed the pace for what the slice does not size (a round's
    // end, the compose's ranges) and a sun change ran ~10x slower than the same rays at boot (2026-10-25). A fixed 8,192
    // slice was ~6-8 ms a frame on Crytek Sponza. --sweep-slice N: a fixed slice, no budget.
    private float sweepGpuMs = 3f;
    private bool sweepSliceFixed;
    private const int SweepBudgetFrames = 16;
    private Dictionary<string, GpuPassTotal>? sweepBudgetAt;
    private long sweepBudgetFrame = -1, sweepBudgetTexelPasses;
    private int sweepBudgetSlice = 2048;
    // The cook screen up, the user waits on the cook, not the frame: the full slice.
    private int SweepSliceBudgeted => sweepSliceFixed || CookScreenNow ? sweepSlice : Math.Min(sweepBudgetSlice, sunNodeSlice);

    // Once a frame while the sweep is in the frame: at a window's end the slice resized.
    private void SweepBudgetStep()
    {
        if (!sweepInFrame || sweepSliceFixed || !host.Timing.GpuTimestampsSupported) return;
        var totals = host.Timing.GpuPassTotals;
        if (sweepBudgetAt is null) { sweepBudgetAt = new(totals); sweepBudgetFrame = framesRendered; return; }
        if (framesRendered - sweepBudgetFrame < SweepBudgetFrames) return;
        double sliceMs = 0;
        foreach (var (name, total) in totals)
            if (name.StartsWith("sun-sweep-rays", StringComparison.Ordinal) || name.StartsWith("sun-sweep-closest", StringComparison.Ordinal)
                || name.StartsWith("sun-sweep-shade", StringComparison.Ordinal))
                sliceMs += total.TotalMs - (sweepBudgetAt.TryGetValue(name, out var before) ? before.TotalMs : 0);
        var texelPasses = sweepBudgetTexelPasses;
        sweepBudgetAt = new(totals);
        sweepBudgetFrame = framesRendered;
        sweepBudgetTexelPasses = 0;
        // A window the sweep had too few passes in measures nothing of its cost (and the timings lag a frame or two).
        if (texelPasses < 4 * 1024 || sliceMs <= 0) return;
        var target = sweepGpuMs / (sliceMs / texelPasses);
        // Halfway there (geometrically) a window: the measures lag and are noisy.
        var next = (int)Math.Clamp(Math.Sqrt(sweepBudgetSlice * target), 512, 32768) / 128 * 128;
        if (Math.Abs(next - sweepBudgetSlice) >= 512 && sweepBudgetLog++ < 200)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] sweep budget: the slice's passes {1e3 * sliceMs / texelPasses:0.00} ms a thousand texels; slice {sweepBudgetSlice:N0} -> {next:N0} ({sweepGpuMs:0.#} ms a frame)."));
        sweepBudgetSlice = next;
    }
    private int sweepBudgetLog;
    // A round's last slice resolved in the frame: its end (BeginGpuRefineRound's read-backs) waits for the next frame.
    private bool sweepRoundEndPending;
    // The hit cells' sun (hit_cells.comp mode 1) at a light change, sweepCellsChunk work items a frame (--sweep-cells-chunk).
    private PassHandle sweepCellsPass;
    // The instant layer at a light change (ApplyPatchTexelsNow: patch_dir.comp, texel_patch_light.comp, texel_ratio.comp).
    private PassHandle sweepPatchDirPass, sweepPatchTexelsPass, sweepRatioPass;
    private int sweepInstantChunk = 1 << 20;
    // The fine copy's texels a frame under --sweep-in-frame (--sweep-fine-chunk): all 4.5 M of Crytek Sponza's in one
    // frame held it ~110 ms of the GPU.
    private int sweepFineChunk = 1 << 19;
    private (GpuBufferHandle Ids, int Count, bool Own) sweepFineCopy;
    private int sweepFineCopyFirst;
    // A light change under --sweep-in-frame: its setup's dispatches recorded in the frame (SetupDispatch), not run at once.
    private bool SetupInFrame => sweepInFrame && liveSkyGathered;

    // A buffer a sun change's gather makes as it goes: under --sweep-in-frame mapped and written where it lies -- a
    // device buffer's zeros and contents go up by a submission waited on behind the frames in flight (CreateGpuBuffer's
    // EndSingleTimeCommands): 40-80 ms frames at each round's begin on Crytek Sponza (2026-10-25).
    private GpuBufferHandle SweepBuffer(int bytes, ReadOnlySpan<byte> initial, string name)
    {
        if (!sweepInFrame) return device.CreateGpuBuffer(bytes, initial, name);
        var buffer = device.CreateReadableGpuBuffer(bytes, name);
        if (initial.Length > 0) device.WriteMappedGpuBuffer(buffer, 0, initial);
        return buffer;
    }

    // The gather's counters (a light change's stats): readable under --sweep-in-frame, read where they lie at its end.
    private GpuBufferHandle NewSunNodeStats() => Own(sweepInFrame ? device.CreateReadableGpuBuffer(64, "sponza.sun-nodes.stats")
        : device.CreateGpuBuffer(64, new byte[64], "sponza.sun-nodes.stats"));

    // The coarse sweep's end (FinishSunNodes): the coarse filter and the fine copy recorded, then the fine round started
    // a step later (SweepRoundEndStep, stage 10) once they are done -- on the host the hand-off held a frame ~190 ms.
    private PassHandle sweepCoarseFilterPass, sweepFinePass;

    private void SetupDispatch(PassHandle pass, DispatchCommand command, string name)
    {
        if (SetupInFrame) graph.Dispatch(pass, command);
        else device.DispatchNow(new[] { command }, name);
    }
    private int sweepCellsChunk = 2048;   // the first frames'; then the sweep's slot (CellsBudgetStep)
    private bool sweepCellsFixed;
    // The cells' chunk to the sweep's GPU slot (--sweep-gpu-ms), measured as the slice's is (SweepBudgetStep): a fixed 8,192
    // items were ~15 ms of the GPU a frame on Crytek Sponza by 2026-10-25, ~40 frames of 25-33 ms at each sun change's start.
    private Dictionary<string, GpuPassTotal>? cellsBudgetAt;
    private long cellsBudgetFrame;
    private int cellsBudgetItems;
    // The last measured cost an item (ms): a change's cells begin at the slot's worth of it, not where the last change's
    // ended -- under the change boost the chunk ramped from the 3 ms slot's over most of the pass (0.53 s at 10 ms, 0.45 s
    // at 20, 2026-10-25).
    private double cellsMsPerItem;

    private void BeginCellsBudget()
    {
        cellsBudgetAt = null;
        if (!sweepCellsFixed && cellsMsPerItem > 0) sweepCellsChunk = (int)Math.Clamp(sweepGpuMs / cellsMsPerItem, 512, 65536) / 128 * 128;
    }

    private void CellsBudgetStep()
    {
        if (sweepCellsFixed || !host.Timing.GpuTimestampsSupported) return;
        var totals = host.Timing.GpuPassTotals;
        if (cellsBudgetAt is null || framesRendered - cellsBudgetFrame > 4 * SweepBudgetFrames)
        {
            cellsBudgetAt = new(totals); cellsBudgetFrame = framesRendered; cellsBudgetItems = 0; return;
        }
        if (framesRendered - cellsBudgetFrame < SweepBudgetFrames) return;
        double ms = 0;
        foreach (var (name, total) in totals)
            if (name.StartsWith("sun-sweep-cells", StringComparison.Ordinal))
                ms += total.TotalMs - (cellsBudgetAt.TryGetValue(name, out var before) ? before.TotalMs : 0);
        var items = cellsBudgetItems;
        cellsBudgetAt = new(totals);
        cellsBudgetFrame = framesRendered;
        cellsBudgetItems = 0;
        if (items < 1024 || ms <= 0) return;
        cellsMsPerItem = ms / items;
        var target = sweepGpuMs / cellsMsPerItem;
        sweepCellsChunk = (int)Math.Clamp(Math.Sqrt(sweepCellsChunk * target), 512, 65536) / 128 * 128;
    }
    private PassHandle sweepRaysPass, sweepShadePass, sweepFarFinishPass, sweepFarPoolPass, sweepCensusPass, sweepResolvePass;
    private RayBatchPasses sweepClosestBatch;

    // This pass in the frame: a slice already there, or a new slice behind the frame.
    private bool SweepInFrameNow => sweepSliceInFrame || (sweepInFrame && GatherInBackground && sunNodePass == 0 && !sweepRoundEndPending);
    // The frame a pass was recorded in: one a frame (its external trace takes one piece a frame). The cook screen calls
    // the gather several times a frame, and the gather goes behind the frame inside one of them.
    private long sweepRecordedFrame = -1;
    private bool SweepRecordedThisFrame => sweepRecordedFrame == framesRendered;

    // In this order: the frame runs its passes as declared.
    private void DeclareSweepFrame(Func<string[], ShaderInterface> reflect)
    {
        // Reflected where its pipeline is made (CreateTexelRatioPipeline), which can come after the graph's declaration.
        texelRatioInterface ??= reflect(new[] { "texel_ratio.comp" });
        sweepCellsPass = graph.ComputePass("sun-sweep-cells").Shader(hitCellsInterface).Handle;
        sweepCoarseFilterPass = graph.ComputePass("sun-sweep-coarse-filter").Shader(coarseFilterInterface).Handle;
        sweepFinePass = graph.ComputePass("sun-sweep-fine").Shader(transferFineInterface).Handle;
        sweepPatchDirPass = graph.ComputePass("sun-sweep-patch-dir").Shader(patchDirInterface).Handle;
        sweepPatchTexelsPass = graph.ComputePass("sun-sweep-patch-texels").Shader(texelPatchLightInterface).Handle;
        sweepRatioPass = graph.ComputePass("sun-sweep-ratio").Shader(texelRatioInterface).Handle;
        sweepRaysPass = graph.ComputePass("sun-sweep-rays").Shader(sunNodeRaysInterface).Handle;
        sweepClosestBatch = DeclareClosestBatch("sun-sweep-closest");
        sweepShadePass = graph.ComputePass("sun-sweep-shade").Shader(sunNodeShadeSplitInterface).Handle;
        sweepFarFinishPass = graph.ComputePass("sun-sweep-far-finish").Shader(farFinishInterface).Handle;
        sweepFarPoolPass = graph.ComputePass("sun-sweep-far-pool").Shader(farPoolInterface).Handle;
        sweepCensusPass = graph.ComputePass("sun-sweep-census").Shader(transportCensusInterface).Handle;
        sweepResolvePass = graph.ComputePass("sun-sweep-resolve").Shader(sliceResolveInterface).Handle;
    }

    // --change-stop fine|coarse|screen: a sun change (after the first landing) lands early -- fine: no near round; coarse:
    // no fine round either (the coarse sweep and its copy); screen: with the screen's first gather (--screen-first). The
    // tail past ~10 s of a 20 deg change on Crytek Sponza bought the screen ~0.02 over ~85 s (2026-10-25).
    private int changeStop;
    private bool ChangeStopsAt(int level) => changeStop >= level && liveSkyGathered;

    private void ParseSweepFrame(AppArgs args)
    {
        changeStop = args.String("change-stop") switch { "fine" => 1, "coarse" => 2, "screen" => 3, _ => 0 };
        // On by default since 2026-10-25 (Crytek Sponza: a sun change at ~102-113 fps, against ~25 s of the cook screen at
        // ~4 fps); --no-sweep-in-frame: the host's passes under the cook screen, as before (the parts dumps read at once).
        sweepInFrame = !args.Flag("no-sweep-in-frame");
        changeCookScreen = args.Flag("change-cook-screen");
        if (args.Int("sweep-slice") is { } s) { sweepSlice = Math.Clamp(s, 256, 32768); sweepSliceFixed = true; }
        if (args.Float("sweep-gpu-ms") is { } gm) sweepGpuMs = Math.Max(0.5f, gm);
        if (args.Int("sweep-instant-chunk") is { } ic) sweepInstantChunk = Math.Max(65536, ic);
        if (args.Int("compose-filter-chunk") is { } cf) composeFilterChunk = Math.Max(65536, cf);
        if (args.Int("compose-base-chunk") is { } cb) composeBaseChunk = Math.Max(65536, cb);
        if (args.Int("live-bounce-split") is { } ls) liveBounceSplit = Math.Clamp(ls, 1, 16);
        if (args.Int("sweep-fine-chunk") is { } fc) sweepFineChunk = Math.Max(65536, fc);
        if (args.Int("sweep-cells-chunk") is { } cc) { sweepCellsChunk = Math.Clamp(cc, 1024, 4096 * 64); sweepCellsFixed = true; }
        // Behind the frame is where the in-frame sweep runs (GatherInBackground).
        if (sweepInFrame) backgroundGather = true;
    }

    // SunNodePassNow's pass, recorded: the same rays, trace and shade, in the frame.
    private void SweepFramePass(GpuBufferHandle ids, int first, int slice, int pass, int seedPass)
    {
        sweepSliceInFrame = true;
        sweepRecordedFrame = framesRendered;
        sweepBudgetTexelPasses += slice;
        graph.Dispatch(sweepRaysPass, SunNodeRaysCommand(ids, first, slice, seedPass));
        var cells = HitCellsShade(0);
        var hitSun = HitSunColumn;
        var seed = SunNodeSeed(first, seedPass, 0);
        var count = slice * 32;
        var tracer = frameTracer!;
        if (hitSun >= 0)
        {
            var sun = sunNodeBitDirections[hitSun] with { W = gatherClear ? -sunNodeLift : sunNodeLift };
            var reach = rayReach;
            var disc = hitSunDisc ? SunDiscTangent : 0f;
            graph.External(sweepClosestBatch.Hardware, sync => tracer.EncodeTrace(sync, sunNodeRaysBuffer, 0, sunNodeHits, 0, count, seed,
                sun: sun, reach: reach, discTan: disc));
        }
        else graph.External(sweepClosestBatch.Hardware, sync => tracer.EncodeTrace(sync, sunNodeRaysBuffer, 0, sunNodeHits, 0, count, seed));
        graph.Dispatch(sweepShadePass, leanShade && cells
            ? LeanShadeCommand(ids, first, slice, pass, hitSun >= 0)
            : SunNodeShadeCommand(ids, first, slice, pass, 0, hitSun >= 0 ? 3f : 0f, few: sunNodeSuns!.Length <= 32,
                cells: cells, exactColumn: Math.Max(hitSun, 0)));
        sweepFramePasses++;
    }

    private long sweepFramePasses, sweepFrameSlices;

    // ResolveSliceNow's resolve, recorded after the slice's last pass (with its far finish and census before it). False
    // when the round's last slice is in: its end waits for the next frame.
    private bool SweepResolveFrame(int slice, int passes)
    {
        var refining = sunNodeRefineRound > 0;
        var ids = refining ? sunNodeRefineBuffer : sunNodeTraceBuffer;
        var places = refining ? resolvePlaces[1 - resolveNext] : sunNodeTraceBuffer;
        ResolveDispatch(0, sunNodeFirst, slice, passes, refining, ids, places, sunNodeDarkBar, inFrame: sweepResolvePass);
        sweepSliceInFrame = false;
        sweepFrameSlices++;
        sunNodeFirst += slice;
        var length = refining ? sunNodeRefineIds.Length : sunNodeTraceIds.Length;
        if (sunNodeFirst < length) return true;
        sweepRoundEndPending = true;
        sweepEndStage = 0;
        sweepEndFrame = framesRendered;
        return false;
    }

    // A round's end in the frame, a step a frame (BeginGpuRefineRound's, without its waits): the round's last resolve
    // past the frames in flight, its count and the sweep's histogram read where they lie; the sweep judged again (recorded),
    // its count read the same way; the next round's list sorted on a worker (read and written where it lies). Each wait
    // was a WaitIdle and a submitted copy: ~45 ms a round's end on Crytek Sponza (2026-10-25).
    private int sweepEndStage;
    private long sweepEndFrame;
    private int sweepEndLeft, sweepEndOf;
    private Task<(uint[] Ids, uint[] Places)>? sweepSortTask;
    private Task<(uint[] Ids, uint[]? Mask, long Masked, string Log)>? sweepFinePick;

    private bool SweepWorkDone => framesRendered >= sweepEndFrame + Math.Max(1, device.MaxFramesInFlightCount) + 1;

    private uint ReadMappedWord(GpuBufferHandle buffer)
    {
        Span<byte> b = stackalloc byte[4];
        device.ReadMappedGpuBuffer(buffer, 0, b);
        return BitConverter.ToUInt32(b);
    }

    private uint[] ReadMappedWords(GpuBufferHandle buffer, int offset, int count)
    {
        var words = new uint[count];
        device.ReadMappedGpuBuffer(buffer, offset, System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()));
        return words;
    }

    private void SweepRoundEndStep()
    {
        using var profScope = Prof("gather.round-end");
        switch (sweepEndStage)
        {
            case 9:
            {
                // The fine copy, a chunk a frame (ApplyFineNow left it here); the coarse filter went with the first frame.
                var (ids, count, own) = sweepFineCopy;
                if (sweepFineCopyFirst < count)
                {
                    FineDispatch(ids, count, default, sweepFineCopyFirst, sweepFineChunk);
                    sweepFineCopyFirst += sweepFineChunk;
                    sweepRecordedFrame = framesRendered;
                    sweepEndFrame = framesRendered;
                    return;
                }
                if (own) RetireGpuBuffer(ids);
                sweepFineCopy = default;
                sweepEndStage = 10;
                return;
            }
            case 10:
            {
                // The coarse sweep's end (FinishSunNodes): the coarse filter and the fine copy done; the fine round next.
                if (!SweepWorkDone) return;
                SweepFineCounts();
                DumpParts("copy");
                // The pick on a worker (125 ms on Crytek Sponza's 900 K flagged): the flags read here, the mask written after.
                var words = FineFlagWords();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                sweepFinePick = words.Length == 0 ? null : Task.Run(() => FinePick(words, words.Length / 2, clock));
                sweepEndStage = 11;
                goto case 11;
            }
            case 11:
            {
                if (sweepFinePick is { IsCompleted: false }) return;
                var picked = sweepFinePick is { } pick ? ApplyFinePick(pick.Result) : Array.Empty<uint>();
                sweepFinePick = null;
                RecallCopy(picked);
                if (picked.Length > 0 && !ChangeStopsAt(2))
                {
                    fineRoundIds = picked;
                    nearRoundsLeft = ChangeStopsAt(1) ? 0 : NearRoundsNow;
                    BeginNearCut(picked);
                    StartFineRound(picked);
                    SweepEndDone();
                    return;
                }
                SweepEndDone();
                sunNodeStage = SunNodeStage.Done;
                FinishSunNodesTail();
                return;
            }
            case 0:
            {
                if (!SweepWorkDone) return;
                sweepEndLeft = (int)ReadMappedWord(resolveCount);
                sweepEndOf = sunNodeRefineRound == 0 ? sunNodeTraceIds.Length : sunNodeRefineIds.Length;
                var pooled = SettlePoolNow;
                if (sunNodeRefineRound == 0 && (sweepEndLeft > 0 || pooled))
                {
                    var bytes = new byte[512];
                    device.ReadMappedGpuBuffer(resolveHistogram, 0, bytes);
                    var histogram = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes);
                    long total = 0, seen = 0;
                    foreach (var c in histogram) total += c;
                    for (var b = 0; b < histogram.Length && texelDarkFloor > 0f; b++)
                    {
                        seen += histogram[b];
                        if (seen * 2 < total) continue;
                        sunNodeDarkBar = texelDarkFloor * MathF.Pow(2f, (b + 0.5f) / 4f - 24f);
                        break;
                    }
                    var judged = resolveNext;
                    resolveNext = 1 - resolveNext;
                    device.WriteMappedGpuBuffer(resolveCount, 0, new byte[16]);
                    if (pooled) ResolveDispatch(1, 0, sweepEndOf, 0, false, sunNodeTraceBuffer, sunNodeTraceBuffer, sunNodeDarkBar, inFrame: sweepResolvePass);
                    else ResolveDispatch(1, 0, sweepEndLeft, 0, false, resolveIds[judged], resolvePlaces[judged], sunNodeDarkBar, inFrame: sweepResolvePass);
                    sweepEndFrame = framesRendered;
                    sweepEndStage = 1;
                    return;
                }
                sweepEndStage = 2;
                goto case 2;
            }
            case 1:
            {
                if (!SweepWorkDone) return;
                var after = (int)ReadMappedWord(resolveCount);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] sun nodes: the dark bar {sunNodeDarkBar:G3} ({texelDarkFloor:G2} of the median texel light); judged again in the frame: {after:N0} refine (its slices alone: {sweepEndLeft:N0})."));
                sweepEndLeft = after;
                sweepEndStage = 2;
                goto case 2;
            }
            case 2:
            {
                var left = sweepEndLeft;
                var of = sweepEndOf;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] sun nodes: {of - left:N0} of {of:N0} texels settled after {(sunNodeRefineRound == 0 ? "the sweep" : $"round {sunNodeRefineRound}")} in the frame ({sunNodeClock.Elapsed.TotalMinutes:0.0} min, {sweepFrameSlices:N0} slices, {sweepFramePasses:N0} passes); {left:N0} refine."));
                // --refine-worst: a round that settled all it traced goes on while the pool holds candidates.
                // In the frame only under --patch-rank: its many small rounds cost a round end each (Crytek Sponza, a 20 deg
                // change: the screen 0.086 -> 0.084 for 87.7 -> 98.7 s, 2026-10-25).
                var rankInFrame = refineWorst && patchRank;
                var pooledOn = rankInFrame && sunNodeRefineRound > 0 && worstPoolIds.Length > 0;
                var stops = (left == 0 && !pooledOn) || RefineStops(of, left);
                if (stops)
                {
                    if (left > 0 || pooledOn)
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"[VulkanSponza] sun nodes: round {sunNodeRefineRound} settled {of - left:N0} of {of:N0} ({RefineStopsWhy(of, left)}): refining stops; {left:N0} keep their light as it is."));
                    SweepEndFinish();
                    return;
                }
                // The round's list in id order (as SortResolveList's), on a worker: the appends land as the threads meet.
                // --refine-worst: ranked with the pool (RankWorst, as the host's round end; by patch under --patch-rank).
                var ids = new byte[left * 4];
                var places = new byte[left * 4];
                device.ReadMappedGpuBuffer(resolveIds[resolveNext], 0, ids);
                device.ReadMappedGpuBuffer(resolvePlaces[resolveNext], 0, places);
                float[]? scores = null;
                if (rankInFrame && !resolveScores[resolveNext].Equals(default(GpuBufferHandle)))
                {
                    scores = new float[left];
                    device.ReadMappedGpuBuffer(resolveScores[resolveNext], 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(scores.AsSpan()));
                }
                sweepSortTask = Task.Run(() =>
                {
                    var i = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(ids).ToArray();
                    var p = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(places).ToArray();
                    if (scores is not null) return RankWorst(i, p, scores, left);
                    Array.Sort(i, p);
                    return (i, p);
                });
                sweepEndStage = 3;
                return;
            }
            case 3:
            {
                if (sweepSortTask is not { IsCompleted: true } task) return;
                var (ids, places) = task.Result;
                sweepSortTask = null;
                if (ids.Length == 0) { SweepEndFinish(); return; }
                device.WriteMappedGpuBuffer(resolveIds[resolveNext], 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(ids.AsSpan()));
                device.WriteMappedGpuBuffer(resolvePlaces[resolveNext], 0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(places.AsSpan()));
                sunNodeRefineIds = ids;
                sunNodeRefineBuffer = resolveIds[resolveNext];
                resolveNext = 1 - resolveNext;
                device.WriteMappedGpuBuffer(resolveCount, 0, new byte[16]);
                sunNodeRefineRound++;
                sunNodeRefineRays += (long)ids.Length * RefinePasses * 32;
                sunNodeFirst = 0;
                sunNodePass = 0;
                SweepEndDone();
                return;
            }
        }
    }

    // The round's end done: the compose follows (it ran after every slice on the host path), once -- the round's resolves
    // are past the frames in flight by now, so not again the next frame (splitComposeAgainFrame) as after a live write.
    private void SweepEndDone()
    {
        sweepEndStage = 0;
        sweepRoundEndPending = false;
        splitComposeDirty = true;
    }

    private void SweepEndFinish()
    {
        SweepEndDone();
        gpuResolving = false;
        FinishSunNodes();
    }
}
