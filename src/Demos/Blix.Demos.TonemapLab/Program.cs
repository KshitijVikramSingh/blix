using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Blix;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Demos.TonemapLab;

// The tonemap lab's interactive end: a procedural panoramic world pushed through auto-exposure (or a manual EV), white
// balance and a choice of tone mappers (Blix's four, and GT7 with the lab's tuned parameters), to an SDR or an HDR window.
// The python lab (lab/tonemap) fits things and writes the specs; this is where they are looked at, with sliders.
//
//   ./blix run Blix.Demos.TonemapLab                  an SDR window
//   BLIX_HDR=1 ./blix run Blix.Demos.TonemapLab       an HDR window: RGBA16F in extended-linear sRGB, 1.0 = the UI's white,
//                                                     headroom above it (the overlay shows what the display is granting).
//   --selftest out.pfm    render the hue ramp through GT7 to frame-buffer values, write them, exit; the lab compares them with
//                         gt7.py (lab/tonemap/tests/test_glsl_parity.py).
//   --capture stem --capture-frame N    write what the window would show (stem.pfm, stem.png), exit.
//   --adaptation-selftest out.json      run the adaptation model through two scripted scenes and write S and A (no window).
//   --meter-selftest in.pfm --meter-out out.txt    the trimmed meter of a captured frame (no window).
//   --hdri name           start in an environment map (lab/tonemap/data/hdri, fetched by tools/fetch_hdris.py); --hdri-dir to point elsewhere.
//   --glare 0..1          glare mix (default 1), --airy px the PSF's Airy radius (default 2).
//   --scatter x           scatter glare amount (1 = the standard's, default); --tunnel-ev EV sets how far below the world the tunnel is.
//   --pan deg/s           pan the camera automatically; --shutter 0..1 sets the motion-blur shutter (0 = off).
//   --awb off             start with auto white balance off.
//   --awb-selftest in.pfm --awb-out out.txt      the auto white balance's measure, cast and settled state of a captured frame (no window).
//   --hdri-selftest in.hdr --hdri-out out.txt    the loader's weighted meter and a few raw pixels (no window).
//   (the three above are held to the python by lab/tonemap/tests/test_csharp_parity.py)
//
// Left drag looks around, the wheel zooms, Up/Down step exposure, E toggles auto-exposure, R resets the view, Escape quits.
// "Look at the tunnel" and "Look at the sun" are instant camera cuts: watch the exposure follow. The overlay (`) has the rest.
public static class Program
{
    public static int Main(string[] args) => BlixApps.Main(args, Run);

    private static int Run(AppArgs args)
    {
        if (args.Flag("white-balance-selftest"))
        {
            Console.WriteLine(WhiteBalance.SelfTest());
            return 0;
        }
        if (args.String("adaptation-selftest") is { } adaptationOut) return SelfTests.Adaptation(adaptationOut);
        if (args.String("meter-selftest") is { } meterIn) return SelfTests.MeterOfFrame(meterIn, args.String("meter-out") ?? "meter.txt");
        if (args.String("awb-selftest") is { } awbIn) return SelfTests.AwbOfFrame(awbIn, args.String("awb-out") ?? "awb.txt");
        if (args.String("hdri-selftest") is { } hdriIn) return SelfTests.HdriOfFile(hdriIn, args.String("hdri-out") ?? "hdri.txt");

        var loop = new LabLoop(args);
        using var window = new Window(loop, WindowOptions.FromArgs(args, new WindowOptions("Blix — Tonemap Lab", 1280, 720)));
        window.Run();
        return 0;
    }
}

internal static class SelfTests
{
    // Two scripted scenes through AdaptationState, written as S and A per step, for adaptation.py to recompute: an 8 EV tunnel
    // at 20 Hz, and a 20 EV drop held for an hour at 0.5 Hz (the one the 12 EV span matters in).
    public static int Adaptation(string path)
    {
        var p = new AdaptationParams();
        var json = new StringBuilder("{");
        Series(json, "tunnel", 0.05, 800, t => t < 10.0 ? 0.0 : t < 18.0 ? -8.0 : 0.0, p, first: true);
        Series(json, "deep_night", 2.0, 1800, t => t < 10.0 ? 0.0 : -20.0, p, first: false);
        json.Append('}');
        File.WriteAllText(path, json.ToString());
        Console.WriteLine($"[tonemap-lab] wrote {path}");
        return 0;
    }

