using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanSponza;

// The Khronos Intel Sponza scene on the Vulkan backend — Blix's heavy-scene
// rendering and measurement application. It loads cooked siblings only
// (.blixtex BC textures, .blixprobe IBL, .blixmesh geometry with LOD chains +
// spatial split) across the main + curtains + ivy + trees packs.
//
// Render graph, abbreviated: scene cull → depth + normal pre-pass (two-phase
// occlusion) → the traced sun shadows (Metal) and RTGI's incident light → lit
// scene (R11G11B10F, optional MSAA resolve) → parity TAA or MetalFX → present.
//
// Performance shape: screen-space-error LOD over meshopt chains, cook-time spatial split of
// oversized primitives, all geometry consolidated into one shared VB/IB (draws
// are sub-ranges), and cutout foliage routed to depth-writing MASK + alpha-to-
// coverage so the hero tree isn't overdraw-bound. CPU-phase timing
// (cpu-wait/encode/submit) is surfaced in the diagnostics overlay.
//
// Asset story: BLIX_SPONZA_ASSETS names the cooked pack set, and is required.
// Missing-asset startup prints the setup instruction and exits cleanly.
//
// ── Executable spec for (engine primitives this demo proves) ──
//   • Cooked-asset pipeline: .blixmesh/.blixtex/.blixprobe via MaterialTextureLoader
//     + AsyncLoadQueue + MeshBundler (one shared VB/IB, draws are sub-ranges)
//   • GPU-driven indirect draw, screen-space-error LOD over meshopt chains
//   • RenderGraph at scale: graphics + compute + external Metal passes, history reads,
//     depth/normal pre-pass, an occlusion Hi-Z, incident light, TAA, and MSAA resolves
// ── Intentionally owns (stays local; don't extract until a 2nd consumer needs it) ──
//   • Sponza-specific draw groups, LOD/cull policy, cutout-foliage routing
//   • RTGI, the traced shadows, the exact pass wiring, measurement modes, and per-scene tuning
//     (this is a research renderer, not a reusable scene renderer)
public static class Program
{
    public static int Main(string[] args) => BlixApps.Main(args, Run);

    private static int Run(AppArgs args)
    {
        // --win W H: the single most informative perf switch this demo has. Fragment and bandwidth
        // cost scale with pixels; geometry cost does not. Halving each side quarters the first and
        // leaves the second alone, so one paired run says which wall the frame is against — a
        // question no amount of per-pass timing can answer on a tile-based GPU, where the
        // timestamps bracket encoder submission rather than execution.
        // Opens with the overlay showing, as a research renderer is mostly read through it. ` hides it.
        var scene = SceneProfile.Named(args.String("scene"));
        // GPU captures' environment (SponzaLoop.GpuCapture.cs), before the Vulkan instance and the Metal device exist.
        SponzaLoop.ApplyGpuCaptureEnvironment(args);
        LogBuild();
        // BLIX_RUN_LABEL: what this run is (its tag, its flags), in the title -- so a window in a sequence of runs says which
        // one it is.
        var runLabel = Environment.GetEnvironmentVariable("BLIX_RUN_LABEL") is { Length: > 0 } rl ? " — " + rl : "";
        var defaults = new WindowOptions($"Blix — {scene.Title}{runLabel}", 1440, 810) { Diagnostics = true };
        if (args.Values("win", 2) is [var w, var h])
        {
            var width = int.Parse(w, CultureInfo.InvariantCulture);
            var height = int.Parse(h, CultureInfo.InvariantCulture);
            if (width < 160 || height < 120)
            {
                throw new AppArgsException($"--win expects at least 160 120, got {width} {height}.");
            }

            defaults = defaults with { Width = width, Height = height };
        }

        var loop = new SponzaLoop(args, scene);
        using var window = new Window(loop, WindowOptions.FromArgs(args, defaults));
        window.Run();
        return 0;
    }

    // Which build this is, first in the log: the configuration, when the binary and its shaders were built, and a hash of
    // the shaders it will load -- so a run read against an edit can be told from a stale one (2026-10-19).
    private static void LogBuild()
    {
        var assembly = typeof(Program).Assembly;
        var config = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>().FirstOrDefault()?.Configuration ?? "?";
        var dir = Path.GetDirectoryName(assembly.Location) ?? ".";
        var shaders = Path.Combine(dir, "Shaders");
        var spv = Directory.Exists(shaders) ? Directory.GetFiles(shaders, "*.spv").OrderBy(f => f, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
        using var sha = System.Security.Cryptography.SHA256.Create();
        foreach (var f in spv)
        {
            var bytes = File.ReadAllBytes(f);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var newest = spv.Length == 0 ? DateTime.MinValue : spv.Max(File.GetLastWriteTime);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] build: {config}, {dir}; binary {File.GetLastWriteTime(assembly.Location):MM-dd HH:mm:ss}, shaders {spv.Length} newest {newest:MM-dd HH:mm:ss}, hash {Convert.ToHexString(sha.Hash!)[..12]}."));
    }
}

internal sealed partial class SponzaLoop : IGameLoop, IDebuggable, IDebugSelectable, IDebugInspectable, IDisposable
{
    public string DebugName => "vulkan-sponza";

    // Read during OnLoad, because most of what it sets needs the device.
    private readonly AppArgs args;

    public SponzaLoop(AppArgs args, SceneProfile scene)
    {
        this.args = args;
        this.scene = scene;
        camera = SceneCamera(scene);
    }

    /// <summary>Which scene this run draws (<c>--scene</c>), and the constants sized to it.</summary>
    private readonly SceneProfile scene;

    // The engine picks and highlights; these say what there is to pick. See SceneSelection.
    public void CollectSelectables(List<DebugSelectable> destination) => sceneSelection.CollectSelectables(destination);

    public bool TryGetBounds(string entityPath, out Bounds3 bounds) => sceneSelection.TryGetBounds(entityPath, out bounds);

    // What the primitive is, then the one edit that belongs to it: its LOD margin, which coarsens or
    // sharpens that primitive alone against the global budget. Declared here so it sits on the
    // Selection tab beside what it edits. Nothing is saved.
    public void Inspect(string entityPath, DebugContext debug)
    {
        sceneSelection.Inspect(entityPath, debug);
        if (TryResolveMargin(entityPath, out var margins, out var at))
        {
            using (debug.Scope("LOD"))
            {
                var edited = debug.Controls.Float("margin (×px)", margins[at], 0f, 8f);
                if (edited != margins[at])
                {
                    margins[at] = edited;
                    var global = ReferenceEquals(margins, blendLodMargins) ? opaquePlacements.Count + at : at;
                    pendingMarginEdit = (global + 1, edited);
                }
            }
        }
    }

    private IRenderHost host = null!;
    private IGraphicsDevice device = null!;
    private RenderGraph graph = null!;
    private ulong graphResourceGeneration;

