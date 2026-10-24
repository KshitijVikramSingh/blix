using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The moving sun's relight (sun_flip_push.comp): once a gather has landed, each small step of a moving sun (--sun-move)
// is a push from the texels whose sun bit flipped plus the sun-linear record's change at the new sun, not a gather.
// tools/gi-model/sun_flip_push.py on Crytek Sponza: at 3 and 10 deg the flips are ~80% of the change from 0.2-0.8% of
// rays and 1.4-3.1% of the texels; the push simulated at 32-64 rays a finest flipped texel left the receivers 0.003-0.006
// off where a fresh 128-ray gather's own noise is 0.12, at ~1/20-1/50 of the rays. A regather moved a step ~20 s, the
// sun 10 deg on meanwhile at 0.5 deg/s, the fine (cloth) last (2026-10-25).
// A step, a stage a frame: the old sun's bits kept and the GI's sun turned to the frame's (the live chain shoots the new
// bits); the flip list recorded once they are in; its count read past the frames in flight; the rays a chunk a frame
// (rays, the closest batch, the deposit); every texel's record and sun part moved; the compose. --no-flip-push: the
// moving sun chases with regathers, as before. The bounce's change (6-7% of a step's) is left: no cheap source of it
// beat none in the model.
internal sealed partial class SponzaLoop
{
    private bool flipPushOff;
    // --flip-push-change (a measurement): a --sun-change taken as one push step, not a regather -- the push judged on a
    // single step against full gathers at its sun (tools/gi-model/sun_flip_push.py's sun pairs).
    private bool flipPushChange, flipDumpedSingle;
    private string flipSources = "fine";
    private int flipRaysPer = 32, flipChunkRays = 1 << 18;
    private float flipStepDeg = 0.5f;
    private ShaderInterface flipPushInterface = null!;
    private PipelineHandle flipPushPipeline;
    private PassHandle flipListPass, flipRaysPass, flipDepositPass, flipApplyPass;
    private RayBatchPasses flipClosestBatch;
    private GpuBufferHandle flipOldBits, flipList, flipRays, flipHits, flipAccum;
    private int flipStage, flipCount, flipFirst;
    // The list's entries (a block of rays each): 4 M, 128 M rays at 32 a block -- past it a step's tail is dropped.
    private const int FlipListRoom = 1 << 22;
    private long flipFrame, flipBitsFrame = -1, flipCensusFrame = -1;
    private Vector3 flipOldSun, flipNewSun;
    private uint flipSeed = 1;
    private readonly System.Diagnostics.Stopwatch flipClock = new();
    private int flipSteps;

