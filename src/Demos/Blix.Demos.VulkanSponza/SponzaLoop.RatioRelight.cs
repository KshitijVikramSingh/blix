using System.Runtime.InteropServices;
using System.Globalization;
using System.Numerics;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The ratio relight (texel_ratio.comp): a sun change's first image is the new instant layer times each texel's last
// gathered-over-instant ratio, instead of the instant layer alone. The instant layer is kept when it is written (boot
// and every re-aim), the ratio taken at each landing, and applied at the next re-aim -- the gather then lands over it
// as before. Crevices, 2026-10-20, against the full gather at the new sun: 5 degrees 11.4% -> 2.3% mean error, 20
// degrees 12.8% -> 5.9%, a 60-degree jump 11.6% -> 10.2%. --no-ratio-relight: the instant layer alone.
internal sealed partial class SponzaLoop
{
    private ShaderInterface texelRatioInterface = null!;
    private PipelineHandle texelRatioPipeline;
    private GpuBufferHandle sunRatioBuffer;
    private bool ratioRelight = true;
    // The buffer holds a ratio (a landing's) rather than an instant layer.
    private bool sunRatioHeld;
    private const float RatioMax = 16f;
    // --sun-change-select T: a sun change gathers again only the texels the relit instant moved by over T (texel_ratio.comp,
    // mode 2); the rest keep it. Crytek Sponza's model (tools/gi-model/sun_change_select.py): over 10-20%, 42-48% of the
    // texels, the rest as near the full gather as a second full gather is. 0: off (every texel, as before). 0.1 by default
    // since 2026-10-25 (the coarse sweep alone: end to end every live texel 0.061 mean error against a full gather's 0.059).
    private float sunChangeSelect = 0.1f;
    private GpuBufferHandle sunLandedBuffer, changedBitsBuffer;
    // The frame mode 2 marked the texels in (-1: not this change): read once past the frames in flight.
    private long changedMarkedFrame = -1;
    private uint[]? sunChangeMask;

    private void CreateTexelRatioPipeline(Func<string[], ShaderInterface> reflect, string shaderDir)
    {
        texelRatioInterface ??= reflect(new[] { "texel_ratio.comp" });
        texelRatioPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_ratio.comp.spv")), texelRatioInterface, "texel_ratio")), "texel_ratio"));
    }

    private void RatioPass(int mode)
    {
        if (texelLiveIds is not { } live || splitLiveBuffer.Equals(default(GpuBufferHandle))) return;
        if (sunRatioBuffer.Equals(default(GpuBufferHandle)))
            sunRatioBuffer = Own(device.CreateGpuBuffer(texelCount * 4, new byte[texelCount * 4], "sponza.split.sun-ratio"));
        var select = sunChangeSelect > 0f;
        if (select && sunLandedBuffer.Equals(default(GpuBufferHandle)))
        {
            sunLandedBuffer = Own(device.CreateGpuBuffer(texelCount * 4, new byte[texelCount * 4], "sponza.split.sun-landed"));
            changedBitsBuffer = Own(device.CreateReadableGpuBuffer((texelCount + 31) / 32 * 4, "sponza.split.changed"));
        }
        if (select && mode == 2)
        {
            device.WriteMappedGpuBuffer(changedBitsBuffer, 0, new byte[(texelCount + 31) / 32 * 4]);
            changedMarkedFrame = framesRendered;
        }
        const int perDispatch = 65535 * 64;
        for (var first = 0; first < live.Length; first += perDispatch)
            SetupDispatch(sweepRatioPass, new DispatchCommand(texelRatioPipeline, (Math.Min(perDispatch, live.Length - first) + 63) / 64, 1, 1,
                new ShaderUniform[]
                {
                    new("uRatio", new Vector4Uniform(new Vector4(live.Length, mode, RatioMax, first))),
                    new("uSelect", new Vector4Uniform(new Vector4(select ? sunChangeSelect : 0f, Luminance(EffectiveSunIrradiance), lastDarkBar, 0f))),
                },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new[]
                {
                    new ShaderBufferBinding("LiveIds", splitLiveBuffer),
                    new ShaderBufferBinding("TexelSun", EnsureLivePart(true)),
                    new ShaderBufferBinding("SunRatio", sunRatioBuffer),
                    ProvenanceBinding(),
                    new ShaderBufferBinding("TexelSky", EnsureLivePart(false)),
                    new ShaderBufferBinding("SunLanded", select ? sunLandedBuffer : sunRatioBuffer),
                    new ShaderBufferBinding("Changed", select ? changedBitsBuffer : sunRatioBuffer),
                }), "texel-ratio");
    }

    // The coarse sweep begins (after the visible and changed, or without it): its list cut to the texels mode 2 marked,
    // once they are readable (the frames in flight past; on the host at once).
    private void SelectSunChangeTexels()
    {
        sunChangeMask = null;
        if (changedMarkedFrame < 0 || sunNodeTraceIds.Length == 0) return;
        if (SetupInFrame && framesRendered < changedMarkedFrame + Math.Max(1, device.MaxFramesInFlightCount) + 1) return;
        if (!SetupInFrame) device.WaitIdle();
        changedMarkedFrame = -1;
        var mask = new uint[(texelCount + 31) / 32];
        device.ReadMappedGpuBuffer(changedBitsBuffer, 0, MemoryMarshal.AsBytes(mask.AsSpan()));
        var all = sunNodeTraceIds;
        var kept = all.Where(id => (mask[id >> 5] >> (int)(id & 31) & 1u) != 0).ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] sun change select: {kept.Length:N0} of {all.Length:N0} coarse texels moved over {sunChangeSelect:0.##} of their light; the rest keep the relit instant."));
        sunChangeMask = mask;
        RetireTraceBuffer();
        sunNodeTraceIds = kept;
        sunNodeTraceBuffer = Own(SweepBuffer(Math.Max(16, kept.Length * 4), MemoryMarshal.AsBytes(kept.AsSpan()), "sponza.sun-nodes.selected"));
    }

    // The fine pick, cut the same way -- only under --sun-change-select-fine: a fine pick left out is not kept on the instant
    // layer but copied from the coarse (the copy it was flagged for getting wrong): Crytek Sponza end to end, those 0.280
    // mean error against the instant layer's 0.167 and a second full gather's 0.126; the fine set's energy 0.990.
    private bool sunChangeSelectFine;
    private uint[] SelectFine(uint[] ids)
    {
        if (!sunChangeSelectFine || sunChangeMask is not { } mask) return ids;
        var kept = ids.Where(id => (mask[id >> 5] >> (int)(id & 31) & 1u) != 0).ToArray();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] sun change select: {kept.Length:N0} of {ids.Length:N0} fine picks moved over {sunChangeSelect:0.##}."));
        return kept;
    }

    // The instant layer is in the sun part (ApplyPatchTexelsNow): relit by the held ratio if there is one, kept either way.
    private void RatioAtInstant()
    {
        if (!ratioRelight || !liveTexels) return;
        var relit = sunRatioHeld;
        RatioPass(relit ? 2 : 0);
        sunRatioHeld = false;
        if (relit)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] ratio relight: the instant layer times the last landing's gathered/instant ratio (cap {RatioMax:0})."));
    }

    // The cook landed: the gathered sun part over the instant kept for it.
    private void RatioAtLanding()
    {
        if (!ratioRelight || !liveTexels || sunRatioHeld) return;
        RatioPass(1);
        sunRatioHeld = true;
    }
}
