using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --far-transport (plan.md, "Cook the transport, not the light", N3): the far field's record cooked as TRANSPORT -- per
// patch per bin, what its texels' far rays met (texel_nodes_shade.glsl, FarTransport), no light in it -- and relit for
// each cook's lights (texel_far_relight.comp) into the radiance record the sweep reads. The first cook records it; a cook
// after a sun move keeps it and only relights: no far ray traced again. The patches' solve and the texels' sun bits run
// for the new sun as before, and the sweep's near rays (short: past the reach the record answers).
internal sealed partial class SponzaLoop
{
    private bool farTransportMode;
    private GpuBufferHandle farTransportRecord, farPatchAlbedo;
    private int farTransportPatches;
    private bool farTransportRecorded, farRelit, farRecordWasKept;
    private ShaderInterface farRelightInterface = null!;
    private PipelineHandle farRelightPipeline;
    private const int FarSlots = 3, FarTransportStride = 2 * FarSlots + 3;
    // The complete lists (SponzaLoop.Transport.cs, BuildFarLists): the transport cook's couplings and sky escapes binned
    // per patch, built once for the session's patches; the relight reads them instead of the slots. The 3-slot record named
    // ~14% of a bin's hits on Cornell (a 24-degree bin spans many patches) and lit the rest at the slots' mean: each bin off
    // by ~14%, shared by its patch's texels -- clouds on the ceiling and walls (2026-10-14).
    private GpuBufferHandle farListStart, farListIds, farListWeights;
    private int farListPatches;
    // The lists on the CPU: the GPU's copy is let go after each relight (445 MB on Sponza, read for a fraction of a second)
    // and uploaded again for the next one, rather than built again (~15 s).
    private (uint[] Start, uint[] Ids, float[] Weights)? farListsCpu;
    // A list's reach: couplings nearer than this are left to the texels' own rays (a texel's reach is 0.25-1.2 m).
    private float FarListReach => 3f * transportSpacing;

    private void ReadFarTransportArgs(AppArgs args)
    {
        farTransportMode = args.Flag("far-transport");
    }

    private void DeclareFarRelight(Func<string[], ShaderInterface> reflect) =>
        farRelightInterface = reflect(new[] { "texel_far_relight.comp" });

    private void CreateFarRelightPipeline(string shaderDir) =>
        farRelightPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_far_relight.comp.spv")), farRelightInterface, "texel_far_relight")), "texel_far_relight"));

