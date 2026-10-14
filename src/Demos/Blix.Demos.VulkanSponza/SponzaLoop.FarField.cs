using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The far field (texel_far.glsl; plan.md, "Short rays, then ask the patch"): a split cook's texel traces what lies near it
// and reads what lies beyond its reach from its patch's record. The noise census (2026-10-14, SponzaLoop.NoiseCensus.cs):
// the 7 M texels the sweep left unsettled had 85% of their variance from beyond 1 m, and 80% of them would settle on
// their near rays alone -- shaded texels finding a little of the sunlit floor across the hall, ray by ray. The patches
// already hold that light; the texels were fetching it again, 16 M times over.
//   THE RECORD PASS  before the sweep, --far-record-rays (128) a texel: every ray past its texel's reach adds its light
//                    into its patch's bin (texel_nodes_shade.glsl, recording). A patch pools ~80 texels' rays. Then
//                    judged (texel_far_judge.comp): a patch whose far light's error is over half the texels' bar gets
//                    another round for its texels, up to --far-record-max (1,024) rays a texel; then a bin still too
//                    uncertain is made unknown, its rays the texels' own. The first build had no such judge: the crevices'
//                    dark corridor, lit through a doorway one bin wide, read a few hundred rays' guess at it, shared by a
//                    patch -- its points 0.056 off the path trace against 0.029 (2026-10-14).
//   THE SWEEP        a ray whose bin the record knows stops at the reach (texel_nodes_rays.comp); one that gets there is
//                    far -- its light the record's. Each texel keeps, per bin, how much of its rays went far
//                    (NodeFarBins); a slice's last step (texel_far_finish.comp) adds the far light to its sums. The settling
//                    then judges the near rays' noise alone.
// --no-far-field: every ray its own, as before (the A/B). Only for the split cook (its few columns: the record is a
// patch x 36 bins x (3 + 4 x columns) words -- Sponza's two columns 300 MB).
internal sealed partial class SponzaLoop
{
    private bool farField = true;
    private int farRecordRays = 128, farRecordMax = 1024;
    private const int FarBins = 36;
    private const float FarFixed = 4096f;
    // A bin is known once its rays' weight sums to this (a cosine ray's weight is pi): a few rays' worth.
    private const float FarKnownWeight = 8f * MathF.PI;
    private bool farOn, farRecording, farJudgePending;
    private int farRecordPass, farRecordRound;
    private uint[] farRecordIds = Array.Empty<uint>();
    private GpuBufferHandle farRecordIdsBuffer, farVerdict;
    private int[]? farPatchOf;
    private long farRecordTotalRays;
    private ShaderInterface farJudgeInterface = null!;
    private PipelineHandle farJudgePipeline;
    private readonly System.Diagnostics.Stopwatch farClock = new();
    private GpuBufferHandle farRecord, farBinsBuffer;
    private ShaderInterface farFinishInterface = null!;
    private PassHandle farFinishPass;
    private PipelineHandle farFinishPipeline;

    private void ReadFarArgs(AppArgs args)
    {
        farField = !args.Flag("no-far-field");
        if (args.Int("far-record-rays") is { } fr) farRecordRays = Math.Max(32, fr / 32 * 32);
        if (args.Int("far-record-max") is { } fm) farRecordMax = Math.Max(farRecordRays, fm);
    }

    // The cache's key: what changes the far field's answer.
    private string FarKey => farField && sunNodeGuide ? string.Create(CultureInfo.InvariantCulture, $";far {farRecordRays} {farRecordMax} judged w2") : "";

    private void DeclareFarPasses(Func<string[], ShaderInterface> reflect)
    {
        farFinishInterface = reflect(new[] { "texel_far_finish.comp" });
        farJudgeInterface = reflect(new[] { "texel_far_judge.comp" });
        farFinishPass = graph.ComputePass("sun-nodes-far-finish").Shader(farFinishInterface).Handle;
    }

    private void CreateFarPipeline(string shaderDir)
    {
        farFinishPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_far_finish.comp.spv")), farFinishInterface, "texel_far_finish")), "texel_far_finish"));
        farJudgePipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_far_judge.comp.spv")), farJudgeInterface, "texel_far_judge")), "texel_far_judge"));
    }