    private static void Series(StringBuilder json, string name, double dt, int steps, Func<double, double> level, AdaptationParams p, bool first)
    {
        var state = new AdaptationState();
        state.Reset(level(0.0));
        var s = new double[steps];
        var a = new double[steps];
        for (var i = 0; i < steps; i++)
        {
            state.Observe(level(i * dt));
            state.Step(dt, p);
            (s[i], a[i]) = (state.Short, state.Long);
        }
        string Join(double[] v) => string.Join(",", v.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
        json.Append(first ? "" : ",").Append($"\"{name}\":{{\"S\":[{Join(s)}],\"A\":[{Join(a)}]}}");
    }

    // The auto white balance on a captured frame, for awb.py to recompute: the measured mean, its cast, and the state after
    // 100 steps of 0.1 s at a bright and at a dark meter level, from D65, with the gains that state applies.
    public static int AwbOfFrame(string pfmPath, string outPath)
    {
        var (rgb, w, h) = ReadPfm(pfmPath);
        var rgba = new float[w * h * 4];
        for (var i = 0; i < w * h; i++) { for (var c = 0; c < 3; c++) rgba[i * 4 + c] = rgb[i * 3 + c]; rgba[i * 4 + 3] = 0f; }
        var p = new AwbParams();
        string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var mean = AutoWhiteBalance.Measure(rgba, w * h, p, useAlphaAsSky: false);
        var cast = AutoWhiteBalance.CastOf(mean, p);
        var lines = new List<string> { $"mean {R(mean.R)} {R(mean.G)} {R(mean.B)}", $"cast {R(cast.Mired)} {R(cast.Tint)}" };
        foreach (var ev in new[] { -3.0, -7.0 })
        {
            var state = new AutoWhiteBalance();
            state.Reset(AutoWhiteBalance.D65Mired, 0.0);
            for (var i = 0; i < 100; i++) state.Step(0.1, cast, ev, p);
            var g = state.Gains(p);
            lines.Add($"state {R(ev)} {R(state.Mired)} {R(state.Tint)} {R(g.R)} {R(g.G)} {R(g.B)}");
        }
        File.WriteAllLines(outPath, lines);
        Console.WriteLine($"[tonemap-lab] wrote {outPath}");
        return 0;
    }

    public static int MeterOfFrame(string pfmPath, string outPath)
    {
        var (rgb, _, _) = ReadPfm(pfmPath);
        var hist = Meter.Histogram(rgb);
        var lines = new List<string>();
        foreach (var trim in new[] { 0.0, 0.03, 0.09, 0.15 })
        {
            var ev = Meter.TrimmedEv(hist, trim);
            lines.Add(trim.ToString("R", CultureInfo.InvariantCulture) + " " + ev.ToString("R", CultureInfo.InvariantCulture));
        }
        File.WriteAllLines(outPath, lines);
        Console.WriteLine($"[tonemap-lab] wrote {outPath}");
        return 0;
    }

    // What the demo's HDRI loader sees in a .hdr: the solid-angle-weighted trimmed meter (what it normalises by) and a few raw pixels,
    // for hdri.py to recompute with its own Radiance reader. The two decoders (stb here, hdri.py's) must agree on the values.
    public static int HdriOfFile(string path, string outPath)
    {
        var hdr = ImageLoader.LoadRgba32F(path);
        var lines = new List<string>
        {
            "size " + hdr.Width.ToString(CultureInfo.InvariantCulture) + " " + hdr.Height.ToString(CultureInfo.InvariantCulture),
            "meter " + Meter.TrimmedEv(Meter.HistogramEquirect(hdr.Pixels, hdr.Width, hdr.Height), 0.09).ToString("R", CultureInfo.InvariantCulture),
        };
        foreach (var (x, y) in new[] { (10, 10), (1024, 512), (500, 300), (1500, 800), (2047, 1023), (700, 250) })
        {
            var i = (y * hdr.Width + x) * 4;
            lines.Add("pixel " + x.ToString(CultureInfo.InvariantCulture) + " " + y.ToString(CultureInfo.InvariantCulture) + " "
                      + string.Join(" ", Enumerable.Range(0, 3).Select(c => hdr.Pixels[i + c].ToString("R", CultureInfo.InvariantCulture))));
        }
        File.WriteAllLines(outPath, lines);
        Console.WriteLine($"[tonemap-lab] wrote {outPath}");
        return 0;
    }

    // An RGB PFM (what BLIX_SHOT_HDR and PfmWriter produce): three little-endian floats a pixel, bottom row first. Row order
    // does not matter to a histogram, so it is not undone.
    private static (float[] Rgb, int Width, int Height) ReadPfm(string path)
    {
        using var stream = File.OpenRead(path);
        string Line()
        {
            var sb = new StringBuilder();
            int c;
            while ((c = stream.ReadByte()) is >= 0 and not '\n') sb.Append((char)c);
            return sb.ToString();
        }
        if (Line() != "PF") throw new InvalidDataException($"{path}: not an RGB PFM");
        var size = Line().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (w, h) = (int.Parse(size[0], CultureInfo.InvariantCulture), int.Parse(size[1], CultureInfo.InvariantCulture));
        Line();
        var bytes = new byte[w * h * 12];
        stream.ReadExactly(bytes);
        var rgb = new float[w * h * 3];
        Buffer.BlockCopy(bytes, 0, rgb, 0, bytes.Length);
        return (rgb, w, h);
    }
}

internal sealed class LabLoop : IGameLoop, IDebuggable, IDisposable
{
    public string DebugName => "tonemap-lab";

    private static readonly string[] Operators = { "Blix ACES", "Blix AgX", "Blix Reinhard", "Blix neutral", "GT7" };
    private static readonly string[] Patterns = { "scene", "EDR patches (raw)", "hue ramp (through the pipeline)" };
    private static readonly string[] Presets = { "published GT7", "tuned for HDR (floor 0.30)", "custom (the sliders)" };

    // The two GT7 parameter sets. The tuned one is lab/tonemap/results/hdr_tuned.json, copied here: a fit run elsewhere,
    // chosen by eye on an XDR display, and held by that file's test.
    private static readonly Gt7Set Published = new(0.25f, 0.538f, 0.444f, 1.280f, 0.6f, 0.98f, 1.16f, 0f);
    private static readonly Gt7Set Tuned = new(0.15621057f, 0.54029137f, 0.24963356f, 1.27967033f, 0.97293870f, 0.87544020f, 1.25911918f, 2.25951590f);

    private const int MeterEveryFrames = 6;   // the meter pass is read back about ten times a second at 60 Hz

    private readonly string? capturePath;
    private readonly string? hdriDirectory;
    private readonly string? startHdri;
    private readonly int captureFrame;
    private readonly string? selfTestPath;
    private readonly bool trace;          // --trace: one line per meter reading, for watching the adaptation without a person
    private readonly float cutAtSeconds;  // --cut-at S: an instant camera cut to the tunnel after S seconds
    private bool cutDone;
    private readonly float selfTestPeak;
    private readonly int selfTestPreset;

    private IGraphicsDevice device = null!;
    private IRenderHost host = null!;
    private RenderGraph graph = null!;
    private FullscreenPass fullscreen = null!;
    private GraphResourceHandle sceneHandle;
    private GraphResourceHandle toneHandle;
    private GraphResourceHandle meterHandle;
    private PassHandle scenePass;
    private PassHandle tonePass;
    private PassHandle meterPass;
    private ShaderProgramHandle sceneProgram;
    private ShaderProgramHandle presentProgram;
    private PipelineHandle scenePipeline;
    private PipelineHandle meterPipeline;
    private PipelineHandle presentPipeline;
    private PipelineHandle tonePipeline;

