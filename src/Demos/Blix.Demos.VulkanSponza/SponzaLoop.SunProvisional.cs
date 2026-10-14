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

    private void DeclareSunProvisional(Func<string[], ShaderInterface> reflect) =>
        sunProvisionalInterface = reflect(new[] { "texel_sun_provisional.comp" });

    private void CreateSunProvisionalPipeline(string shaderDir) =>
        sunProvisionalPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_sun_provisional.comp.spv")), sunProvisionalInterface, "texel_sun_provisional")),
            "texel_sun_provisional"));

    private bool SunOnlyCook => splitCooking && sunNodeKinds.Length > 0 && sunNodeKinds.All(k => k == 1);

    // Before a sun-only cook's first sweep pass: the provisional part, read back into the landing's mirror and up.
    private void SunProvisionalNow()
    {
        if (sunProvisionalDone || !SunOnlyCook || splitSunWords is not { } old || sunNodePatchLight.Equals(default(GpuBufferHandle))) return;
        sunProvisionalDone = true;
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
                    new("uProvisional", new Vector4Uniform(new Vector4(count, first, 0f, sunNodeSuns!.Length))),
                    new("uSunIrradiance", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
                    new("uCookedGrid", new Vector4Uniform(cookedGrid)),
                    new("uCookedDims", new Vector4Uniform(cookedDims)),
                    new("uFar", new Vector4Uniform(new Vector4(blend ? 1f : 0f, transportSpacing, 0f, 0f))),
                },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new[]
                {
                    new ShaderBufferBinding("Texels", texelBuffer),
                    new ShaderBufferBinding("SunNodesLive", sunNodeLive),
                    new ShaderBufferBinding("TexelPatch", sunNodeTexelPatch),
                    new ShaderBufferBinding("SunNodePatchLight", sunNodePatchLight),
                    new ShaderBufferBinding("SunWords", words),
                    new ShaderBufferBinding("FarRecord", words),
                    new ShaderBufferBinding("PatchNeighbours", blend ? farNeighbours : words),
                }.Concat(CookedBuffers()).Where(b => declared.Contains(b.Name)).ToArray()) }, "sun-nodes-sun-provisional");
        }
        device.WaitIdle();
        var provisional = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(words, 0, texelCount * 8).AsSpan()).ToArray();
        RetireGpuBuffer(words);
        UploadSplitPart(provisional, sunPart: true);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] split light: the new sun's provisional part up from its patches' light ({live.Length:N0} texels{(blend ? ", blended over neighbours" : "")}) in {clock.Elapsed.TotalSeconds:0.0} s; the cook sharpens it as it lands."));
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
