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
    private readonly GraphResourceHandle[] taaHandles = new GraphResourceHandle[2];
    private readonly PassHandle[] taaPassHandles = new PassHandle[2];
    private readonly PipelineHandle[] taaPipelines = new PipelineHandle[2];
    private int taaWrite;
    private int taaWriteNext;
    private Matrix4x4 prevTaaViewProj = Matrix4x4.Identity;
    private bool taaHistoryValid;
    private Matrix4x4 viewProjJittered = Matrix4x4.Identity;
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
    // <b>4x, and the reason is alpha-to-coverage QUANTISATION rather than edge quality.</b> This was
    // 2x on a perf argument -- 4x costs roughly 17 ms more, while 2x sits level with MSAA off, and 2x
    // tracked 4x on the metrics then available (mean 23.92 against 24.08, edge energy 11.69 against
    // 11.34, where OFF breaks away from both).
    //
    // Those metrics were measuring the wrong thing. Alpha-to-coverage writes a sample MASK, so the
    // number of samples is the number of coverage levels it can express: at 2 samples a leaf fragment
    // can only be 0, 0.5 or 1 covered, and the rest of the pixel keeps whatever draws next -- the
    // skybox. A leaf whose true coverage is 0.2 therefore composites as half sky, and so does one at
    // 0.8. Every partial canopy pixel is dragged toward 50% sky. 4x gives five levels instead of
    // three, which is not sharper edges, it is less wrong colour.
    //
    // Independently confirmed three ways before this change: --msaa1 removes the canopy blue entirely
    // (coverage goes binary), making the depth pre-pass admit a dilated silhouette made it worse
    // (more partial fragments), and every ambient viz channel rendered the same blue over foliage
    // because none of them were being written there at all.
    //
    // <b>OFF by default now, and the argument above is the price being paid.</b> The whole case for
    // 4x was alpha-to-coverage quantisation over foliage, and at one sample there is no sample mask
    // at all: alphaToCoverage is disabled with nothing to spread coverage across, the cutout falls
    // back to a plain discard, and every leaf edge goes binary. The decorrelating hash that fixed
    // the canopy (blix_coverageMask) has nothing to decorrelate either — one sample is one bit.
    // So this trades the foliage silhouette and the whole 18% for the perf loop, deliberately and
    // reversibly: --msaa2 and --msaa4 put it back, and the reasoning above is kept intact rather
    // than rewritten, because it is the thing to re-read when the canopy looks wrong again.
    private int MsaaSamples = 1;
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
    // The interpolated world normal, written by the depth pre-pass. What the incident field used
    // to infer from the depth buffer, and could not for foliage, two-sided cloth or silhouettes.
    private GraphResourceHandle prepassNormalHandle;
    private GraphResourceHandle prepassNormalResolveHandle;
    /// <summary>The normal target a reader should sample: resolved under MSAA, direct at one sample.</summary>
    private GraphResourceHandle SampleablePrepassNormal =>
        MsaaSamples > 1 ? prepassNormalResolveHandle : prepassNormalHandle;

    // rgb = incident bounced radiance, a = baked sky visibility, at incidentScale.
    private GraphResourceHandle incidentHandle;
    private PassHandle incidentPassHandle;
    private PipelineHandle incidentPipeline;
    // What the lit pass actually samples: the coarse field reconstructed to full resolution.
    private GraphResourceHandle incidentFullHandle;
    private PassHandle incidentResolvePassHandle;
    private PipelineHandle incidentResolvePipeline;
    private ShaderProgramHandle gtaoProgram;
    private PipelineHandle gtaoPipeline;
    private ShaderProgramHandle gtaoDenoiseProgram;
    private PipelineHandle gtaoDenoisePipeline;
    private readonly AmbientSettings ambient = new();

    // Baked sky visibility (.blixsky), uploaded as an Rgba16F 3D texture of L1 coefficients.
    // Three volumes for nine L2 coefficients: L0+L1, four L2 terms, and the last with slots spare.
    private readonly TextureHandle[] skyVisibilityTextures = new TextureHandle[3];
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
    // What colour each cell is, at half the occupancy resolution. Without it the injection knows a
    // surface is there and nothing about it, so every bounce carried the sun's hue and Sponza's
    // curtains bled no colour at all.
    private TextureHandle albedoTexture;
    // The sheen half of the probe. Zero mips means the probe predates .blixprobe v4 and the shader
    // is told so rather than handed the specular cube, which would render a plausible non-answer.
    private TextureHandle sheenEnvTexture;
    private TextureHandle sheenLutTexture;
    private float sheenMipCount;
    // <b>Two, and the reason is a barrier rather than a buffer.</b> Measured: the injection
    // dispatch alone costs nothing (21.09 ms against a 21.08 ms baseline) and the lit pass's two 3D
    // fetches alone cost nothing (20.97 ms) — but together they cost 15 ms. Independent costs do not
    // do that; a DEPENDENCY does. Sampling in the same frame the compute wrote forces a barrier, and
    // the compute stops overlapping with the rest of the frame.
    //
    // So the lit pass reads what the previous frame solved. The probe grid is already amortised over
    // eight frames, so one more frame of latency is beneath what the amortisation itself introduces.
    // Two ping-pong octahedral atlases: one 8x8 tile per probe, 6x6 interior plus a border ring.
    private readonly TextureHandle[] bounceTextures = new TextureHandle[2];
    // The visibility half: mean and mean-square distance per direction, same tile layout. Without
    // it the volume has no way to know a probe sits behind a wall from a shading point's view.
    private readonly TextureHandle[] bounceDepthTextures = new TextureHandle[2];

    /// <summary>
    /// Probe counts for the BOUNCE, which are no longer the sky visibility's.
    /// </summary>
    /// <remarks>
    /// <b>An octahedral probe carries thirty-six directional samples where L1 carried four, so it
    /// wants far fewer probes.</b> Keeping 41,472 of them would spend the entire gain on solving a
    /// field at a spacing its own resolution no longer needs. Half the visibility grid on each axis
    /// is an eighth of the marching — and the marching is what this pass costs.
    /// </remarks>
    private int bounceX, bounceY, bounceZ;

    // Which probes shading read, and how fast an unread one forgets. 0 frames disables sleeping
    // entirely, which is the A/B: every probe solved every sweep, as before.
    private TextureHandle probeUsageTexture;
    private PassHandle probeUsagePassHandle;
    private ShaderInterface usageInterface = null!;
    private PipelineHandle probeUsagePipeline;
    // <b>Off by default, because the win is real in the pass and not in the frame.</b> The marker
    // works: sky-inject 1.45 -> 0.82 ms for 0.18 ms of its own, with the image identical to
    // 0.13/255. And the FRAME gets slower — consistently, across reps, while the GPU pass timers
    // insist it should not. The cost is in synchronisation those timers do not see: a compute pass
    // consuming the depth the pre-pass just wrote forces a barrier mid-frame, and on a tile-based
    // GPU that breaks pass merging. The same family as the fragment-store finding one layer up.
    //
    // Measuring it properly needs --ab, which interleaves arms inside one process — this laptop
    // drifts far enough between sequential runs that sleep=0 alone moved 24.8 to 34.3 ms.
    // Smoothed frame period, for the overlay's "share of frame" readouts. The A/B harness keeps
    // its own unsmoothed samples.
    private double lastFramePeriodMs;
    // <b>How long a probe nobody has sampled keeps solving before it stops.</b> Shipped at 0 — never
    // sleep — which is the honest baseline a feature should be measured against and a poor default
    // to leave in place once it has been. The scene has 4,992 probes and a camera sees a fraction
    // of them; the rest were re-solving every frame for nobody.
    //
    // 20 frames is a third of a second at 60 fps: long enough that a probe drifting in and out of
    // view does not thrash, short enough that the field behind you stops costing anything quickly.
    // The cost of sleeping is WAKE LATENCY, which only a moving camera can show — see --ab sleep.
    /// <b>16 frames, and the number that decides it is the probe count, not this one.</b>
    ///
    /// Injection costs rays per frame, which is probes x rays / period. Sleeping was measured at
    /// 1.81 ms and very nearly dropped on that basis — a field with holes in it did not look worth
    /// 1.8 ms, and the debug view's rows of black probes were an accurate picture of those holes.
    /// But that measurement was taken at 5,376 probes, and the grid now ships at 41,472. Multiplying
    /// the probe count by eight and changing neither of the other two terms multiplies the cost by
    /// eight: sky-inject went from 2.96 ms to 26.26 ms with sleeping off, which is the entire frame
    /// budget spent solving probes nobody is looking at.
    ///
    /// So the feature whose value looked marginal is the one that makes the density affordable, and
    /// it looked marginal precisely because there were few probes to sleep. A quarter of a second at
    /// 60 fps: long enough that a probe drifting in and out of view does not thrash, short enough
    /// that the field behind you stops costing almost immediately. 0 still means never sleep, and
    /// is the honest baseline any change to the injector should be measured against.
    private float probeSleepFrames = 16f;
    /// <summary>The sleep setting the --ab sleep arm restores in its off phase.</summary>
    private float ProbeSleepNow => abMode == "sleep" && AbOffPhase ? 0f : probeSleepFrames;
    private const int OctTile = 8;
    private int bounceWrite;
    /// <summary>--probe-carryless: one atlas, no per-frame carry. MEASURED AND REJECTED; see below.</summary>
    /// <remarks>
    /// <b>The carry looked like the biggest unclaimed saving in the pass and it is worth almost
    /// nothing.</b> The atlases alternate each frame, so a probe that is not solving copies its 8x8
    /// tile into the other buffer — 619k texel copies at the shipped density, 4.8M at eight times
    /// it, in the irradiance atlas and the depth atlas both, and independent of the refresh period.
    /// Counted as operations that is enormous. Costed as BANDWIDTH it is about 76 MB a frame against
    /// this chip's ~120 GB/s: roughly two percent, which is another way of saying unmeasurable.
    ///
    /// Collapsing to a single atlas removes it entirely and measured 2.22 ms against the pair's
    /// 1.61 ms for the same dispatch at 8x density — WORSE, inside noise but certainly not better,
    /// with identical frame times. The plausible mechanism is that a single atlas makes the injector
    /// sample the image it writes, and aliasing one texture as sampled and storage in a dispatch
    /// defeats texture caching or forces conservative hazard handling. So the trade is a real data
    /// race for no measured gain, and the pair stays.
    ///
    /// Kept as a flag rather than deleted because the measurement is worth being able to repeat, and
    /// because the carry does become dominant if probe count ever outruns bandwidth.
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
    // 0 reads the grid as the boolean it never was; 1 reads the density the baker writes. A live
    // A/B, because sweeping this across processes is what let the two sides disagree unnoticed.
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
    // Global rather than per-material: glTF carries KHR_materials_transmission, and Sponza authors
    // it nowhere — every material reports TransmissionFactor 0, so a baked per-material value would
    // be zero everywhere and buy nothing until someone authors it.
    private float injectTranslucency = 0.5f;

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
    private float probeField;      // 0 = bounce, 1 = sky visibility
    private float probeExposure = 1f;

    // --- Froxel volumetric fog --------------------------------------------
    // A compute pass fills a view-aligned 3D grid with sun in-scattering and
    // transmittance (shadow-aware, sampling the cascade maps), ordered after
    // the shadow passes and before the lit pass, which composites it per
    // fragment. The first real consumer of the compute layer + 3D textures.
    // Grid resolution. Each (x,y) thread marches all Z slices sampling the
    // cascades, so cost scales with X*Y*Z — this should become a renderer
    // quality preset (quarter/half/full) rather than a fixed size.
    // <b>A froxel is a fixed number of PIXELS, not a fixed fraction of a resolution nobody set.</b>
    // The grid was 128x72 whatever the window was — on this machine's Retina framebuffer that is
    // twenty physical pixels across one froxel, which is why the fog read as chunks of weather
    // rather than as air, and why the noise field and the shaft edges both landed under the
    // sampling rate. Eight is the edge of a froxel in framebuffer pixels, so the fog's screen-space
    // frequency is now a property of the renderer instead of an accident of the window size.
    //
    // The z count is the subdivision of the fog's RANGE and has nothing to do with the screen, so
    // it stays a constant. Cost scales with the product: XY with the framebuffer, Z with this.
    private const int FroxelPixels = 8;
    /// <summary>--fog-slices N: depth slices in the froxel grid. See the note on the default.</summary>
    /// <remarks>
    /// <b>48 was compensating for having no history, and it no longer has to.</b> The slice count
    /// was carrying the smoothness on its own: with the sample parked at each segment's midpoint
    /// every frame, the only defence against banding along the ray was making the segments short.
    /// The temporal path jitters that sample within its segment and integrates across frames, which
    /// is the same smoothness bought by accumulation rather than by resolution — so the count can
    /// come down and the cost with it. Set it back to 48 to compare, and Fog/Temporal 0 to see what
    /// the count alone was doing.
    /// </remarks>
    private int froxelGridZ = 24;
    private int froxelGridX, froxelGridY;
    private int froxelGridBinding = -1;

    // The grid dimensions a framebuffer of this size asks for. Floored well above zero so a
    // minimised or absurdly small window still has a grid to dispatch over.
    private static (int X, int Y) FroxelGridSize(int width, int height) =>
        (Math.Max(8, (width  + FroxelPixels - 1) / FroxelPixels),
         Math.Max(8, (height + FroxelPixels - 1) / FroxelPixels));
    private ShaderProgramHandle froxelProgram;
    private PipelineHandle froxelPipeline;
    private TextureHandle froxelGridTexture;
    // <b>History for the MEDIUM, ping-ponged.</b> rgb = in-scattered radiance, a = extinction, both
    // per froxel and both pre-integration. See froxel.comp for why the integrated grid cannot carry
    // history itself. A pair rather than one texture because this one really is read-while-written:
    // the reprojected fetch lands wherever the camera moved, not on the texel being stored.
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

    // Frames rendered since the scene finished loading. The reproducible clock: the orbit's angle
    // and the --shot deadline both read it, so a capture is a function of the flag alone.
    private int postLoadFrames;
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
    // --no-prepass: drop the depth pre-pass for good, not as an A/B phase.
    //
    // <b>It is not an optimisation being removed, it is a dependency, and that is the finding.</b>
    // GTAO and the Hi-Z pyramid both need depth BEFORE shading, and only a pre-pass can give them
    // that -- so removing it removes screen-space ambient occlusion with it. Which turns out to be
    // the interesting part: judged from the chair, that image reads BETTER (the green volume around
    // the cypress and the streak on the wall both go), and --ab prepass measured the pair at 0.802x
    // of frame, about 12.6 ms. The baked sky-visibility volume is the honest occlusion term anyway --
    // measured, volumetric, and it knows a courtyard is a well; GTAO is a sub-metre screen-space
    // approximation sitting on top of it.
    private bool noPrepass;
    /// <summary>--probe &lt;name&gt;: a cooked .blixprobe to prefer over the default list.</summary>
    private string? probeName;
    /// <summary>--bounce-div N: bounce grid = visibility grid / N. 1 ships — see below.</summary>
    /// <remarks>
    /// <b>Continuous, because the interesting densities are not integers.</b> As an int the only
    /// step below 2 was 1, which is eight times the probes — a divisor is per-AXIS and probe count
    /// is its cube. Anybody asking for "twice the probes" wants 2 / cbrt(2) = 1.587, and asking the
    /// question at all should not require accepting an 8x jump in the injection dispatch.
    /// </remarks>
    /// <remarks>
    /// <b>1 ships, which is the end of this axis rather than a point along it.</b> The bounce grid
    /// now equals the cooked sky-visibility grid at 48x27x32 — 41,472 probes — and it cannot
    /// usefully go finer, because probes sharing a visibility cell interpolate from data that does
    /// not vary between them. Getting there costs about 2 ms and 40 MB of atlas, judged worth it by
    /// eye against 1x and 4x with the same lighting and camera.
    ///
    /// What it does NOT fix is the banding aligned to the grid: an eight-probe trilinear blend is
    /// C0, so there is a derivative discontinuity at every cell boundary, and density moves those
    /// bands closer together without removing one. That artifact needs a different tool, and the
    /// axis being exhausted is what makes cook-time probe placement the next question rather than a
    /// later one.
    /// </remarks>
    private float bounceDiv = 1f;
    /// <summary>--sun-overhead: straight down, so the courtyard is lit while base lighting is worked on.</summary>
    private bool sunOverhead;
    private static readonly string[] DefaultProbeCandidates =
        { "pizzo_pernice_puresky_4k.blixprobe", "kloppenheim_05_4k.blixprobe",
          "autumn_field_4k.blixprobe", "rogland_overcast_4k.blixprobe", "sky_hdr.blixprobe" };
    private string abMode = "";
    private bool abFlat;
    private const int AbPeriodFrames = 120;
    /// <summary>--orbit: a closed camera path, one revolution per A/B phase. See ApplyOrbit.</summary>
    private bool orbit;
    private const int OrbitFrames = AbPeriodFrames;
    // <b>--ab lod with two budgets, because "is LOD worth it" and "is 1.0 px worth it over 0.3" are
    // different questions and only the first had an instrument.</b> Comparing budgets across
    // separate runs is exactly what this laptop's thermal drift destroys — the MSAA attempt went
    // 43.85 to 61.91 ms over four runs with nothing changed. Two budgets alternating INSIDE one
    // process is the only shape that survives here. Defaults reproduce the original arm: the
    // configured budget against no LOD at all.
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
    };

    // Live overrides for the two cloth numbers, so they can be found by eye and then written back
    // into the patch. Off by default: the cooked value is the real one.
    private bool clothOverride;
    // Seeded from what the patch now ships, so flipping the override on does not jump the image.
    private float sheenRoughness = 1.0f;
    private float diffuseTransmit = 0.0f;

    // --ao-fullres: run ambient visibility at framebuffer resolution instead of half. Half res is
    // the right default for a low-frequency term, but a crease a few centimetres wide is not low
    // frequency, and at half res plus a 3x3 bilateral it spans about one texel before being blurred
    // with its neighbours. This exists to find out whether fold detail is lost to RESOLUTION rather
    // than to the search radius.
    private float aoScale = 0.5f;

    // The incident-light field's resolution, as a fraction of the framebuffer. Half by default:
    // the probe volume it reconstructs is coarser than that by a wide margin. 1 via --incident-full
    // for the paired comparison.
    private float incidentScale = 0.5f;
    // <b>On by default: the lit pass reads both probe-volume terms from the half-res field.</b>
    // -14.64 ms on the orbit, -29.99 ms with the occupancy march on, for 0.99 mean sRGB at the
    // arcade camera it was judged from and 1.28 on the orbit. --no-incident restores the inline
    // lookups, which is still how the field is priced (--ab incident) and compared.
    private bool incidentField = true;

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
    /// <summary>--shadow-maps A,B,C: the three cascade resolutions. See the note on the default.</summary>
    /// <remarks>
    /// <b>Two costs move together here, which is what makes resolution worth revisiting.</b> A
    /// cascade's world texel is its span over its size, and the caster budget is derived from that
    /// texel — so halving a map both quarters the fill AND doubles the geometric error its casters
    /// may carry, which coarsens their LOD. Isolation measured cascade 0 at 2.5x cascade 2 for
    /// exactly these two reasons compounding.
    ///
    /// 2048 over 14 m is a 1.8 cm texel, and the surface filter is a 4-tap Vogel disc of 2 texels
    /// radius — so the map resolves detail three times finer than anything that reads it.
    /// </remarks>
    private static int[] ShadowMapSizes = { 2048, 2048, 1024 };
    // View-space depth boundaries: cascade i covers (Splits[i], Splits[i+1]).
    // [1..3] (the cascade far distances) are live-tunable from the overlay.
    // <b>Cascade 0 reaches 14 m, not 6.</b> At 6 m the switch to cascade 1 happened close enough to
    // the camera that the resolution step was visible as choppy shadow edges on anything mid-range.
    // 2048 texels over 14 m is 7 mm each, which is still finer than the geometry has detail.
    private readonly float[] cascadeSplits = { 0.1f, 14f, 30f, 60f };
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
    private readonly long[] cascadeTriangles = new long[CascadeCount];
    private long cameraTriangles;
    private readonly long[] cascadeTriangleSum = new long[CascadeCount];
    private long cameraTriangleSum;
    private long triangleFrames;
    private bool triangleWindowOpen;
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
    private Matrix4x4 prevAmbientViewProj = Matrix4x4.Identity;
    private bool ambientHistoryValid;
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
    // <b>The SKY, as opposed to the sky convolved for specular.</b> envCubeTexture above holds the
    // prefiltered chain on the cooked path — its name has been lying — so the background was being
    // drawn from a 128px roughness-0 convolution while the sharp 256px cube the cook ships went
    // unread. The procedural fallback bound the real cube, which is why this only ever looked wrong
    // with a probe: a soft, out-of-focus sky seen through a window.
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
        public int PickLod(Vector3 cameraPos, float errorScale, float errorPixels, int currentLevel)
        {
            if (LodIndexCounts.Length <= 1 || errorPixels <= 0f) return 0;
            var nearest = Vector3.Clamp(cameraPos, Bounds.Min, Bounds.Max);
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

            // <b>A threshold with no memory oscillates on the threshold.</b> The test above is a
            // hard comparison against a continuous distance, so a primitive parked near a boundary
            // flips level on sub-millimetre camera movement — every frame, forever. That is the
            // flicker on the pillars, and no budget setting removes it: moving the budget only
            // moves where the boundary is, and there is always geometry sitting on it.
            //
            // The band is deliberately asymmetric toward quality. Refining happens the instant the
            // finer level is asked for, because the cost of being briefly too detailed is some
            // triangles. Coarsening waits until the coarser level is comfortably inside the budget
            // rather than merely inside it, because the cost of being briefly too coarse is the
            // artifact this exists to stop.
            currentLevel = Math.Clamp(currentLevel, 0, LodIndexCounts.Length - 1);
            if (wanted <= currentLevel) return wanted;
            return SettleCoarser(currentLevel, wanted, errorPixels / LodHysteresis, pixelsPerWorld);
        }

        /// <summary>The coarsest level between here and `wanted` that clears the hysteresis band.</summary>
        /// <remarks>
        /// <b>Refusing the target is not the same as refusing to move.</b> This used to return the
        /// current level whenever the wanted one failed the band — so a primitive entitled to level
        /// two, but reaching for three, kept level zero. The LOD census caught it as an impossibility:
        /// the count of primitives at full detail ROSE as the budget was loosened, 1031 to 1148,
        /// which no threshold that only relaxes can do.
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
        /// <b>A shadow cascade does not measure error in camera pixels, and asking it to was
        /// costing the whole caster redraw.</b> The camera's budget is a screen quantity because
        /// perspective makes the same deviation matter less with distance. A cascade is orthographic
        /// — its shadow-map texel is the same size in metres everywhere inside it — so the tolerance
        /// there is distance-independent, and it is not a taste setting either: geometry that
        /// deviates by less than a texel cannot move the shadow it casts. Casters were being held to
        /// the accuracy of the shaded silhouette instead, which nothing was ever going to look at.
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
    // How far inside the budget a coarser level has to be before it is taken. 1.0 restores the
    // old memoryless behaviour exactly; 1.35 is roughly a sixth of a level's error step at the
    // 2x-per-level decimation this cook produces, which is enough to clear the camera jitter that
    // was driving the oscillation without noticeably delaying a real transition.
    private const float LodHysteresis = 1.35f;
    /// <summary>What multiple of the global LOD budget alpha-cutout geometry starts at.</summary>
    /// <remarks>
    /// <b>1, because it was measured and it is not the lever.</b> Foliage costs pixels, not
    /// geometry: removing the packs entirely takes 9.85 ms off the frame, while an EIGHT times
    /// budget removes only 14% of submitted triangles — the same leaves cover the same screen area
    /// with fewer triangles behind them. Overriding it bought a few percent of geometry and risked
    /// silhouettes on the one asset whose silhouette is the point. --foliage-lod still sweeps it.
    /// </remarks>
    private static float FoliageLodMargin = 1f;
    private Vector3 foliageCentre;
    private bool foliageValid;
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
    // <b>The level each primitive is currently AT, which a screen-space-error test needs and did
    // not have.</b> Shared by the camera, blend and cascade fills: all of them select at the same
    // camera position under the same budget, so they must reach the same answer or the depth
    // pre-pass and the lit pass stop matching. One array per drawable list is what guarantees that
    // — the second and third callers in a frame re-confirm a decision rather than re-taking it.
    private int[] opaqueLodState = System.Array.Empty<int>();
    private int[] blendLodState = System.Array.Empty<int>();
    // Cascades select on their own budget now (see PickLodWorld), so they cannot share the camera's
    // state — and they cannot share each other's either, because each cascade has its own texel.
    private int[][] cascadeLodState = System.Array.Empty<int[]>();
    // How many shadow texels of geometric deviation a caster may have. Below one texel the error
    // cannot move the shadow at all; a little over one is where it starts to be theoretically
    // visible and still is not, because the PCF kernel is wider than that.

    // Slack on the camera frustum test, in metres. See the call site in OnRender.
    private const float CameraCullMargin = 0.5f;
    /// <summary>--no-hashed-alpha: binary cutouts at one sample, the state before the hashed test.</summary>
    private bool hashedAlpha = true;
    private static readonly GraphicsColor MultiSelectColor = new(0.95f, 0.75f, 0.2f, 1f);
    private readonly HashSet<Key> heldKeys = new();
    private Matrix4x4 viewProj;

    // Sun travel direction, recomputed from sunYaw/sunPitch (overlay Sun
    // scope). Initialised in OnLoad from the default direction below. Note the
    // IBL cubes are baked once with SkyBakeSunDirection, so live sun changes
    // relight the direct sun + shadows but not the indirect IBL.
    // Authored, not derived: this is the sun the scene was actually tuned under (yaw -22.7 deg,
    // pitch -73.1 deg), read back off a live session rather than guessed. The cooked probe reports
    // its own sun and used to overwrite this at load — see alignSunToProbe, which keeps that path
    // available. The cost of authoring it is that the baked environment's sun disc sits where the
    // HDR put it, ~27 deg away, so IBL specular no longer agrees with the cast shadows; the sun's
    // irradiance and its shadows do still agree with each other, which is the pair that shows.
    private Vector3 sunDirection = Vector3.Normalize(new Vector3(-0.1120f, -0.9568f, -0.2682f));

    // --sun-from-probe restores the old behaviour: take the sun from the HDR the IBL was baked
    // from, so the visible sky sun and the cast shadows line up and the authored angle is ignored.
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

    /// <summary>Multiplier on the MEASURED sun irradiance. 1.0 is what the probe reported.</summary>
    /// <remarks>
    /// <b>A knob the units arc deliberately deleted, coming back with a different meaning.</b> What
    /// was removed were intensity dials that stood in for a measurement nobody had taken — the sun's
    /// brightness was whatever made the image look right. It is measured now, off the HDR the IBL is
    /// baked from, so this multiplies a known quantity instead of replacing it: 1.0 is the
    /// measurement, and anything else is a scene decision somebody can read and argue with. The same
    /// shape as BounceStrength, and for the same reason.
    /// <para>
    /// It scales the sun everywhere at once — the raster's direct term, the shadowed sun inside the
    /// probe injection, and therefore the bounce as well. A sun that is brighter for the eye and not
    /// for the indirect solve would be a lie the second bounce would expose.
    /// </para>
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
    [Tune(0f, 0.5f)]    public float Density = 0.018f; // extinction coefficient, per metre
    [Tune(0f, 1f)]      public float Scatter = 0.6f;   // scattering albedo: the share of extinction that scatters
    [Tune(-0.9f, 0.9f)] public float PhaseG = 0.6f;    // Henyey-Greenstein anisotropy
    // <b>A fallback, not a look.</b> The medium's ambient in-scatter is the sky-visibility volume
    // and the probe field; this value is what it scatters when neither has been baked, and turning
    // it up with them loaded is disagreeing with a measurement.
    [Tune(0f, 0.2f)]    public float Ambient = 0.005f;
    [Tune(10f, 150f)]   public float Far = 60f;        // grid far distance (metres)
    // <b>A length, like the ambient radius.</b> The height over which the medium thins by a factor
    // of e, measured from the volume floor. Air does this; a constant-density volume does not, and
    // a constant-density volume is what makes fog read as a filter laid over the picture.
    [Tune(2f, 100f)]    public float HeightFalloff = 14f;
    // How much of the density comes from the drifting noise field rather than the smooth falloff.
    // 0 is the old even haze. The feature size and drift speed are derived from the fog's range,
    // not dialled — they are what keeps the look the same when the range changes.
    [Tune(0f, 1f)]      public float Noise = 0.55f;
    // <b>How much of the medium comes from the previous frame.</b> Not a quality dial with a
    // "better" end: it trades stability for latency, and the lower bound is not "worse", it is
    // "only this frame". 0 disables the whole temporal path, which is the A/B and the fallback.
    // 0.9 is about a ten-frame time constant — long enough to integrate the jittered slice samples
    // into a soft shaft, short enough that dragging the sun does not leave a trail behind it.
    [Tune(0f, 0.98f)]   public float Temporal = 0.9f;
    // Replaces the fog with where its history was REFUSED — bright at disocclusions and at the edge
    // the camera turned onto, black wherever the previous frame was trusted. Expect a black screen
    // when standing still: that is the instrument working, not failing.
    [Tune]              public bool ShowHistory;
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
    // <b>Not an albedo any more, and the rename is the point.</b> Surface colour now comes from
    // the baked albedo grid, so this multiplies a physical quantity instead of standing in for one.
    // 1.0 means "exactly the bounce the measured albedos give"; Sponza's average out around 0.2,
    // which is a good deal less indirect light than the old 0.35 scalar was handing every surface.
    // Above 1 is then a legible artistic decision rather than a fudge with a misleading name.
    [Tune(0f, 4f)]    public float BounceStrength = 1.0f;
    // <b>How much of the visibility comes from previous frames.</b> GTAO is four slices per pixel,
    // and the spatial denoise downstream was sized on the argument that each pixel only has to be
    // unbiased rather than quiet. Accumulation extends that argument by one step — unbiased over
    // TIME as well — which is what lets the slice and step counts come down. 0 disables it and is
    // the honest baseline.
    [Tune(0f, 0.97f)] public float Temporal = 0.9f;
    // Replaces the visibility with where history was refused or clamped back: bright at
    // disocclusions and the screen edge, black where the previous frame was trusted.
    [Tune]            public bool ShowRejection;
    // Horizon slices and steps along each. 4x6 = 24 taps was sized against the spatial denoise
    // alone; with history integrating over frames as well, the honest question is how far these
    // fall before the image moves, and that is a sweep rather than an opinion.
    [Tune(1f, 8f)]    public float Slices = 4f;
    [Tune(1f, 12f)]   public float Steps = 6f;
}