    // Environment maps: index 0 is the procedural world, then the HDRIs found on disk.
    private HdriLibrary hdris = null!;
    private string[] environmentOptions = { "procedural world" };
    private int environment;
    private int loadedEnvironment;
    private TextureHandle activeCube;
    private bool dress;   // lamps, tunnel and signs on top of an HDRI (always on over the procedural world)

    private int frameCount;
    private bool captured;

    // Camera and world.
    private float yaw;
    private float pitch = 0.06f;
    private float fovDegrees = 70f;
    private float worldEv;
    private float sunElevation = 28f;
    private float sunAzimuth = 20f;
    private float lampNits = 4000f;
    private float signNits = 1.0e5f;
    private bool signs = true;

    // Auto-exposure. The meter pass renders the world at 1/8 resolution, read back every few frames; the state steps every
    // frame, at a time scale so a 30-minute dusk can be watched in half a minute.
    private readonly AdaptationParams adapt = new();
    private readonly AdaptationState state = new();
    private bool autoExposure = true;
    private float compensationEv;
    private float timeScale = 1f;
    private bool meterPending;

    // Glare and motion blur (lab/tonemap/tmlab/glare.py, motionblur.py). Both act on scene-linear light, before exposure.
    private GaussianChains glare = null!;
    private GaussianChains scatter = null!;   // veiling glare: Stiles-Holladay 10/theta^2, lab/tonemap/results/scatter_fit.json
    private float scatterAmount = 1f;          // 1 = the standard's amount for an age-25 observer (6.6% of the light)
    private const float ScatterFraction = 0.0662f;   // scatter_fit.json fraction_age25
    private ShaderProgramHandle downProgram, blurProgram;
    private float glareMix = 1f;          // 0 off .. 1 the full diffraction PSF
    private float airyRadiusPx = 2f;      // the PSF's scale: 1.22 lambda N / pitch, in full-resolution pixels
    private float tunnelEv = -6.8f;      // how far below the world the tunnel interior is (was -8; lifted 1.2 EV: -5.1, -5.6, -6.6 read too bright live, -7.2 too dark)
    private bool motionBlur = true;
    private float shutter = 0.5f;         // fraction of the frame the shutter is open (0.5 = a 180 degree shutter)
    private int blurSamples = 16;
    private float panDegPerSecond;        // an automatic camera pan, for watching the blur without a hand on the mouse
    private float blurYaw, blurPitch;     // how far the view swept during the last frame's shutter
    private float prevYaw, prevPitch;

    // Auto white balance (lab/tonemap/tmlab/awb.py): measured from the same meter readback, stepped every frame like the exposure.
    private readonly AwbParams awbParams = new();
    private readonly AutoWhiteBalance awb = new();
    private bool autoWhiteBalance = true;
    private (double Mired, double Tint) awbMeasured = (AutoWhiteBalance.D65Mired, 0.0);
    private int cycleDirection;       // -1 dusk, +1 dawn, 0 none
    private float cycleTime;
    private float cycleSeconds = 30f;

    // Look.
    private int pattern;
    private int op = 4;
    // About -6 EV puts the noon ground (2200-3000 nits, 250 per scene unit) at mid grey, for the manual case; the auto-exposure
    // finds it by itself.
    private float exposureEv = -6f;
    private float kelvin = 6504f;
    private float tint;
    private int preset = 1;
    private Gt7Set gt7 = Tuned;
    private float peakNits = 1000f;
    private bool trackHeadroom = true;   // GT7's peak follows what the display is actually granting
    private float sdrWhiteNits = 250f;
    private float anchorNits = 250f;
    private bool forceSdrGt7;

    public LabLoop(AppArgs args)
    {
        capturePath = args.String("capture");
        hdriDirectory = args.String("hdri-dir");
        startHdri = args.String("hdri");
        captureFrame = args.Int("capture-frame") ?? 30;
        selfTestPath = args.String("selftest");
        trace = args.Flag("trace");
        cutAtSeconds = args.Float("cut-at") ?? -1f;
        selfTestPeak = args.Float("peak") ?? 1000f;
        selfTestPreset = args.String("gt7") == "published" ? 0 : 1;
        if (args.Int("pattern") is { } startPattern) pattern = Math.Clamp(startPattern, 0, Patterns.Length - 1);
        if (args.Float("exposure") is { } startEv) { exposureEv = startEv; autoExposure = false; }
        if (args.Float("world-ev") is { } startWorld) worldEv = startWorld;
        if (args.Flag("manual")) autoExposure = false;
        if (args.Float("glare") is { } startGlare) glareMix = startGlare;
        if (args.Float("airy") is { } startAiry) airyRadiusPx = startAiry;
        if (args.Float("scatter") is { } startScatter) scatterAmount = startScatter;
        if (args.Float("tunnel-ev") is { } startTunnel) tunnelEv = startTunnel;
        if (args.Float("shutter") is { } startShutter) shutter = startShutter;
        if (args.Float("pan") is { } startPan) panDegPerSecond = startPan;
        if (args.String("awb") == "off") autoWhiteBalance = false;
        if (capturePath is not null || selfTestPath is not null) autoExposure = false;   // a capture is a fixed number
        if (args.String("gt7") is { } startPreset && selfTestPath is null)
        {
            // --gt7 published|tuned: start the interactive window in that parameter set.
            preset = startPreset == "published" ? 0 : 1;
            gt7 = preset == 0 ? Published : Tuned;
        }
        if (selfTestPath is not null)
        {
            // The deterministic case: the ramp, GT7, neutral exposure and white balance, the chosen peak.
            pattern = 2;
            op = 4;
            preset = selfTestPreset;
            gt7 = preset == 0 ? Published : Tuned;
            peakNits = selfTestPeak;
            captureFrame = 8;
        }
    }

