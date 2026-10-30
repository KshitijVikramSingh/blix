using Blix.Graphics;
using Blix.Render;

namespace Blix.Demos.TonemapLab;

// Glare: the scene convolved with the diffraction PSF of a circular aperture (six Gaussians, lab/tonemap/tmlab/glare.py), the eye's
// scatter (four Gaussians in degrees) and the aperture-blade spikes (one streak pass). The constants are copied here and into
// present.frag and held equal to results/*.json by tests/test_glare_constants.py.
//
// ONE shared pyramid: the scene is halved eight times once, and every term reads the level its width needs (a separable blur is at
// most 33 taps, so a term hundreds of pixels wide is a small blur of a heavily downsampled copy). An earlier version built a
// separate downsample chain for every term, 64 passes in all and 34 ms of isolated GPU time at 2560x1440; sharing the pyramid
// is 27 passes (--bench prints the cost; tests/test_glare_impulse.py checks the PSF that comes out against the python model).
internal static class GlareFit
{
    /// <summary>Diffraction: Gaussian widths in Airy radii (the fit's; the weights are in present.frag), and each term's mip level.</summary>
    public static readonly float[] Sigmas = { 0.350345143f, 1.21842113f, 4.03871211f, 13.3788193f, 44.239211f, 152.640498f };
    public static readonly int[] Levels = { 1, 1, 2, 3, 5, 7 };   // term 0 is the scene itself while its width is under a pixel

    /// <summary>Scatter (Stiles-Holladay, results/scatter_fit.json): widths in degrees on the screen; levels chosen for up to ~30 px per degree.</summary>
    public static readonly float[] ScatterSigmasDeg = { 0.909161469f, 2.96041278f, 9.6337886f, 19.7788583f };
    public static readonly int[] ScatterLevels = { 4, 5, 7, 8 };

    /// <summary>Aperture-blade spikes (results/spike_fit.json): total line-density amplitude over all spikes, and where the kernel starts (Airy radii).</summary>
    public const float SpikeAmplitudeTotal = 0.1724f;
    public const float SpikeKnee = 1.5f;
}

/// <summary>The aperture-blade spike pass: the scene to 1/4 resolution, then one streak pass over every spike line.</summary>

/// <summary>The shared pyramid and every glare term's passes (diffraction, scatter, spike streaks).</summary>
internal sealed class GlareGraph : IDisposable
{
    public const int PyramidLevels = 8;
    /// <summary>Below this width (full-resolution pixels) diffraction term 0 is the scene itself: a sub-pixel blur is not worth two passes.</summary>
    public const float IdentitySigmaPx = 0.9f;

    private sealed class Term
    {
        public int Level;
        public GraphResourceHandle HTarget, VTarget;
        public PassHandle HPass, VPass;
        public PipelineHandle HPipe, VPipe;
    }

    private readonly RenderGraph graph;
    private readonly IGraphicsDevice device;
    private readonly FullscreenPass fullscreen;
    private readonly GraphResourceHandle input;
    private readonly GraphResourceHandle[] pyramid = new GraphResourceHandle[PyramidLevels + 1];
    private readonly PassHandle[] downPass = new PassHandle[PyramidLevels + 1];
    private readonly PipelineHandle[] downPipe = new PipelineHandle[PyramidLevels + 1];
    private readonly Term[] diffraction = new Term[GlareFit.Sigmas.Length];
    private readonly Term[] scatter = new Term[GlareFit.ScatterSigmasDeg.Length];
    private GraphResourceHandle streakTarget;
    private PassHandle streakPass;
    private PipelineHandle streakPipe;

    public GlareGraph(IGraphicsDevice device, RenderGraph graph, GraphResourceHandle input,
        ShaderInterface down, ShaderInterface blur, ShaderInterface streak)
    {
        this.device = device;
        this.graph = graph;
        this.input = input;
        fullscreen = new FullscreenPass(device, "glare.fullscreen");
        pyramid[0] = input;
        for (var l = 1; l <= PyramidLevels; l++)
        {
            pyramid[l] = graph.ColorTarget($"pyramid.{l}", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(1f / (1 << l)));
            downPass[l] = graph.GraphicsPass($"pyramid.{l}").Target(pyramid[l], LoadOp.Clear, StoreOp.Store).Read(pyramid[l - 1]).Shader(down).Handle;
        }
        for (var i = 0; i < diffraction.Length; i++) diffraction[i] = MakeTerm($"glare.t{i}", GlareFit.Levels[i], blur);
        for (var i = 0; i < scatter.Length; i++) scatter[i] = MakeTerm($"scatter.t{i}", GlareFit.ScatterLevels[i], blur);
        streakTarget = graph.ColorTarget("spikes.streak", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(0.25f));
        streakPass = graph.GraphicsPass("spikes.streak").Target(streakTarget, LoadOp.Clear, StoreOp.Store).Read(pyramid[2]).Shader(streak).Handle;
    }