    // Graph resources + passes.
    private GraphResourceHandle hdrHandle;       // 1× resolve target (present samples this)
    private readonly GraphResourceHandle[] taaHandles = new GraphResourceHandle[2];
    private readonly PassHandle[] taaPassHandles = new PassHandle[2];
    private readonly PipelineHandle[] taaPipelines = new PipelineHandle[2];
    private int taaWrite;
    private int taaWriteNext;
    private Matrix4x4 prevTaaViewProj = Matrix4x4.Identity;
    private bool taaHistoryValid;
    // taa.frag's modes (each a flag, so each change is measured alone): --taa-reproject motion|old, --taa-clip
    // variance|box, --taa-gamma G; --taa-count-rejection counts clipped history into taaStats (refused, total).
    // Measured on Sponza, still camera, 32 frames (presented median variation / median error against the jittered
    // frames' supersample): the old resolve 3.16% / 4.20%, its error barely under one raw jittered frame (4.95%).
    // Motion reprojection (history stops being resampled at the jitter offset): 1.89% / 2.31%. Heavier history then
    // got steadier but WRONGER (0.9: 1.12% / 4.68%, 4.51% of it bias): R11G11B10F history rounds to ~1.6% steps, and
    // once a frame's 10% step is below half of one, history stops converging. Rgba16F: 0.9 gives 0.83% / 0.94%.
    // Variance clipping measured worse than the min/max box here (5 taps: 0.7 2.25% / 2.53%).
    private bool taaMotionReprojection = true;
    private bool taaVarianceClip;
    private float taaGamma = 1f;
    private bool taaCountRejection;
    // --taa-accumulate N: a per-pixel frame count (in the Rgba16F history's alpha), blended at 1/count up to N for a
    // still pixel (10 by a pixel of motion); 0 is the fixed weight. --taa-relax W: the clamp box W times wider where
    // a pixel does not move. Sponza, still camera, presented median variation / pixels over 2% / error against a
    // 256-frame supersample: fixed 0.9 0.83% / 23.9% / 1.05%; N 32, W 3: 0.22% / 7.8% / 1.19%, the sub-pixel
    // foliage settling (its pixels were clipped, and so reset, every frame). The accumulating arm's error is ~0.65
    // points more bias, not from precision (a dithered write), tonemapping (a linear blend) or window lag (K 256):
    // open. Needs the 16-bit history (R11G11B10F has no alpha).
    private float taaAccumulate = 32f;
    private float taaRelax = 3f;
    private GpuBufferHandle taaStats;
    private Matrix4x4 viewProjJittered = Matrix4x4.Identity;
    // This frame's TAA jitter, a clip-space translation (identity without TAA): viewProjJittered = viewProj x it.
    private Matrix4x4 cameraJitter = Matrix4x4.Identity;
    private GraphResourceHandle hdrMsaaHandle;   // MSAA colour the lit pass renders into
    private GraphResourceHandle depthHandle;     // MSAA depth (matches hdrMsaa)
    // Single-sample is the performance-research default. --msaa2/--msaa4 restore alpha-to-coverage
    // for foliage: 2x exposes three coverage levels and 4x exposes five, so 4x gives the canopy a
    // materially better partial-coverage colour. At 1x cutouts use binary discard. Paired runs
    // measured 4x at roughly 18% of the then-current frame, so the quality trade remains explicit.
    private int MsaaSamples = 1;
    private PassHandle litPassHandle;

    // Depth + normal pre-pass: renders non-blend geometry before the lit pass,
    // so expensive lit fragments run only on visible pixels and later screen-space
    // work reads an authored world normal rather than reconstructing one from depth.
    // Reuses lit.vert so the lit pass's LessEqual test matches the depth exactly.
    // It rasterises into sample-count-matched depth; under MSAA it also resolves depth to a
    // sampleable 1x target for the passes that read it, riding the pass store rather than
    // requiring another geometry pass. At 1x the original target is used.
    private PassHandle depthPrepassHandle;

    // Shared by the projection and the occlusion pyramid's linearisation.
    private float CameraNearPlane => scene.NearPlane;
    // The scene's (SceneProfile.FarPlane): a 30 m atrium and a 180 m street want different reaches.
    private float CameraFarPlane => scene.FarPlane;
    private Matrix4x4 cameraView;
    private Matrix4x4 cameraProjection;
    private GraphResourceHandle depthResolveHandle;   // 1x resolve of the MSAA depth

    /// <summary>The scene depth something can SAMPLE: the resolve under MSAA, else the target itself.</summary>
    /// <remarks>Resolving a single-sample attachment is invalid; at 1x the depth target itself is
    /// already sampleable.</remarks>
    private GraphResourceHandle SampleableSceneDepth =>
        MsaaSamples > 1 ? depthResolveHandle : depthHandle;
    // --- The occlusion cull's Hi-Z pyramid ------------------------------------
    // Six levels from half the framebuffer down, each the min/max linear view depth of its parent's
    // footprint. Separate targets rather than mips of one image — see hiz_build.frag for why.
    private const int HiZLevels = 6;
    private ShaderProgramHandle hiZProgram;

    // The interpolated world normal, written by the depth pre-pass. What the incident field used
    // to infer from the depth buffer, and could not for foliage, two-sided cloth or silhouettes.
    private GraphResourceHandle prepassNormalHandle;
    private GraphResourceHandle prepassNormalResolveHandle;
    /// <summary>The normal target a reader should sample: resolved under MSAA, direct at one sample.</summary>
    // Stage 4e: the pre-pass's surface identity and motion. SurfaceKey (R32Uint): the surface each pixel shows (the
    // transform row's key, 0 for none). Velocity (Rg16F): where that surface point was last frame, as this frame's
    // un-jittered uv minus last frame's (previous transform x previous camera). Single-sample only: integers do not
    // resolve, so under MSAA there are none and consumers keep their own reprojection.
    private GraphResourceHandle surfaceKeyHandle;
    private GraphResourceHandle velocityHandle;
    // World motion (Rgba16F, xyz): that surface point's world position now minus last frame's. Velocity says where to
    // look on screen; this says where the point was in the world, rigid or deforming alike, without the key standing
    // for a transform.
    private GraphResourceHandle motionHandle;
    // Only --surface-check reads world motion; elsewhere the full-resolution Rgba16F target was written by both
    // pre-passes and loaded again by the late one for nothing. The shaders still write it (location 3): with no
    // attachment there the writes are dropped, as under MSAA.
    private bool MotionTarget => SurfaceTargets && surfaceCheck;
    private bool SurfaceTargets => MsaaSamples == 1;
    // The surface-key target as the previous frame left it (copied at the end of each frame), and TAA's per-pixel
    // accumulated count, ping-ponged with its colour (R32Uint: an integer, read at the nearest pixel).
    private GraphResourceHandle surfaceKeyHistoryHandle;
    private PassHandle surfaceKeyHistoryPassHandle;
    private PipelineHandle surfaceKeyHistoryPipeline;
    // Stage 4f: --taa-dynamic-count N, the count a pixel whose shading depends on the mover accumulates up to.
    private float taaDynamicCount = 4f;
    // Last frame's un-jittered view-projection, for the velocity (the camera's half of it).
    private Matrix4x4 velocityPrevViewProj;
    private bool velocityPrevValid;

