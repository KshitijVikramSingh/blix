using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Graphics.Vulkan;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanSponza;

// The Khronos Intel Sponza scene on the Vulkan backend — the renderer's
// performance + asset-pipeline proving ground (Vector A binding model + Vector B
// render graph). Loads cooked siblings only (.blixtex BC textures, .blixprobe
// IBL, .blixmesh geometry with LOD chains + spatial split) across the main +
// curtains + ivy + trees packs.
//
// Render graph: shadow cascades (×3) → froxel fog (optional compute) →
// depth pre-pass → lit-scene (R11G11B10F, 4× MSAA → resolve) → present (tonemap).
//
// Performance shape: screen-space-error LOD over meshopt chains, cook-time spatial split of
// oversized primitives, all geometry consolidated into one shared VB/IB (draws
// are sub-ranges), and cutout foliage routed to depth-writing MASK + alpha-to-
// coverage so the hero tree isn't overdraw-bound. CPU-phase timing
// (cpu-wait/encode/submit) is surfaced in the diagnostics overlay.
//
// Asset story: shares the existing Blix.Demos.SponzaModern/Assets tree
// (csproj links it in, runtime reads from the demo's own Assets/ copy
// in bin/). Missing-asset path prints a clear instruction and exits 0.
//
// ── Executable spec for (engine primitives this demo proves) ──
//   • Cooked-asset pipeline: .blixmesh/.blixtex/.blixprobe via GltfTextureLoader
//     + AsyncLoadQueue + MeshBundler (one shared VB/IB, draws are sub-ranges)
//   • GPU-driven indirect draw, screen-space-error LOD over meshopt chains
//   • RenderGraph at scale: cascaded shadows, froxel-fog compute, depth pre-pass,
//     MSAA → resolve → tonemap
// ── Intentionally owns (stays local; don't extract until a 2nd consumer needs it) ──
//   • Sponza-specific draw groups, LOD/cull policy, cutout-foliage routing
//   • the exact pass wiring + per-scene tuning (this is the renderer's torture test,
//     not a reusable scene renderer — there deliberately isn't one)
public static class Program
{
    public static void Main()
    {
        // --win W H: the single most informative perf switch this demo has. Fragment and bandwidth
        // cost scale with pixels; geometry cost does not. Halving each side quarters the first and
        // leaves the second alone, so one paired run says which wall the frame is against — a
        // question no amount of per-pass timing can answer on a tile-based GPU, where the
        // timestamps bracket encoder submission rather than execution.
        var args = Environment.GetCommandLineArgs();
        var width = 1440;
        var height = 810;
        for (var i = 0; i < args.Length - 2; i++)
        {
            if (args[i] != "--win") continue;
            if (int.TryParse(args[i + 1], out var w) && int.TryParse(args[i + 2], out var h)
                && w >= 160 && h >= 120)
            {
                width = w;
                height = h;
            }
        }

        var loop = new SponzaLoop();
        using var window = new Window(loop, new WindowOptions("Blix — Vulkan Sponza", width, height));
        window.Run();
    }
}

internal sealed partial class SponzaLoop : IGameLoop, IInputHandler, IDebuggable, IDisposable
{
    public string DebugName => "vulkan-sponza";

    private IRenderHost host = null!;
    private VulkanGraphicsDevice vk = null!;
    private RenderGraph graph = null!;