    // UploadSunNodes: on for a guided split cook of a few columns (the guide patch is the far field's when a texel saw none).
    private void BeginFarField(int suns)
    {
        farOn = farField && splitCooking && sunNodeGuided && !sunNodeExact && sunNodeStride == 1 && suns <= 3 && cookedPacked is not null;
        farRecording = farOn;
        farRecordPass = 0;
        farRecordRound = 0;
        farJudgePending = false;
        farPatchOf = null;
        farRecordTotalRays = 0;
        if (!farOn)
        {
            Console.WriteLine($"[VulkanSponza] far field: off{(farField ? " (not a guided split cook of up to 3 columns)" : " (--no-far-field)")}.");
            return;
        }
        var patches = cookedPacked!.Length / 3;
        var bytes = checked((int)((long)patches * FarBins * (3 + 4 * suns) * 4));
        farRecord = Own(device.CreateGpuBuffer(bytes, new byte[bytes], "sponza.far.record"));
        farVerdict = Own(device.CreateGpuBuffer(patches * 4, name: "sponza.far.verdict"));
        farRecordIds = sunNodeTraceIds;
        farRecordIdsBuffer = sunNodeTraceBuffer;
        farBinsBuffer = Own(device.CreateGpuBuffer(checked(sunNodeSlice * FarBins * 16), name: "sponza.far.bins"));
        farClock.Restart();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] far field: on; recording {farRecordRays} rays a texel into {patches:N0} patches' records ({bytes / 1048576.0:0} MB) before the sweep."));
    }

    private void ReleaseFarField()
    {
        foreach (var b in new[] { farRecord, farBinsBuffer, farVerdict })
            if (!b.Equals(default(GpuBufferHandle))) RetireGpuBuffer(b);
        if (!farRecordIdsBuffer.Equals(sunNodeTraceBuffer) && !farRecordIdsBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(farRecordIdsBuffer);
        farRecord = farBinsBuffer = farVerdict = farRecordIdsBuffer = default;
        farRecordIds = Array.Empty<uint>();
        farPatchOf = null;
        farOn = farRecording = false;
    }

    // The uniform the rays and the shade take: 0 off (and while discovering), 1 recording, 2 reading.
    private Vector4 FarUniform(int discover) => new(
        !farOn || discover > 0 ? 0f : farRecording ? 1f : 2f,
        transportSpacing,
        FarKnownWeight * FarFixed,
        sunNodeSuns?.Length ?? 0);

    // The record's buffer where a shader declares it (a placeholder when off: never indexed).
    private GpuBufferHandle FarRecordBuffer => farOn ? farRecord : sunNodeAccum;
    private GpuBufferHandle FarBinsBuffer => farOn ? farBinsBuffer : sunNodeAccum;

    // The record pass, a pass at a time (immediate or in the frame): a round's texels in slices, farRecordRays / 32 passes
    // each; nothing read back. A round done, the patches judged (the next call: the frame's passes run by then); those
    // that need more give their texels another round, until none does or the texels have farRecordMax rays. Then the
    // record's last word, and the sweep.
    private void FarRecordStep(bool now)
    {
        if (farJudgePending)
        {
            FarJudgeRound();
            return;
        }
        var live = farRecordIds;
        var slice = Math.Min(sunNodeSlice, live.Length - sunNodeFirst);
        var passes = farRecordRays / 32;
        var seedPass = 4096 + farRecordRound * passes + farRecordPass;
        if (now) SunNodePassNow(farRecordIdsBuffer, sunNodeFirst, slice, farRecordPass, seedPass);
        else DispatchSunNodePass(farRecordIdsBuffer, sunNodeFirst, slice, farRecordPass, discover: 0, seedPass);
        if (++farRecordPass < passes) return;
        farRecordPass = 0;
        sunNodeFirst += slice;
        if (sunNodeFirst < live.Length) return;
        farRecordTotalRays += (long)live.Length * farRecordRays;
        farJudgePending = true;
    }

    private DispatchCommand FarJudgeCommand(int mode)
    {
        var patches = cookedPacked!.Length / 3;
        var declared = farJudgeInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        return new DispatchCommand(farJudgePipeline, (patches + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uJudge", new Vector4Uniform(new Vector4(mode, texelNoise * 0.5f, patches, FarKnownWeight * FarFixed))),
                new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                new("uCookedDims", new Vector4Uniform(cookedDims)),
                new("uFar", new Vector4Uniform(FarUniform(0))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("PatchVerdict", farVerdict),
                new ShaderBufferBinding("FarRecord", farRecord),
            }.Concat(CookedBuffers()).Where(b => declared.Contains(b.Name)).ToArray());
    }

    private void FarJudgeRound()
    {
        farJudgePending = false;
        device.WaitIdle();
        var patches = cookedPacked!.Length / 3;
        device.DispatchNow(new[] { FarJudgeCommand(0) }, "sun-nodes-far-judge");
        var verdict = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(farVerdict, 0, patches * 4).AsSpan()).ToArray();
        // Each texel's patch, as the shaders pick it (its nearest patch it can see, else its guide's), read once.
        if (farPatchOf is null)
        {
            var seen = MemoryMarshal.Cast<byte, int>(device.ReadGpuBuffer(sunNodeTexelPatch, 0, texelCount * 4).AsSpan()).ToArray();
            var guide = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(sunNodeGuidePatch, 0, texelCount * 4).AsSpan()).ToArray();
            farPatchOf = new int[texelCount];
            for (var i = 0; i < texelCount; i++) farPatchOf[i] = seen[i] >= 0 ? seen[i] : (int)guide[i] - 2;
        }
        var patchOf = farPatchOf;
        var needing = 0;
        foreach (var v in verdict) if (v != 0) needing++;
        var next = farRecordIds.Where(id => patchOf[id] >= 0 && verdict[patchOf[id]] != 0).ToArray();
        farRecordRound++;
        var raysSoFar = farRecordRound * farRecordRays;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] far field: record round {farRecordRound} ({raysSoFar} rays a texel): {patches - needing:N0} of {patches:N0} patches' far light within {texelNoise * 50:0.#}%; {next.Length:N0} texels {(next.Length > 0 && raysSoFar < farRecordMax ? "record more" : "left as they are")} ({farClock.Elapsed.TotalMinutes:0.0} min)."));
        sunNodeFirst = 0;
        sunNodePass = 0;
        if (next.Length > 0 && raysSoFar < farRecordMax)
        {
            if (!farRecordIdsBuffer.Equals(sunNodeTraceBuffer)) RetireGpuBuffer(farRecordIdsBuffer);
            farRecordIds = next;
            farRecordIdsBuffer = Own(device.CreateGpuBuffer(next.Length * 4, MemoryMarshal.AsBytes(next.AsSpan()), "sponza.far.record-ids"));
            return;
        }
        device.DispatchNow(new[] { FarJudgeCommand(1) }, "sun-nodes-far-last-word");
        device.WaitIdle();
        farRecording = false;
        farPatchOf = null;   // 64 MB a 16 M-texel scene: only the rounds need it
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] far field: recorded in {farClock.Elapsed.TotalMinutes:0.0} min ({farRecordTotalRays / 1e9:0.00} G rays, {farRecordRound} rounds); uncertain bins left to the texels' own rays; the sweep reads it."));
    }

    // A slice's passes done (the sweep's or a round's): its texels' far light into their sums, before they are read back.
    private DispatchCommand FarFinishCommand(GpuBufferHandle ids, int first, int slice, int passes)
    {
        var declared = farFinishInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        return new DispatchCommand(farFinishPipeline, (slice + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uFinish", new Vector4Uniform(new Vector4(first, slice, passes, sunNodeSuns!.Length))),
                new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                new("uCookedDims", new Vector4Uniform(cookedDims)),
                new("uFar", new Vector4Uniform(FarUniform(0))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("Texels", texelBuffer),
                new ShaderBufferBinding("NodeIds", ids),
                new ShaderBufferBinding("TexelPatch", sunNodeTexelPatch),
                new ShaderBufferBinding("NodeGuidePatch", sunNodeGuidePatch),
                new ShaderBufferBinding("NodeFarBins", farBinsBuffer),
                new ShaderBufferBinding("NodeAccum", sunNodeAccum),
                new ShaderBufferBinding("FarRecord", farRecord),
            }.Concat(CookedBuffers()).Where(b => declared.Contains(b.Name)).ToArray());
    }

    private void FarFinish(bool now, GpuBufferHandle ids, int first, int slice, int passes)
    {
        if (!farOn || farRecording) return;
        var command = FarFinishCommand(ids, first, slice, passes);
        if (now) device.DispatchNow(new[] { command }, "sun-nodes-far-finish");
        else graph.Dispatch(farFinishPass, command);
    }
}