    private void DumpFlipParts(string stage)
    {
        if (Environment.GetEnvironmentVariable("BLIX_DUMP_PARTS") is not { } prefix) return;
        DumpParts(stage, sunBits: true);
        if (!texelSunWBuffer.Equals(default(GpuBufferHandle)))
            File.WriteAllBytes($"{prefix}.{stage}.0.sunw", device.ReadGpuBuffer(texelSunWBuffer, 0, texelCount * 16));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] flip push: parts dumped ({stage}); the sun toward {GiToSun}."));
    }

    private void ParseFlipPush(AppArgs args)
    {
        // Opt-in (--flip-push): judged on Crytek Sponza's 10 deg step against a full gather at its sun, the push's totals came
        // right but not its texels -- whole-light error 0.32-0.42 against the old light's 0.31 at 4.7-75 M rays, its
        // landings spread over the lookup's candidates or given to the one at the hit (2026-10-25). The moving sun chases
        // with regathers (the selects) by default.
        flipPushOff = !args.Flag("flip-push");
        flipPushChange = args.Flag("flip-push-change");
        flipSources = args.String("flip-sources", "fine");
        if (args.Int("flip-rays") is { } fr) flipRaysPer = Math.Clamp(fr, 1, 1024);
        if (args.Float("flip-step") is { } fs) flipStepDeg = Math.Max(0.05f, fs);
    }

    // The push steers the moving sun: a gather has landed (its records are up) and no regather is in flight.
    private bool FlipPushSteering => (sunMoving || flipPushChange) && !flipPushOff && SunLinearOn && liveSkyGathered && liveCookWarm
        && !texelSunWBuffer.Equals(default(GpuBufferHandle)) && (flipStage > 0 || !LightChangeInFlight);

    private void DeclareFlipPush(Func<string[], ShaderInterface> reflect)
    {
        flipPushInterface = reflect(new[] { "sun_flip_push.comp" });
        flipListPass = graph.ComputePass("flip-push-list").Shader(flipPushInterface).Handle;
        flipRaysPass = graph.ComputePass("flip-push-rays").Shader(flipPushInterface).Handle;
        flipClosestBatch = DeclareClosestBatch("flip-push-closest");
        flipDepositPass = graph.ComputePass("flip-push-deposit").Shader(flipPushInterface).Handle;
        flipApplyPass = graph.ComputePass("flip-push-apply").Shader(flipPushInterface).Handle;
    }

    private void CreateFlipPushPipeline(string shaderDir) =>
        flipPushPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "sun_flip_push.comp.spv")), flipPushInterface, "sun_flip_push")), "sun_flip_push"));

    private void EnsureFlipBuffers()
    {
        if (!flipAccum.Equals(default(GpuBufferHandle))) return;
        var live = texelLiveIds!.Length;
        flipOldBits = Own(device.CreateReadableGpuBuffer((texelCount + 31) / 32 * 4, "sponza.flip.old-bits"));
        flipList = Own(device.CreateReadableGpuBuffer(16 + FlipListRoom * 4, "sponza.flip.list"));
        flipRays = Own(device.CreateGpuBuffer(flipChunkRays * 32, name: "sponza.flip.rays"));
        flipHits = Own(device.CreateGpuBuffer(flipChunkRays * 48 + 80, name: "sponza.flip.hits"));
        flipAccum = Own(device.CreateGpuBuffer(checked(texelCount * 16), name: "sponza.flip.accum"));
    }

    private DispatchCommand FlipCommand(int mode, int threads, int first)
    {
        var declared = flipPushInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        var stand = flipAccum;
        return new DispatchCommand(flipPushPipeline, (threads + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uPass", new Vector4Uniform(new Vector4(threads, mode, flipRaysPer, first))),
                new("uNewSun", new Vector4Uniform(new Vector4(flipNewSun, sunNodeLift))),
                new("uOldSun", new Vector4Uniform(new Vector4(flipOldSun, rayReach))),
                new("uGrid", new Vector4Uniform(texelGrid)),
                new("uLookup", new Vector4Uniform(new Vector4(sunNodePlane, sunNodeSigma, sunNodeHashSlots, flipSeed))),
                new("uBits", new Vector4Uniform(new Vector4(liveSunWords, liveSunColumn, FlipListRoom,
                    flipSources == "both" ? 0f : flipSources == "coarse" ? -1f - transferMinLevel : TransferOn ? transferMinLevel : 99f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("LiveIds", splitLiveBuffer),
                new ShaderBufferBinding("OldBits", flipOldBits),
                new ShaderBufferBinding("SunNodeBits", liveSunBits),
                new ShaderBufferBinding("FlipList", flipList),
                new ShaderBufferBinding("FlipRays", flipRays),
                new ShaderBufferBinding("FlipHits", flipHits),
                new ShaderBufferBinding("SunNodeHash", sunNodeHash.Equals(default(GpuBufferHandle)) ? stand : sunNodeHash),
                new ShaderBufferBinding("FoliagePoints", sunNodeFoliagePoints.Equals(default(GpuBufferHandle)) ? stand : sunNodeFoliagePoints),
                new ShaderBufferBinding("FoliageHash", sunNodeFoliageHash.Equals(default(GpuBufferHandle)) ? stand : sunNodeFoliageHash),
                new ShaderBufferBinding("TexelPatch", liveTexelPatch.Equals(default(GpuBufferHandle)) ? stand : liveTexelPatch),
                new ShaderBufferBinding("PatchTerms", solveBuffers[7]),
                new ShaderBufferBinding("Accum", flipAccum),
                new ShaderBufferBinding("TexelSunW", texelSunWBuffer),
                new ShaderBufferBinding("SplitSun", EnsureLivePart(true)),
            }.Where(b => declared.Contains(b.Name)).ToArray());
    }

    // StepSunMove, each frame while the sun moves: a step's stage.
    private bool FlipPushStep()
    {
        if (!FlipPushSteering) return false;
        var frameSun = -Vector3.Normalize(sunDirection);
        switch (flipStage)
        {
            case 0:
            {
                // A single step's result (--flip-push-change): dumped once its apply has run (the frame after it).
                if (flipPushChange && flipSteps == 1 && !flipDumpedSingle)
                {
                    flipDumpedSingle = true;
                    DumpFlipParts("push-after-1");
                    // A measurement's run: done once its step is dumped (it sat out its timeout before).
                    if (Environment.GetEnvironmentVariable("BLIX_DUMP_PARTS") is not null) host.RequestClose();
                }
                // The GI's sun's bits must be in (a step starts from them), and the sun far enough on.
                if (!liveSunDone || liveSunToward != giSunTarget || sunNodeHash.Equals(default(GpuBufferHandle))) return true;
                var turned = MathF.Acos(Math.Clamp(Vector3.Dot(giSunTarget, frameSun), -1f, 1f)) * 180f / MathF.PI;
                if (turned < flipStepDeg) return true;
                // BLIX_DUMP_PARTS: the parts before the first step and after the 1st, 2nd and 20th (the apply is done by
                // the next step's start), with the records (.sunw) beside each.
                if (flipSteps is 0 or 1 or 2 or 20) DumpFlipParts(flipSteps == 0 ? "push-before" : $"push-after-{flipSteps}");
                // The last step's census (its deposits are done by now).
                if (flipCensusFrame >= 0 && (flipSteps <= 20 || flipSteps % 20 == 0))
                {
                    var census = new uint[4];
                    device.ReadMappedGpuBuffer(flipList, 0, MemoryMarshal.AsBytes(census.AsSpan()));
                    long missed = census[1], none = census[2], took = census[3];
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"[VulkanSponza] flip push #{flipSteps} census: rays that met nothing {missed:N0}, hits with no receiver {none:N0} ({100.0 * none / Math.Max(1, none + took):0.0}% of the hits), hits that took one {took:N0}."));
                    flipCensusFrame = -1;
                }
                EnsureFlipBuffers();
                device.WriteMappedGpuBuffer(flipOldBits, 0, MemoryMarshal.AsBytes(SunColumnBits().AsSpan()));
                flipOldSun = giSunTarget;
                flipNewSun = frameSun;
                giSunTarget = frameSun;   // the live chain shoots its bits (RecordLivePatchLight)
                flipClock.Restart();
                flipStage = 1;
                return true;
            }
            case 1:
            {
                // A frame after the bits' last chunk was recorded: the list's pass may come before theirs in a frame.
                if (!liveSunDone || liveSunToward != flipNewSun) { flipBitsFrame = -1; return true; }
                if (flipBitsFrame < 0) { flipBitsFrame = framesRendered; return true; }
                if (framesRendered == flipBitsFrame) return true;
                device.WriteMappedGpuBuffer(flipList, 0, new byte[16]);
                var live = texelLiveIds!.Length;
                graph.Dispatch(flipListPass, FlipCommand(0, live, 0));
                flipFrame = framesRendered;
                flipStage = 2;
                return true;
            }
            case 2:
            {
                if (framesRendered < flipFrame + Math.Max(1, device.MaxFramesInFlightCount) + 1) return true;
                flipCount = (int)Math.Min(ReadMappedWord(flipList), (uint)FlipListRoom);
                flipFirst = 0;
                flipStage = flipCount > 0 ? 3 : 4;
                return true;
            }
            case 3:
            {
                // A chunk of the rays a frame: the rays, their closest hits, the deposit.
                var total = (long)flipCount * flipRaysPer;
                var n = (int)Math.Min(flipChunkRays, total - flipFirst);
                graph.Dispatch(flipRaysPass, FlipCommand(1, n, flipFirst));
                RecordClosestBatch(flipClosestBatch, flipRays, flipHits, n, flipSeed * 7919u);
                graph.Dispatch(flipDepositPass, FlipCommand(2, n, flipFirst));
                flipFirst += n;
                if (flipFirst >= total) flipStage = 4;
                return true;
            }
            default:
            {
                graph.Dispatch(flipApplyPass, FlipCommand(3, texelLiveIds!.Length, 0));
                flipCensusFrame = framesRendered;
                splitComposeDirty = true;
                MarkIncidentDirty();
                flipSeed++;
                flipSteps++;
                var step = MathF.Acos(Math.Clamp(Vector3.Dot(flipOldSun, flipNewSun), -1f, 1f)) * 180f / MathF.PI;
                if (flipSteps <= 20 || flipSteps % 20 == 0)
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"[VulkanSponza] flip push #{flipSteps}: a {step:0.00} deg step, {flipCount:N0} blocks of {flipRaysPer} rays from the flipped texels, {(long)flipCount * flipRaysPer / 1e6:0.00} M rays, in {flipClock.Elapsed.TotalMilliseconds:0} ms."));
                flipStage = 0;
                return true;
            }
        }
    }
}