    private GraphResourceHandle SampleablePrepassNormal =>
        MsaaSamples > 1 ? prepassNormalResolveHandle : prepassNormalHandle;

    // rgb = incident bounced radiance, a = sky visibility, at full resolution: the lit pass samples it as it is.
    private GraphResourceHandle incidentHandle;
    // Clears the full-size targets to zero while RTGI's readout is not ready: the lit pass reads no indirect light then.
    private PassHandle incidentPassHandle;
    // The incident light's luminance gradient with the normal: how the lit pass carries light evaluated at the
    // geometric normal to its normal-mapped one (rtgi_readout.frag).
    private GraphResourceHandle incidentGradientHandle;
    // RTGI's readout into half-size targets, and lighting-upsample bringing them (and the half-res traced shadow mask)
    // back to incidentHandle / incidentGradientHandle, the sun's visibility in the gradient's w.
    private GraphResourceHandle incidentHalfHandle, incidentGradientHalfHandle;
    private PassHandle lightingUpsamplePassHandle;
    private PipelineHandle lightingUpsamplePipeline;
    private ShaderInterface lightingUpsampleInterface = null!;
    // Which of its 2x2 a half-res pixel stands for this frame (LightingSubpixel), all four every four frames.
    private uint lightingFrame;
    private bool halfLightingNow;
    // RenderSettings.HalfResShadows, this frame: the sun rays on the half-res grid and the upsample carrying the mask.
    private bool halfShadowsNow;

    // The sheen half of the probe. Zero mips means the probe predates .blixprobe v4 and the shader
    // is told so rather than handed the specular cube, which would render a plausible non-answer.
    private TextureHandle sheenEnvTexture;
    private TextureHandle sheenLutTexture;
    private float sheenMipCount;
    private ShaderInterface cullInterface = null!;
    private ShaderInterface occlusionInterface = null!;
    // Smoothed frame period, for the overlay's "share of frame" readouts. The A/B harness keeps
    // its own unsmoothed samples.
    private double lastFramePeriodMs;

    private ShaderProgramHandle prepassOpaqueProgram;
    private ShaderProgramHandle prepassMaskProgram;
    private PipelineHandle prepassOpaquePipeline;
    private PipelineHandle prepassMaskPipeline;

    // Streamed-load preview: textures upload over frames via the loader's queue;
    // until they finish, the geometry renders flat (lit.vert + flat.frag, no
    // material textures sampled) and shadows, GI and IBL are skipped. The full
    // lit loop starts once textureLoader.PendingCount hits 0.
    private MaterialTextureLoader textureLoader = null!;
    private ShaderProgramHandle flatProgram;
    private PipelineHandle flatPipeline;
    private bool fullyLoaded; // textures all streamed → full render path

    // Lit pipelines — four variants spanning (Opaque|Mask vs Blend) ×
    // (BackFaceCulling vs NoCulling). All share one shader program; only
    // the depth/blend/rasterizer state varies. AlphaMode.Mask runs through
    // the opaque pipeline with the alpha-discard branch active in the
    // fragment shader (its `alphaCutoff > 0` guard skips when it's zero).
    private ShaderProgramHandle litProgram;
    // lit_opaque.frag: opaque materials' build, without the cutout, keeping the early depth test (lit_body.glsl).
    private ShaderProgramHandle litOpaqueProgram;
    private PipelineHandle opaqueSolidPipelineEarly, opaqueDoubleSidedPipelineEarly;
    // lit_lean.frag: the opaque build without the branches its materials never take (LIT_RARE), for the materials with
    // neither sheen nor diffuse transmission (not in richMaterials) on a frame where the rest cannot be taken (LitLeanNow).
    private ShaderProgramHandle litLeanProgram;
    private PipelineHandle opaqueSolidPipelineLean, opaqueDoubleSidedPipelineLean;
    private readonly HashSet<MaterialHandle> richMaterials = new();
    private PipelineHandle opaqueSolidPipeline;
    private PipelineHandle opaqueDoubleSidedPipeline;
    // Depth-WRITING twins of the opaque lit pipelines, for --ab prepass. The normal pair tests
    // LessEqual and does not write, because the pre-pass already laid the complete depth down; with
    // the pre-pass skipped the lit pass has to establish depth itself or it draws in submission
    // order over a cleared buffer.
    private PipelineHandle opaqueSolidPipelineWrites;
    private PipelineHandle opaqueDoubleSidedPipelineWrites;
    private PipelineHandle blendSolidPipeline;
    private PipelineHandle blendDoubleSidedPipeline;

    // Skybox program + pipeline. Drawn between opaque/mask and blend so
    // translucent windows composite over the sky correctly.
    private ShaderProgramHandle skyProgram;
    private PipelineHandle skyPipeline;

    // Present.
    private ShaderProgramHandle presentProgram;
    private PipelineHandle presentPipeline;
    private FullscreenPass fullscreen = null!;

    // [Tune]-tagged settings, auto-bound to the overlay's groups via ObjectTunables.
    private readonly ShadowsSettings shadows = new();
    private readonly RenderSettings render = new();
    // --- Headless capture -------------------------------------------------
    // Demo-owned capture records the final image and selected intermediate targets; Blix.Tools.Shot
    // covers Studio rather than this renderer.
    private string? shotPath;
    private int shotFrame = 240;
    private bool shotResized;
    private (int Width, int Height)? shotSizeAtLoad;        // long enough for the streamed textures to land
    // --frames-after-load N: close N frames after texture streaming finishes. A host --frames count
    // cannot bound this, because loading takes a varying ~1000 frames and the lit path only runs after it.
    private int? framesAfterLoad;
    private int framesRendered;
    // Frame periods for capture statistics. The long window distinguishes an effect from the
    // machine's ordinary frame-time spread.
    private readonly double[] framePeriodsMs = new double[600];
    private int framePeriodCount;
    private long lastFrameStamp;

    // Record the streamed flat path separately from the fully lit path. Both run in one process
    // with the same scene submission, making their timing comparison resistant to cross-run drift.
    private readonly double[] flatPeriodsMs = new double[600];
    private int flatPeriodCount;

    // Frames rendered since the scene finished loading. The reproducible clock: the walk, TAA's
    // jitter and the --shot deadline all read it, so a capture is a function of the flags alone.
    private int postLoadFrames;
    private bool shotWritten;

