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

    private void CreateTexelRatioPipeline(Func<string[], ShaderInterface> reflect, string shaderDir)
    {
        texelRatioInterface = reflect(new[] { "texel_ratio.comp" });
        texelRatioPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "texel_ratio.comp.spv")), texelRatioInterface, "texel_ratio")), "texel_ratio"));
    }

    private void RatioPass(int mode)
    {
        if (texelLiveIds is not { } live || splitLiveBuffer.Equals(default(GpuBufferHandle))) return;
        if (sunRatioBuffer.Equals(default(GpuBufferHandle)))
            sunRatioBuffer = Own(device.CreateGpuBuffer(texelCount * 4, new byte[texelCount * 4], "sponza.split.sun-ratio"));
        const int perDispatch = 65535 * 64;
        for (var first = 0; first < live.Length; first += perDispatch)
            device.DispatchNow(new[] { new DispatchCommand(texelRatioPipeline, (Math.Min(perDispatch, live.Length - first) + 63) / 64, 1, 1,
                new ShaderUniform[] { new("uRatio", new Vector4Uniform(new Vector4(live.Length, mode, RatioMax, first))) },
                Array.Empty<ShaderTextureBinding>(),
                Buffers: new[]
                {
                    new ShaderBufferBinding("LiveIds", splitLiveBuffer),
                    new ShaderBufferBinding("TexelSun", EnsureLivePart(true)),
                    new ShaderBufferBinding("SunRatio", sunRatioBuffer),
                }) }, "texel-ratio");
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