    // Graph resources + passes.
    private GraphResourceHandle hdrHandle;       // 1× resolve target (present samples this)
    private GraphResourceHandle hdrMsaaHandle;   // MSAA colour the lit pass renders into
    private GraphResourceHandle depthHandle;     // MSAA depth (matches hdrMsaa)
    // 4x MSAA, and it costs about 3.5 ms of a 21 ms frame — roughly 18%, measured in paired
    // alternating runs (4x: 20.80/21.08, 1x: 16.93/17.78).
    //
    // <b>The note that used to sit here said the opposite and was believed all session.</b> It read
    // "measured geometry-bound (cutting MSAA 4->2 left frame time flat — the GPU wall is
    // triangle/binning cost, not fragment/MSAA)". That measurement predates the depth pre-pass, the
    // LOD chains and the indirect path, and it could not be re-checked because --msaa1 lost the
    // device: the pre-pass resolved its depth unconditionally, which is right at 4x and invalid at
    // 1x. A claim nobody could test is how a stale number survives three arcs.
    //
    // Kept at 4x deliberately rather than by default: this renderer's whole thesis is stable
    // silhouettes without temporal reconstruction, and 18% is what that costs when the alternative
    // is TAA. R11G11B10F already quarters the scene-colour tile against the old Rgba16F.
    // 4 by default.
    //
    // --msaa1 drops to a single sample. It used to lose the device; the cause was the depth
    // pre-pass resolving unconditionally, which is invalid with nothing to resolve. See
    // SampleableSceneDepth.
    private int MsaaSamples = 4;
    private PassHandle litPassHandle;

    // Depth pre-pass: renders non-blend geometry depth-only into depthHandle
    // before the lit pass, so the expensive lit fragments run only on visible
    // pixels (kills Sponza overdraw). Reuses lit.vert (invariant gl_Position)
    // so the lit pass's LessEqual test matches the pre-pass depth exactly.
    private PassHandle depthPrepassHandle;

    // --- Ambient visibility (GTAO) ----------------------------------------
    // The pre-pass already rasterises every non-blend drawable into the 4x MSAA depth. It now also
    // RESOLVES that depth to a 1x target, because a multisampled attachment is not sampleable and
    // GTAO has to read depth. The resolve rides along with the pass's store rather than costing a
    // second geometry pass, which is what makes a screen-space occlusion term affordable in a
    // FORWARD renderer with MSAA — the combination that usually forces a G-buffer.
    // Named, because three places now need to agree about them: the projection, the Hi-Z
    // linearisation, and GTAO's background test.
    private const float CameraNearPlane = 0.1f;
    private const float CameraFarPlane = 200f;
    private Matrix4x4 cameraView;
    private Matrix4x4 cameraProjection;
    private GraphResourceHandle depthResolveHandle;   // 1x resolve of the MSAA depth

    /// <summary>The scene depth something can SAMPLE: the resolve under MSAA, else the target itself.</summary>
    /// <remarks>
    /// <b>Resolving a single-sample attachment is invalid, and doing it anyway lost the device.</b>
    /// The pre-pass called ResolveDepth unconditionally, which is right at 4x and meaningless at 1x
    /// — --msaa1 faulted in vkQueueWaitIdle with ErrorDeviceLost and the flag sat documented as
    /// broken. At one sample the depth target is already 1x and already sampleable, so there is
    /// nothing to resolve and nothing to allocate.
    ///
    /// The studio has had exactly this property for the same reason, under the same name. Two
    /// renderers needing the same decision is the usual sign it should be shared, and the shape of
    /// it — a target plus the question "what can read this" — is what a render graph ought to answer
    /// on its own rather than each consumer tracking a resolve by hand.
    /// </remarks>
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
    private ShaderProgramHandle gtaoProgram;
    private PipelineHandle gtaoPipeline;
    private ShaderProgramHandle gtaoDenoiseProgram;
    private PipelineHandle gtaoDenoisePipeline;
    private readonly AmbientSettings ambient = new();

    // Baked sky visibility (.blixsky), uploaded as an Rgba16F 3D texture of L1 coefficients.
    private TextureHandle skyVisibilityTexture;
    private Vector3 skyVolumeMin;
    private Vector3 skyVolumeInvSpan;
    private bool skyVolumeLoaded;

    // --sky. <b>Off by default, because the baked half is correct and the picture is not.</b>
    // Visibility is verified against the analytic upper hemisphere and the enclosure it reports is
    // real, but multiplying sky irradiance by it removes light that nothing yet replaces: the term
    // that fills a courtyard is the SUN bouncing off its lit walls, at irradiance 17, and no
    // sun-independent bake can carry that. On by default this makes Sponza darker and worse.
    private bool skyVisibilityEnabled;