    // --ab MODE (--ab-flat: --ab flat): after loading, alternate between two arms every AbPeriodFrames
    // frames, bucketing frame times separately.
    //
    // Alternate A/B arms inside one process so thermal drift affects both samples on the same run.
    // "" = off. "flat" swaps the whole lit path for flat.frag; "shadow" zeroes uShadowStrength, and
    // the shading modes (textures, pbr, ibl, normal) set one of uAbFlags. Those are uniform-driven,
    // so they alternate without touching pipelines — which is what makes a fine interleave possible.
    // --no-prepass: drop the depth pre-pass for good, not as an A/B phase.
    private bool noPrepass;
    /// <summary>--probe &lt;name&gt;: a cooked .blixprobe to prefer over the default list.</summary>
    private string? probeName;
    /// <summary>--sun-overhead: straight down, so the courtyard is lit while base lighting is worked on.</summary>
    private bool sunOverhead;
    private string abMode = "";
    private bool abFlat;
    private const int AbPeriodFrames = 120;
    // --ab lod alternates two error budgets in-process. Defaults compare the configured budget with
    // full detail; explicit arms compare two non-zero quality points without cross-run drift.
    private float lodArmOn = -1f;
    private float lodArmOff;
    private float LodArmPixels => AbOffPhase ? lodArmOff : (lodArmOn >= 0f ? lodArmOn : render.LodErrorPixels);

    private bool AbOffPhase => abMode.Length > 0 && (framesRendered / AbPeriodFrames) % 2 == 1;
    private bool AbFlatPhase => abFlat && AbOffPhase;

    // --viz N: write a shading input instead of the lit colour. See lit.frag's uVizChannel.
    private float vizChannel;

    /// <summary>What each viz channel shows, in the order lit.frag tests them.</summary>
    private static readonly string[] VizChannelNames =
    {
        "Lit scene", "Geometric normal", "Shading normal", "Tangent-space normal",
        "Front/back facing", "Tangent", "Bitangent", "Sky visibility",
        "Bounce radiance (raw)", "Bounce contribution", "Direct sun only",
        "Texture AO",
        "Ambient: sky specular", "Ambient: transmitted", "Ambient: bounce",
        "Albedo (as shaded: texture, factor, vertex colour)",
        "Pre-pass normal (what the incident field reads)",
    };

    // How the lit pass carries the incident field's light to its normal-mapped normal (frame.uIncidentGradient).
    // --incident-normal-bias B: mips down the normal map is read for the gradient; --incident-gradient-clamp C: the
    // correction's factor stays in [1 - C, 1 + C] (it was [0, 3]). Sponza, still camera: pixels the gradient moves
    // (TAA off) against the presented image's median variation (TAA on): bias 0 (clamp 0-3) 13.0% / 6.69%, 1 5.3% /
    // 4.78%, 2 1.7% / 3.67%, 3 0.56% / 3.44%, none 0 / 3.40%. The detail IS the shimmer: what a normal map adds below
    // a few pixels, TAA's jitter samples differently every frame. Under the reworked TAA (motion reprojection,
    // Rgba16F history, 0.9) the same sweep reads bias 0/1/2 at 1.83/1.24/0.97% (no gradient 0.94%, default look
    // 0.83%), and at 1 the user saw "tiny spots of light and dark across walls": the presented-variation heat map
    // shows weave- and grain-scale speckle on every textured surface with it and none without. 2.
    private float incidentNormalBias = 2f;
    private float incidentGradientClamp = 0.5f;

    // Camera frustum culling (the overlay's Render > Frustum cull): off, every placement is drawn.
    private bool cullEnabled = true;
    // Triangles each fill submitted, so a pass's cost can be split between geometry and fill.
    private long fillIndirectTriangles;
    // FillIndirect's per-drawable level choices (-1 = culled), and how far into visibleScratch this
    // frame's fills reached (what UploadVisible sends).
    private int[] levelScratch = System.Array.Empty<int>();
    private int visibleWritten;
    private long cameraTriangles;
    private long cameraTriangleSum;
    private long triangleFrames;
    /// <summary>--no-foliage: skip the ivy and tree packs, to price alpha-cutout overdraw.</summary>
    private bool noFoliage;
    private int refBounces = 3;
    private int refPaths = 1024;
    private int refSeed;
    // The GPU reference meets thin sheets (KHR_materials_diffuse_transmission) as the spec has them (reference_wave.comp):
    // a path reflects off a sheet with 1 - T of its albedo or passes to the far side tinted T c, and the sun on either side
    // lights both. --ref-sky S: the sky's radiance scaled (0: the sun alone, for closed forms).
    private float refSky = 1f;
    // The GPU reference's sun rays (the wavefront's, on Metal) each toward its own point of the sun's disc, as the frame's
    // direct sun has it: one sun for the picture and its truth (2026-10-21; the gate's history before was against a point
    // sun). --ref-sun point: the disc's centre.
    private bool refSunDisc = true;

    // IBL textures: a cooked .blixprobe (real GGX prefilter) when present, else
    // the procedural sky bake (see OnLoad). Generated/uploaded once at load.
    //   envCube           prefiltered specular (mip chain at increasing roughness)
    //   irradianceCube    cosine-weighted hemisphere convolution of the sky
    //   brdfLut           split-sum BRDF integration (R = F0 scale, G = F0 bias)
    // Bound at set 1 (per-pass), so every draw in the lit pass samples them.
    private TextureHandle envCubeTexture;
    // Unfiltered environment cube used for the visible background. Specular IBL uses the separate
    // prefiltered mip chain above.
    private TextureHandle skyCubeTexture;
    private TextureHandle irradianceCubeTexture;
    private TextureHandle brdfLutTexture;
    // Mip count of whatever's bound to uPrefilteredEnv — the procedural env
    // cube (EnvMips) by default, or the cooked probe's GGX-prefilter mip count
    // when a .blixprobe is loaded. Drives the roughness→LOD mapping in lit.frag.
    private float iblPrefilterMips = ProceduralSky.EnvMips;

    // Bake-time sun direction (the irradiance + env cubes are baked once
    // with this sun position; live changes to the runtime sun direction
    // don't relight the IBL). Matches the runtime sun for consistency.
    private static readonly Vector3 SkyBakeSunDirection =
        Vector3.Normalize(new Vector3(0.35f, -0.85f, 0.25f));

