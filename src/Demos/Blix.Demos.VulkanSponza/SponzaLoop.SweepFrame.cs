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
        return false;
    }

    // A frame after a round's last resolve was recorded (that frame's work submitted, ahead of the read-backs on the
    // queue): the round's end, as ResolveSliceNow's -- the next round, or the sweep finished. The compose follows.
    // False when the gather stops here this frame.
    private bool SweepRoundEnd()
    {
        sweepRoundEndPending = false;
        splitComposeDirty = true;
        splitComposeAgainFrame = framesRendered + 1;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] sweep in the frame: round {sunNodeRefineRound} in, {sweepFrameSlices:N0} slices and {sweepFramePasses:N0} passes so far."));
        if (BeginGpuRefineRound()) return true;
        gpuResolving = false;
        FinishSunNodes();
        return false;
    }
}