    // Runtime sun-bounce injection over the shipped voxel grid.
    private TextureHandle occupancyTexture;
    // <b>Two, and the reason is a barrier rather than a buffer.</b> Measured: the injection
    // dispatch alone costs nothing (21.09 ms against a 21.08 ms baseline) and the lit pass's two 3D
    // fetches alone cost nothing (20.97 ms) — but together they cost 15 ms. Independent costs do not
    // do that; a DEPENDENCY does. Sampling in the same frame the compute wrote forces a barrier, and
    // the compute stops overlapping with the rest of the frame.
    //
    // So the lit pass reads what the previous frame solved. The probe grid is already amortised over
    // eight frames, so one more frame of latency is beneath what the amortisation itself introduces.
    private readonly TextureHandle[] bounceTextures = new TextureHandle[2];
    private int bounceWrite;
    private int skyBounceBinding = -1;   // where uSkyBounce sits in passBindings
    private ShaderProgramHandle injectProgram;
    private PipelineHandle injectPipeline;
    private PassHandle injectPassHandle;
    private ShaderTextureBinding[] injectBindings = Array.Empty<ShaderTextureBinding>();
    private int probeX, probeY, probeZ, occX, occY, occZ;
    private Vector3 skyVolumeSpan;
    private bool bounceReady;
    private const int InjectRays = 64;
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
    private const int InjectPeriod = 32;

    // Splitting the sky path's cost between its two halves: the compute that solves the bounce, and
    // the two 3D fetches every lit fragment then makes. Amortising the dispatch 8x recovered only 4
    // of 19 ms, which says most of the cost is not the solving — but "says" is not "measured", and
    // every time this session a guess about where cost lives has been wrong.
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
    private GltfTextureLoader textureLoader = null!;
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

    // --- Froxel volumetric fog --------------------------------------------
    // A compute pass fills a view-aligned 3D grid with sun in-scattering and
    // transmittance (shadow-aware, sampling the cascade maps), ordered after
    // the shadow passes and before the lit pass, which composites it per
    // fragment. The first real consumer of the compute layer + 3D textures.
    // Grid resolution. Each (x,y) thread marches all Z slices sampling the
    // cascades, so cost scales with X*Y*Z — this should become a renderer
    // quality preset (quarter/half/full) rather than a fixed size.
    private const int FroxelGridX = 128;
    private const int FroxelGridY = 72;
    private const int FroxelGridZ = 48;
    private ShaderProgramHandle froxelProgram;
    private PipelineHandle froxelPipeline;
    private TextureHandle froxelGridTexture;
    private PassHandle froxelPassHandle;
    // Volumetric-fog tunables — [Tune]-tagged, auto-bound to the overlay "Fog"
    // group via ObjectTunables (see FogSettings).
    private readonly FogSettings fog = new();
    private readonly ShadowsSettings shadows = new();
    private readonly RenderSettings render = new();
    // --- Headless capture -------------------------------------------------
    // <b>Sponza could not show anyone a picture of itself.</b> Blix.Tools.Shot renders the LAB, so
    // every question about this scene came down to booting it headed and asking a person to look —
    // which works for "does that read right" and not at all for "what number is in that buffer".
    // The first intermediate target this renderer ever had (ambient visibility) is also the first
    // one nobody could inspect, and the two facts met on the same afternoon.
    private string? shotPath;
    private int shotFrame = 240;        // long enough for the streamed textures to land
    private int framesRendered;
    // Frame periods for the capture's statistics. A tail of five frames cannot tell a 10 ms effect
    // from this machine's own spread, which is the mistake the first two perf matrices made.
    private readonly double[] framePeriodsMs = new double[600];
    private int framePeriodCount;
    private long lastFrameStamp;

    // <b>The same frame, shaded two ways, seconds apart in one process.</b> While textures stream the
    // scene draws through flat.frag — same geometry, same 401 draws, same depth pre-pass, no
    // materials, no IBL, no shadows, no fog. Then it flips to the full path. That is a controlled
    // experiment the renderer has been running at every startup since it was written, and nobody
    // was recording it: identical thermal state, identical submission, one variable.
    //
    // It is worth more than any of the cross-process A/Bs attempted today, all of which were
    // swamped by thermal drift between runs.
    private readonly double[] flatPeriodsMs = new double[600];
    private int flatPeriodCount;
    private bool shotWritten;