    // Per-primitive draw payload. PipelineHandle is picked once at load
    // time from the material's AlphaMode + DoubleSided combination.
    private sealed record Drawable(
        // All primitives' vertices live in one shared VB and their LOD indices
        // in one shared IB (u16 or u32 per drawable). A draw is a sub-range:
        // vertexOffset = BaseVertex, indexOffset = LodFirstIndex[lod].
        bool IndicesAreU32,
        int BaseVertex,
        // Per-LOD firstIndex into the shared IB (LodFirstIndex[0] = full detail).
        int[] LodFirstIndex,
        int[] LodIndexCounts,
        // World-space geometric error per LOD level (0 for LOD0). Drives
        // screen-space-error selection in PickLod.
        float[] LodErrors,
        MaterialHandle Material,
        PipelineHandle Pipeline,
        // The primitive's own bounds, in mesh space. Where it stands is its placements' (each a
        // world matrix and the world AABB culling and LOD read); see Placement.
        Bounds3 MeshBounds,
        // The alpha cutoff (0 for OPAQUE): a cutout's group draws with the mask pipelines.
        float AlphaCutoff,
        // Source primitive name — selection/inspection identity (debug only).
        string Name,
        // This primitive's placements: a contiguous run of its bucket's placement list.
        int PlacementStart = 0,
        int PlacementCount = 0,
        // Each level's clusters, mesh space, FirstIndex relative to that level's LodFirstIndex. Empty for
        // a level the cook did not cluster.
        IReadOnlyList<MeshCluster>[]? LodClusters = null,
        // Both faces are drawn, so a cluster's normal cone says nothing about whether it can be seen.
        bool DoubleSided = false)
    {
        // Screen-space-error LOD: pick the COARSEST level whose stored world
        // error projects to ≤ errorPixels at the nearest point of the bounds,
        // which are one placement's (the same primitive stands at many distances).
        //   errorScale = viewportH / (2·tan(fovY/2))  → pixels-per-world at unit
        //   distance; pixels-per-world at distance d = errorScale / d.
        // errorPixels ≤ 0 forces full detail. Distance is to the NEAREST point
        // on the AABB (not the centre) so a big primitive you stand inside stays
        // detailed where it matters instead of coarsening on a far centre.
        // Identical across the lit and depth-pre-pass passes so depth stays
        // invariant. 1-LOD drawables (uncooked) always pick 0.
        public int PickLod(in Bounds3 bounds, Vector3 cameraPos, float errorScale, float errorPixels, int currentLevel)
        {
            if (LodIndexCounts.Length <= 1 || errorPixels <= 0f) return 0;
            var nearest = Vector3.Clamp(cameraPos, bounds.Min, bounds.Max);
            var d = MathF.Max((cameraPos - nearest).Length(), 0.01f);
            var pixelsPerWorld = errorScale / d;
            var wanted = 0;
            // Errors increase monotonically with level, so stop at the first
            // level that exceeds the budget — all coarser ones do too.
            for (var l = 1; l < LodIndexCounts.Length; l++)
            {
                if (LodErrors[l] * pixelsPerWorld <= errorPixels) wanted = l;
                else break;
            }

            // Hysteresis prevents continuous camera motion from toggling primitives parked on an
            // error boundary. Refinement is immediate; coarsening waits until the candidate is
            // comfortably within budget, biasing uncertainty toward detail.
            currentLevel = Math.Clamp(currentLevel, 0, LodIndexCounts.Length - 1);
            if (wanted <= currentLevel) return wanted;
            return SettleCoarser(currentLevel, wanted, errorPixels / LodHysteresis, pixelsPerWorld);
        }

        /// <summary>The coarsest level between here and `wanted` that clears the hysteresis band.</summary>
        /// <remarks>
        /// Tests intermediate levels in order, so a rejected target does not prevent settling on an
        /// earlier coarse level that already clears the hysteresis band.
        /// </remarks>
        private int SettleCoarser(int from, int wanted, float bandedBudget, float pixelsPerWorld)
        {
            var settled = from;
            for (var l = from + 1; l <= wanted; l++)
            {
                if (LodErrors[l] * pixelsPerWorld <= bandedBudget) settled = l;
                else break;
            }
            return settled;
        }
    }

    // Staging captured per primitive during load; consolidated into the shared
    // buffers once all packs are in (so the big VB/IB span every pack).
    private sealed record DrawableStaging(
        byte[] VertexBytes,
        int VertexCount,
        IReadOnlyList<MeshLod> Lods,
        MaterialHandle Material,
        PipelineHandle Pipeline,
        Bounds3 Bounds,
        // The base colour texture, which the rays' surface bake reads.
        TextureHandle Albedo,
        float AlphaCutoff,
        bool IsBlend,
        string Name,
        // Where the primitive stands: one world matrix per placement.
        Matrix4x4[] Worlds,
        // Which instance each world is (parallel to Worlds), and where the primitive came from: together, the
        // surface each placement row is (SurfaceKeyOf).
        PlacementInstance[] Instances,
        Blix.Assets.PrimitiveSource? Source,
        // The material's base colour factor (linear rgb, alpha): what a ray's surface bake multiplies the texture by.
        Vector4 BaseColor,
        // KHR_materials_diffuse_transmission: its factor times its colour factor (zero: none) -- what a sheet of it lets
        // through to its far side, which the bounce light will read as the sheet's glow (rayPlacementTransmission).
        Vector3 Transmission = default,
        // And the factor alone (the share of the light the transmission lobe takes; the base lobe keeps 1 - it): what the
        // GPU reference's thin sheets reflect and pass (reference_wave.comp).
        float TransmissionFactor = 0f,
        // KHR_materials_diffuse_transmission's factor texture (default: none -- the factor alone): its alpha, averaged over
        // each triangle by the rays' surface bake, scales the factor per row (ray_surface_bake.comp, BlixRayTransmission).
        TextureHandle TransmissionTexture = default,
        // And its colour texture (default: none -- white): its mean over each triangle tints the row's light through
        // (BlixRayTransmission's upper bytes, RowTransmissionColour).
        TextureHandle TransmissionColorTexture = default,
        // --cloth-transmission's curtains (ClothTransmission): their light through is the albedo's hue, capped at what
        // the lit face reflects (lit.frag, RtRowTransmit); an authored material's is its own colour, uncapped.
        bool Cloth = false);

    /// <summary>One placement of a unique primitive: which drawable, its row in the transform table, and its world AABB.</summary>
    /// <remarks>
    /// The unit of culling and LOD selection, which is why per-element state (LOD margins and levels) is
    /// indexed by placement. A primitive the file places once is one drawable with one placement; one it
    /// places a thousand times is still one drawable, and its geometry is uploaded once.
    /// </remarks>
    private readonly record struct Placement(int Drawable, int Transform, Bounds3 Bounds);

    /// <summary>A unique cooked primitive, in mesh space, with every world it is placed at and which instance each is.</summary>
    private sealed record PlacedPrimitive(ModelData.Primitive Primitive, Matrix4x4[] Worlds, PlacementInstance[] Instances);

    /// <summary>Which instance a world is: the pack, the node that draws it, and which of that node's draws.</summary>
    /// <remarks>
    /// A node draws its mesh once, or once per <c>EXT_mesh_gpu_instancing</c> instance (Draw counts them). With the
    /// primitive's <see cref="Blix.Assets.PrimitiveSource"/> this is a surface's identity (stage 4e): every cooked
    /// chunk of one source primitive, drawn by one instance, is one surface.
    /// </remarks>
    private readonly record struct PlacementInstance(int Pack, int Node, int Draw);