    public void OnLoad(IRenderHost renderHost, IGraphicsDevice graphicsDevice)
    {
        host = renderHost;
        device = graphicsDevice;
        renderHost.SetTitle(device.HdrOutput ? "Blix — Tonemap Lab (HDR output)" : "Blix — Tonemap Lab");

        graph = new RenderGraph(device);
        var size = new MatchSwapchainGraphSize(1.0f);
        sceneHandle = graph.ColorTarget("scene", TextureFormat.Rgba16F, size);
        toneHandle = graph.ColorTarget("tone", TextureFormat.Rgba16F, size);
        meterHandle = graph.ColorTarget("meter", TextureFormat.Rgba16F, new MatchSwapchainGraphSize(0.125f));

        var shaderDir = AppFiles.Shaders;
        var sceneInterface = ShaderReflection.ForProgram(shaderDir, "quad.vert", "scene.frag");
        var presentInterface = ShaderReflection.ForProgram(shaderDir, "quad.vert", "present.frag");
        var downInterface = ShaderReflection.ForProgram(shaderDir, "quad.vert", "down.frag");
        var blurInterface = ShaderReflection.ForProgram(shaderDir, "quad.vert", "blur.frag");

        scenePass = graph.GraphicsPass("scene").Target(sceneHandle, LoadOp.Clear, StoreOp.Store).Shader(sceneInterface).Handle;
        // The world again at 1/8 resolution, for the meter: the same shader, so what is metered is what is seen.
        meterPass = graph.GraphicsPass("meter").Target(meterHandle, LoadOp.Clear, StoreOp.Store).Shader(sceneInterface).Handle;
        // The present shader into an offscreen target: what a capture reads back, in the numbers the window gets.
        glare = new GaussianChains(device, graph, sceneHandle, downInterface, blurInterface, "glare", GlareFit.Sigmas, GlareFit.Levels);
        scatter = new GaussianChains(device, graph, sceneHandle, downInterface, blurInterface, "scatter", GlareFit.ScatterSigmasDeg, GlareFit.ScatterLevels);
        var toneBuilder = graph.GraphicsPass("tone").Target(toneHandle, LoadOp.Clear, StoreOp.Store).Read(sceneHandle);
        foreach (var output in glare.Outputs) toneBuilder = toneBuilder.Read(output);
        foreach (var output in scatter.Outputs) toneBuilder = toneBuilder.Read(output);
        tonePass = toneBuilder.Shader(presentInterface).Handle;
        graph.Compile();

        var vert = File.ReadAllBytes(Path.Combine(shaderDir, "quad.vert.spv"));
        sceneProgram = device.CreateShaderProgramFromSpv(vert, File.ReadAllBytes(Path.Combine(shaderDir, "scene.frag.spv")), sceneInterface, "scene");
        presentProgram = device.CreateShaderProgramFromSpv(vert, File.ReadAllBytes(Path.Combine(shaderDir, "present.frag.spv")), presentInterface, "present");
        downProgram = device.CreateShaderProgramFromSpv(vert, File.ReadAllBytes(Path.Combine(shaderDir, "down.frag.spv")), downInterface, "glare-down");
        blurProgram = device.CreateShaderProgramFromSpv(vert, File.ReadAllBytes(Path.Combine(shaderDir, "blur.frag.spv")), blurInterface, "glare-blur");
        glare.BuildPipelines(device, downProgram, blurProgram);
        scatter.BuildPipelines(device, downProgram, blurProgram);

        PipelineDescription Offscreen(ShaderProgramHandle program, PassHandle pass) => new(
            program, FullscreenPass.Layout, PrimitiveTopology.Triangles, DepthState.Disabled, RasterizerState.NoCulling,
            new[] { BlendState.Disabled }, RenderTarget: graph.GetPassSurface(pass));
        scenePipeline = device.CreatePipeline(Offscreen(sceneProgram, scenePass), "scene");
        meterPipeline = device.CreatePipeline(Offscreen(sceneProgram, meterPass), "meter");
        tonePipeline = device.CreatePipeline(Offscreen(presentProgram, tonePass), "tone");
        presentPipeline = device.CreatePipeline(new PipelineDescription(
            presentProgram, FullscreenPass.Layout, PrimitiveTopology.Triangles, DepthState.Disabled, RasterizerState.NoCulling,
            BlendState.Disabled), "present");

        fullscreen = new FullscreenPass(device, "fullscreen");
        hdris = new HdriLibrary(device, hdriDirectory ?? HdriLibrary.FindDirectory());
        activeCube = hdris.Empty;
        environmentOptions = new[] { "procedural world" }.Concat(hdris.Names).ToArray();
        if (startHdri is not null)
        {
            var found = Array.IndexOf(environmentOptions, startHdri);
            if (found > 0) environment = found; else Console.Error.WriteLine($"[tonemap-lab] no HDRI named '{startHdri}' in {hdris.Directory ?? "(no directory)"}; staying procedural.");
        }
        Console.WriteLine($"[tonemap-lab] environments: {hdris.Names.Count} HDRIs in {hdris.Directory ?? "(none found; run lab/tonemap/tools/fetch_hdris.py)"}");
        Console.WriteLine($"[tonemap-lab] output: {(device.HdrOutput ? "HDR (RGBA16F, extended-linear sRGB)" : "SDR (sRGB)")}"
                          + (device.HdrOutput ? "" : "  (BLIX_HDR=1 asks for HDR)"));
    }