    // --no-mask: force every cutout material's alphaCutoff to zero. Nothing then routes to a MASK
    // pipeline, so no pass samples albedo just to discover a fragment is air — not the camera pass,
    // not the depth pre-pass, not any shadow cascade. The picture is wrong on purpose (leaves become
    // solid cards); the point is the frame time beside it.
    private bool forceOpaqueMask;

    // --no-vsync: uncap the presentation so the frame timer reports work rather than refresh.
    private bool startUnsynced;

    // --ab-flat: after loading, alternate between the FLAT path and the full lit path every
    // AbPeriodFrames frames, bucketing frame times separately.
    //
    // <b>Interleaved, because this machine cannot be measured any other way.</b> Every cross-process
    // A/B attempted on it was swamped by thermal drift: a resolution sweep came out monotonically
    // SLOWER as pixels decreased, purely because the later runs were hotter, and an AO pair came out
    // with the sign reversed. Alternating inside one process puts both arms on the same thermal
    // ramp, interleaved finely enough that drift affects them equally.
    // "" = off. "flat" swaps the whole lit path for flat.frag; "shadow" zeroes uShadowStrength;
    // "gtao" zeroes the search radius. The last two are uniform-driven, so they alternate without
    // touching pipelines — which is what makes a fine interleave possible at all.
    private string abMode = "";
    private bool abFlat;
    private const int AbPeriodFrames = 120;
    private bool AbOffPhase => abMode.Length > 0 && (framesRendered / AbPeriodFrames) % 2 == 1;
    private bool AbFlatPhase => abFlat && AbOffPhase;

    // --viz N: write a shading input instead of the lit colour. See lit.frag's uVizChannel.
    private float vizChannel;

    // --ao-fullres: run ambient visibility at framebuffer resolution instead of half. Half res is
    // the right default for a low-frequency term, but a crease a few centimetres wide is not low
    // frequency, and at half res plus a 3x3 bilateral it spans about one texel before being blurred
    // with its neighbours. This exists to find out whether fold detail is lost to RESOLUTION rather
    // than to the search radius.
    private float aoScale = 0.5f;

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
    // binding 3 and picks one per fragment by view-space depth.
    private const int CascadeCount = 3;
    // Per-cascade shadow-map resolution. The near two cascades carry the
    // detail the eye lands on, so they stay at 2048²; the far cascade covers a
    // huge world area where per-texel detail matters least, so it's 1024². The
    // lit/froxel PCF reads textureSize() so it adapts to each map automatically;
    // only texel-snapping + bias need the per-cascade size (see UpdateCascades).
    private static readonly int[] ShadowMapSizes = { 2048, 2048, 1024 };
    // View-space depth boundaries: cascade i covers (Splits[i], Splits[i+1]).
    // [1..3] (the cascade far distances) are live-tunable from the overlay.
    private readonly float[] cascadeSplits = { 0.1f, 6f, 22f, 60f };
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
    // Per-cascade base depth bias in NDC units, derived each frame from that
    // cascade's world-space texel size ÷ ortho depth range (≈ BiasTexels
    // shadow texels of slope-independent offset). The fragment shader adds a
    // grazing-angle slope term on top.
    /// <summary>One shadow texel in WORLD units, per cascade — derived from the cascade fit.</summary>
    /// <remarks>
    /// Replaces cascadeDepthBias. The shared shadow path offsets its sample along the surface
    /// normal by a multiple of this, which is a length the fit already knows; the depth bias it
    /// replaces was that length turned into an NDC constant and then scaled by two tuned numbers.
    /// </remarks>
    private readonly float[] cascadeTexelWorld = new float[CascadeCount];
    // Per-cascade shadow-caster survivor counts after frustum culling,
    // surfaced live in the diagnostics overlay (see Debug()).
    private readonly int[] cascadeDrawCounts = new int[CascadeCount];
    // Two shadow caster pipelines: opaque casters use a push-only program (no
    // descriptor sets → zero per-draw transient allocations), mask foliage uses
    // the alpha-cutout program (binds albedo). Routed per drawable by cutoff.
    private ShaderProgramHandle shadowOpaqueProgram;
    private PipelineHandle shadowOpaquePipeline;
    private ShaderProgramHandle shadowMaskProgram;
    private PipelineHandle shadowMaskPipeline;
    // Diagnostics overlay visibility, toggled with Cmd+C. Applied to
    // DebugState.Enabled each frame in Debug() (which runs unconditionally).
    private bool overlayEnabled = true;


