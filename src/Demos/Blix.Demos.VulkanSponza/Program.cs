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
// Render graph, abbreviated: three shadow cascades → probe/froxel compute →
// depth + normal pre-pass → Hi-Z → GTAO + denoise → incident-light field →
// lit scene (R11G11B10F, optional MSAA resolve) → parity TAA → present.
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
//   • RenderGraph at scale: graphics + compute, history reads, cascaded shadows,
//     depth/normal pre-pass, Hi-Z, GTAO, incident light, TAA, and MSAA resolves
// ── Intentionally owns (stays local; don't extract until a 2nd consumer needs it) ──
//   • Sponza-specific draw groups, LOD/cull policy, cutout-foliage routing
//   • probe transport, the exact pass wiring, measurement modes, and per-scene tuning
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
        var defaults = new WindowOptions($"Blix — {scene.Title}", 1440, 810) { Diagnostics = true };
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
        cascadeSplits = (float[])scene.CascadeSplits.Clone();
        // Before ObjectTunables reads the flags, so --tune and the overlay start from the scene's values.
        shadows.SunDistance = scene.SunDistance;
        fog.Far = scene.FogFar;
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
    // --taa-linear: blend in linear space rather than luminance-tonemapped (the control for that blend's bias).
    private bool taaLinearBlend;
    // --taa-history11: the old R11G11B10F history (see above).
    private bool taaHistory16 = true;
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
    private PassHandle depthPrepassHandle;

    // --- Ambient visibility (GTAO) ----------------------------------------
    // The pre-pass rasterises every non-blend drawable into sample-count-matched depth. Under MSAA
    // it also resolves depth to a sampleable 1x target for GTAO; at 1x the original target is used.
    // The resolve rides the pass store rather than requiring another geometry pass.
    // Shared by projection, Hi-Z linearisation, and GTAO's background test.
    private const float CameraNearPlane = 0.1f;
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
    private GraphResourceHandle ambientHandle;        // rgb = bent normal (world), a = visibility
    // --- Hi-Z depth pyramid -----------------------------------------------
    // Six levels from half the framebuffer down, each the min/max linear view depth of its parent's
    // footprint. Separate targets rather than mips of one image — see hiz_build.frag for why.
    private const int HiZLevels = 6;
    private readonly GraphResourceHandle[] hiZHandles = new GraphResourceHandle[HiZLevels];
    private readonly PassHandle[] hiZPassHandles = new PassHandle[HiZLevels];
    private ShaderProgramHandle hiZProgram;
    private readonly PipelineHandle[] hiZPipelines = new PipelineHandle[HiZLevels];

    private GraphResourceHandle ambientDenoisedHandle; // what the lit pass actually samples
    private PassHandle gtaoPassHandle;
    private PassHandle gtaoDenoisePassHandle;
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
    private bool SurfaceTargets => MsaaSamples == 1;
    // The surface-key target as the previous frame left it (copied at the end of each frame), and TAA's per-pixel
    // accumulated count, ping-ponged with its colour (R32Uint: an integer, read at the nearest pixel).
    private GraphResourceHandle surfaceKeyHistoryHandle;
    private PassHandle surfaceKeyHistoryPassHandle;
    private PipelineHandle surfaceKeyHistoryPipeline;
    private readonly GraphResourceHandle[] taaCountHandles = new GraphResourceHandle[2];
    // --taa-no-key: the surface-identity test off in taa_surface.frag (the A/B for what it rejects).
    private bool taaNoKey;
    // Last frame's un-jittered view-projection, for the velocity (the camera's half of it).
    private Matrix4x4 velocityPrevViewProj;
    private bool velocityPrevValid;

    private GraphResourceHandle SampleablePrepassNormal =>
        MsaaSamples > 1 ? prepassNormalResolveHandle : prepassNormalHandle;

    // rgb = incident bounced radiance, a = baked sky visibility, at incidentScale.
    private GraphResourceHandle incidentHandle;
    private PassHandle incidentPassHandle;
    private PipelineHandle incidentPipeline;
    // What the lit pass actually samples: the coarse field reconstructed to full resolution.
    private GraphResourceHandle incidentFullHandle;
    // The incident light's luminance gradient with the normal, at incidentScale and resolved to full size: how the
    // lit pass carries light evaluated at the geometric normal to its normal-mapped one (incident_clipmap.frag).
    private GraphResourceHandle incidentGradientHandle;
    private GraphResourceHandle incidentGradientFullHandle;
    private PassHandle incidentResolvePassHandle;
    private PipelineHandle incidentResolvePipeline;
    private ShaderProgramHandle gtaoProgram;
    private PipelineHandle gtaoPipeline;
    private ShaderProgramHandle gtaoDenoiseProgram;
    private PipelineHandle gtaoDenoisePipeline;
    private readonly AmbientSettings ambient = new();

    // Baked sky visibility (.blixsky), uploaded as three Rgba16F 3D textures of L2 coefficients.
    // Three volumes for nine L2 coefficients: L0+L1, four L2 terms, and the last with slots spare.
    private readonly TextureHandle[] skyVisibilityTextures = new TextureHandle[3];
    private TextureHandle skyVisibilityTexture;
    private Vector3 skyVolumeMin;
    private Vector3 skyVolumeInvSpan;
    private bool skyVolumeLoaded;

    // The standard path enables baked sky visibility and runtime bounce. --no-sky disables both.
    private bool skyVisibilityEnabled;

    // Runtime sun-bounce injection over the shipped voxel grid.
    private TextureHandle occupancyTexture;
    // Linear surface colour per occupancy cell, used by transport at voxel hits.
    private TextureHandle albedoTexture;
    // The sheen half of the probe. Zero mips means the probe predates .blixprobe v4 and the shader
    // is told so rather than handed the specular cube, which would render a plausible non-answer.
    private TextureHandle sheenEnvTexture;
    private TextureHandle sheenLutTexture;
    private float sheenMipCount;
    // Ping-pong atlases let shading read the prior solve without a same-frame compute-to-fragment
    // dependency. That dependency measured roughly 15 ms even though either side alone was near
    // free; one frame of latency is smaller than the probe sweep's own amortisation.
    private readonly TextureHandle[] bounceTextures = new TextureHandle[2];
    // Directional mean hit distance, variance, and reachability, using the same tile layout. These
    // let consumers reject a probe hidden behind geometry from the shading point.
    private readonly TextureHandle[] bounceDepthTextures = new TextureHandle[2];

    /// <summary>
    /// Probe counts for the dynamic incident-light atlas.
    /// </summary>
    /// <remarks>
    /// Each probe carries a 6x6 directional interior. Spatial density is controlled independently
    /// from the baked visibility grid because marching cost scales with total probe count.
    /// </remarks>
    private int bounceX, bounceY, bounceZ;

    // Which probes shading read, and how quickly an unread probe sleeps. Zero disables sleeping.
    private TextureHandle probeUsageTexture;
    private PassHandle probeUsagePassHandle;
    private ShaderInterface usageInterface = null!;
    private ShaderInterface cullInterface = null!;
    private ShaderInterface occlusionInterface = null!;
    private PipelineHandle probeUsagePipeline;
    // Usage marking reads prior-frame depth before the current pre-pass. A current-frame dependency
    // made the isolated injection cheaper but the whole frame slower by breaking tile pass merging.
    // Smoothed frame period, for the overlay's "share of frame" readouts. The A/B harness keeps
    // its own unsmoothed samples.
    private double lastFramePeriodMs;
    /// <summary>Frames an unread probe remains awake; zero is the no-sleep baseline.</summary>
    /// <remarks>
    /// Sixteen frames is the current balance between camera-motion wake latency and dense-grid
    /// injection cost. At 41,472 probes, disabling sleep measured 2.96 ms -> 26.26 ms.
    /// </remarks>
    private float probeSleepFrames = 16f;
    /// <summary>The sleep setting the --ab sleep arm restores in its off phase.</summary>
    private float ProbeSleepNow => abMode == "sleep" && AbOffPhase ? 0f : probeSleepFrames;
    private const int OctTile = 8;
    private int bounceWrite;
    /// <summary>Experimental single-atlas transport path retained for the carry-cost comparison.</summary>
    /// <remarks>
    /// Carrying skipped tiles costs about 76 MB/frame at the tested dense configuration. Removing
    /// it measured 2.22 ms versus 1.61 ms for ping-pong and introduces sampled/storage aliasing, so
    /// the paired path remains standard. The flag preserves a repeatable comparison.
    /// </remarks>
    private bool probeCarryless;
    private bool probePingPong => !probeCarryless;
    private int BounceRead => probePingPong ? (bounceWrite ^ 1) : bounceWrite;
    private int skyBounceBinding = -1;   // where uSkyBounce sits in passBindings
    private ShaderProgramHandle injectProgram;
    private PipelineHandle injectPipeline;
    private PassHandle injectPassHandle;
    private ShaderTextureBinding[] injectBindings = Array.Empty<ShaderTextureBinding>();
    private int probeX, probeY, probeZ, occX, occY, occZ, albX, albY, albZ;
    private Vector3 skyVolumeSpan;
    private bool bounceReady;
    private float injectRays = 256f;
    // False treats occupancy as binary; true uses the baker's continuous density. Keep this as a
    // live A/B so the interpretation and its image effect remain visible in the same process.
    private bool injectDensity = true;
    // Frames for a full refresh of the probe grid, chosen by sweeping it against both cost and the
    // converged image:
    //
    //     period   8   36.15 ms   scene mean 0.0348
    //     period  16   30.72 ms   scene mean 0.0348
    //     period  32   28.21 ms   scene mean 0.0349
    //     period  64   25.62 ms   scene mean 0.0323
    //
    // Thirty-two is where the output stops changing and the cost has not yet stopped falling: the
    // same picture as eight, eight milliseconds cheaper, a full refresh in about 0.6 s at 50 fps. At
    // sixty-four the steady state starts to drift, which is the multi-bounce feedback no longer
    // keeping up with its own convergence.
    private float injectPeriod = 32f;
    // A SCALE on the baked per-material value, not the value. 1.0 takes the material at its word;
    // the slider stays because the term is judged by eye, and dragging it to 0 is the A/B.
    //
    // <b>It was a global 0.5 for the whole scene, and the note explaining why named the wrong
    // extension.</b> That note said Sponza authors transmission nowhere, every material reporting
    // TransmissionFactor 0 — true, and about KHR_materials_transmission, the clear pane you see
    // THROUGH. What the bounce needed was KHR_materials_diffuse_transmission, the thin sheet that
    // GLOWS backlit, which the scene's patches author on exactly the surfaces that have it:
    // LeafSpring and IvyLeaf at 0.45, and the three curtains at an explicit measured 0. Two
    // extensions, two lines apart in the material table, and checking the near one made a
    // per-material bake look like it would buy nothing.
    //
    // The cost of the conflation was not subtle once found: every non-opaque cell in the scene
    // scattered half its light, so the curtains passed tinted light in the bounce that their own
    // material says they do not pass.
    private float injectTransmissionScale = 1.0f;

    // Isolate the sky path's two costs: the compute solve and the two 3D fetches made by every lit
    // fragment. Amortising the dispatch 8x recovered only 4 of 19 ms, so keep both switches for a
    // direct measurement rather than attributing the remainder by inference.
    private bool skipInject;    // --sky-no-inject
    private bool skipSkySample; // --sky-no-sample

    private ShaderProgramHandle prepassOpaqueProgram;
    private ShaderProgramHandle prepassMaskProgram;
    private PipelineHandle prepassOpaquePipeline;
    private PipelineHandle prepassMaskPipeline;

    // Streamed-load preview: textures upload over frames via the loader's queue;
    // until they finish, the geometry renders flat (lit.vert + flat.frag, no
    // material textures sampled) and shadows/IBL/fog are skipped. The full
    // lit+shadow loop starts once textureLoader.PendingCount hits 0.
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
    // Its own dummy pair rather than FullscreenPass's: that one indexes three vertices for a
    // triangle and a probe impostor needs four for a quad. Contents are never read — the vertex
    // shader synthesises corners from gl_VertexIndex, same trick, one more corner.
    private VertexBufferHandle probeVb;
    private IndexBufferHandle probeIb;
    private ShaderProgramHandle probeProgram;
    private PipelineHandle probePipeline;

    // The probe view. Off by default — it draws a sphere per probe, and there are 41,472 of them.
    private bool showProbes;
    private float probeRadius = 0.08f;
    // 0 = bounce, 1 = sky visibility, 2 = usefulness, 3 = reachability.
    private float probeField;
    private float probeExposure = 1f;

    // --- Froxel volumetric fog --------------------------------------------
    // A compute pass fills a view-aligned 3D grid with sun in-scattering and
    // transmittance (shadow-aware, sampling the cascade maps), ordered after
    // the shadow passes and before the lit pass, which composites it per
    // fragment. The first real consumer of the compute layer + 3D textures.
    // Each XY froxel spans eight framebuffer pixels; Z subdivides the fog range independently.
    // Compute cost scales with X*Y*Z, so resizing changes screen-space density while the slice
    // control changes depth integration quality.
    private const int FroxelPixels = 8;
    /// <summary>--fog-slices N: depth slices in the froxel grid. See the note on the default.</summary>
    /// <remarks>
    /// Twenty-four slices pair with jittered temporal accumulation. Use 48 slices and zero temporal
    /// weight to compare depth resolution without history.
    /// </remarks>
    private int froxelGridZ = 24;
    private int froxelGridX, froxelGridY;
    private int froxelGridBinding = -1;
    private int skyFroxelGridBinding = -1;

    // The grid dimensions a framebuffer of this size asks for. Floored well above zero so a
    // minimised or absurdly small window still has a grid to dispatch over.
    private static (int X, int Y) FroxelGridSize(int width, int height) =>
        (Math.Max(8, (width  + FroxelPixels - 1) / FroxelPixels),
         Math.Max(8, (height + FroxelPixels - 1) / FroxelPixels));
    private ShaderProgramHandle froxelProgram;
    private PipelineHandle froxelPipeline;
    private TextureHandle froxelGridTexture;
    // Ping-ponged pre-integration medium history: rgb is scattered radiance and a is extinction per
    // froxel. Reprojection may read a different texel from the one being written, so aliasing one
    // texture would be a real read/write hazard.
    private readonly TextureHandle[] fogScatterTextures = new TextureHandle[2];
    private int fogScatterWrite;
    private Matrix4x4 prevFogViewProj = Matrix4x4.Identity;
    private Vector3 prevFogCamPos;
    private bool fogHistoryValid;
    private PassHandle froxelPassHandle;
    // Volumetric-fog tunables — [Tune]-tagged, auto-bound to the overlay "Fog"
    // group via ObjectTunables (see FogSettings).
    private readonly FogSettings fog = new();
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

    // Frames rendered since the scene finished loading. The reproducible clock: the orbit's angle
    // and the --shot deadline both read it, so a capture is a function of the flag alone.
    private int postLoadFrames;
    private bool shotWritten;

    // --no-mask: force every cutout material's alphaCutoff to zero. Nothing then routes to a MASK
    // pipeline, so no pass samples albedo just to discover a fragment is air — not the camera pass,
    // not the depth pre-pass, not any shadow cascade. The picture is wrong on purpose (leaves become
    // solid cards); the point is the frame time beside it.
    private bool forceOpaqueMask;

    // --ab-flat: after loading, alternate between the FLAT path and the full lit path every
    // AbPeriodFrames frames, bucketing frame times separately.
    //
    // Alternate A/B arms inside one process so thermal drift affects both samples on the same run.
    // "" = off. "flat" swaps the whole lit path for flat.frag; "shadow" zeroes uShadowStrength;
    // "gtao" zeroes the search radius. The last two are uniform-driven, so they alternate without
    // touching pipelines — which is what makes a fine interleave possible at all.
    // --no-prepass: drop the depth pre-pass for good, not as an A/B phase.
    //
    // --no-prepass also removes its Hi-Z and GTAO dependants. It measures that whole dependency
    // chain, not a depth-only optimization; the recorded comparison was 0.802x frame time.
    private bool noPrepass;
    /// <summary>--probe &lt;name&gt;: a cooked .blixprobe to prefer over the default list.</summary>
    private string? probeName;
    /// <summary>--bounce-div N: dynamic-light grid dimensions equal visibility dimensions divided by N.</summary>
    /// <remarks>
    /// This is continuous because total probe count is cubic in the per-axis divisor. One matches
    /// the cooked visibility grid (48x27x32), costing about 2 ms and 40 MB. Finer spacing cannot add
    /// baked visibility detail; grid-aligned interpolation bands require better placement or
    /// reconstruction rather than still more probes.
    /// </remarks>
    private float bounceDiv = 1f;
    /// <summary>--sun-overhead: straight down, so the courtyard is lit while base lighting is worked on.</summary>
    private bool sunOverhead;
    private string abMode = "";
    private bool abFlat;
    private const int AbPeriodFrames = 120;
    /// <summary>--orbit: a closed camera path, one revolution per A/B phase. See ApplyOrbit.</summary>
    private bool orbit;
    // --orbit-frames N: a slower revolution, for what a walking camera sees (screen-probe history); the A/B
    // alignment above holds only at the default.
    private int OrbitFrames = AbPeriodFrames;
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
        "Probe UV", "Raw probe L0",
        "Bounce radiance (raw)", "Bounce contribution", "Direct sun only",
        "GTAO visibility", "Texture AO", "Occlusion product",
        "Probe confidence (red = fallback)",
        "Ambient: sky diffuse", "Ambient: sky specular",
        "Ambient: transmitted", "Ambient: bounce",
        "Probe leak (red = weight through walls)",
        "Pre-pass normal (what the incident field reads)",
    };

    // --ao-fullres: run ambient visibility at framebuffer resolution instead of half. Half res is
    // the right default for a low-frequency term, but a crease a few centimetres wide is not low
    // frequency, and at half res plus a 3x3 bilateral it spans about one texel before being blurred
    // with its neighbours. This exists to find out whether fold detail is lost to RESOLUTION rather
    // than to the search radius.
    private float aoScale = 0.5f;

    // The incident-light field's resolution, as a fraction of the framebuffer. Full by default: at half the
    // bounce visibly loses detail on close, folded surfaces (seen headed on Sponza, 2026-10-05), for a cost
    // within run-to-run noise (31.2 / 32.7 ms full against 31.1 / 32.3 half on the orbit). --incident-scale
    // for anything less.
    private float incidentScale = 1f;
    // Standard path: reconstruct sky visibility and bounce into the half-resolution incident field.
    // This measured -14.64 ms on the orbit and -29.99 ms with occupancy marching. --no-incident
    // preserves the inline reference path for comparison.
    private bool incidentField = true;

    // Multi-bounce feedback strength in the injection solve. 0 = single bounce, which is the
    // discriminator for whether a colour cast is transport leakage accumulating over rounds.
    private float injectFeedback = 1f;
    // Occupancy-march strength for feedback visibility, parallel to shading's uProbeOcclusion.
    private float transportOcclusion;

    // --ao-debug N: make the GTAO pass write an intermediate instead of the bent normal. See gtao.frag.
    private float aoDebug;

    private bool fogStress;             // --fog-stress: auto-toggle fog to exercise the on/off barrier transitions under validation
    private int fogStressFrame;

    // --- Cascaded sun shadow maps -----------------------------------------
    // Three depth-only cascades fitted to camera-frustum slices, snapped to
    // the texel grid for swim-free motion. One depth target + one graphics
    // pass per cascade; a single shadow pipeline serves all three (the
    // per-cascade light view-proj rides the shadow.vert push constant). The
    // lit pass samples all three via a Count=3 sampler array at set 1
    // binding 3 and selects the first cascade whose fitted volume contains the fragment.
    private const int CascadeCount = 3;
    // Per-cascade shadow-map resolution. The near two cascades carry the
    // detail the eye lands on, so they stay at 2048²; the far cascade covers a
    // huge world area where per-texel detail matters least, so it's 1024². The
    // lit/froxel PCF reads textureSize() so it adapts to each map automatically;
    // only texel-snapping + bias need the per-cascade size (see UpdateCascades).
    /// <summary>--shadow-maps A,B,C: the three cascade resolutions. See the note on the default.</summary>
    /// <remarks>
    /// Resolution affects both fill and caster LOD because world texel size supplies the caster
    /// error budget. At the current fit, cascade 0 measured about 2.5x cascade 2. Its 2048 map
    /// resolves more finely than the two-texel surface filter, leaving room for future retuning.
    /// </remarks>
    private static int[] ShadowMapSizes = { 2048, 2048, 1024 };
    // Camera-depth slices fit each cascade; shader selection is by fitted-volume containment. The
    // scene's own (SceneProfile.CascadeSplits), copied because the overlay edits them live.
    private readonly float[] cascadeSplits;
    // Per-cascade frustum culling of shadow casters (overlay toggle + margin).
    private bool cullEnabled = true;
    private float cullMargin = 0.5f;
    private readonly GraphResourceHandle[] cascadeHandles = new GraphResourceHandle[CascadeCount];
    private readonly PassHandle[] cascadePassHandles = new PassHandle[CascadeCount];
    private readonly Matrix4x4[] cascadeViewProj = new Matrix4x4[CascadeCount];
    // Shadow-map caching: the last VP a cascade's depth map was rendered with.
    // A cascade's texel-snapped VP is bit-identical frame-to-frame while the
    // camera + sun + splits hold (and for sub-texel camera moves, thanks to the
    // snap), so we skip re-rendering an unchanged cascade and let the lit pass
    // sample the persisted depth target. Drops the full ~11.5M-tri redraw per
    // static cascade — the dominant GPU cost when the view is still.
    private readonly Matrix4x4[] cachedCascadeViewProj = new Matrix4x4[CascadeCount];
    // Whether each cascade re-rendered this frame (vs. served from cache) —
    // surfaced in the overlay so the caching is visible.
    private readonly bool[] cascadeRendered = new bool[CascadeCount];
    /// <summary>One shadow texel in WORLD units, per cascade — derived from the cascade fit.</summary>
    /// <remarks>The shared shadow path derives normal offset and filter scale from this length.</remarks>
    private readonly float[] cascadeTexelWorld = new float[CascadeCount];
    // Per-cascade shadow-caster survivor counts after frustum culling,
    // surfaced live in the diagnostics overlay (see Debug()).
    private readonly int[] cascadeDrawCounts = new int[CascadeCount];
    /// <summary>Caster count each cascade was last rendered with — see the cache in OnRender.</summary>
    private readonly int[] cachedCascadeCasters = new int[CascadeCount];
    // The LOD budget each cached cascade was built at; see the cache test in UpdateCascades.
    private readonly float[] cachedCascadeLod = new float[CascadeCount];
    private readonly Matrix4x4[] cachedCascadeCamera = new Matrix4x4[CascadeCount];
    // --- cascade scheduling ---------------------------------------------------
    // How much larger than its slice each cascade is fitted, and how many frames it may be reused.
    // Cascade 0 is never reused: it carries contact shadows, it is the cheapest of the three to
    // re-render, and padding it would cost exactly the sharpness it exists for.
    private static readonly float[] CascadePad = { 0f, 0.20f, 0.45f };
    private static readonly int[] CascadeInterval = { 1, 2, 4 };
    private readonly Matrix4x4[] cascadeRenderViewProj = new Matrix4x4[CascadeCount];
    private readonly Vector3[] cascadeFitCentre = new Vector3[CascadeCount];
    private readonly int[] cascadeFittedFrame = new int[CascadeCount];
    private readonly float[] cascadeTexelRendered = new float[CascadeCount];
    private readonly bool[] cascadeDue = new bool[CascadeCount];
    // Triangles each fill submitted, so a pass's cost can be split between geometry and fill.
    private long fillIndirectTriangles;
    // FillIndirect's per-drawable level choices (-1 = culled), and how far into visibleScratch this
    // frame's fills reached (what UploadVisible sends).
    private int[] levelScratch = System.Array.Empty<int>();
    private int visibleWritten;
    private readonly long[] cascadeTriangles = new long[CascadeCount];
    private long cameraTriangles;
    private readonly long[] cascadeTriangleSum = new long[CascadeCount];
    private long cameraTriangleSum;
    private long triangleFrames;
    /// <summary>--shadow-lod N: caster geometric error allowed, in shadow-map texels.</summary>
    private float shadowLodTexels = 1.5f;
    /// <summary>The camera frustum for this frame; the cascades cull their casters against it.</summary>
    private Frustum cameraFrustumThisFrame;
    /// <summary>--no-caster-cull turns off culling casters by where their shadow can land.</summary>
    private bool shadowCasterCull = true;
    /// <summary>--probe-reference: path-trace a few probes on the CPU and print them beside the field.</summary>
    private bool probeReference;
    /// <summary>--no-sky-bounce: the injector scatters the sun only, for the CPU reference to match.</summary>
    private bool noSkyBounce;
    /// <summary>--no-foliage: skip the ivy and tree packs, to price alpha-cutout overdraw.</summary>
    private bool noFoliage;
    private int refBounces = 3;
    // --ref-probes N: how many probes the references path-trace (12 by default, spread through the volume).
    private int referenceProbes = 12;
    private Matrix4x4 prevAmbientViewProj = Matrix4x4.Identity;
    private bool ambientHistoryValid;
    // Two shadow caster pipelines: opaque casters use a push-only program (no
    // descriptor sets → zero per-draw transient allocations), mask foliage uses
    // the alpha-cutout program (binds albedo). Routed per drawable by cutoff.
    private ShaderProgramHandle shadowOpaqueProgram;
    private PipelineHandle shadowOpaquePipeline;
    private ShaderProgramHandle shadowMaskProgram;
    private PipelineHandle shadowMaskPipeline;


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
        // Shadow-caster cutout inputs: the albedo handle the shadow.frag
        // samples for MASK foliage, the alpha cutoff (0 for OPAQUE), and
        // baseColorFactor.a (glTF effective-alpha multiplier).
        TextureHandle Albedo,
        float AlphaCutoff,
        float BaseColorAlpha,
        // Pre-built once at load so the per-frame mask shadow draws don't
        // allocate a binding array each (×3 cascades × every frame).
        ShaderTextureBinding[] ShadowAlbedoBinding,
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
        // Identical across lit, depth-pre-pass, and shadow passes so depth stays
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

        /// <summary>The coarsest level whose deviation stays under a WORLD-space bound.</summary>
        /// <remarks>
        /// Orthographic cascade texels have a constant world size, so caster error is bounded in
        /// metres rather than camera pixels. Sub-texel deviations cannot move the sampled shadow.
        /// </remarks>
        public int PickLodWorld(float worldError, int currentLevel)
        {
            if (LodIndexCounts.Length <= 1 || worldError <= 0f) return 0;
            var wanted = 0;
            for (var l = 1; l < LodIndexCounts.Length; l++)
            {
                if (LodErrors[l] <= worldError) wanted = l;
                else break;
            }
            // Same asymmetry as above, for the same reason: a cascade refits as the camera moves,
            // so its texel size changes and its thresholds move with it.
            currentLevel = Math.Clamp(currentLevel, 0, LodIndexCounts.Length - 1);
            if (wanted <= currentLevel) return wanted;
            return SettleCoarser(currentLevel, wanted, worldError / LodHysteresis, pixelsPerWorld: 1f);
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
        TextureHandle Albedo,
        float AlphaCutoff,
        float BaseColorAlpha,
        ShaderTextureBinding[] ShadowAlbedoBinding,
        bool IsBlend,
        string Name,
        // Where the primitive stands: one world matrix per placement.
        Matrix4x4[] Worlds,
        // Which instance each world is (parallel to Worlds), and where the primitive came from: together, the
        // surface each placement row is (SurfaceKeyOf).
        PlacementInstance[] Instances,
        Blix.Assets.PrimitiveSource? Source,
        // The material's base colour factor (linear rgb, alpha): what a ray's surface bake multiplies the texture by.
        Vector4 BaseColor);

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
    /// chunk of one source primitive, drawn by one instance, is one surface. Node -1: the --flatten path, which
    /// keeps no node (each baked primitive is its own instance).
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
    // One indirect buffer per shadow cascade — each culls against its own frustum
    // (culled objects get instanceCount 0), so the per-cascade visible sets differ.
    private readonly IndirectBufferHandle[] cascadeIndirect = new IndirectBufferHandle[CascadeCount];
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
    // Each placement row's SurfaceKey (stage 4e): one dense id per (instance, source primitive), from 1; every
    // cooked chunk of one source primitive on one instance shares it. 0 is no surface.
    private readonly List<uint> sceneTransformSurfaceKeys = new();
    // Set 3: the transform table and this frame's visible placement indices (every pass's list end to end).
    private IMaterialBindings? sceneInstances;
    private uint[] visibleScratch = System.Array.Empty<uint>();
    // How many indirect commands each drawable owns per pass: one per LOD level the deepest chain has, so
    // a drawable's placements at different levels draw from one indirect run.
    private int lodSlots = 1;
    // GPU culling (the default; --cpu-cull is the A/B): scene_cull.comp builds every pass's indirect records
    // and visible list on the GPU, and the draws read them from sceneArgs. The placements, drawables and
    // levels it reads are uploaded once; per-placement LOD state lives in cullState across frames. set 3
    // is then these buffers by name (sceneBuffers) rather than the sceneInstances material.
    private bool gpuCull = true;
    private bool litFlat;
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
    // --flatten: the old shape through the new path. Every placement baked into its own primitive and
    // placed once at identity, so a same-binary A/B isolates instancing from everything else.
    private bool flatten;
    private bool sceneLoaded;
    // Background pack parse + budgeted main-thread drain (engine primitive):
    // started in OnLoad, drained by TryFinishLoad in OnUpdate. Produces the flat
    // primitive list off-thread; staging runs on the render thread.
    private readonly Blix.Render.AsyncLoadQueue<PlacedPrimitive> meshLoad = new();

    // Per-frame-reused, content-constant buffers built once at load (avoids
    // re-allocating them every frame). passBindings: the lit pass's set-1 IBL +
    // shadow-cascade textures (all stable handles).
    private ShaderTextureBinding[] passBindings = null!;
    // The skybox's own list: it samples six of the lit pass's textures, and a name a program does
    // not declare is an error, not a skip.
    private ShaderTextureBinding[] skyBindings = null!;
    // Mask shadow pushes differ per draw (alpha params), so they can't share one
    // buffer like opaque casters. Pool + reuse the byte[]s across frames instead
    // of allocating per draw: CmdPushConstants copies the bytes at record time,
    // so a buffer is free for reuse once the frame's commands are recorded. The
    // cursor resets each frame and the pool grows to the per-frame high-water mark.
    private readonly List<byte[]> maskPushPool = new();
    private int maskPushCursor;

    // The engine's camera controller: look, orbit, fly, zoom, and --cam, starting where the scene's profile
    // says. The three names below are what the renderer reads; they are the controller's now.
    private readonly CameraController camera;
    private Vector3 cameraPosition => camera.Position;
    private Vector3 cameraForward => camera.Forward;
    private float fovYRadians => camera.FieldOfView;
    private float aspect = 16f / 9f;

    private static CameraController SceneCamera(SceneProfile scene)
    {
        var lens = new Camera3D { VerticalFieldOfView = MathF.PI / 3f, NearPlane = CameraNearPlane, FarPlane = scene.FarPlane };
        return new CameraController(lens)
        {
            Position = scene.StartPosition, Yaw = scene.StartYaw, Pitch = scene.StartPitch, MoveSpeed = scene.MoveSpeed,
        };
    }
    private float renderHeightPx = 810f; // updated on resize; drives screen-space-error LOD
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
    private Vector3 foliageCentre;
    private bool foliageValid;
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
    // Cascades select against their own world-space texel budgets (see PickLodWorld), so each keeps
    // state independent from the camera and from the other cascades.
    private int[][] cascadeLodState = System.Array.Empty<int[]>();
    // How many shadow texels of geometric deviation a caster may have. Below one texel the error
    // cannot move the shadow at all; a little over one is where it starts to be theoretically
    // visible and still is not, because the PCF kernel is wider than that.

    // Slack on the camera frustum test, in metres. See the call site in OnRender.
    private const float CameraCullMargin = 0.5f;
    /// <summary>--no-hashed-alpha: use binary rather than hashed cutouts at one sample.</summary>
    private bool hashedAlpha = true;
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
    /// <summary>Mean sky visibility over the baked volume, for the probe census to compare against.</summary>
    private double meanSkyVisibility;
    /// <summary>Per-cell sky visibility, kept so the probe census can bin the field by enclosure.</summary>
    private float[] cellSkyVisibility = System.Array.Empty<float>();

    /// <summary>Multiplier on measured probe sun irradiance; 1 preserves the measurement.</summary>
    /// <remarks>
    /// This is an explicit scene multiplier over a known quantity, not a replacement intensity.
    /// It applies consistently to direct raster lighting and transport injection.
    /// </remarks>
    private float sunStrength = 1f;

    /// <summary>The sun as everything downstream should see it.</summary>
    private Vector3 EffectiveSunIrradiance => sunIrradiance * sunStrength;

    // Shader-uniform tunables (sun/ambient intensity, metallic/normal/shadow
    // thresholds, cascade-viz) are declared with //@tune in lit.frag and
    // auto-bound to the overlay by this panel — no per-variable field + dial +
    // UBO pack here. The panel owns the live values (seeded from the shader's
    // tag defaults) and feeds them into the per-frame write by name; CPU-side
    // readers (e.g. the froxel sun term) pull via tunePanel.Value(...).
    private ShaderTunablePanel tunePanel = null!;
    // [Tune]-tagged CPU settings objects (Fog, …) auto-paneled in the overlay.
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
    private readonly Dictionary<string, (MaterialHandle Mat, TextureHandle Albedo, float Cutoff, float Alpha)> materialCache = new(StringComparer.Ordinal);
    private (MaterialHandle Mat, TextureHandle Albedo, float Cutoff, float Alpha)? noMaterialCache;
    // The graph owns its passes' render passes and framebuffers and its targets' memory, and only its
    // Dispose frees them: the device destroys what is in its own tables and nothing else. Sponza
    // disposed only the fullscreen pass, and validation counted 70 objects alive at vkDestroyDevice
    // (34 render passes, 17 framebuffers, 19 allocations). Null when the pack set was missing and
    // OnLoad returned before building it.
    public void Dispose()
    {
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
// Volumetric-fog tunables, auto-exposed via [Tune] (overlay "Fog" group —
// FogSettings → "Fog"). The fields are the source of truth; the render loop
// reads fog.Density etc. directly, the overlay reflects + edits them.
internal sealed class FogSettings
{
    [Tune] public bool Enabled;                       // OnLoad enables it unless --no-fog is present
    [Tune(0f, 0.5f)]    public float Density = 0.018f; // extinction coefficient, per metre
    [Tune(0f, 1f)]      public float Scatter = 0.6f;   // scattering albedo: the share of extinction that scatters
    [Tune(-0.9f, 0.9f)] public float PhaseG = 0.6f;    // Henyey-Greenstein anisotropy
    // Fallback ambient in-scatter when neither sky visibility nor incident-light data is available.
    [Tune(0f, 0.2f)]    public float Ambient = 0.005f;
    [Tune(10f, 150f)]   public float Far = 60f;        // grid far distance (metres)
    // Height in metres over which density falls by e, measured from the volume floor.
    [Tune(2f, 100f)]    public float HeightFalloff = 14f;
    // How much density comes from the drifting noise field rather than the smooth falloff. Zero is
    // uniform haze. Feature size and drift speed derive from range so the look scales with it.
    [Tune(0f, 1f)]      public float Noise = 0.55f;
    // History weight trades stability for latency; zero is the current-frame-only reference path.
    // 0.9 is about a ten-frame time constant — long enough to integrate the jittered slice samples
    // into a soft shaft, short enough that dragging the sun does not leave a trail behind it.
    [Tune(0f, 0.98f)]   public float Temporal = 0.9f;
    // Visualize fog-history rejection: bright at disocclusions and newly exposed screen edges,
    // black where history was accepted. A still camera should therefore be nearly black.
    [Tune]              public bool ShowRejection = false;
}

// Tonemap operators (overlay Render → Tonemap). The value indexes present.frag's branch, so the
// build checks these names against the //@tune enum{} above uTonemap there.
[ShaderEnum("present.frag", "uTonemap")]
internal enum TonemapMode { Reinhard, ACES, AgX, Hejl }

// Sun-shadow tunables (overlay "Shadows" group). Read live by UpdateCascades
// (the ortho-fit distance) and the per-frame uShadowStrength write.
//
// <b>BiasTexels was here and was connected to nothing.</b> It sat in this group as a
// [Tune(0f, 6f)] slider, and the only other mention of it in the tree was a comment describing a
// per-cascade NDC bias array that had already been deleted. Depth bias now lives in the shared
// blix_sun_shadow_cascaded path as a slope-scaled constant, and the receiver offset and filter
// width are derived from cascadeTexelWorld by the cascade fit -- so there was no longer a quantity
// for this dial to move. It is deleted rather than re-wired: a control that silently does nothing
// is worse than no control, because dragging it is evidence the bias is not the problem.
internal sealed class ShadowsSettings
{
    [Tune]            public bool Enabled = true;
    [Tune(10f, 120f)] public float SunDistance = 40f;
}

// Ambient-visibility tunables (overlay "Ambient" group).
//
// Radius is a scene-space length over which one surface shades another. GTAO intentionally has no
// extra strength, power, or bias controls layered over its visibility integral.
internal sealed class AmbientSettings
{
    [Tune]            public bool Enabled = true;
    [Tune(0.1f, 4f)]  public float RadiusMetres = 0.8f;

    // Multiplier over transport computed from measured sun, geometry, and the baked albedo grid.
    // One preserves that result; values above one are an explicit scene-level artistic choice.
    [Tune(0f, 4f)]    public float BounceStrength = 1.0f;
    // How much visibility comes from previous frames. The shipping shader uses two slices with four
    // radial steps; spatial denoise and temporal accumulation supply convergence. Zero disables
    // history and remains the baseline.
    [Tune(0f, 0.97f)] public float Temporal = 0.9f;
    // Visualize ambient-history rejection or clamping: bright at disocclusions and screen edges,
    // black where history was accepted.
    [Tune]            public bool ShowRejection = false;
}

// Misc render tunables (overlay "Render" group). Vsync stays a manual toggle —
// it's a device property, not a field.
internal sealed class RenderSettings
{
    // Display exposure, independent from measured sun/sky energy. Two is the current authored
    // presentation default; lighting-strength overrides remain separately visible scene choices.
    [Tune(0.05f, 16f)] public float Exposure = 2.0f;
    [Tune]             public TonemapMode Tonemap = TonemapMode.AgX;
    // Two pixels is the current cost/quality knee: 1 -> 2 px removed 22% of submitted geometry for
    // 1.5 ms, while 2 -> 4 px saved another 17% for only 0.56 ms. Use the exact LOD census when
    // retuning rather than relying on noisy frame time alone.
    [Tune(0f, 8f)]     public float LodErrorPixels = 2.0f;
    // History weight for the full-screen resolve; zero is the current-frame reference. History is
    // rejected off-screen and neighbourhood-clamped so the present frame remains authoritative.
    [Tune(0f, 0.97f)]  public float Taa = 0.9f;
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