    public void OnUpdate(Time time)
    {
        var input = host.Input;
        var dt = Math.Min((double)time.Delta, 0.1);
        if (input[Key.Escape].Pressed) host.RequestClose();
        if (input[Key.R].Pressed) { yaw = 0f; pitch = 0.06f; fovDegrees = 70f; }
        if (input[Key.Up].Pressed) exposureEv = Math.Clamp(exposureEv + 0.25f, -10f, 10f);
        if (input[Key.Down].Pressed) exposureEv = Math.Clamp(exposureEv - 0.25f, -10f, 10f);

        var look = fovDegrees / 70f;
        if (input[MouseButton.Left].Down)
        {
            yaw -= input.MouseDelta.X * 0.0045f * look;
            pitch = Math.Clamp(pitch + input.MouseDelta.Y * 0.0045f * look, -1.4f, 1.4f);
        }
        if (input.MouseWheel.Y != 0f) fovDegrees = Math.Clamp(fovDegrees - input.MouseWheel.Y * 3f, 15f, 110f);

        if (panDegPerSecond != 0f) yaw += panDegPerSecond * MathF.PI / 180f * (float)dt;

        if (cutAtSeconds >= 0f && !cutDone && time.Total >= cutAtSeconds)
        {
            cutDone = true;
            yaw = MathF.PI;
            pitch = 0.04f;
        }

        // The dusk or dawn run: the world level ramps over cycleSeconds of real time, and the adaptation below runs timeScale
        // times faster, so 30 s at x60 is the 30 minutes the model's specs are written for.
        if (cycleDirection != 0)
        {
            cycleTime += (float)dt;
            var fraction = Math.Clamp(cycleTime / cycleSeconds, 0f, 1f);
            worldEv = cycleDirection < 0 ? -14f * fraction : -14f * (1f - fraction);
            if (fraction >= 1f) cycleDirection = 0;
        }

        // The meter pass drawn last frame is read back now (the device waits, so it is complete): a small, rare stall.
        if (meterPending)
        {
            meterPending = false;
            var bytes = device.ReadTexture(graph.GetColorTexture(meterHandle), out var w, out var h, out var format);
            if (format == TextureFormat.Rgba16F)
            {
                var m = Meter.TrimmedEv(Meter.HistogramRgba16F(bytes, w * h), adapt.TrimHi);
                if (!state.Started) state.Reset(m); else state.Observe(m);
                awbMeasured = AutoWhiteBalance.CastOf(AutoWhiteBalance.Measure(bytes, w * h, awbParams), awbParams);
                if (!awb.Started) awb.Step(1e9, awbMeasured, m, awbParams);   // the first reading is taken as already adapted to
                if (trace) Console.WriteLine($"[trace] t={time.Total:F2} m={m:F2} wb={1e6 / awb.Mired:F0}K/{awb.Tint:+0.00;-0.00} S={state.Short:F2} A={state.Long:F2} error={state.Error:+0.00;-0.00} exposure={state.ExposureEv:F2} EV");
            }
        }
        state.Step(dt * timeScale, adapt);
        if (awb.Started) awb.Step(dt * timeScale, awbMeasured, state.Metered, awbParams);

        if (environment != loadedEnvironment)
        {
            loadedEnvironment = environment;
            activeCube = environment > 0 ? hdris.Get(environmentOptions[environment]).Cube : hdris.Empty;
        }

        // A capture is read back a few frames after it was drawn, so the GPU has finished with it.
        if (capturePath is not null || selfTestPath is not null)
        {
            if (!captured && frameCount >= captureFrame + 3)
            {
                captured = true;
                WriteCapture(selfTestPath ?? capturePath!);
                host.RequestClose();
            }
        }
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;
        // How far the view turned since the last frame, scaled by the shutter: the sweep the blur averages over.
        var dyaw = MathF.IEEERemainder(yaw - prevYaw, 2f * MathF.PI);
        var dpitch = pitch - prevPitch;
        (prevYaw, prevPitch) = (yaw, pitch);
        var blurring = motionBlur && shutter > 0f && frameCount > 1;
        (blurYaw, blurPitch) = blurring ? (dyaw * shutter, dpitch * shutter) : (0f, 0f);
        var scenePush = new ScenePush
        {
            Yaw = yaw,
            Pitch = pitch,
            TanHalfFov = MathF.Tan(fovDegrees * MathF.PI / 360f),
            Aspect = frame.Width / (float)Math.Max(1, frame.Height),
            Time = (float)time.Total,
            SunElevation = sunElevation * MathF.PI / 180f,
            SunAzimuth = sunAzimuth * MathF.PI / 180f,
            WorldEv = worldEv,
            LampNits = lampNits,
            SignNits = signNits,
            Signs = signs ? 1 : 0,
            UseHdri = loadedEnvironment > 0 ? 1 : 0,
            Dress = dress ? 1 : 0,
            BlurYaw = blurYaw,
            TunnelEv = tunnelEv,
            BlurPitch = blurPitch,
            BlurSamples = blurYaw != 0f || blurPitch != 0f ? blurSamples : 1,
        }.ToBytes();

        var worldBinding = new[] { new ShaderTextureBinding("uHdri", activeCube) };
        graph.Pass(scenePass, scope => fullscreen.Draw(scope, scenePipeline, worldBinding, pushConstants: scenePush),
            clearColor: new GraphicsColor(0, 0, 0, 1));
        if (frameCount % MeterEveryFrames == 1 && pattern == 0)
        {
            graph.Pass(meterPass, scope => fullscreen.Draw(scope, meterPipeline, worldBinding, pushConstants: scenePush),
                clearColor: new GraphicsColor(0, 0, 0, 1));
            meterPending = true;
        }

        var glareOn = glareMix > 0f && pattern == 0;
        var scatterOn = scatterAmount > 0f && pattern == 0;
        if (glareOn) glare.Record(airyRadiusPx);
        if (scatterOn) scatter.Record(frame.Height / fovDegrees);   // sigmas are in degrees: pixels per degree of vertical field of view

        var sceneTexture = graph.GetColorTexture(sceneHandle);
        // The glare terms are bound only when this frame drew them (an undrawn target has no defined contents); otherwise the
        // scene stands in and the shader's glare mix is 0.
        var sceneBinding = new ShaderTextureBinding[11];
        sceneBinding[0] = new ShaderTextureBinding("uScene", sceneTexture);
        for (var i = 0; i < 6; i++)
            sceneBinding[i + 1] = new ShaderTextureBinding($"uGlare{i}", glareOn ? graph.GetColorTexture(glare.Outputs[i]) : sceneTexture);
        for (var i = 0; i < 4; i++)
            sceneBinding[i + 7] = new ShaderTextureBinding($"uScatter{i}", scatterOn ? graph.GetColorTexture(scatter.Outputs[i]) : sceneTexture);
        if ((capturePath is not null || selfTestPath is not null) && frameCount == captureFrame)
        {
            var push = MakePresentPush(selfTest: selfTestPath is not null).ToBytes();
            graph.Pass(tonePass, scope => fullscreen.Draw(scope, tonePipeline, sceneBinding, pushConstants: push),
                clearColor: new GraphicsColor(0, 0, 0, 1));
        }
        graph.Execute(commandList);

        var presentPush = MakePresentPush(selfTest: false).ToBytes();
        commandList.Pass(
            "present",
            new RenderPassDescription(Target: RenderSurfaceHandle.Default, ClearColors: new GraphicsColor?[] { new GraphicsColor(0, 0, 0, 1) }, ClearDepth: true),
            pass => fullscreen.Draw(pass, presentPipeline, sceneBinding, pushConstants: presentPush));
    }