    // IBL textures: a cooked .blixprobe (real GGX prefilter) when present, else
    // the procedural sky bake (see OnLoad). Generated/uploaded once at load.
    //   envCube           prefiltered specular (mip chain at increasing roughness)
    //   irradianceCube    cosine-weighted hemisphere convolution of the sky
    //   brdfLut           split-sum BRDF integration (R = F0 scale, G = F0 bias)
    // Bound at set 1 (per-pass), so every draw in the lit pass samples them.
    private TextureHandle envCubeTexture;
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
        // World-space AABB (the static importer bakes node transforms into the
        // vertices, so object bounds == world bounds) — used for per-cascade
        // shadow frustum culling AND distance-based LOD selection.
        Bounds3 Bounds,
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
        string Name)
    {
        // Screen-space-error LOD: pick the COARSEST level whose stored world
        // error projects to ≤ errorPixels at the nearest point of the bounds.
        //   errorScale = viewportH / (2·tan(fovY/2))  → pixels-per-world at unit
        //   distance; pixels-per-world at distance d = errorScale / d.
        // errorPixels ≤ 0 forces full detail. Distance is to the NEAREST point
        // on the AABB (not the centre) so a big primitive you stand inside stays
        // detailed where it matters instead of coarsening on a far centre.
        // Identical across lit, depth-pre-pass, and shadow passes so depth stays
        // invariant. 1-LOD drawables (uncooked) always pick 0.
        public int PickLod(Vector3 cameraPos, float errorScale, float errorPixels)
        {
            if (LodIndexCounts.Length <= 1 || errorPixels <= 0f) return 0;
            var nearest = Vector3.Clamp(cameraPos, Bounds.Min, Bounds.Max);
            var d = MathF.Max((cameraPos - nearest).Length(), 0.01f);
            var pixelsPerWorld = errorScale / d;
            var level = 0;
            // Errors increase monotonically with level, so stop at the first
            // level that exceeds the budget — all coarser ones do too.
            for (var l = 1; l < LodIndexCounts.Length; l++)
            {
                if (LodErrors[l] * pixelsPerWorld <= errorPixels) level = l;
                else break;
            }
            return level;
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
        string Name);

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
    private bool sceneLoaded;
    // Background pack parse + budgeted main-thread drain (engine primitive):
    // started in OnLoad, drained by TryFinishLoad in OnUpdate. Produces the flat
    // primitive list off-thread; staging runs on the render thread.
    private readonly Blix.Render.AsyncLoadQueue<GltfPrimitive> meshLoad = new();

    // Per-frame-reused, content-constant buffers built once at load (avoids
    // re-allocating them every frame). identityPush: the per-draw model push
    // (always identity — transforms are baked into the vertices). passBindings:
    // the lit pass's set-1 IBL + shadow-cascade textures (all stable handles).
    private byte[] identityPush = null!;
    private ShaderTextureBinding[] passBindings = null!;
    private ShaderTextureBinding[] froxelBindings = null!;
    // Mask shadow pushes differ per draw (alpha params), so they can't share one
    // buffer like opaque casters. Pool + reuse the byte[]s across frames instead
    // of allocating per draw: CmdPushConstants copies the bytes at record time,
    // so a buffer is free for reuse once the frame's commands are recorded. The
    // cursor resets each frame and the pool grows to the per-frame high-water mark.
    private readonly List<byte[]> maskPushPool = new();
    private int maskPushCursor;

    // Camera + input. Yaw 0 = looking down -Z; positive X is "right".
    // Initial pose aims at the +X end-wall lavabo (wall fountain) so the
    // sculpture is in the first frame, useful for normal-map debugging.
    private Vector3 cameraPosition = new(-9f, 3f, 0f);
    private Vector3 cameraForward = Vector3.UnitX;
    private float camYaw = MathF.PI / 2f;
    private float camPitch;
    private float aspect = 16f / 9f;
    private float fovYRadians = MathF.PI / 3f;
    private float renderHeightPx = 810f; // updated on resize; drives screen-space-error LOD
    // Screen-space-error LOD threshold: a level is used when its baked geometric
    // error projects to ≤ this many pixels at the viewing distance. ~1px is the
    // "imperceptible" target; raise to trim more aggressively, 0 forces full
    // detail. Replaces the old metres-per-level distance — this is resolution-,
    // FOV-, and primitive-size-aware (a big surface you stand on stays detailed;
    // a small far prop drops early). NOTE: a single huge primitive still gets one
    // level (its near edge pins it), which is why cook-time spatial split (B) is
    // the next step — this metric just makes selection principled.
    // pixels-per-world at unit distance = viewportH / (2·tan(fovY/2)); PickLod
    // divides by the view distance. Recomputed lazily from the fields above.
    private float LodErrorScale => renderHeightPx * 0.5f / MathF.Tan(fovYRadians * 0.5f);
    private bool mouseLook;
    private float lastMouseX, lastMouseY;   // latest cursor pos (for click-to-pick)
    // Diagnostics selection: a contributor registered after consolidation so a
    // left-click ray-picks a primitive and the Selection panel can inspect it.
    private Blix.Diagnostics.DebugSystem? debugSystem;
    private readonly SceneSelection sceneSelection = new();
    // Live multi-selection (ephemeral): set of picked entity paths + the primary
    // (last-picked, gets the framework highlight + inspector). Cmd-click adds.
    private readonly HashSet<string> selection = new();
    private string? primarySelection;
    // Per-drawable LOD error-margin multipliers (×global px budget), keyed by
    // drawable index — parallel to opaque/blend drawables, default 1.0. Live,
    // ephemeral; edited via the Selection panel, consumed by PickLod.
    private float[] opaqueLodMargins = System.Array.Empty<float>();
    private float[] blendLodMargins = System.Array.Empty<float>();
    private static readonly GraphicsColor MultiSelectColor = new(0.95f, 0.75f, 0.2f, 1f);
    private readonly HashSet<Key> heldKeys = new();
    private Matrix4x4 viewProj;

    // Sun travel direction, recomputed from sunYaw/sunPitch (overlay Sun
    // scope). Initialised in OnLoad from the default direction below. Note the
    // IBL cubes are baked once with SkyBakeSunDirection, so live sun changes
    // relight the direct sun + shadows but not the indirect IBL.
    private Vector3 sunDirection = Vector3.Normalize(new Vector3(0.35f, -0.85f, 0.25f));
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

    // Material handle cached by glTF material (and a slot for the null/untextured
    // fallback) so de-batched per-primitive drawables don't build duplicates.
    // <b>Keyed by the material's IDENTITY, not by the object.</b> GltfMaterial is a record whose
    // reference was the key here, so two imports of one file would build two GPU materials for one
    // material — inert only because this demo loads each of its four packs exactly once.
    //
    // The identity is shared with the engine; the STORAGE is not, and that asymmetry is deliberate.
    // A texture has one canonical GPU form, so TextureRegistry can hold it for everyone. A material
    // does not: the UBO layout is this game's, and the value below is a Sponza-shaped tuple. A
    // generic registry over that would be a type parameter pretending to be a shared decision.
    private readonly Dictionary<string, (MaterialHandle Mat, TextureHandle Albedo, float Cutoff, float Alpha)> materialCache = new(StringComparer.Ordinal);
    private (MaterialHandle Mat, TextureHandle Albedo, float Cutoff, float Alpha)? noMaterialCache;
    public void Dispose() => fullscreen?.Dispose();

}

