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
    // A round's last slice resolved in the frame: its end (BeginGpuRefineRound's read-backs) waits for the next frame.
    private bool sweepRoundEndPending;
    private PassHandle sweepRaysPass, sweepShadePass, sweepFarFinishPass, sweepFarPoolPass, sweepCensusPass, sweepResolvePass;
    private RayBatchPasses sweepClosestBatch;

    // This pass in the frame: a slice already there, or a new slice behind the frame.
    private bool SweepInFrameNow => sweepSliceInFrame || (sweepInFrame && GatherInBackground && sunNodePass == 0 && !sweepRoundEndPending);
    // The frame a pass was recorded in: one a frame (its external trace takes one piece a frame). The cook screen calls
    // the gather several times a frame, and the gather goes behind the frame inside one of them.
    private long sweepRecordedFrame = -1;
    private bool SweepRecordedThisFrame => sweepRecordedFrame == framesRendered;

    // In this order: the frame runs its passes as declared.
    private void DeclareSweepFrame()
    {
        sweepRaysPass = graph.ComputePass("sun-sweep-rays").Shader(sunNodeRaysInterface).Handle;
        sweepClosestBatch = DeclareClosestBatch("sun-sweep-closest");
        sweepShadePass = graph.ComputePass("sun-sweep-shade").Shader(sunNodeShadeSplitInterface).Handle;
        sweepFarFinishPass = graph.ComputePass("sun-sweep-far-finish").Shader(farFinishInterface).Handle;
        sweepFarPoolPass = graph.ComputePass("sun-sweep-far-pool").Shader(farPoolInterface).Handle;
        sweepCensusPass = graph.ComputePass("sun-sweep-census").Shader(transportCensusInterface).Handle;
        sweepResolvePass = graph.ComputePass("sun-sweep-resolve").Shader(sliceResolveInterface).Handle;
    }

    private void ParseSweepFrame(AppArgs args)
    {
        sweepInFrame = args.Flag("sweep-in-frame");
        if (args.Int("sweep-slice") is { } s) sweepSlice = Math.Clamp(s, 256, 32768);
        // Behind the frame is where the in-frame sweep runs (GatherInBackground).
        if (sweepInFrame) backgroundGather = true;
    }

    // SunNodePassNow's pass, recorded: the same rays, trace and shade, in the frame.
    private void SweepFramePass(GpuBufferHandle ids, int first, int slice, int pass, int seedPass)
    {
        sweepSliceInFrame = true;
        sweepRecordedFrame = framesRendered;
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

    private bool SweepWorkDone => framesRendered >= sweepEndFrame + Math.Max(1, device.MaxFramesInFlightCount) + 1;

    private uint ReadMappedWord(GpuBufferHandle buffer)
    {
        Span<byte> b = stackalloc byte[4];
        device.ReadMappedGpuBuffer(buffer, 0, b);
        return BitConverter.ToUInt32(b);
    }

    private void SweepRoundEndStep()
    {
        using var profScope = Prof("gather.round-end");
        switch (sweepEndStage)
        {
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
                var stops = left == 0 || sunNodeRefineRound > 0 && of - left < splitRefineMinShare * of;
                if (stops)
                {
                    SweepEndFinish();
                    return;
                }
                // The round's list in id order (as SortResolveList's), on a worker: the appends land as the threads meet.
                var ids = new byte[left * 4];
                var places = new byte[left * 4];
                device.ReadMappedGpuBuffer(resolveIds[resolveNext], 0, ids);
                device.ReadMappedGpuBuffer(resolvePlaces[resolveNext], 0, places);
                sweepSortTask = Task.Run(() =>
                {
                    var i = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(ids).ToArray();
                    var p = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(places).ToArray();
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
