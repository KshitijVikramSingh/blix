using Blix.Graphics;
using Blix.Render;

namespace Blix.Demos.TonemapLab;

// Glare: the scene convolved with the diffraction PSF of a circular aperture, as a sum of six Gaussians (lab/tonemap/tmlab/glare.py
// fits it; results/glare_fit.json holds the numbers, copied here and into present.frag and held equal by
// tests/test_glare_constants.py). Each term is the scene blurred by one Gaussian, taken from the resolution its width needs: a
// separable blur is at most 33 taps, so a term hundreds of pixels wide is a small blur of a heavily downsampled copy. One
// PostChain per term: some half-resolution steps, then a horizontal and a vertical blur.
internal static class GlareFit
{
    /// <summary>Diffraction: Gaussian widths in Airy radii (the fit's; the weights are in present.frag), and each term's mip level.</summary>
    public static readonly float[] Sigmas = { 0.350345143f, 1.21842113f, 4.03871211f, 13.3788193f, 44.239211f, 152.640498f };
    public static readonly int[] Levels = { 0, 0, 2, 3, 5, 7 };

    /// <summary>Scatter (Stiles-Holladay, results/scatter_fit.json): widths in degrees on the screen; levels chosen for up to ~30 px per degree.</summary>
    public static readonly float[] ScatterSigmasDeg = { 0.909161469f, 2.96041278f, 9.6337886f, 19.7788583f };
    public static readonly int[] ScatterLevels = { 4, 5, 7, 8 };

    /// <summary>Aperture-blade spikes (results/spike_fit.json): total line-density amplitude over all spikes, and where the kernel starts (Airy radii).</summary>
    public const float SpikeAmplitudeTotal = 0.1724f;
    public const float SpikeKnee = 1.5f;
}

/// <summary>The aperture-blade spike pass: the scene to 1/4 resolution, then one streak pass over every spike line.</summary>
internal sealed class SpikeChain : IDisposable
{
    private readonly PostChain chain;
    public GraphResourceHandle Output => chain.Output;

    public SpikeChain(IGraphicsDevice device, RenderGraph graph, GraphResourceHandle input, ShaderInterface down, ShaderInterface streak)
    {
        var stages = new[]
        {
            new PostStage("spikes.down1", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(0.5f), down, "uSrc"),
            new PostStage("spikes.down2", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(0.25f), down, "uSrc"),
            new PostStage("spikes.streak", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(0.25f), streak, "uSrc"),
        };
        chain = new PostChain(device, graph, input, stages, "spikes");
    }

    public void BuildPipelines(IGraphicsDevice device, ShaderProgramHandle downProgram, ShaderProgramHandle streakProgram)
    {
        chain.BuildPipelines((stage, surface) => device.CreatePipeline(new PipelineDescription(
            stage.Name.EndsWith(".streak", StringComparison.Ordinal) ? streakProgram : downProgram, FullscreenPass.Layout,
            PrimitiveTopology.Triangles, DepthState.Disabled, RasterizerState.NoCulling, new[] { BlendState.Disabled },
            RenderTarget: surface), stage.Name));
    }

    /// <summary>Record the passes. Blades >= 3; rotation in radians; airyRadiusPx in full-resolution pixels; strength scales the amplitude.</summary>
    public void Record(int blades, float rotation, float airyRadiusPx, float strength, float reachPx)
    {
        // Even N: N spikes on N/2 lines; odd N: 2N spikes on N lines. Spacing between lines is pi / lines either way.
        var lines = blades % 2 == 0 ? blades / 2 : blades;
        var spikes = 2 * lines;
        var push = new byte[28];
        BitConverter.TryWriteBytes(push.AsSpan(0), rotation + MathF.PI / blades);
        BitConverter.TryWriteBytes(push.AsSpan(4), GlareFit.SpikeAmplitudeTotal / spikes * strength);
        BitConverter.TryWriteBytes(push.AsSpan(8), airyRadiusPx / 4f);
        BitConverter.TryWriteBytes(push.AsSpan(12), GlareFit.SpikeKnee);
        BitConverter.TryWriteBytes(push.AsSpan(16), reachPx / 4f);
        BitConverter.TryWriteBytes(push.AsSpan(20), lines);
        BitConverter.TryWriteBytes(push.AsSpan(24), Math.Clamp((int)(reachPx / 4f), 16, 400));   // taps a side, one texel apart
        chain.Record((index, stage) => stage.Name.EndsWith(".streak", StringComparison.Ordinal) ? push : null);
    }

    public void Dispose() => chain.Dispose();
}

internal sealed class GaussianChains : IDisposable
{
    private readonly PostChain[] chains;
    private readonly float[] sigmas;
    private readonly int[] levels;
    private readonly ShaderInterface downInterface;
    private readonly ShaderInterface blurInterface;

    public GraphResourceHandle[] Outputs { get; }

    public GaussianChains(IGraphicsDevice device, RenderGraph graph, GraphResourceHandle input, ShaderInterface down, ShaderInterface blur,
        string name, float[] sigmas, int[] levels)
    {
        downInterface = down;
        blurInterface = blur;
        this.sigmas = sigmas;
        this.levels = levels;
        chains = new PostChain[sigmas.Length];
        Outputs = new GraphResourceHandle[sigmas.Length];
        for (var i = 0; i < sigmas.Length; i++)
        {
            var stages = new List<PostStage>();
            for (var k = 1; k <= levels[i]; k++)
                stages.Add(new PostStage($"{name}{i}.down{k}", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(1f / (1 << k)), down, "uSrc"));
            var size = new MatchSwapchainGraphSize(1f / (1 << levels[i]));
            stages.Add(new PostStage($"{name}{i}.blurH", TextureFormat.Rgba16F, size, blur, "uSrc"));
            stages.Add(new PostStage($"{name}{i}.blurV", TextureFormat.Rgba16F, size, blur, "uSrc"));
            chains[i] = new PostChain(device, graph, input, stages, $"{name}{i}");
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

    /// <summary>Record every term's passes. <paramref name="pixelsPerUnit"/> converts a sigma's unit (Airy radii, or degrees) to full-resolution pixels.</summary>
    public void Record(float pixelsPerUnit)
    {
        for (var i = 0; i < sigmas.Length; i++)
        {
            var sigma = Math.Max(0.3f, sigmas[i] * pixelsPerUnit / (1 << levels[i]));
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