// Volumetric-fog tunables, auto-exposed via [Tune] (overlay "Fog" group —
// FogSettings → "Fog"). The fields are the source of truth; the render loop
// reads fog.Density etc. directly, the overlay reflects + edits them.
internal sealed class FogSettings
{
    [Tune] public bool Enabled;                       // compute cost; off by default
    [Tune(0f, 0.5f)]    public float Density = 0.018f; // extinction scale
    [Tune(0f, 1f)]      public float Scatter = 0.6f;   // scattering albedo
    [Tune(-0.9f, 0.9f)] public float PhaseG = 0.6f;    // Henyey-Greenstein anisotropy
    [Tune(0f, 0.2f)]    public float Ambient = 0.005f; // ambient in-scatter floor
    [Tune(10f, 150f)]   public float Far = 60f;        // grid far distance (metres)
}

// Tonemap operators (overlay Render → Tonemap); the enum's int value indexes
// present.frag's branch, so declaration order must match the shader.
internal enum TonemapMode { Reinhard, ACES, AgX, Hejl }

// Sun-shadow tunables (overlay "Shadows" group). Read live by UpdateCascades
// (bias + ortho-fit distance) and the per-frame uShadowStrength write.
internal sealed class ShadowsSettings
{
    [Tune]            public bool Enabled = true;
    [Tune(0f, 6f)]    public float BiasTexels = 1.5f;
    [Tune(10f, 120f)] public float SunDistance = 40f;
}