// Misc render tunables (overlay "Render" group). Vsync stays a manual toggle —
// it's a device property, not a field.
internal sealed class RenderSettings
{
    // <b>Raised from 0.5, because three sessions in a row moved it and none of them moved it down.</b>
    // Dumps recorded exposure at 1.65, 4.04 and 2.07 against a shipped 0.5, always paired with a sun
    // strength of 2.2x to 5.2x — the scene arrives far darker than anybody wants to look at it. This
    // is the display-side half of that gap and the safe half to change: it says how bright the image
    // is presented, not how bright the sun IS. The other half is a claim about a measured irradiance
    // and it needs an investigation, not a default.
    [Tune(0.05f, 16f)] public float Exposure = 2.0f;
    [Tune]             public TonemapMode Tonemap = TonemapMode.AgX;
    [Tune(0.3f, 60f)]  public float MoveSpeed = 4.5f;
    // <b>Two pixels, and the census is why rather than taste.</b> Submitted triangles fall about a
    // fifth per doubling of this budget all the way out — the chain does not saturate until 8 px —
    // but frame time stops following it long before that: 1 px to 2 px drops 22% of the geometry
    // for 1.5 ms, and 2 px to 4 px drops another 17% for 0.56 ms, which is inside this laptop's
    // noise. Past here the frame is not geometry-bound, so a tighter budget buys triangles that
    // nothing is waiting on. Judge a change to it with lod-tris in the overlay, not the frame
    // counter: the count is exact and the clock on this machine is not.
    [Tune(0f, 8f)]     public float LodErrorPixels = 2.0f;
    // <b>How much of the image comes from previous frames. 0 is off, and off is the baseline.</b>
    // This costs a full-screen resolve and buys stability, not milliseconds — the temporal work that
    // bought time was the fog's slice count and GTAO's tap count, and both are already spent. The
    // policy it implements is that the present frame owns the image and history only quietens it:
    // rejected off-screen, clamped to the current neighbourhood everywhere else.
    [Tune(0f, 0.97f)]  public float Taa = 0.7f;
    // Bright where history was refused or clamped back. A still camera should show nearly nothing.
    [Tune]             public bool ShowTaaRejection;
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
