using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// A sun moved (the user's, 2026-10-14: curtains and leaves held the old sun's brightness for minutes after it moved off
// them, and a second move waited for the first's whole cook):
//   PROVISIONAL  a sun-only cook's first pass writes every live texel's sun part from the patches' light for the NEW sun
//                (texel_sun_provisional.comp: its patch's incident light, blended over its patch's neighbours), so no
//                texel keeps the old sun; the cook's slices overwrite it, sharper, as they land.
//   RESTART      a sun moved while a sun-only cook traces abandons that cook (its partial light never cached) and starts
//                the newest sun's. Before tracing (the solve, the patch pick: seconds) the move waits, as before.
internal sealed partial class SponzaLoop
{
    private ShaderInterface sunProvisionalInterface = null!;
    private PipelineHandle sunProvisionalPipeline;
    private bool sunProvisionalDone;

    private void DeclareSunProvisional(Func<string[], ShaderInterface> reflect)
    {
        sunProvisionalInterface = reflect(new[] { "texel_sun_provisional.comp" });
        liveProvisionalPass = graph.ComputePass("live-provisional").Shader(sunProvisionalInterface).Handle;
        splitScatterInterface = reflect(new[] { "split_scatter.comp" });
        splitScatterPass = graph.ComputePass("split-scatter").Shader(splitScatterInterface).Handle;
    }