    // The peak GT7 aims at. On an HDR surface that has been granted headroom, the display's own: UI white times the headroom
    // it reports (it moves with the brightness setting), so the shoulder ends exactly where the display does and nothing is
    // clipped by the OS. Otherwise the slider.
    private float EffectivePeakNits =>
        trackHeadroom && device.HdrOutput && host.DisplayHeadroom > 1.05f
            ? Math.Max(250f, sdrWhiteNits * host.DisplayHeadroom)
            : peakNits;

    // The exposure multiplier: the adaptation's (the metered level lands on mid grey, plus the compensation) once it has a
    // reading, the manual EV before that and whenever auto-exposure is off.
    private float ExposureMultiplier =>
        autoExposure && state.Started
            ? MathF.Pow(2f, (float)state.ExposureEv + compensationEv)
            : MathF.Pow(2f, exposureEv);

    private PresentPush MakePresentPush(bool selfTest)
    {
        var (wr, wg, wb) = WhiteBalance.Gains(kelvin, tint);
        if (autoWhiteBalance && awb.Started)
        {
            var (ar, ag, ab) = awb.Gains(awbParams);
            (wr, wg, wb) = (wr * ar, wg * ag, wb * ab);
        }
        if (selfTest) (wr, wg, wb) = (1f, 1f, 1f);
        // GT7 asks for a peak; a surface with no headroom cannot show one, so it runs at the SDR paper white instead.
        var gt7Sdr = op == 4 && (forceSdrGt7 || !device.HdrOutput) && !selfTest;
        return new PresentPush
        {
            Exposure = selfTest ? 1f : ExposureMultiplier,
            AnchorNits = anchorNits,
            SdrWhiteNits = sdrWhiteNits,
            PeakNits = EffectivePeakNits,
            WbR = wr, WbG = wg, WbB = wb,
            Alpha = gt7.Alpha, GrayPoint = gt7.GrayPoint, LinearSection = gt7.LinearSection, ToeStrength = gt7.ToeStrength,
            BlendRatio = gt7.BlendRatio, FadeStart = gt7.FadeStart, FadeEnd = gt7.FadeEnd, ShoulderTail = gt7.ShoulderTail,
            Operator = op,
            Output = selfTest ? 2 : 0,
            Pattern = pattern,
            GlareMix = glareMix > 0f && pattern == 0 ? glareMix : 0f,
            ScatterAmount = scatterAmount > 0f && pattern == 0 ? Math.Min(scatterAmount * ScatterFraction, 1f) : 0f,
            Gt7Sdr = gt7Sdr ? 1 : 0,
        };
    }

    private void WriteCapture(string path)
    {
        var bytes = device.ReadTexture(graph.GetColorTexture(toneHandle), out var width, out var height, out var format);
        if (format != TextureFormat.Rgba16F)
        {
            Console.Error.WriteLine($"[tonemap-lab] expected Rgba16F, read {format}.");
            return;
        }
        var rgb = new float[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            for (var c = 0; c < 3; c++) rgb[i * 3 + c] = (float)BitConverter.ToHalf(bytes, i * 8 + c * 2);
        }
        var stem = Path.ChangeExtension(path, null);
        PfmWriter.WriteRgb(stem + ".pfm", rgb, width, height);

        // A PNG a person can open: the values the window was sent, clamped to SDR white and sRGB-encoded.
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            for (var c = 0; c < 3; c++) rgba[i * 4 + c] = ToSrgbByte(rgb[i * 3 + c]);
            rgba[i * 4 + 3] = 255;
        }
        PngWriter.WriteRgba8(stem + ".png", rgba, width, height);
        Console.WriteLine($"[tonemap-lab] wrote {stem}.pfm and {stem}.png ({width}x{height}, output {(device.HdrOutput ? "HDR" : "SDR")})");
    }