    // BeginFarField, transport mode: the record kept from an earlier cook of the same patches (relit, not traced), or a new
    // one to record; the patches' albedo; every patch's direct sun for this cook (PatchDirectNow, before the relight).
    private bool BeginFarTransport(int patches, int suns)
    {
        if (!farTransportMode) return false;
        farRelit = false;
        if (farTransportPatches != patches && !farTransportRecord.Equals(default(GpuBufferHandle)))
        {
            RetireGpuBuffer(farTransportRecord);
            farTransportRecord = default;
            farTransportRecorded = false;
        }
        // The patches' albedo, which the relight reads, once for these patches.
        if (farTransportPatches != patches || farPatchAlbedo.Equals(default(GpuBufferHandle)))
        {
            var albedo = transportSolve!.Albedo.Select(a => new Vector4(a, 0f)).ToArray();
            if (!farPatchAlbedo.Equals(default(GpuBufferHandle))) RetireGpuBuffer(farPatchAlbedo);
            farPatchAlbedo = Own(device.CreateGpuBuffer(albedo.Length * 16, MemoryMarshal.AsBytes(albedo.AsSpan()), "sponza.far.patch-albedo"));
            farTransportPatches = patches;
        }
        // With the complete lists the slots' record is never written: none allocated (248 MB on Sponza).
        if (farTransportRecord.Equals(default(GpuBufferHandle)) && transportBuildFarLists is null)
        {
            var bytes = checked((int)((long)patches * FarBins * FarTransportStride * 4));
            farTransportRecord = Own(device.CreateGpuBuffer(bytes, new byte[bytes], "sponza.far.transport"));
            farTransportRecorded = false;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] far transport: a light-free record of {patches:N0} patches ({bytes / 1048576.0:0} MB) to record, then relit for each cook's lights."));
        }
        // Every patch's direct sun for this cook's suns (texel_patch_direct.comp), before the relight reads it.
        if (!patchDirectSums.Equals(default(GpuBufferHandle))) RetireGpuBuffer(patchDirectSums);
        var words = patches * (suns + 1);
        patchDirectSums = Own(device.CreateGpuBuffer(words * 4, new byte[words * 4], "sponza.far.patch-direct"));
        patchDirectDone = false;
        // With the complete lists (the transport cook's couplings, binned) the relight writes every bin of the record: the
        // texels' record pass (Sponza: ~6 min) would be overwritten whole, so it is not run at all. Without them, the slots'
        // record is kept from an earlier cook, or recorded.
        if (transportBuildFarLists is not null || farTransportRecorded)
        {
            Console.WriteLine(transportBuildFarLists is not null
                ? "[VulkanSponza] far transport: the far record from the patches' complete lists -- relit for these lights, no far ray traced."
                : "[VulkanSponza] far transport: the record kept from the last cook -- relit for these lights, no far ray traced.");
            farRecordWasKept = true;
            return true;   // no record pass
        }
        farRecordWasKept = false;
        return false;
    }

    private float FarTransportUniform => farTransportMode && farRecording ? 1f : 0f;

    // Before the sweep's first pass: every patch's direct sun, then the record relit into the radiance record.
    private void FarRelightNow()
    {
        if (!farTransportMode || farRelit || !farOn || farRecording) return;
        PatchDirectNow();
        var clock = Stopwatch.StartNew();
        var keptRecord = farTransportRecorded && farRecordWasKept;
        farTransportRecorded = true;
        farRelit = true;
        var patches = farTransportPatches;
        var suns = sunNodeSuns!.Length;
        var declared = farRelightInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        var lists = EnsureFarLists(patches);
        var items = patches * FarBins;
        // The first cook's record was traced too: the relit light compared bin by bin before it is overwritten.
        var compare = !keptRecord;
        var stats = Own(device.CreateGpuBuffer(20 * 4, new byte[20 * 4], "sponza.far.relight-stats"));
        var groups = (items + 63) / 64;
        var groupsX = Math.Min(groups, 65535);
        device.DispatchNow(new[] { new DispatchCommand(farRelightPipeline, groupsX, (groups + groupsX - 1) / groupsX, 1,
            new ShaderUniform[]
            {
                new("uRelight", new Vector4Uniform(new Vector4(patches, suns, FarKnownWeight * FarFixed, FarBins))),
                new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
                new("uSky", new Vector4Uniform(new Vector4(1f, 1f, 0f, 0f))),
                new("uFar", new Vector4Uniform(FarUniform(0))),
                new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                new("uCookedDims", new Vector4Uniform(cookedDims)),
                new("uCompare", new Vector4Uniform(new Vector4(compare ? 1f : 0f, lists ? 1f : 0f, transportRays * MathF.PI * FarFixed, 0f))),
            },
            new[] { new ShaderTextureBinding("uSkyRadiance", envCubeTexture) },
            Buffers: new[]
            {
                // The slots' record, read only without the lists (none allocated with them: a placeholder).
                new ShaderBufferBinding("FarTransport", farTransportRecord.Equals(default(GpuBufferHandle)) ? stats : farTransportRecord),
                new ShaderBufferBinding("SunNodePatchLight", sunNodePatchLight),
                new ShaderBufferBinding("PatchAlbedo", farPatchAlbedo),
                new ShaderBufferBinding("PatchDirectSums", patchDirectSums),
                new ShaderBufferBinding("SunNodes", sunNodeSunsBuffer),
                new ShaderBufferBinding("FarRecord", farRecord),
                new ShaderBufferBinding("RelightStats", stats),
                new ShaderBufferBinding("FarListStart", lists ? farListStart : stats),
                new ShaderBufferBinding("FarListIds", lists ? farListIds : stats),
                new ShaderBufferBinding("FarListWeights", lists ? farListWeights : stats),
            }.Concat(CookedBuffers()).Where(b => declared.Contains(b.Name)).ToArray()) }, "sun-nodes-far-relight");
        device.WaitIdle();
        // The judge's last word on the relit record: a bin whose list leaves its light too uncertain is made unknown.
        if (lists)
        {
            device.DispatchNow(new[] { FarJudgeCommand(1) }, "sun-nodes-far-relight-last-word");
            device.WaitIdle();
        }
        if (compare)
        {
            var s = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(stats, 0, 20 * 4).AsSpan()).ToArray();
            string[] names = lists ? new[] { "lists: with hits", "-", "-", "-", "lists: sky only" } : new[] { "unnamed <10%", "unnamed 10-30%", "unnamed 30-60%", "unnamed >60%", "sky only" };
            for (var b = 0; b < 5; b++)
            {
                var n = Math.Max(s[b * 4], 1u);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] far transport, relit against traced ({names[b]}): {s[b * 4]:N0} bins, |off| {s[b * 4 + 1] / 10.0 / n:0.0}% on average, {(s[b * 4 + 2] / 1000.0 / n - 1.0) * 100:+0.0;-0.0}% signed, unnamed share {s[b * 4 + 3] / 10.0 / n:0.0}%."));
            }
        }
        RetireGpuBuffer(stats);
        // The lists' GPU copy let go until the next relight (the CPU's kept).
        foreach (var b in new[] { farListStart, farListIds, farListWeights })
            if (!b.Equals(default(GpuBufferHandle))) RetireGpuBuffer(b);
        farListStart = farListIds = farListWeights = default;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] far transport: the far record relit for these lights in {clock.Elapsed.TotalMilliseconds:0} ms ({items:N0} patch-bins)."));
    }

    // The lists, built once for these patches (seconds on the CPU) and kept for every relight.
    private bool EnsureFarLists(int patches)
    {
        if (transportBuildFarLists is null) return false;
        if (farListPatches == patches && !farListStart.Equals(default(GpuBufferHandle))) return true;
        var clock = Stopwatch.StartNew();
        var built = farListsCpu is not null && farListPatches == patches;
        if (!built) farListsCpu = transportBuildFarLists(FarListReach);
        var (start, ids, weights) = farListsCpu!.Value;
        foreach (var b in new[] { farListStart, farListIds, farListWeights })
            if (!b.Equals(default(GpuBufferHandle))) RetireGpuBuffer(b);
        farListStart = Own(device.CreateGpuBuffer(start.Length * 4, MemoryMarshal.AsBytes(start.AsSpan()), "sponza.far.list-start"));
        farListIds = Own(device.CreateGpuBuffer(Math.Max(ids.Length, 1) * 4, MemoryMarshal.AsBytes(ids.AsSpan()), "sponza.far.list-ids"));
        farListWeights = Own(device.CreateGpuBuffer(Math.Max(weights.Length, 1) * 4, MemoryMarshal.AsBytes(weights.AsSpan()), "sponza.far.list-weights"));
        farListPatches = patches;
        long sky = 0;
        foreach (var id in ids) if ((id & 0x80000000u) != 0) sky++;
        // --live-texels: not kept on the CPU (445 MB on Sponza) -- read from the GI bundle again for the next light.
        if (liveTexels) farListsCpu = null;
        device.WaitIdle();
        WriteGpuMemory("with the far lists up");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] far transport: complete lists for {patches:N0} patches -- {ids.Length - sky:N0} couplings past {FarListReach:0.00} m and {sky:N0} sky directions ({(ids.Length * 8L + start.Length * 4L) / 1048576.0:0} MB) {(built ? "uploaded again" : "built")} in {clock.Elapsed.TotalSeconds:0.0} s."));
        return true;
    }

}