    private Term MakeTerm(string name, int level, ShaderInterface blur)
    {
        var size = new MatchSwapchainGraphSize(1f / (1 << level));
        var t = new Term { Level = level };
        t.HTarget = graph.ColorTarget($"{name}.h", TextureFormat.Rgba16F, size);
        t.VTarget = graph.ColorTarget($"{name}.v", TextureFormat.Rgba16F, size);
        t.HPass = graph.GraphicsPass($"{name}.blurH").Target(t.HTarget, LoadOp.Clear, StoreOp.Store).Read(pyramid[level]).Shader(blur).Handle;
        t.VPass = graph.GraphicsPass($"{name}.blurV").Target(t.VTarget, LoadOp.Clear, StoreOp.Store).Read(t.HTarget).Shader(blur).Handle;
        return t;
    }

    /// <summary>Everything the present pass reads, for the tone pass's declared reads.</summary>
    public IEnumerable<GraphResourceHandle> Outputs()
    {
        foreach (var t in diffraction) yield return t.VTarget;
        foreach (var t in scatter) yield return t.VTarget;
        yield return streakTarget;
    }

    public GraphResourceHandle DiffractionOutput(int i) => diffraction[i].VTarget;
    public GraphResourceHandle ScatterOutput(int i) => scatter[i].VTarget;
    public GraphResourceHandle StreakOutput => streakTarget;

    /// <summary>After graph.Compile().</summary>
    public void BuildPipelines(ShaderProgramHandle downProgram, ShaderProgramHandle blurProgram, ShaderProgramHandle streakProgram)
    {
        PipelineHandle Make(ShaderProgramHandle program, PassHandle pass, string name) => device.CreatePipeline(new PipelineDescription(
            program, FullscreenPass.Layout, PrimitiveTopology.Triangles, DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, RenderTarget: graph.GetPassSurface(pass)), name);
        for (var l = 1; l <= PyramidLevels; l++) downPipe[l] = Make(downProgram, downPass[l], $"pyramid.{l}");
        foreach (var t in diffraction.Concat(scatter))
        {
            t.HPipe = Make(blurProgram, t.HPass, "blurH");
            t.VPipe = Make(blurProgram, t.VPass, "blurV");
        }
        streakPipe = Make(streakProgram, streakPass, "streak");
    }

    private void Draw(PassHandle pass, PipelineHandle pipe, GraphResourceHandle source, byte[]? push)
    {
        var binding = new ShaderTextureBinding("uSrc", graph.GetColorTexture(source));
        graph.Pass(pass, scope => fullscreen.Draw(scope, pipe, new[] { binding }, push));
    }

    /// <summary>Record the pyramid down to <paramref name="levels"/> (what the enabled terms need).</summary>
    public void RecordPyramid(int levels)
    {
        for (var l = 1; l <= Math.Min(levels, PyramidLevels); l++) Draw(downPass[l], downPipe[l], pyramid[l - 1], null);
    }

    private void RecordTerm(Term t, float sigmaFullPx)
    {
        var sigma = Math.Max(0.3f, sigmaFullPx / (1 << t.Level));   // in texels of this level
        Draw(t.HPass, t.HPipe, pyramid[t.Level], BlurPush(1f, 0f, sigma));
        Draw(t.VPass, t.VPipe, t.HTarget, BlurPush(0f, 1f, sigma));
    }

    /// <summary>Highest pyramid level the diffraction terms need at this Airy radius (1 when term 0 is the scene itself).</summary>
    public static int DiffractionLevelsNeeded => GlareFit.Levels.Max();
    public static int ScatterLevelsNeeded => GlareFit.ScatterLevels.Max();

    /// <summary>True when diffraction term 0 is the scene itself at this Airy radius (so its output is not drawn).</summary>
    public static bool Term0IsIdentity(float airyRadiusPx) => GlareFit.Sigmas[0] * airyRadiusPx < IdentitySigmaPx;

    public void RecordDiffraction(float airyRadiusPx)
    {
        for (var i = Term0IsIdentity(airyRadiusPx) ? 1 : 0; i < diffraction.Length; i++)
            RecordTerm(diffraction[i], GlareFit.Sigmas[i] * airyRadiusPx);
    }

    public void RecordScatter(float pixelsPerDegree)
    {
        for (var i = 0; i < scatter.Length; i++) RecordTerm(scatter[i], GlareFit.ScatterSigmasDeg[i] * pixelsPerDegree);
    }

    /// <summary>Blades >= 3; rotation in radians; airyRadiusPx in full-resolution pixels; strength scales the amplitude; reachPx the streak reach.</summary>
    public void RecordSpikes(int blades, float rotation, float airyRadiusPx, float strength, float reachPx)
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
        Draw(streakPass, streakPipe, pyramid[2], push);
    }

    private static byte[] BlurPush(float dx, float dy, float sigma)
    {
        var bytes = new byte[12];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), dx);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), dy);
        BitConverter.TryWriteBytes(bytes.AsSpan(8), sigma);
        return bytes;
    }

    public void Dispose() => fullscreen.Dispose();
}