    private static byte ToSrgbByte(float linear)
    {
        linear = Math.Clamp(linear, 0f, 1f);
        var encoded = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(encoded * 255f), 0, 255);
    }

    public void Debug(DebugContext debug)
    {
        debug.Values.Value("surface", device.HdrOutput ? "HDR: RGBA16F, extended-linear sRGB" : "SDR: sRGB (BLIX_HDR=1 for HDR)");
        debug.Values.Value("display headroom", $"{host.DisplayHeadroom:F2} x UI white (1.00 = the display is granting none)");
        pattern = debug.Controls.Enum("Pattern", pattern, Patterns);

        using (debug.Scope("exposure"))
        {
            autoExposure = debug.Controls.Toggle("Auto exposure [E]", autoExposure, Key.E);
            if (autoExposure)
            {
                compensationEv = debug.Controls.Float("Compensation EV", compensationEv, -4f, 4f);
            }
            else
            {
                exposureEv = debug.Controls.Float("Exposure EV [Up/Dn]", exposureEv, -10f, 10f);
            }
            if (state.Started)
            {
                debug.Values.Value("metered level", $"{state.Metered:F2} EV");
                debug.Values.Value("short-term S", $"{state.Short:F2} EV (window {state.Long - adapt.RangeDark:F1} .. {state.Long + adapt.RangeLight:F1})");
                debug.Values.Value("long-term A", $"{state.Long:F2} EV (span {state.DayLevel + adapt.LightLimit - adapt.LongRange:F1} .. {state.DayLevel + adapt.LightLimit:F1})");
                debug.Values.Value("error m - S", $"{state.Error:+0.00;-0.00} EV  (negative = darker than proper)");
                debug.Values.Value("exposure", $"{ExposureMultiplier:F5} = {MathF.Log2(ExposureMultiplier):F2} EV");
            }
            else
            {
                debug.Values.Value("metered level", "(no reading yet: needs the scene pattern)");
            }
        }

        using (debug.Scope("adaptation (lab/tonemap/tmlab/adaptation.py)"))
        {
            timeScale = debug.Controls.Float("Time scale (x real time)", timeScale, 1f, 240f);
            adapt.TauSLight = debug.Controls.Float("short-term, brightening tau s", adapt.TauSLight, 0.1f, 2f);
            adapt.TauSDark = debug.Controls.Float("short-term, darkening tau s", adapt.TauSDark, 0.3f, 6f);
            adapt.RangeDark = debug.Controls.Float("range below long-term EV", adapt.RangeDark, 1f, 3f);
            adapt.RangeLight = debug.Controls.Float("range above long-term EV", adapt.RangeLight, 1f, 3f);
            debug.Values.Value("short-term range, total", $"{adapt.RangeDark + adapt.RangeLight:F1} EV (GT7: 3 to 5)");
            adapt.TauALight = debug.Controls.Float("long-term, brightening tau s", adapt.TauALight, 20f, 600f);
            adapt.TauADark = debug.Controls.Float("long-term, darkening tau s", adapt.TauADark, 60f, 3000f);
            adapt.LightLimit = debug.Controls.Float("light limit, EV above day level", adapt.LightLimit, 0f, 6f);
            adapt.TrimHi = debug.Controls.Float("meter ignores the brightest", adapt.TrimHi, 0f, 0.15f);
            if (debug.Controls.Button("Reset adaptation to this view")) { if (state.Started) state.Reset(state.Metered); }
            if (debug.Controls.Button("Defaults"))
            {
                var d = new AdaptationParams();
                (adapt.TauSLight, adapt.TauSDark, adapt.RangeDark, adapt.RangeLight) = (d.TauSLight, d.TauSDark, d.RangeDark, d.RangeLight);
                (adapt.TauALight, adapt.TauADark, adapt.LightLimit, adapt.TrimHi) = (d.TauALight, d.TauADark, d.LightLimit, d.TrimHi);
            }
        }

        using (debug.Scope("auto white balance (lab/tonemap/tmlab/awb.py)"))
        {
            autoWhiteBalance = debug.Controls.Toggle("Auto white balance [W]", autoWhiteBalance, Key.W);
            awbParams.Strength = debug.Controls.Float("strength (0 off, 1 cancels the cast)", awbParams.Strength, 0f, 1f);
            awbParams.Tau = debug.Controls.Float("time constant s", awbParams.Tau, 0.5f, 60f);
            awbParams.Virtual = debug.Controls.Float("virtual D65 light weight", awbParams.Virtual, 0f, 0.5f);
            awbParams.SkyWeight = debug.Controls.Float("sky counts for", awbParams.SkyWeight, 0f, 1f);
            awbParams.Sat0 = debug.Controls.Float("saturation discount (half weight at)", awbParams.Sat0, 0.1f, 1f);
            awbParams.TintLimit = debug.Controls.Float("tint limit", awbParams.TintLimit, 0f, 0.2f);
            awbParams.KelvinLo = debug.Controls.Float("lowest believed K", awbParams.KelvinLo, 1700f, 4000f);
            awbParams.KelvinHi = debug.Controls.Float("highest believed K", awbParams.KelvinHi, 7000f, 20000f);
            awbParams.ConfLo = debug.Controls.Float("colour not believed below meter EV", awbParams.ConfLo, -14f, -4f);
            awbParams.ConfHi = debug.Controls.Float("fully believed above meter EV", awbParams.ConfHi, -8f, 0f);
            debug.Values.Value("measured light", $"{1e6 / awbMeasured.Mired:F0} K, tint {awbMeasured.Tint:+0.000;-0.000}");
            debug.Values.Value("estimate", awb.Started ? $"{1e6 / awb.Mired:F0} K, tint {awb.Tint:+0.000;-0.000}" : "(no reading yet)");
            if (awb.Started)
            {
                var (ar, ag, ab) = awb.Gains(awbParams);
                debug.Values.Value("gains applied", $"{ar:F3} {ag:F3} {ab:F3}");
            }
            if (debug.Controls.Button("Adapt white balance to this view")) awb.Step(1e9, awbMeasured, state.Metered, awbParams);
            if (debug.Controls.Button("Reset to D65")) awb.Reset(AutoWhiteBalance.D65Mired, 0.0);
        }

        using (debug.Scope("glare and motion blur (lab/tonemap/tmlab/glare.py, motionblur.py)"))
        {
            glareMix = debug.Controls.Float("glare mix (0 off, 1 diffraction PSF)", glareMix, 0f, 1f);
            airyRadiusPx = debug.Controls.Float("Airy radius px (PSF scale)", airyRadiusPx, 0.5f, 4f);
            scatterAmount = debug.Controls.Float("scatter (1 = Stiles-Holladay, age 25)", scatterAmount, 0f, 4f);
            motionBlur = debug.Controls.Toggle("Motion blur", motionBlur);
            shutter = debug.Controls.Float("shutter (fraction of the frame open)", shutter, 0f, 1f);
            blurSamples = (int)debug.Controls.Float("blur samples", blurSamples, 4f, 48f);
            panDegPerSecond = debug.Controls.Float("auto pan deg/s", panDegPerSecond, -240f, 240f);
            debug.Values.Value("blur sweep", $"{blurYaw * 180f / MathF.PI:F2} deg yaw, {blurPitch * 180f / MathF.PI:F2} deg pitch this frame");
        }

        using (debug.Scope("white balance (manual, on top of the above)"))
        {
            kelvin = debug.Controls.Float("Illuminant K (6504 = neutral)", kelvin, 2000f, 12000f);
            tint = debug.Controls.Float("Tint (+ magenta)", tint, -0.3f, 0.3f);
            if (debug.Controls.Button("Reset white balance")) { kelvin = 6504f; tint = 0f; }
        }

        using (debug.Scope("tone map"))
        {
            op = debug.Controls.Enum("Operator", op, Operators);
            var newPreset = debug.Controls.Enum("GT7 parameters", preset, Presets);
            if (newPreset != preset)
            {
                preset = newPreset;
                if (preset == 0) gt7 = Published;
                else if (preset == 1) gt7 = Tuned;
            }
            trackHeadroom = debug.Controls.Toggle("Track display headroom", trackHeadroom);
            peakNits = debug.Controls.Float("Display peak nits (when not tracking)", peakNits, 250f, 4000f);
            sdrWhiteNits = debug.Controls.Float("UI white nits (EDR 1.0)", sdrWhiteNits, 80f, 800f);
            anchorNits = debug.Controls.Float("Scene white nits (anchor)", anchorNits, 80f, 800f);
            forceSdrGt7 = debug.Controls.Toggle("GT7 in SDR mode", forceSdrGt7);
            debug.Values.Value("GT7 peak now", $"{EffectivePeakNits:F0} nits = {EffectivePeakNits / sdrWhiteNits:F2} x UI white");
        }

        using (debug.Scope("GT7 parameters"))
        {
            var g = gt7;
            g = g with { Alpha = debug.Controls.Float("alpha", g.Alpha, 0.001f, 0.95f) };
            g = g with { GrayPoint = debug.Controls.Float("gray point", g.GrayPoint, 0.1f, 1.5f) };
            g = g with { LinearSection = debug.Controls.Float("linear section", g.LinearSection, 0.1f, 0.95f) };
            g = g with { ToeStrength = debug.Controls.Float("toe strength", g.ToeStrength, 1f, 2f) };
            g = g with { BlendRatio = debug.Controls.Float("blend ratio", g.BlendRatio, 0f, 1f) };
            g = g with { FadeStart = debug.Controls.Float("fade start", g.FadeStart, 0.5f, 1.5f) };
            g = g with { FadeEnd = debug.Controls.Float("fade end", g.FadeEnd, 0.7f, 2.5f) };
            g = g with { ShoulderTail = debug.Controls.Float("shoulder tail (0 = GT7)", g.ShoulderTail, 0f, 3f) };
            if (g != gt7) { gt7 = g; preset = 2; }
        }

        using (debug.Scope("world"))
        {
            environment = debug.Controls.Enum("Environment", environment, environmentOptions);
            if (environment > 0) dress = debug.Controls.Toggle("Lamps, tunnel and signs on the HDRI", dress);
            worldEv = debug.Controls.Float("World level EV (0 noon .. -14 night)", worldEv, -14f, 2f);
            cycleSeconds = debug.Controls.Float("Dusk/dawn run, seconds", cycleSeconds, 5f, 180f);
            if (debug.Controls.Button("Run dusk (0 to -14 EV)")) { cycleDirection = -1; cycleTime = 0f; }
            if (debug.Controls.Button("Run dawn (-14 to 0 EV)")) { cycleDirection = 1; cycleTime = 0f; }
            debug.Values.Value("run", cycleDirection == 0 ? "none (set the time scale to 60 to see 30 s as 30 minutes)" : $"{(cycleDirection < 0 ? "dusk" : "dawn")} {cycleTime:F0}/{cycleSeconds:F0} s");
            sunElevation = debug.Controls.Float("Sun elevation deg", sunElevation, 0f, 90f);
            sunAzimuth = debug.Controls.Float("Sun azimuth deg", sunAzimuth, -180f, 180f);
            lampNits = debug.Controls.Float("Lamp nits", lampNits, 100f, 20000f);
            signNits = debug.Controls.Float("Sign nits", signNits, 1000f, 1.0e6f);
            signs = debug.Controls.Toggle("Flashing signs", signs);
            tunnelEv = debug.Controls.Float("Tunnel EV below the world", tunnelEv, -10f, -4f);
            fovDegrees = debug.Controls.Float("Field of view deg", fovDegrees, 15f, 110f);
            if (debug.Controls.Button("Look at the tunnel")) { yaw = MathF.PI; pitch = 0.04f; }
            if (debug.Controls.Button("Look at the sun"))
            {
                yaw = sunAzimuth * MathF.PI / 180f;
                pitch = sunElevation * MathF.PI / 180f;
            }
        }
    }

    public void Dispose()
    {
        fullscreen?.Dispose();
        glare?.Dispose();
        scatter?.Dispose();
        hdris?.Dispose();
        if (device is not null)
        {
            foreach (var (program, pipeline) in new[] { (sceneProgram, scenePipeline), (presentProgram, presentPipeline) })
            {
                device.DestroyPipeline(pipeline);
                device.DestroyShaderProgram(program);
            }
            device.DestroyPipeline(tonePipeline);
            device.DestroyPipeline(meterPipeline);
            device.DestroyShaderProgram(downProgram);
            device.DestroyShaderProgram(blurProgram);
        }
        graph?.Dispose();
    }
}

internal readonly record struct Gt7Set(
    float Alpha, float GrayPoint, float LinearSection, float ToeStrength,
    float BlendRatio, float FadeStart, float FadeEnd, float ShoulderTail);

// The push blocks, written out to match scene.frag and present.frag member for member (all four-byte scalars, so no padding).
[StructLayout(LayoutKind.Sequential)]
internal struct ScenePush
{
    public float Yaw, Pitch, TanHalfFov, Aspect, Time, SunElevation, SunAzimuth, WorldEv, LampNits, SignNits, BlurYaw, BlurPitch, TunnelEv;
    public int Signs, UseHdri, Dress, BlurSamples;

    public readonly byte[] ToBytes()
    {
        var copy = this;
        return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref copy, 1)).ToArray();
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PresentPush
{
    public float Exposure, AnchorNits, SdrWhiteNits, PeakNits, WbR, WbG, WbB;
    public float Alpha, GrayPoint, LinearSection, ToeStrength, BlendRatio, FadeStart, FadeEnd, ShoulderTail, GlareMix, ScatterAmount;
    public int Operator, Output, Pattern, Gt7Sdr;

    public readonly byte[] ToBytes()
    {
        var copy = this;
        return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref copy, 1)).ToArray();
    }
}