    private void CreateSunProvisionalPipeline(string shaderDir)
    {
        sunProvisionalPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_sun_provisional.comp.spv")), sunProvisionalInterface, "texel_sun_provisional")),
            "texel_sun_provisional"));
        splitScatterPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "split_scatter.comp.spv")), splitScatterInterface, "split_scatter")), "split_scatter"));
    }

    private bool SunOnlyCook => splitCooking && sunNodeKinds.Length > 0 && sunNodeKinds.All(k => k == 1);

    // Before a sun-only cook's first sweep pass: the provisional part, read back into the landing's mirror and up.
    // --live-texels: every column's (the sky's too), the moment the live patch light rests -- each texel's coarse light
    // for the lights as they are, so nothing waits for the gather's sweep (SponzaLoop.LivePatchLight.cs).
    private void SunProvisionalNow()
    {
        // --live-texels writes its own the moment the live patch light rests (LiveProvisionalNow), not here.
        if (liveTexels || sunProvisionalDone || sunNodePatchLight.Equals(default(GpuBufferHandle))) return;
        if (!SunOnlyCook || splitSunWords is null) return;
        sunProvisionalDone = true;
        ProvisionalPart(0, sunPart: true, sunNodeSuns!.Length, sunNodeLive, sunNodeTexelPatch, sunNodePatchLight);
    }

    // Column k's part from the patches' light: over the part as it is (or nothing yet), per unit of the sun's irradiance
    // for the sun's, as it is for the sky's.
    private void ProvisionalPart(int column, bool sunPart, int columns, GpuBufferHandle liveIds, GpuBufferHandle texelPatch, GpuBufferHandle patchLight)
    {
        if (liveTexels)
        {
            ProvisionalPartLive(column, sunPart, columns, liveIds, texelPatch, patchLight);
            return;
        }
        var old = (sunPart ? splitSunWords : splitSkyWords) ?? new uint[texelCount * 2];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var live = texelLiveIds!;
        var words = Own(device.CreateGpuBuffer(checked(texelCount * 8), MemoryMarshal.AsBytes(old.AsSpan(0, texelCount * 2)), "sponza.split.sun-provisional"));
        var blend = farOn && !farNeighbours.Equals(default(GpuBufferHandle));
        var declared = sunProvisionalInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        const int chunk = 65535 * 64;
        for (var first = 0; first < live.Length; first += chunk)
        {
            var count = Math.Min(chunk, live.Length - first);
            device.DispatchNow(new[] { new DispatchCommand(sunProvisionalPipeline, (count + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uProvisional", new Vector4Uniform(new Vector4(count, first, column, columns))),
                    new("uSunIrradiance", new Vector4Uniform(new Vector4(sunPart ? EffectiveSunIrradiance : Vector3.One, 0f))),
                    new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                    new("uCookedDims", new Vector4Uniform(cookedDims)),
                    new("uFar", new Vector4Uniform(new Vector4(blend ? 1f : 0f, transportSpacing, 0f, 0f))),
                },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new[]
                {
                    new ShaderBufferBinding("Texels", texelBuffer),
                    new ShaderBufferBinding("SunNodesLive", liveIds),
                    new ShaderBufferBinding("TexelPatch", texelPatch),
                    new ShaderBufferBinding("SunNodePatchLight", patchLight),
                    new ShaderBufferBinding("SunWords", words),
                    new ShaderBufferBinding("FarRecord", words),
                    new ShaderBufferBinding("PatchNeighbours", blend ? farNeighbours : words),
                }.Concat(CookedBuffers()).Where(b => declared.Contains(b.Name)).ToArray()) }, "sun-nodes-sun-provisional");
        }
        device.WaitIdle();
        var provisional = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(words, 0, texelCount * 8).AsSpan()).ToArray();
        RetireGpuBuffer(words);
        UploadSplitPart(provisional, sunPart);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] split light: the {(sunPart ? "sun's" : "sky's")} provisional part up from its patches' light ({live.Length:N0} texels{(blend ? ", blended over neighbours" : "")}) in {clock.Elapsed.TotalSeconds:0.0} s; the cook sharpens it as it lands."));
    }

    // --live-texels: into the part's buffer in place, in the frame -- no copy up, no wait, no read back. The round trip
    // (128 MB each way, the GPU idled first) took up to 8 s a part once memory paged (2026-10-16).
    private PassHandle liveProvisionalPass;

    private void ProvisionalPartLive(int column, bool sunPart, int columns, GpuBufferHandle liveIds, GpuBufferHandle texelPatch, GpuBufferHandle patchLight)
    {
        var live = texelLiveIds!;
        var part = EnsureLivePart(sunPart);
        var declared = sunProvisionalInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        const int chunk = 65535 * 64;
        for (var first = 0; first < live.Length; first += chunk)
        {
            var count = Math.Min(chunk, live.Length - first);
            graph.Dispatch(liveProvisionalPass, new DispatchCommand(sunProvisionalPipeline, (count + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uProvisional", new Vector4Uniform(new Vector4(count, first, column, columns))),
                    new("uSunIrradiance", new Vector4Uniform(new Vector4(sunPart ? EffectiveSunIrradiance : Vector3.One, 0f))),
                    new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                    new("uCookedDims", new Vector4Uniform(cookedDims)),
                    new("uFar", new Vector4Uniform(Vector4.Zero)),
                },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new[]
                {
                    new ShaderBufferBinding("Texels", texelBuffer),
                    new ShaderBufferBinding("SunNodesLive", liveIds),
                    new ShaderBufferBinding("TexelPatch", texelPatch),
                    new ShaderBufferBinding("SunNodePatchLight", patchLight),
                    new ShaderBufferBinding("SunWords", part),
                    new ShaderBufferBinding("FarRecord", part),
                    new ShaderBufferBinding("PatchNeighbours", part),
                }.Concat(CookedBuffers()).Where(b => declared.Contains(b.Name)).ToArray()));
        }
        splitComposeDirty = true;
        splitComposeAgainFrame = framesRendered + 1;
    }

    // SplitSunChanged while a cook runs: a sun-only cook that is tracing gives way to the newest sun.
    private bool RestartSunCook()
    {
        if (!SunOnlyCook || sunNodeStage != SunNodeStage.Tracing) return false;
        device.WaitIdle();
        ResetSunNodeRefine();
        sunNodeRefineRays = 0;
        ReleaseSunNodeCook();
        sunNodeResults = Array.Empty<uint[]>();
        sunNodeDirsAsked = null;
        sunNodeReadback = false;
        sunNodeFirst = 0;
        sunNodePass = 0;
        splitCooking = false;
        sunNodeStage = SunNodeStage.Off;
        Console.WriteLine("[VulkanSponza] split light: the sun moved again while its part cooked -- that cook abandoned (nothing of it cached), the newest sun's starts.");
        return true;
    }
}