    // Shared geometry buffers (one VB + one IB per index width), built from the
    // staging list by ConsolidateBuffers (via MeshBundler). Every draw is a
    // sub-range; the per-group indirect draws in OnRender index into these.
    private readonly List<DrawableStaging> staging = new();
    private VertexBufferHandle sharedVb;
    private IndexBufferHandle sharedIbU16;
    private IndexBufferHandle sharedIbU32;
    private VertexLayout sharedLayout = VertexPosition3NormalTangentTexture.Layout;

    // Opaque drawables are grouped by (pipeline, material, index-width) so each
    // group is a contiguous run in the shared buffers + the indirect buffer.
    // Each frame fills one VkDrawIndexedIndirectCommand per drawable (LOD by
    // SSE), then issues ONE vkCmdDrawIndexedIndirect per group.
    private readonly record struct OpaqueGroup(
        PipelineHandle Pipeline, MaterialHandle Material, bool IsU32, bool IsMask, int Start, int Count);
    private readonly List<OpaqueGroup> opaqueGroups = new();
    // Blend (glass) groups + buffer — same per-(pipeline,material,width) grouping,
    // no cull, drawn after opaque + sky in the lit pass.
    private readonly List<OpaqueGroup> blendGroups = new();
    private IndirectBufferHandle blendIndirect;
    private IndirectBufferHandle opaqueIndirect;
    private byte[] indirectScratch = System.Array.Empty<byte>();
    // Two-bucket draw order: opaque/mask first, blend last. Within each
    // bucket draws stay in glTF primitive order; back-to-front sort for
    // the blend bucket is a deferred polish (would matter when the demo
    // moves at speed through translucent surfaces).
    private readonly List<Drawable> opaqueDrawables = new();
    private readonly List<Drawable> blendDrawables = new();
    // Their placements, contiguous per drawable in drawable order (Drawable.PlacementStart/Count).
    private readonly List<Placement> opaquePlacements = new();
    private readonly List<Placement> blendPlacements = new();
    // Every placement's world matrix, both buckets: set 3 binding 0 (instances.glsl), written once.
    private readonly List<Matrix4x4> sceneTransforms = new();
    // Each placement's material, by the same row: CPU only, for the surface-key census to compare against (it was
    // screen probes' identity before the SurfaceKey, and two walls of one stone share it).
    private readonly List<uint> sceneTransformMaterials = new();
    // Which placement instance each row is (stage 4e): the mover moves one instance's rows together.
    private readonly List<PlacementInstance> sceneTransformInstances = new();
    // Each placement row's SurfaceKey (stage 4e): one dense id per (instance, source primitive), from 1; every
    // cooked chunk of one source primitive on one instance shares it. 0 is no surface.
    private readonly List<uint> sceneTransformSurfaceKeys = new();
    // Set 3: the transform table and this frame's visible placement indices (every pass's list end to end).
    private IMaterialBindings? sceneInstances;
    private uint[] visibleScratch = System.Array.Empty<uint>();
    // How many indirect commands each drawable owns per pass: one per LOD level the deepest chain has, so
    // a drawable's placements at different levels draw from one indirect run.
    private int lodSlots = 1;
    // Indirect records a drawable: lodSlots, or 1 when no drawable is placed twice (SponzaLoop.Scene.cs).
    private int recordSlots = 1;
    // GPU culling (the default; --cpu-cull is the A/B): scene_cull.comp builds every pass's indirect records
    // and visible list on the GPU, and the draws read them from sceneArgs. The placements, drawables and
    // levels it reads are uploaded once; per-placement LOD state lives in cullState across frames. set 3
    // is then these buffers by name (sceneBuffers) rather than the sceneInstances material.
    private bool gpuCull = true;
    private bool litFlat;
    private bool litEarlyZ = true;
    private PipelineHandle flatSolidPipeline;
    private PassHandle cullPassHandle;
    private PipelineHandle cullPipeline;
    // Two-phase occlusion (scene_occlusion.comp): the early depth's own pyramid, the pass that tests against
    // it, and the late pre-pass that draws what it finds. Off under --no-occlusion and --cpu-cull.
    private bool occlusionCull = true;
    private bool occlusionCut;
    private readonly GraphResourceHandle[] occZHandles = new GraphResourceHandle[HiZLevels];
    private readonly PassHandle[] occZPassHandles = new PassHandle[HiZLevels];
    private readonly PipelineHandle[] occZPipelines = new PipelineHandle[HiZLevels];
    private PassHandle occlusionPassHandle;
    private PipelineHandle occlusionPipeline;
    private PassHandle latePrepassHandle;
    private GpuBufferHandle cullPlacements, cullDrawables, cullLods, cullState, cullCursor, sceneArgs, sceneVisible, sceneTransformBuffer,
        sceneSurfaceKeyBuffer, scenePreviousTransformBuffer;
    private ShaderBufferBinding[] cullBuffers = System.Array.Empty<ShaderBufferBinding>();
    private ShaderBufferBinding[] sceneBuffers = System.Array.Empty<ShaderBufferBinding>();
    // A Selection-panel margin edit not yet sent: (global placement index + 1, value), carried by the
    // next frame's classify dispatches into the one thread that owns the row.
    private (int Placement, float Margin) pendingMarginEdit;
    private bool sceneLoaded;
    // Background pack parse + budgeted main-thread drain (engine primitive):
    // started in OnLoad, drained by TryFinishLoad in OnUpdate. Produces the flat
    // primitive list off-thread; staging runs on the render thread.
    private readonly Blix.Render.AsyncLoadQueue<PlacedPrimitive> meshLoad = new();

    // Per-frame-reused, content-constant buffers built once at load (avoids
    // re-allocating them every frame). passBindings: the lit pass's set-1 IBL and
    // incident-light textures (all stable handles).
    private ShaderTextureBinding[] passBindings = null!;
    // The skybox's own list: it samples one of the lit pass's textures, and a name a program does
    // not declare is an error, not a skip.
    private ShaderTextureBinding[] skyBindings = null!;

    // The engine's camera controller: look, orbit, fly, zoom, and --cam, starting where the scene's profile
    // says. The three names below are what the renderer reads; they are the controller's now.
    private readonly CameraController camera;
    private Vector3 cameraPosition => camera.Position;
    private Vector3 cameraForward => camera.Forward;
    private float fovYRadians => camera.FieldOfView;
    private float aspect = 16f / 9f;