// Ambient-visibility tunables (overlay "Ambient" group).
//
// <b>One number, and it is a length.</b> The radius is the distance over which one surface is
// considered to shade another — a property of the scene's scale, like fog far, not a dial for
// taste. There is deliberately no strength, no power curve and no bias: GTAO's integral already
// answers "how much sky does this point see", and a knob on top of it exists only to disagree with
// the answer. This lighting model just deleted five of those.
internal sealed class AmbientSettings
{
    [Tune]            public bool Enabled = true;
    [Tune(0.1f, 4f)]  public float RadiusMetres = 0.8f;

    // <b>The one number the bounce invents.</b> Everything else in the injection derives from
    // geometry and the measured sun; this stands in for a per-voxel albedo the grid does not carry.
    // Sponza's stone sits around 0.3-0.4 and its cloth lower, so 0.35 is the scene's average rather
    // than a dial for taste — and when the grid learns material, this stops being a constant.
    [Tune(0f, 1f)]    public float BounceAlbedo = 0.35f;
}

// Misc render tunables (overlay "Render" group). Vsync stays a manual toggle —
// it's a device property, not a field.
internal sealed class RenderSettings
{
    [Tune(0.05f, 16f)] public float Exposure = 0.5f;
    [Tune]             public TonemapMode Tonemap = TonemapMode.AgX;
    [Tune(0.3f, 60f)]  public float MoveSpeed = 4.5f;
    [Tune(0f, 8f)]     public float LodErrorPixels = 1.0f;
}

// Pickable scene primitives for the diagnostics overlay. Built after geometry
// consolidation: one DebugSelectable per drawable, keyed by a session-stable
// path. Implements the engine's selection + inspection hooks so a click
// ray-picks a primitive and the Selection panel shows its identity + LOD info.
// Selection is live debug state only — nothing persists.
internal sealed class SceneSelection : IDebugSelectable, IDebugInspectable
{
    public string DebugName => "scene";

    private readonly List<DebugSelectable> selectables = new();
    private readonly Dictionary<string, Entry> byPath = new();
    private readonly record struct Entry(string Name, Bounds3 Bounds, int LodLevels, float MaxError);

    public void Rebuild(
        IReadOnlyList<(string Path, string Name, Bounds3 Bounds, int LodLevels, float MaxError)> items)
    {
        selectables.Clear();
        byPath.Clear();
        foreach (var it in items)
        {
            selectables.Add(new DebugSelectable(it.Path, it.Bounds));
            byPath[it.Path] = new Entry(it.Name, it.Bounds, it.LodLevels, it.MaxError);
        }
    }

    public void CollectSelectables(List<DebugSelectable> destination) => destination.AddRange(selectables);

    // Bounds for a path (for the demo to draw multi-select highlights).
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
        debug.Values.Value("lod-levels", e.LodLevels);
        debug.Values.Value("lod-max-error", e.MaxError);
    }
}
