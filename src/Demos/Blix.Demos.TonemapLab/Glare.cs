using Blix.Graphics;
using Blix.Render;

namespace Blix.Demos.TonemapLab;

// Glare: the scene convolved with the diffraction PSF of a circular aperture, as a sum of six Gaussians (lab/tonemap/tmlab/glare.py
// fits it; results/glare_fit.json holds the numbers, copied here and into present.frag and held equal by
// tests/test_glare_constants.py). Each term is the scene blurred by one Gaussian, taken from the resolution its width needs: a
// separable blur is at most 33 taps, so a term hundreds of pixels wide is a small blur of a heavily downsampled copy. One
// PostChain per term: some half-resolution steps, then a horizontal and a vertical blur.
internal sealed class GlareChains : IDisposable
{
    /// <summary>Gaussian widths in Airy radii (the fit's; the weights are in present.frag). Each term is blurred at the mip level in Levels (0 = full resolution).</summary>
    public static readonly float[] Sigmas = { 0.350345143f, 1.21842113f, 4.03871211f, 13.3788193f, 44.239211f, 152.640498f };
    private static readonly int[] Levels = { 0, 0, 2, 3, 5, 7 };

    private readonly PostChain[] chains = new PostChain[6];
    private readonly ShaderInterface downInterface;
    private readonly ShaderInterface blurInterface;

    public GraphResourceHandle[] Outputs { get; } = new GraphResourceHandle[6];

    public GlareChains(IGraphicsDevice device, RenderGraph graph, GraphResourceHandle input, ShaderInterface down, ShaderInterface blur)
    {
        downInterface = down;
        blurInterface = blur;
        for (var i = 0; i < 6; i++)
        {
            var stages = new List<PostStage>();
            for (var k = 1; k <= Levels[i]; k++)
                stages.Add(new PostStage($"glare{i}.down{k}", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(1f / (1 << k)), down, "uSrc"));
            var size = new MatchSwapchainGraphSize(1f / (1 << Levels[i]));
            stages.Add(new PostStage($"glare{i}.blurH", TextureFormat.Rgba16F, size, blur, "uSrc"));
            stages.Add(new PostStage($"glare{i}.blurV", TextureFormat.Rgba16F, size, blur, "uSrc"));
            chains[i] = new PostChain(device, graph, input, stages, $"glare{i}");
            Outputs[i] = chains[i].Output;
        }
    }

    /// <summary>After graph.Compile().</summary>
    public void BuildPipelines(IGraphicsDevice device, ShaderProgramHandle downProgram, ShaderProgramHandle blurProgram)
    {
        foreach (var chain in chains)
        {
            chain.BuildPipelines((stage, surface) => device.CreatePipeline(new PipelineDescription(
                stage.Name.Contains(".down", StringComparison.Ordinal) ? downProgram : blurProgram, FullscreenPass.Layout,
                PrimitiveTopology.Triangles, DepthState.Disabled, RasterizerState.NoCulling, new[] { BlendState.Disabled },
                RenderTarget: surface), stage.Name));
        }
    }

    /// <summary>Record every term's passes. <paramref name="airyRadiusPx"/> is the Airy radius in full-resolution pixels.</summary>
    public void Record(float airyRadiusPx)
    {
        for (var i = 0; i < 6; i++)
        {
            var sigma = Math.Max(0.3f, Sigmas[i] * airyRadiusPx / (1 << Levels[i]));
            var term = i;
            chains[i].Record((index, stage) =>
            {
                if (stage.Name.EndsWith(".blurH", StringComparison.Ordinal)) return Push(1f, 0f, sigma);
                if (stage.Name.EndsWith(".blurV", StringComparison.Ordinal)) return Push(0f, 1f, sigma);
                return null;
            });
        }
    }

    private static byte[] Push(float dx, float dy, float sigma)
    {
        var bytes = new byte[12];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), dx);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), dy);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), sigma);
        return bytes;
    }

    public void Dispose()
    {
        foreach (var chain in chains) chain?.Dispose();
    }
}