    private static CameraController SceneCamera(SceneProfile scene)
    {
        var lens = new Camera3D { VerticalFieldOfView = MathF.PI / 3f, NearPlane = scene.NearPlane, FarPlane = scene.FarPlane };
        return new CameraController(lens)
        {
            Position = scene.StartPosition, Yaw = scene.StartYaw, Pitch = scene.StartPitch, MoveSpeed = scene.MoveSpeed,
        };
    }
    private float renderHeightPx = 810f; // the render height in pixels, set each frame (RenderFrame); drives screen-space-error LOD
    // Screen-space-error LOD threshold: a level is used when its baked geometric error projects to
    // no more than this many pixels at the viewing distance. About 1 px targets imperceptibility;
    // raise it to trim more aggressively, while zero forces full detail. The metric accounts for
    // resolution, FOV, and primitive size. A huge unsplit primitive still receives one level, with
    // its near edge pinning the choice; cook-time spatial splitting supplies finer granularity.
    // pixels-per-world at unit distance = viewportH / (2·tan(fovY/2)); PickLod
    // divides by the view distance. Recomputed lazily from the fields above.
    private float LodErrorScale => renderHeightPx * 0.5f / MathF.Tan(fovYRadians * 0.5f);
    // How far inside budget a coarser level must be before selection. 1.35 is roughly one sixth of
    // a level step for the cook's 2x decimation and filters camera jitter near a threshold.
    private const float LodHysteresis = 1.35f;
    /// <summary>What multiple of the global LOD budget alpha-cutout geometry starts at.</summary>
    /// <remarks>
    /// One preserves the global budget. Foliage measured pixel-bound: an 8x margin removed only 14%
    /// of submitted triangles while risking silhouette quality. --foliage-lod retains the sweep.
    /// </remarks>
    private static float FoliageLodMargin = 1f;
    // What a pick can land on, rebuilt after consolidation. The loop answers the engine's selection
    // and inspection hooks by handing them to it (below); the engine does the picking.
    private readonly SceneSelection sceneSelection = new();
    // Per-placement LOD error-margin multipliers (×global px budget), keyed by
    // placement index — parallel to opaque/blend placements, default 1.0. Live,
    // ephemeral; edited via the Selection panel, consumed by PickLod.
    private float[] opaqueLodMargins = System.Array.Empty<float>();
    private float[] blendLodMargins = System.Array.Empty<float>();
    // Persistent per-placement camera LOD state supplies hysteresis and keeps depth, lit, and blend
    // passes on the same level decision within a frame.
    private int[] opaqueLodState = System.Array.Empty<int>();
    private int[] blendLodState = System.Array.Empty<int>();

    // Slack on the camera frustum test, in metres. See the call site in OnRender.
    private const float CameraCullMargin = 0.5f;
    /// <summary>--no-hashed-alpha: use binary rather than hashed cutouts at one sample.</summary>
    private Matrix4x4 viewProj;

    // Sun travel direction, recomputed from overlay yaw/pitch. A cooked probe with a detected sun
    // supplies the initial direction by default so direct light, shadows, and the visible sky agree;
    // --sun-authored retains this fallback direction instead. Live edits do not rebake IBL cubes.
    private Vector3 sunDirection = Vector3.Normalize(new Vector3(-0.1120f, -0.9568f, -0.2682f));

    // True unless --sun-authored opts out; probes without a detectable sun retain the fallback.
    private bool alignSunToProbe;

    private float sunYaw;
    private float sunPitch;
    /// <summary>
    /// The sun's irradiance, as MEASURED from the probe. Not a knob.
    /// </summary>
    /// <remarks>
    /// Fallback for the procedural sky, which has no measured sun. A real probe overwrites it, and
    /// the fallback's job is only to keep the demo lit when there is no HDR to measure — it is the
    /// one place a number is still chosen rather than derived, and it says so.
    /// </remarks>
    private Vector3 sunIrradiance = new(9.42f, 9.42f, 9.42f);

    /// <summary>Multiplier on measured probe sun irradiance; 1 preserves the measurement.</summary>
    /// <remarks>
    /// This is an explicit scene multiplier over a known quantity, not a replacement intensity.
    /// It applies consistently to direct raster lighting and transport injection.
    /// </remarks>
    private float sunStrength = 1f;

    /// <summary>The sun as everything downstream should see it.</summary>
    private Vector3 EffectiveSunIrradiance => sunIrradiance * sunStrength;

    // Shader-uniform tunables (the cutout and bounce-term diagnostics) are declared with //@tune in
    // frame.glsl and auto-bound to the overlay by this panel — no per-variable field + dial + UBO
    // pack here. The panel owns the live values (seeded from the shader's tag defaults) and feeds
    // them into the per-frame write by name.
    private ShaderTunablePanel tunePanel = null!;
    // [Tune]-tagged CPU settings objects (Shadows, Render, the camera) auto-paneled in the overlay.
    private ObjectTunables tuneObjects = null!;

    // Main thread (from OnUpdate): drain the background-parsed primitives into
    // GPU resources WITHOUT freezing. The bulk of the cost is per-material
    // texture read+upload (~7s if done at once), so the AsyncLoadQueue time-slices
    // it: it stages only a few-ms budget of primitives per frame. The window
    // renders its loading clear (responsive) between chunks; once the queue drains
    // we consolidate the shared buffers and flip sceneLoaded. (Backgrounding the
    // texture reads or placeholder-streaming would make the scene *appear* sooner
    // — see the PR notes — but this fully removes the freeze with no extra
    // plumbing.)
    private const double LoadBudgetMs = 8.0;

    // Cache demo-specific GPU material bindings by canonical material identity. Texture identity is
    // shared through TextureRegistry; binding layout and packed values remain owned by Sponza.
    private readonly Dictionary<string, (MaterialHandle Mat, TextureHandle Albedo, float Cutoff)> materialCache = new(StringComparer.Ordinal);
    private (MaterialHandle Mat, TextureHandle Albedo, float Cutoff)? noMaterialCache;
    // The graph owns its passes' render passes and framebuffers and its targets' memory, and only its
    // Dispose frees them: the device destroys what is in its own tables and nothing else. Sponza
    // disposed only the fullscreen pass, and validation counted 70 objects alive at vkDestroyDevice
    // (34 render passes, 17 framebuffers, 19 allocations). Null when the pack set was missing and
    // OnLoad returned before building it.
    public void Dispose()
    {
        ProfileSessionEnd();
        fullscreen?.Dispose();
        ReleaseDeviceResources();
        graph?.Dispose();
    }

}

// <b>The [Tune] bools below carry an explicit `= false`.</b> The overlay assigns them through
// reflection, which the compiler cannot see, so each one raised a CS0649 "never assigned". Three
// false positives in the warning channel is how the two fields that WERE dead -- a never-read
// vsync flag and a never-used triangle-window bool -- sat there being reported and ignored. The
// initialiser costs nothing and keeps that channel worth reading.

// Tonemap operators (overlay Render → Tonemap). The value indexes present.frag's branch, so the
// build checks these names against the //@tune enum{} above uTonemap there.
[ShaderEnum("present.frag", "uTonemap")]
internal enum TonemapMode { Reinhard, ACES, AgX, Hejl }

// Sun-shadow tunables (overlay "Shadows" group): the traced shadows (SponzaLoop.RtShadows) and the
// per-frame uShadowStrength write read them live.
internal sealed class ShadowsSettings
{
    [Tune]            public bool Enabled = true;
    // The sun's angular diameter the traced shadows see, in degrees (the real sun's is 0.53). Each frame
    // the rays aim at another point of the disc and TAA averages them: a penumbra that widens with the distance from
    // the occluder, hard at contact. 0: a point sun, every shadow hard -- the default since 2026-10-21, for a while:
    // with the penumbra filter, shadow interleave and TAA off, a point sun gives a reproducibly correct image, and the
    // GPU reference follows it (--ref-sun disc reads this). --sun-disc D: the disc.
    [Tune(0f, 5f)]    public float SunDiscDegrees = 0f;
    // The disc's penumbra filtered in space (rt_shadow_filter.comp): every ray toward the sun's centre (coherent, no
    // direction per pixel), the hard shadow widened by blocker distance x the disc. Stable without TAA. Off: each ray
    // toward its own point of the disc, a different one each frame, and TAA averages them (noisy, incoherent rays).
    // Off by default (2026-10-21; a point sun needs none). --penumbra-filter: on.
    [Tune]            public bool PenumbraFilter = false;
    // The traced shadows: a quarter of the pixels traced a frame, one of each 2x2 in turn (rt_shadow_fill.comp fills the
    // rest from last frame's mask, reprojected, or this frame's neighbours). Off: every pixel every frame -- the default
    // (2026-10-21: no frame's shadow from another's). --rt-shadow-interleave: on.
    [Tune]            public bool Interleave = false;
}

// Misc render tunables (overlay "Render" group). Vsync stays a manual toggle —
// it's a device property, not a field.
internal sealed class RenderSettings
{
    // Display exposure, independent from measured sun/sky energy: it accounts for the display level of
    // the physically attenuated composition, while transport strength remains one so material albedo is
    // not silently reinterpreted. One is the presentation default (a scene's profile or --exposure may
    // set another); lighting-strength overrides remain separately visible scene choices.
    [Tune(0.05f, 16f)] public float Exposure = 1.0f;
    [Tune]             public TonemapMode Tonemap = TonemapMode.AgX;
    // Two pixels is the current cost/quality knee: 1 -> 2 px removed 22% of submitted geometry for
    // 1.5 ms, while 2 -> 4 px saved another 17% for only 0.56 ms. Use the exact LOD census when
    // retuning rather than relying on noisy frame time alone.
    [Tune(0f, 8f)]     public float LodErrorPixels = 2.0f;
    // The scene's resolution as a fraction of the window's, per axis (RenderGraph.RenderScale): every scene pass renders
    // at it, TAA resolves at it, and the present pass scales the result up to the window. --render-scale S. 1 by default
    // (the user's, 2026-10-22: the image judged at full resolution, as the gate runs; 0.75 was the 2026-10-20 baseline,
    // the tree view 21.5 -> 28.5 fps).
    [Tune(0.5f, 1f)]   public float RenderScale = 1f;
    // The traced sun shadows at half resolution, as RTGI's readout always is, each half-res pixel standing for one of its
    // 2x2 (a different one each frame with TAA, gathering all four; without, the same one), brought back to full
    // resolution along the depth and normals with the incident light (lighting_upsample.frag). Off by
    // default again since 2026-10-27 (the user's: full-resolution shadows); on 2026-10-24-27, for the trace growing with
    // the sunlit pixels, 7.5-18 ms a frame close to a sunlit wall at full resolution, 0.6-1.8 at half -- at the cost of
    // blocky patches the upsample could not hide, worst on cloth. Cloth's shadows stair-step at either (to come).
    // --half-res-shadows: on.
    [Tune]             public bool HalfResShadows = false;
    // Two-phase occlusion culling of the camera's opaque placements (scene_occlusion.comp; --no-occlusion turns it off
    // for the whole run). Off: the frustum alone decides -- the reference, for geometry that breaks up and comes back.
    [Tune]             public bool OcclusionCull = true;
    // History weight for the full-screen resolve; zero is the current-frame reference. History is
    // rejected off-screen and neighbourhood-clamped so the present frame remains authoritative.
    // 0 by default (2026-10-21): each frame its own, reproducibly; --taa W for the history.
    [Tune(0f, 0.97f)]  public float Taa = 0f;
    // Bright where history was refused or clamped back. A still camera should show nearly nothing.
    [Tune]             public bool ShowTaaRejection = false;
}

// Pickable scene primitives for the diagnostics overlay. Built after geometry
// consolidation: one DebugSelectable per drawable, keyed by a session-stable
// path. The loop hands the engine's selection and inspection hooks to it, so
// the engine's pick lands on a primitive and the Selection panel shows its
// identity and LOD info. Selection is live debug state only — nothing persists.
internal sealed class SceneSelection
{
    private readonly List<DebugSelectable> selectables = new();
    private readonly Dictionary<string, Entry> byPath = new();
    private readonly record struct Entry(string Name, Bounds3 Bounds, int LodLevels, float MaxError);

    public void Rebuild(
        IReadOnlyList<(string Path, string Name, Bounds3 Bounds, DebugPickGeometry Geometry, int LodLevels, float MaxError)> items)
    {
        selectables.Clear();
        byPath.Clear();
        foreach (var it in items)
        {
            selectables.Add(new DebugSelectable(it.Path, it.Bounds, it.Geometry, it.Name));
            byPath[it.Path] = new Entry(it.Name, it.Bounds, it.LodLevels, it.MaxError);
        }
    }

    public void CollectSelectables(List<DebugSelectable> destination) => destination.AddRange(selectables);

    // By path, which is what keeps the engine's highlight from walking every primitive each frame.
    public bool TryGetBounds(string entityPath, out Bounds3 bounds)
    {
        if (byPath.TryGetValue(entityPath, out var e)) { bounds = e.Bounds; return true; }
        bounds = default;
        return false;
    }

    public void Inspect(string entityPath, DebugContext debug)
    {
        if (!byPath.TryGetValue(entityPath, out var e)) return;
        debug.Values.Value("name", e.Name);
        debug.Values.Value("bounds-min", e.Bounds.Min);
        debug.Values.Value("bounds-max", e.Bounds.Max);
        // Grouped with the margin the loop adds to the same scope, so the Selection tab shows the LOD
        // facts and the edit to them together.
        using (debug.Scope("LOD"))
        {
            debug.Values.Value("levels", e.LodLevels);
            debug.Values.Value("max-error", e.MaxError);
        }
    }
}
