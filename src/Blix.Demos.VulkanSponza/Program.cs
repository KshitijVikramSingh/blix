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
// Performance shape (see docs/renderer.md → Geometry LOD + the PR that landed
// this): screen-space-error LOD over meshopt chains, cook-time spatial split of
// oversized primitives, all geometry consolidated into one shared VB/IB (draws
// are sub-ranges), and cutout foliage routed to depth-writing MASK + alpha-to-
// coverage so the hero tree isn't overdraw-bound. CPU-phase timing
// (cpu-wait/encode/submit) is surfaced in the diagnostics overlay.
//
// Asset story: shares the existing Blix.Demos.SponzaModern/Assets tree
// (csproj links it in, runtime reads from the demo's own Assets/ copy
// in bin/). Missing-asset path prints a clear instruction and exits 0.
public static class Program
{
    public static void Main()
    {
        var loop = new SponzaLoop();
        using var window = new Window(loop, new WindowOptions("Blix — Vulkan Sponza", 1440, 810));
        window.Run();
    }
}

internal sealed class SponzaLoop : IGameLoop, IInputHandler, IDebuggable, IDisposable
{
    public string DebugName => "vulkan-sponza";

    private IRenderHost host = null!;
    private VulkanGraphicsDevice vk = null!;
    private RenderGraph graph = null!;

    // Graph resources + passes.
    private GraphResourceHandle hdrHandle;       // 1× resolve target (present samples this)
    private GraphResourceHandle hdrMsaaHandle;   // MSAA colour the lit pass renders into
    private GraphResourceHandle depthHandle;     // MSAA depth (matches hdrMsaa)
    // 4× MSAA. Measured geometry-bound (cutting MSAA 4→2 left frame time flat —
    // the GPU wall is triangle/binning cost, not fragment/MSAA), so 2× bought no
    // frame time and we keep 4× for edge quality. R11G11B10F already quarters the
    // scene-colour tile vs the old 4×/Rgba16F, recovering the memory/bandwidth.
    private const int MsaaSamples = 4;
    private PassHandle litPassHandle;

    // Depth pre-pass: renders non-blend geometry depth-only into depthHandle
    // before the lit pass, so the expensive lit fragments run only on visible
    // pixels (kills Sponza overdraw). Reuses lit.vert (invariant gl_Position)
    // so the lit pass's LessEqual test matches the pre-pass depth exactly.
    private PassHandle depthPrepassHandle;
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
    private PipelineHandle blendSolidPipeline;
    private PipelineHandle blendDoubleSidedPipeline;

    // Skybox program + pipeline. Drawn between opaque/mask and blend so
    // translucent windows composite over the sky correctly.
    private ShaderProgramHandle skyProgram;
    private PipelineHandle skyPipeline;

    // Present.
    private ShaderProgramHandle presentProgram;
    private PipelineHandle presentPipeline;
    private VertexBufferHandle presentDummyVB;
    private IndexBufferHandle presentDummyIB;

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
    // group via ObjectTunables (see FogSettings); replaces six hand-wired dials.
    private readonly FogSettings fog = new();
    private readonly ShadowsSettings shadows = new();
    private readonly RenderSettings render = new();
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
    // How far behind the scene slab the light "eye" sits, in world units.
    // Larger keeps the whole atrium height inside each cascade's near/far.
    // Live-tunable from the overlay (Shadows scope).
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
    private readonly float[] cascadeDepthBias = new float[CascadeCount];
    // Shadow depth bias in shadow-texels (live-tunable from the overlay).
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


    // IBL textures (procedural sky bake, CPU-side, generated once at load).
    //   envCube           prefiltered specular stand-in (mip chain at increasing roughness)
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
        // GPU-driven-rendering Stage 0: all primitives' vertices live in one
        // shared VB and their LOD indices in one shared IB (u16 or u32 per
        // drawable). A draw is now a sub-range: vertexOffset = BaseVertex,
        // indexOffset = LodFirstIndex[lod]. No per-prim buffers.
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

    // Stage 0 shared geometry buffers (one VB + one IB per index width), built
    // by ConsolidateBuffers from the staging lists. The eventual indirect path
    // draws ranges out of these; today the per-drawable loops do.
    private readonly List<DrawableStaging> staging = new();
    private VertexBufferHandle sharedVb;
    private IndexBufferHandle sharedIbU16;
    private IndexBufferHandle sharedIbU32;
    private VertexLayout sharedLayout = VertexPosition3NormalTangentTexture.Layout;

    // GPU-driven Stage 1: opaque drawables are emitted grouped by
    // (pipeline, material, index-width) so each group is a contiguous run in the
    // shared buffers + the indirect buffer. Each frame we fill one
    // VkDrawIndexedIndirectCommand per opaque drawable (LOD picked by SSE), then
    // issue ONE vkCmdDrawIndexedIndirect per group instead of a draw per object.
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
    private readonly Vector3 ambientColor = new(0.42f, 0.50f, 0.62f);  // unused; kept for layout compat

    // Shader-uniform tunables (sun/ambient intensity, metallic/normal/shadow
    // thresholds, cascade-viz) are declared with //@tune in lit.frag and
    // auto-bound to the overlay by this panel — no per-variable field + dial +
    // UBO pack here. The panel owns the live values (seeded from the shader's
    // tag defaults) and feeds them into the per-frame write by name; CPU-side
    // readers (e.g. the froxel sun term) pull via tunePanel.Value(...).
    private ShaderTunablePanel tunePanel = null!;
    // [Tune]-tagged CPU settings objects (Fog, …) auto-paneled in the overlay.
    private ObjectTunables tuneObjects = null!;

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        vk = (VulkanGraphicsDevice)graphicsDevice;
        textureLoader = new GltfTextureLoader(vk);
        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        renderHeightPx = host.LogicalSize.Height;

        // Volumetric fog is off by default (it adds a per-frame compute pass);
        // launch with --fog to start with it on, or toggle it in the overlay.
        var cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Contains("--fog")) fog.Enabled = true;
        // --fog-stress flips fog on/off every ~90 frames so a validation run
        // exercises the compute storage-image layout transitions across the
        // disabled↔enabled boundary (the highest-risk sync path).
        if (cmdArgs.Contains("--fog-stress")) { fogStress = true; fog.Enabled = true; }

        // Seed sun yaw/pitch from the default direction so the Sun controls
        // start matching the baked look.
        sunPitch = MathF.Asin(Math.Clamp(sunDirection.Y, -1f, 1f));
        sunYaw = MathF.Atan2(sunDirection.X, -sunDirection.Z);

        // Diagnostics overlay: GPU info + this loop's shadow/camera controls,
        // live values, and cascade gizmos (see Debug()). Replaces the old
        // Console-log + hardcoded-key debugging.
        if (host is IDebugHost debugHost && debugHost.System is { } dbg)
        {
            // Only register the GPU contributor. The runtime already runs this
            // loop's Debug() via Run(debuggable) since it implements IDebuggable
            // — also registering it would run (and render its controls) twice.
            graphicsDevice.RegisterDebug(dbg);
            // Kept so click-to-pick can CollectSelectables()/Select(); the
            // SceneSelection contributor is registered after consolidation.
            debugSystem = dbg;
        }

        // --- Locate Sponza glTF -----------------------------------------
        // The setup-sponza-modern.sh script populates this path; when it
        // hasn't been run, exit cleanly so CI on a vanilla checkout doesn't
        // fail. The glTF filename varies across Khronos pack revisions, so
        // we glob for the first .gltf under main_sponza/.
        //
        // BLIX_SPONZA_ASSETS lets the (~19GB) pack set live on an external
        // SSD shared across machines: when set, the runtime reads straight
        // from it and the csproj skips copying anything into bin/. Falls
        // back to the bin-local Assets/ copy when the var is unset.
        var assetsRoot = Environment.GetEnvironmentVariable("BLIX_SPONZA_ASSETS") is { Length: > 0 } envAssetsRoot
            ? envAssetsRoot
            : Path.Combine(AppContext.BaseDirectory, "Assets");
        var mainPackDir = Path.Combine(assetsRoot, "main_sponza");
        if (!Directory.Exists(mainPackDir))
        {
            Console.WriteLine($"[VulkanSponza] Main Sponza assets not found at {mainPackDir}.");
            Console.WriteLine("[VulkanSponza] Run tools/setup-sponza-modern.sh once to populate from your local Khronos packs,");
            Console.WriteLine("[VulkanSponza] or set BLIX_SPONZA_ASSETS to an existing pack dir (e.g. on an external SSD).");
            host.RequestClose();
            return;
        }
        var gltfPath = Directory.EnumerateFiles(mainPackDir, "*.gltf", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (gltfPath is null)
        {
            Console.WriteLine($"[VulkanSponza] No .gltf file in {mainPackDir}. Re-run tools/setup-sponza-modern.sh.");
            host.RequestClose();
            return;
        }

        // --- IBL: cooked .blixprobe (real GGX prefilter) or procedural ------
        // Prefer a cooked probe baked from the HDR sky (real GGX importance-
        // sampled specular + cosine irradiance + split-sum BRDF LUT, RGBA16F).
        // Falls back to the procedural analytic-sky bake when no probe is
        // present (vanilla checkout that hasn't run the cook step).
        // Probe preference: autumn_field (has a sun → high light/dark contrast
        // for punchy shadows; we align our directional sun to its detected sun)
        // → rogland overcast (sunless ambient, our own sun) → old sky → procedural.
        // The sun-alignment is automatic: HdrSunFinder returns null for skies
        // with no clear sun, so we only align when the probe actually has one.
        string[] probeCandidates = { "autumn_field_4k.blixprobe", "rogland_overcast_4k.blixprobe", "sky_hdr.blixprobe" };
        var probePath = probeCandidates
            .Select(p => Path.Combine(assetsRoot, "textures", p))
            .FirstOrDefault(File.Exists);
        if (probePath is not null)
        {
            try
            {
                var baked = EnvironmentBaker.UploadCookedProbe(vk, BlixProbeReader.Read(probePath), "sponza.ibl");
                envCubeTexture = baked.Probe.PrefilteredSpecular;
                irradianceCubeTexture = baked.Probe.DiffuseIrradiance;
                brdfLutTexture = baked.BrdfLut;
                iblPrefilterMips = baked.Probe.PrefilteredSpecularMipCount;
                Console.WriteLine($"[VulkanSponza] IBL: cooked probe {Path.GetFileName(probePath)} ({iblPrefilterMips} GGX prefilter mips).");
                // Align the directional sun (key light + shadow caster) to the
                // probe's detected sun so cast shadows match the visible sky sun.
                // FROM-sun-into-scene convention, matching sunDirection.
                if (baked.Probe.SunDirectionFromEquirect is { } hdrSun)
                {
                    sunDirection = Vector3.Normalize(hdrSun);
                    sunPitch = MathF.Asin(Math.Clamp(sunDirection.Y, -1f, 1f));
                    sunYaw = MathF.Atan2(sunDirection.X, -sunDirection.Z);
                    Console.WriteLine($"[VulkanSponza]   sun aligned to probe: {sunDirection}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VulkanSponza] probe load failed ({ex.Message}); using procedural IBL.");
                BakeProceduralIbl();
            }
        }
        else
        {
            Console.WriteLine("[VulkanSponza] IBL: no cooked probe (run tools/setup-sponza-modern.sh / blix-cook probe); using procedural sky.");
            BakeProceduralIbl();
        }

        // --- Render graph ------------------------------------------------
        graph = new RenderGraph(vk);
        var fullSize = new MatchSwapchainGraphSize(1.0f);
        // R11G11B10F (not Rgba16F): half the bytes/pixel → half the MSAA tile
        // footprint + resolve bandwidth on TBDR, for an opaque HDR radiance
        // target only ever sampled .rgb by tonemap. No alpha (glass blends with
        // source alpha, which needs no dst-alpha channel).
        hdrHandle = graph.ColorTarget("hdr", TextureFormat.R11G11B10F, fullSize);
        // MSAA colour + depth the lit pass renders into; resolves to hdr.
        hdrMsaaHandle = graph.ColorTarget("hdr-msaa", TextureFormat.R11G11B10F, fullSize, samples: MsaaSamples);
        depthHandle = graph.DepthTarget("scene-depth", fullSize, samples: MsaaSamples);

        // One depth target per cascade, sized per ShadowMapSizes (no 2D-array
        // creation API yet; the lit pass binds the three as a Count=3 sampler
        // array — mixed sizes are fine, each has its own view).
        for (var c = 0; c < CascadeCount; c++)
        {
            cascadeHandles[c] = graph.DepthTarget($"sun-cascade{c}",
                new FixedGraphSize(ShadowMapSizes[c], ShadowMapSizes[c]));
        }

        // The per-frame Frame UBO (view-proj, sun, camera, cascades, fog) and
        // its //@tune-decorated shader tunables are declared in lit.frag; their
        // std140 offsets are reflected, not documented here (they used to be a
        // hand-counted table in this comment — see lit.frag for the live layout).
        // Shader binding interfaces — descriptor sets, std140 UBO layouts, and
        // push-constant ranges — are reflected from the compiled SPIR-V at
        // build time (spirv-cross sidecars next to each .spv), not hand-
        // authored. See docs/renderer.md "SPIR-V reflection". Each program
        // reflects exactly what its stages declare; per-draw descriptor binding
        // skips any per-pass texture a program doesn't sample, so the skybox
        // no longer has to restate the lit pass's set-1 bindings for "layout
        // compatibility" — there is no shared bound set to be compatible with.
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        ShaderInterface Reflect(params string[] stages) =>
            ShaderReflection.MergeStages(
                stages.Select(s => ShaderReflection.Load(
                    Path.Combine(shaderDir, s + ".spv.refl.json"))).ToArray());

        var litInterface = Reflect("lit.vert", "lit.frag");
        var skyInterface = Reflect("skybox.vert", "skybox.frag");
        var presentInterface = Reflect("present.vert", "present.frag");
        var shadowOpaqueInterface = Reflect("shadow.vert", "shadow.frag");
        var shadowMaskInterface = Reflect("shadow_mask.vert", "shadow_mask.frag");

        // Scan lit.frag's //@tune decorators (shipped alongside the .spv) and
        // build the overlay's shader-variable panel. The panel owns the live
        // values + the dials; the per-frame write and the froxel sun term pull
        // from it by name. Replaces the hand-wired field/dial/pack per uniform.
        tunePanel = new ShaderTunablePanel(ShaderTunables.Scan(
            File.ReadAllText(Path.Combine(shaderDir, "lit.frag"))));
        tuneObjects = new ObjectTunables(fog, shadows, render);

        // One graphics pass per cascade, each writing its own depth target.
        // Both shadow programs are render-pass-compatible with these passes.
        for (var c = 0; c < CascadeCount; c++)
        {
            cascadePassHandles[c] = graph.GraphicsPass($"sun-cascade{c}")
                .Depth(cascadeHandles[c], LoadOp.Clear, StoreOp.Store)
                .Shader(shadowOpaqueInterface, shadowMaskInterface)
                .Handle;
        }

        // Froxel fog compute pass — fills the 3D scattering grid. Declared
        // between the cascade passes and the lit pass so it runs after the
        // shadow maps are rendered (it samples them) and before the lit pass
        // composites its result. Reflected interface: UBO (set 0 binding 0),
        // the storage grid (binding 1), the cascade shadow maps (binding 2).
        var froxelInterface = Reflect("froxel.comp");
        var froxelPass = graph.ComputePass("froxel-fog").Shader(froxelInterface);
        for (var c = 0; c < CascadeCount; c++)
        {
            froxelPass = froxelPass.Read(cascadeHandles[c]);
        }
        froxelPassHandle = froxelPass.Handle;

        // Depth pre-pass — declared before lit; clears + writes the (4× MSAA)
        // scene depth for all non-blend geometry. Both pre-pass programs reuse
        // litInterface (lit.vert needs set 0 + the model push; the trivial
        // fragments use a subset), so the lit material descriptor set binds to
        // the mask variant unchanged.
        depthPrepassHandle = graph.GraphicsPass("depth-prepass")
            .Depth(depthHandle, LoadOp.Clear, StoreOp.Store)
            .Shader(litInterface)
            .Handle;

        var litPass = graph.GraphicsPass("lit-scene")
            .Target(hdrMsaaHandle, LoadOp.Clear, StoreOp.Store)   // render 4× MSAA
            .ResolveColor(hdrHandle)                              // resolve to 1× for present
            .Depth(depthHandle, LoadOp.Load, StoreOp.Store)       // load the pre-pass depth
            .Shader(litInterface, skyInterface);
        // Declare the cascade depth targets as inputs so the graph orders the
        // shadow passes before the lit pass and transitions them to
        // shader-read layout.
        for (var c = 0; c < CascadeCount; c++)
        {
            litPass = litPass.Read(cascadeHandles[c]);
        }
        litPassHandle = litPass.Handle;
        graph.Compile();

        // --- Shader programs + pipelines --------------------------------
        // shaderDir was resolved above (reflection sidecars live alongside the .spv).
        var litVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "lit.vert.spv"));
        var litFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "lit.frag.spv"));
        litProgram = vk.CreateShaderProgramFromSpv(litVertSpv, litFragSpv, litInterface, "lit");
        // Opaque + Mask share the no-blend pipeline group. Depth is now
        // LessEqual + NO write: the depth pre-pass already wrote the complete
        // scene depth, so the lit pass only shades the front-most fragment
        // (overdraw killed). Mask materials trigger the discard branch via the
        // per-material UBO's alphaCutoff > 0; opaque materials leave it at 0.
        // AlphaToCoverage on the opaque/mask pipelines: cutout foliage (the
        // reclassified blend) outputs a sharpened coverage alpha → antialiased
        // leaf edges under MSAA; solid opaque outputs coverage 1.0 → no effect.
        opaqueSolidPipeline = vk.CreatePipeline(new PipelineDescription(
            litProgram,
            VertexPosition3NormalTangentTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualNoWrite,
            RasterizerState.BackFaceCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPassHandle),
            AlphaToCoverage: true), "lit.opaque");
        opaqueDoubleSidedPipeline = vk.CreatePipeline(new PipelineDescription(
            litProgram,
            VertexPosition3NormalTangentTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualNoWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPassHandle),
            AlphaToCoverage: true), "lit.opaque.doubleSided");

        // Blend: depth-test (so windows don't draw behind walls) but no
        // depth-write (so successive translucent fragments don't z-fight),
        // src-alpha blend. Back-to-front sort within the blend bucket is a
        // deferred polish.
        blendSolidPipeline = vk.CreatePipeline(new PipelineDescription(
            litProgram,
            VertexPosition3NormalTangentTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualNoWrite,
            RasterizerState.BackFaceCulling,
            new[] { BlendState.AlphaBlend },
            RenderTarget: graph.GetPassSurface(litPassHandle)), "lit.blend");
        blendDoubleSidedPipeline = vk.CreatePipeline(new PipelineDescription(
            litProgram,
            VertexPosition3NormalTangentTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualNoWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.AlphaBlend },
            RenderTarget: graph.GetPassSurface(litPassHandle)), "lit.blend.doubleSided");

        // Sky pipeline. Depth-test LessEqual with NO write, so the sky only
        // draws where the depth buffer still holds the clear value (1.0)
        // and never overwrites opaque-geometry depth. Drawn between
        // opaque/mask and blend in OnRender so blend windows composite
        // over the sky.
        var skyVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "skybox.vert.spv"));
        var skyFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "skybox.frag.spv"));
        skyProgram = vk.CreateShaderProgramFromSpv(skyVertSpv, skyFragSpv, skyInterface, "skybox");
        skyPipeline = vk.CreatePipeline(new PipelineDescription(
            skyProgram,
            VertexPosition3NormalTexture.Layout, // ignored — sky vert synthesises positions
            PrimitiveTopology.Triangles,
            DepthState.LessEqualNoWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPassHandle)), "skybox");

        // Shadow caster pipelines. NoCulling so Sponza's double-sided foliage
        // and thin geometry still write depth from both faces; depth-write
        // LessEqual into the cascade target. Both are render-pass-compatible
        // with every cascade pass (all depth-only, same format), so we build
        // them against cascade 0's surface and reuse across cascades.
        var shadowVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow.vert.spv"));
        var shadowFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow.frag.spv"));
        shadowOpaqueProgram = vk.CreateShaderProgramFromSpv(shadowVertSpv, shadowFragSpv, shadowOpaqueInterface, "shadow.opaque");
        shadowOpaquePipeline = vk.CreatePipeline(new PipelineDescription(
            shadowOpaqueProgram,
            VertexPosition3NormalTangentTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(cascadePassHandles[0])), "shadow.opaque");

        var shadowMaskVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow_mask.vert.spv"));
        var shadowMaskFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow_mask.frag.spv"));
        shadowMaskProgram = vk.CreateShaderProgramFromSpv(shadowMaskVertSpv, shadowMaskFragSpv, shadowMaskInterface, "shadow.mask");
        shadowMaskPipeline = vk.CreatePipeline(new PipelineDescription(
            shadowMaskProgram,
            VertexPosition3NormalTangentTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(cascadePassHandles[0])), "shadow.mask");

        // Depth pre-pass pipelines — lit.vert (shared → invariant depth) + a
        // trivial fragment, depth-only into the 4× MSAA pre-pass surface. No
        // culling: solid geometry's nearest face still wins the depth test
        // (matching lit's back-cull front face), and double-sided geometry
        // always writes depth from either view side so the sky never overdraws
        // a back-facing curtain. LessEqualWrite; the lit pass then reads it.
        // Flat preview pipeline (streamed-load phase): lit.vert + flat.frag, lit
        // surface, depth-test no-write (the pre-pass wrote depth). Reuses
        // litInterface; flat.frag samples nothing, so set1/set2 stay unbound (as
        // with the trivial pre-pass program).
        var flatFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "flat.frag.spv"));
        flatProgram = vk.CreateShaderProgramFromSpv(litVertSpv, flatFragSpv, litInterface, "flat");
        flatPipeline = vk.CreatePipeline(new PipelineDescription(
            flatProgram,
            VertexPosition3NormalTangentTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualNoWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPassHandle)), "flat");

        var prepassOpaqueFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "depth_prepass.frag.spv"));
        prepassOpaqueProgram = vk.CreateShaderProgramFromSpv(litVertSpv, prepassOpaqueFragSpv, litInterface, "depth_prepass");
        prepassOpaquePipeline = vk.CreatePipeline(new PipelineDescription(
            prepassOpaqueProgram,
            VertexPosition3NormalTangentTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(depthPrepassHandle)), "depth_prepass");

        var prepassMaskFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "depth_prepass_mask.frag.spv"));
        prepassMaskProgram = vk.CreateShaderProgramFromSpv(litVertSpv, prepassMaskFragSpv, litInterface, "depth_prepass_mask");
        prepassMaskPipeline = vk.CreatePipeline(new PipelineDescription(
            prepassMaskProgram,
            VertexPosition3NormalTangentTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(depthPrepassHandle)), "depth_prepass_mask");

        var presentVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.vert.spv"));
        var presentFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.frag.spv"));
        presentProgram = vk.CreateShaderProgramFromSpv(presentVertSpv, presentFragSpv, presentInterface, "present");
        presentPipeline = vk.CreatePipeline(new PipelineDescription(
            presentProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.Disabled,
            RasterizerState.NoCulling,
            BlendState.Disabled), "present");

        // Dummy VB/IB so the fullscreen-triangle present pass has something
        // to bind. The vertex shader synthesises positions from gl_VertexIndex.
        var dummyVerts = new VertexPosition3NormalTexture[]
        {
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0)),
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0)),
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0)),
        };
        presentDummyVB = vk.CreateVertexBuffer(
            VertexPosition3NormalTexture.CreateBufferData(dummyVerts), "present.dummy.vb");
        presentDummyIB = vk.CreateIndexBuffer(new ushort[] { 0, 1, 2 }, name: "present.dummy.ib");

        // --- Froxel fog compute program + grid ---------------------------
        var froxelSpv = File.ReadAllBytes(Path.Combine(shaderDir, "froxel.comp.spv"));
        froxelProgram = vk.CreateComputeShaderProgramFromSpv(froxelSpv, froxelInterface, "froxel");
        froxelPipeline = vk.CreateComputePipeline(froxelProgram, "froxel");
        // View-aligned 3D scattering grid, sampled trilinearly by the lit pass.
        froxelGridTexture = vk.CreateStorageTexture3D(
            FroxelGridX, FroxelGridY, FroxelGridZ,
            TextureFormat.Rgba16F, SamplerDescription.LinearClamp, "sponza.froxel_grid");

        // Build the per-frame-constant buffers once (graph compiled + all
        // textures created by now). Reused every frame in OnRender.
        identityPush = ModelPushBytes(Matrix4x4.Identity);
        passBindings = new[]
        {
            new ShaderTextureBinding("uIrradiance",     irradianceCubeTexture, Slot: 0),
            new ShaderTextureBinding("uPrefilteredEnv", envCubeTexture,        Slot: 1),
            new ShaderTextureBinding("uBrdfLut",        brdfLutTexture,        Slot: 2),
            new ShaderTextureBinding("uCascadeShadowMaps[0]", graph.GetDepthTexture(cascadeHandles[0]), Slot: 3, ArrayIndex: 0),
            new ShaderTextureBinding("uCascadeShadowMaps[1]", graph.GetDepthTexture(cascadeHandles[1]), Slot: 3, ArrayIndex: 1),
            new ShaderTextureBinding("uCascadeShadowMaps[2]", graph.GetDepthTexture(cascadeHandles[2]), Slot: 3, ArrayIndex: 2),
            new ShaderTextureBinding("uFroxelGrid",           froxelGridTexture, Slot: 4),
        };

        // Froxel compute set-0 image bindings (constant handles): the storage
        // grid it writes (binding 1) + the cascade shadow maps it samples
        // (binding 2, Count=3). The UBO (binding 0) is written per frame.
        froxelBindings = new[]
        {
            new ShaderTextureBinding("uGrid", froxelGridTexture, Slot: 1),
            new ShaderTextureBinding("uCascadeShadowMaps[0]", graph.GetDepthTexture(cascadeHandles[0]), Slot: 2, ArrayIndex: 0),
            new ShaderTextureBinding("uCascadeShadowMaps[1]", graph.GetDepthTexture(cascadeHandles[1]), Slot: 2, ArrayIndex: 1),
            new ShaderTextureBinding("uCascadeShadowMaps[2]", graph.GetDepthTexture(cascadeHandles[2]), Slot: 2, ArrayIndex: 2),
        };

        // --- Load Sponza geometry ---------------------------------------
        // Static-mesh importer: Sponza has no skinning. Each glTF primitive
        // becomes one engine-side mesh with a baked-in node transform; its glTF
        // material (BaseColor/normal/MR/AO/emissive + alpha mode) is resolved
        // and cached by GetMaterial.
        // Parse every pack's geometry/materials on a background thread, in
        // parallel — the ~7s .blixmesh parse + UV-sanitize was the bulk of an
        // ~8s synchronous load that froze the window. Parsing is pure CPU (no
        // GPU), so it's safe off-thread; the GPU work (BuildDrawables +
        // ConsolidateBuffers) stays on the main thread, run by TryFinishLoad
        // from OnUpdate once parsing completes. Until then sceneLoaded stays
        // false and OnRender shows a responsive clear (loading screen).
        // Candles intentionally excluded (no light source in this renderer).
        var packsToParse = new List<(string Name, string Path, string AssetId)>
        {
            ("main", gltfPath, "models/sponza_main"),
        };
        AddOptionalPackPath(packsToParse, assetsRoot, "curtains", "addons/curtains");
        AddOptionalPackPath(packsToParse, assetsRoot, "ivy",      "addons/ivy");
        AddOptionalPackPath(packsToParse, assetsRoot, "trees",    "addons/trees");
        meshLoad.Start(() =>
        {
            var prims = new List<GltfPrimitive>();
            foreach (var (name, model) in ParsePacksParallel(packsToParse))
            {
                prims.AddRange(model.Primitives);
                Console.WriteLine($"[VulkanSponza] {name} pack: {model.Primitives.Length} primitives.");
            }
            return prims;
        });

        UpdateCamera();
    }

    private static void AddOptionalPackPath(
        List<(string Name, string Path, string AssetId)> packs, string assetsRoot, string packDirName, string assetId)
    {
        var packDir = Path.Combine(assetsRoot, packDirName);
        if (!Directory.Exists(packDir)) return;
        var gltf = Directory.EnumerateFiles(packDir, "*.gltf", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (gltf is not null) packs.Add((packDirName, gltf, assetId));
    }

    // Background, parallel: each pack gets its own importer (no shared state) and
    // is parsed to CPU geometry/material data. flipTextureV matches the cook's
    // --flip-v (Intel Sponza is bottom-up); includeTangents forwards the glTF
    // TANGENT for the lit TBN. Returns models in pack order (main first).
    private static List<(string Name, GltfModel Model)> ParsePacksParallel(
        List<(string Name, string Path, string AssetId)> packs)
    {
        var parsed = new (string Name, GltfModel Model)?[packs.Count];
        System.Threading.Tasks.Parallel.For(0, packs.Count, i =>
        {
            var p = packs[i];
            try
            {
                var model = new GltfStaticImporter().Import(
                    new AssetImportContext(AssetId.Parse(p.AssetId), p.Path, flipTextureV: true, includeTangents: true));
                parsed[i] = (p.Name, model);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VulkanSponza] {p.Name} pack failed to parse: {ex.Message}");
            }
        });
        return parsed.Where(r => r.HasValue).Select(r => r!.Value).ToList();
    }

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

    private void TryFinishLoad()
    {
        if (sceneLoaded) return;
        if (meshLoad.IsFaulted)
        {
            Console.WriteLine($"[VulkanSponza] load failed: {meshLoad.Fault?.Message}");
            sceneLoaded = true; // give up → render the empty clear
            return;
        }

        // Stage primitives within the per-frame budget; false while still parsing
        // off-thread or with more to stage. True once every primitive is staged.
        if (!meshLoad.Drain(LoadBudgetMs, StageDrawable)) return;

        // Everything staged → build the shared buffers + finish.
        ConsolidateBuffers();
        RegisterSelectables();
        Console.WriteLine($"[VulkanSponza] total draws: {opaqueDrawables.Count} opaque/mask, {blendDrawables.Count} blend.");
        LogPrimitiveSizeHistogram();
        UpdateCamera();
        sceneLoaded = true;
    }

    // Build the pickable-primitive set (one entry per drawable, opaque + blend)
    // and register it with the debug system, so click-to-pick + the Selection
    // panel work. Paths are session-stable (bucket + index); nothing persists.
    private void RegisterSelectables()
    {
        var items = new List<(string Path, string Name, Bounds3 Bounds, int LodLevels, float MaxError)>(
            opaqueDrawables.Count + blendDrawables.Count);
        void Add(string bucket, List<Drawable> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var d = list[i];
                var maxErr = d.LodErrors.Length > 0 ? d.LodErrors[^1] : 0f;
                items.Add(($"scene/{bucket}/{i}", d.Name, d.Bounds, d.LodIndexCounts.Length, maxErr));
            }
        }
        Add("opaque", opaqueDrawables);
        Add("blend", blendDrawables);
        sceneSelection.Rebuild(items);
        debugSystem?.Register(sceneSelection);

        // Per-drawable LOD margins, default 1.0 (= use the global budget as-is).
        opaqueLodMargins = new float[opaqueDrawables.Count];
        blendLodMargins = new float[blendDrawables.Count];
        System.Array.Fill(opaqueLodMargins, 1f);
        System.Array.Fill(blendLodMargins, 1f);
    }

    // Resolve a selectable path ("scene/<bucket>/<i>") to its LOD-margin array
    // slot. Returns false for unknown buckets / out-of-range indices.
    private bool TryResolveMargin(string path, out float[] arr, out int index)
    {
        arr = System.Array.Empty<float>();
        index = -1;
        var parts = path.Split('/');
        if (parts.Length != 3 || !int.TryParse(parts[2], out index)) return false;
        arr = parts[1] switch
        {
            "opaque" => opaqueLodMargins,
            "blend" => blendLodMargins,
            _ => System.Array.Empty<float>(),
        };
        return index >= 0 && index < arr.Length;
    }

    // Per-primitive LOD0 triangle-size distribution across all opaque drawables.
    // Sponza's geometry is dominated by a few very large primitives (whole floor
    // slabs / walls), which is exactly what makes per-prim center-distance LOD
    // coarse — one level for a huge prim. This histogram sets the threshold for
    // cook-time spatial splitting (only split prims above the fat bucket) and
    // shows how concentrated the triangle budget is in the tail.
    private void LogPrimitiveSizeHistogram()
    {
        // Buckets by LOD0 triangle count. Tracks prim count + summed tris per
        // bucket so we can see where the budget actually lives (count vs mass).
        var edges = new[] { 1_000, 5_000, 20_000, 50_000, int.MaxValue };
        var labels = new[] { "<1k", "1-5k", "5-20k", "20-50k", ">50k" };
        var counts = new int[edges.Length];
        var tris = new long[edges.Length];
        long totalTris = 0;
        var fattest = new List<int>();
        foreach (var d in opaqueDrawables)
        {
            var t = d.LodIndexCounts[0] / 3;
            totalTris += t;
            fattest.Add(t);
            for (var i = 0; i < edges.Length; i++)
            {
                if (t < edges[i]) { counts[i]++; tris[i] += t; break; }
            }
        }
        fattest.Sort((a, b) => b.CompareTo(a));
        var top = string.Join("/", fattest.Take(5).Select(t => $"{t / 1000.0:0.0}k"));
        Console.WriteLine($"[VulkanSponza] opaque LOD0 tris: {totalTris / 1_000_000.0:0.00}M across {opaqueDrawables.Count} prims; top5={top}");
        for (var i = 0; i < edges.Length; i++)
        {
            var pct = totalTris > 0 ? 100.0 * tris[i] / totalTris : 0;
            Console.WriteLine($"[VulkanSponza]   {labels[i],-7}: {counts[i],4} prims, {tris[i] / 1_000_000.0:0.00}M tris ({pct:0}% of budget)");
        }
    }

    // Stage one primitive (or spatial-split chunk): resolve/upload its material
    // and queue its geometry. Each carries its own cooked LOD index chain and
    // selects a level by screen-space error; geometry is concatenated into the
    // shared VB/IB by ConsolidateBuffers, so a draw is just a sub-range.
    // Materials are cached by GltfMaterial so primitives sharing a material reuse
    // one handle + descriptor set. Called incrementally (time-sliced) during the
    // streaming load — the material's texture read+upload is the bulk of the cost.
    private void StageDrawable(GltfPrimitive prim)
    {
        {
            var pm = prim.Material;
            var mesh = prim.Mesh;
            // Glass/transmissive routes to the blend pipeline regardless of its
            // declared alpha mode (see EffectiveTransmission).
            // Only genuinely transmissive materials (glass) need alpha blending.
            // Cutout foliage authored as BLEND (the cypress, etc.) is treated as
            // MASK so it lands in the opaque bucket → written by the depth
            // pre-pass → early-Z. That kills the layered double-sided foliage
            // overdraw that makes the hero tree fragment-bound at Retina res.
            // Its alphaCutoff (set in BuildMaterial) drives the shader discard.
            var isBlend = EffectiveTransmission(pm) > 0f;
            var material = GetMaterial(pm, out var albedo, out var alphaCutoff, out var baseColorAlpha);
            var alphaMode = isBlend ? GltfAlphaMode.Blend
                : alphaCutoff > 0f ? GltfAlphaMode.Mask
                : (pm?.AlphaMode ?? GltfAlphaMode.Opaque);
            var pipeline = PickPipeline(alphaMode, pm?.DoubleSided ?? false);

            sharedLayout = mesh.Layout; // uniform across packs (all cooked --tangents)
            // Cooked LOD chain, or a single level from the runtime-import indices.
            var lods = mesh.Lods ?? new[]
            {
                mesh.Indices32 is { } i32 ? new MeshLod(null, i32) : new MeshLod(mesh.Indices, null)
            };
            // No GPU buffers yet — stage the CPU bytes; ConsolidateBuffers packs
            // every pack into the shared VB/IB once loading is done.
            staging.Add(new DrawableStaging(
                mesh.VertexBytes, mesh.VertexCount, lods, material, pipeline, mesh.Bounds,
                albedo, alphaCutoff, baseColorAlpha,
                new[] { new ShaderTextureBinding("uAlbedo", albedo, Slot: 0) }, isBlend,
                string.IsNullOrEmpty(mesh.Name) ? "primitive" : mesh.Name));
        }
    }

    // Bundle every staged primitive into the shared buffers, then compose the
    // draw lists/groups over the result. The engine's MeshBundler does the
    // geometry packing (one shared VB + per-width u16/u32 IBs, each primitive a
    // BaseVertex + per-LOD firstIndex sub-range — indices stay primitive-local,
    // vkCmdDrawIndexed's vertexOffset rebases them); the game keeps the
    // material/pipeline binding, the opaque/blend split, and the draw groups.
    // Inputs are pre-sorted by (pipeline, material, index-width) and opaque-then-
    // blend, so each (pipeline, material, width) run is contiguous — one indirect
    // call per group, the two buckets contiguous.
    private void ConsolidateBuffers()
    {
        if (staging.Count == 0) return;

        static bool StagingIsU32(DrawableStaging s) => s.Lods[0].Indices32 is not null;
        static IEnumerable<DrawableStaging> Grouped(IEnumerable<DrawableStaging> src) => src
            .OrderBy(s => s.Pipeline.Id).ThenBy(s => s.Material.Id).ThenBy(s => StagingIsU32(s) ? 1 : 0);
        // OrderBy is stable, so prims keep their relative order within a group.
        var ordered = Grouped(staging.Where(s => !s.IsBlend))
            .Concat(Grouped(staging.Where(s => s.IsBlend)))
            .ToList();

        var bundle = Blix.Render.MeshBundler.Bundle(
            ordered.Select(s => new Blix.Render.MeshGeometryInput(
                s.VertexBytes, s.VertexCount, s.Lods, s.Bounds)).ToList(),
            sharedLayout, vk, "sponza.shared");
        sharedVb = bundle.Vertices;
        sharedIbU16 = bundle.Indices16;
        sharedIbU32 = bundle.Indices32;

        // Attach material/pipeline/etc. to each bundled geometry sub-range; split
        // back into the opaque + blend buckets (bundle order == ordered order).
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            var bm = bundle.Meshes[i];
            (s.IsBlend ? blendDrawables : opaqueDrawables).Add(new Drawable(
                bm.IndicesAreU32, bm.BaseVertex, bm.LodFirstIndex, bm.LodIndexCounts, bm.LodErrors,
                s.Material, s.Pipeline, bm.Bounds, s.Albedo, s.AlphaCutoff, s.BaseColorAlpha,
                s.ShadowAlbedoBinding, s.Name));
        }

        // Contiguous (pipeline, material, width) groups over the sorted lists. A
        // material is uniformly mask-or-not, so the group is too (lets the depth
        // pre-pass pick its mask/opaque pipeline + material bind per group).
        static void BuildGroups(List<Drawable> list, List<OpaqueGroup> into)
        {
            into.Clear();
            for (var i = 0; i < list.Count;)
            {
                var d = list[i];
                var j = i + 1;
                while (j < list.Count
                    && list[j].Pipeline == d.Pipeline
                    && list[j].Material == d.Material
                    && list[j].IndicesAreU32 == d.IndicesAreU32) j++;
                into.Add(new OpaqueGroup(d.Pipeline, d.Material, d.IndicesAreU32, d.AlphaCutoff > 0f, i, j - i));
                i = j;
            }
        }
        BuildGroups(opaqueDrawables, opaqueGroups);
        BuildGroups(blendDrawables, blendGroups);

        // One indirect command per drawable, refilled each frame (camera opaque +
        // one per shadow cascade + blend). indirectScratch is sized for the
        // largest list (opaque) and reused for the smaller fills.
        opaqueIndirect = vk.CreateIndirectBuffer(opaqueDrawables.Count, "sponza.opaque.indirect");
        for (var c = 0; c < CascadeCount; c++)
            cascadeIndirect[c] = vk.CreateIndirectBuffer(opaqueDrawables.Count, $"sponza.cascade{c}.indirect");
        if (blendDrawables.Count > 0)
            blendIndirect = vk.CreateIndirectBuffer(blendDrawables.Count, "sponza.blend.indirect");
        indirectScratch = new byte[Math.Max(opaqueDrawables.Count, blendDrawables.Count) * VulkanGraphicsDevice.IndirectCommandStride];
        staging.Clear();
        Console.WriteLine($"[VulkanSponza] bundled geometry: 1 VB ({bundle.VertexCount} verts); {opaqueDrawables.Count} opaque + {blendDrawables.Count} blend draws; {opaqueGroups.Count} opaque indirect groups.");
    }

    // Index buffer a drawable's LOD indices live in (chosen at consolidation).
    private IndexBufferHandle SharedIb(Drawable d) => d.IndicesAreU32 ? sharedIbU32 : sharedIbU16;

    // Fill an indirect buffer: one VkDrawIndexedIndirectCommand per opaque
    // drawable (in grouped order), LOD picked by screen-space error. When a cull
    // frustum is given, culled objects get instanceCount 0 (drawn as a GPU no-op
    // — no compaction needed, so group offsets stay fixed). Written to the
    // current frame slot. Returns the visible count (for the diagnostic).
    private int FillIndirect(List<Drawable> drawables, float[] lodMargins, IndirectBufferHandle buffer, Frustum? cull, float margin)
    {
        var cmds = MemoryMarshal.Cast<byte, uint>(indirectScratch.AsSpan());
        var visible = 0;
        for (var i = 0; i < drawables.Count; i++)
        {
            var d = drawables[i];
            var vis = cull is not { } f || f.Intersects(d.Bounds, margin);
            // Per-primitive LOD margin (live-tunable) scales the global px budget.
            var lod = d.PickLod(cameraPosition, LodErrorScale, render.LodErrorPixels * lodMargins[i]);
            var o = i * 5;
            cmds[o + 0] = (uint)d.LodIndexCounts[lod]; // indexCount
            cmds[o + 1] = vis ? 1u : 0u;               // instanceCount (0 = culled)
            cmds[o + 2] = (uint)d.LodFirstIndex[lod];  // firstIndex
            cmds[o + 3] = (uint)d.BaseVertex;          // vertexOffset
            cmds[o + 4] = 0;                           // firstInstance
            if (vis) visible++;
        }
        // Write exactly this list's prefix; the buffer is sized to its count,
        // and indirectScratch is sized for the largest (opaque) list.
        vk.WriteIndirectCommands(buffer, indirectScratch.AsSpan(0, drawables.Count * VulkanGraphicsDevice.IndirectCommandStride));
        return visible;
    }

    // Material handle cached by glTF material (and a slot for the null/untextured
    // fallback) so de-batched per-primitive drawables don't build duplicates.
    private readonly Dictionary<GltfMaterial, (MaterialHandle Mat, TextureHandle Albedo, float Cutoff, float Alpha)> materialCache = new();
    private (MaterialHandle Mat, TextureHandle Albedo, float Cutoff, float Alpha)? noMaterialCache;
    private MaterialHandle GetMaterial(GltfMaterial? gm, out TextureHandle albedo, out float alphaCutoff, out float baseColorAlpha)
    {
        if (gm is not null && materialCache.TryGetValue(gm, out var c))
        {
            (albedo, alphaCutoff, baseColorAlpha) = (c.Albedo, c.Cutoff, c.Alpha);
            return c.Mat;
        }
        if (gm is null && noMaterialCache is { } nc)
        {
            (albedo, alphaCutoff, baseColorAlpha) = (nc.Albedo, nc.Cutoff, nc.Alpha);
            return nc.Mat;
        }
        var mat = BuildMaterial(gm, out albedo, out alphaCutoff, out baseColorAlpha);
        var entry = (mat, albedo, alphaCutoff, baseColorAlpha);
        if (gm is not null) materialCache[gm] = entry; else noMaterialCache = entry;
        return mat;
    }

    // Transmission for a material, with a demo-level fallback. Intel Sponza
    // authors its glass as an opaque, perfectly-smooth dielectric with NO
    // KHR_materials_transmission — so it renders near-black (4% head-on
    // Fresnel) with only grazing reflections. The asset is missing the
    // metadata, so we tag known glass materials by name and let the generic
    // Fresnel-glass path in lit.frag take over. (Same spirit as the metallic-
    // threshold patch: a demo-level conformance fix over an asset quirk, not a
    // renderer default.) Real assets that ship the extension use it directly.
    private static float EffectiveTransmission(GltfMaterial? m)
    {
        if (m is null) return 0f;
        if (m.TransmissionFactor > 0f) return m.TransmissionFactor;
        return m.Name.ToLowerInvariant().Contains("glass") ? 1.0f : 0f;
    }

    // One engine Material per glTF material. BaseColorFactor / EmissiveFactor /
    // MaterialParams (alphaCutoff, normalScale, roughness, metallic) UBO + the
    // five channel textures (defaults when a channel is absent).
    private MaterialHandle BuildMaterial(
        GltfMaterial? gm, out TextureHandle albedo, out float alphaCutoff, out float baseColorAlpha)
    {
        // Engine resolves the five channel textures (read/decode/upload/dedup/
        // stream + glTF-default fallbacks + per-slot sRGB policy); the game keeps
        // the material UBO write + descriptor binding below.
        var tex = textureLoader.Load(gm);
        albedo = tex.Albedo;

        var baseColorFactor = gm?.BaseColorFactor ?? Vector4.One;
        var emissiveFactor = gm is null ? Vector3.Zero : gm.EmissiveFactor;
        var emissiveStrength = gm?.EmissiveStrength ?? 1.0f;
        // Cutout cutoff. MASK uses its authored cutoff. Non-glass BLEND (foliage
        // authored as blend) is treated as cutout at 0.5 so it can depth-write +
        // early-Z instead of overdrawing. Glass (transmissive) keeps 0 → no
        // discard, true alpha blend.
        alphaCutoff = EffectiveTransmission(gm) > 0f ? 0.0f
            : gm?.AlphaMode == GltfAlphaMode.Mask ? (gm?.AlphaCutoff ?? 0.5f)
            : gm?.AlphaMode == GltfAlphaMode.Blend ? 0.5f
            : 0.0f;
        baseColorAlpha = baseColorFactor.W;
        var normalScale = 1.0f;
        var roughness = gm?.RoughnessFactor ?? 0.8f;
        var metallic = gm?.MetallicFactor ?? 0.0f;
        var transmission = EffectiveTransmission(gm);

        return vk.CreateMaterial(litProgram, name: "sponza.material")
            .SetUniform(binding: 0, "uBaseColorFactor", baseColorFactor)
            .SetUniform(binding: 0, "uEmissiveFactor",
                new Vector4(emissiveFactor.X, emissiveFactor.Y, emissiveFactor.Z, emissiveStrength))
            .SetUniform(binding: 0, "uMaterialParams",
                new Vector4(alphaCutoff, normalScale, roughness, metallic))
            .SetUniform(binding: 0, "uMaterialParams2",
                new Vector4(transmission, 0f, 0f, 0f))
            .SetTexture(binding: 1, tex.Albedo)
            .SetTexture(binding: 2, tex.Normal)
            .SetTexture(binding: 3, tex.Emissive)
            .SetTexture(binding: 4, tex.MetallicRoughness)
            .SetTexture(binding: 5, tex.Occlusion)
            .Handle;
    }

    private PipelineHandle PickPipeline(GltfAlphaMode mode, bool doubleSided) =>
        (mode, doubleSided) switch
        {
            (GltfAlphaMode.Blend, true)  => blendDoubleSidedPipeline,
            (GltfAlphaMode.Blend, false) => blendSolidPipeline,
            (_, true)                    => opaqueDoubleSidedPipeline,
            _                            => opaqueSolidPipeline,
        };

    public void OnUpdate(Time time)
    {
        // Promote the background-parsed packs to GPU resources on the main thread
        // (time-sliced; geometry + flat preview appears in ~1s).
        TryFinishLoad();
        // Stream texture mips into their pre-allocated handles, budgeted per
        // frame; the flat preview holds until this drains. Once empty, the full
        // lit+shadow loop takes over (fullyLoaded).
        if (sceneLoaded && !fullyLoaded)
        {
            textureLoader.Drain(budgetMillis: 6.0);
            if (textureLoader.PendingCount == 0) fullyLoaded = true;
        }

        var dt = (float)time.Delta;

        // Translation: WASD + Space/Ctrl. Sprint via Cmd/Super (engine's Key
        // enum has no Shift).
        var move = Vector3.Zero;
        if (heldKeys.Contains(Key.W)) move += cameraForward;
        if (heldKeys.Contains(Key.S)) move -= cameraForward;
        var right = Vector3.Normalize(Vector3.Cross(cameraForward, Vector3.UnitY));
        if (heldKeys.Contains(Key.D)) move += right;
        if (heldKeys.Contains(Key.A)) move -= right;
        if (heldKeys.Contains(Key.Space)) move += Vector3.UnitY;
        if (heldKeys.Contains(Key.LeftControl)) move -= Vector3.UnitY;
        var sprint = heldKeys.Contains(Key.LeftSuper) || heldKeys.Contains(Key.RightSuper);
        var speed = sprint ? render.MoveSpeed * 3f : render.MoveSpeed;
        if (move != Vector3.Zero)
        {
            cameraPosition += Vector3.Normalize(move) * speed * dt;
        }

        // Rotation: arrow keys (keyboard look — WASD already handles movement).
        // Left/Right yaw, Up/Down pitch; matches the mouse-look sign convention.
        const float lookSpeed = 1.8f; // rad/s
        var look = lookSpeed * dt;
        if (heldKeys.Contains(Key.Left))  camYaw -= look;
        if (heldKeys.Contains(Key.Right)) camYaw += look;
        if (heldKeys.Contains(Key.Up))    camPitch += look;
        if (heldKeys.Contains(Key.Down))  camPitch -= look;
        var pitchLimit = MathF.PI / 2f - 0.01f;
        camPitch = Math.Clamp(camPitch, -pitchLimit, pitchLimit);

        UpdateCamera();
    }

    private void UpdateCamera()
    {
        var cp = MathF.Cos(camPitch);
        cameraForward = Vector3.Normalize(new Vector3(
            cp * MathF.Sin(camYaw),
            MathF.Sin(camPitch),
            -cp * MathF.Cos(camYaw)));
        var view = Matrix4x4.CreateLookAt(cameraPosition, cameraPosition + cameraForward, Vector3.UnitY);
        // Sponza atrium spans tens of metres; far plane needs to be generous.
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(fovYRadians, aspect, 0.1f, 200f);
        viewProj = view * proj;
        UpdateCascades();
    }

    // Recompute the sun travel direction from the overlay-driven yaw/pitch.
    private void UpdateSunDirection()
    {
        var cp = MathF.Cos(sunPitch);
        sunDirection = Vector3.Normalize(new Vector3(
            cp * MathF.Sin(sunYaw),
            MathF.Sin(sunPitch),
            -cp * MathF.Cos(sunYaw)));
    }

    // Refit the cascade light view-projections to the current camera. Each
    // cascade bounds a slice [Splits[c], Splits[c+1]] of the camera frustum:
    // we take the slice's 8 corners, wrap them in a bounding sphere (so the
    // ortho is rotation-invariant), aim the light at the sphere centre, and
    // snap that centre to the shadow-map texel grid so shadows don't swim as
    // the camera moves. Ported from the GL SponzaModern reference, expressed
    // in Vulkan depth-[0,1] conventions (CreateOrthoVulkan + System.Numerics
    // right-handed CreateLookAt, same matrix-multiply order as the camera's
    // viewProj = view * proj).
    private void UpdateCascades()
    {
        var fwd = cameraForward;
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitY));
        var camUp = Vector3.Cross(right, fwd);
        var tanV = MathF.Tan(fovYRadians * 0.5f);
        var tanH = tanV * aspect;

        // Light travel direction; place the eye opposite it, behind the slab.
        var L = Vector3.Normalize(sunDirection);
        var sunUp = MathF.Abs(L.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

        Span<Vector3> corners = stackalloc Vector3[8];
        for (var c = 0; c < CascadeCount; c++)
        {
            var nearD = cascadeSplits[c];
            var farD = cascadeSplits[c + 1];
            var k = 0;
            for (var di = 0; di < 2; di++)
            {
                var d = di == 0 ? nearD : farD;
                var centre = cameraPosition + fwd * d;
                var hh = d * tanV;
                var hw = d * tanH;
                corners[k++] = centre - right * hw - camUp * hh;
                corners[k++] = centre + right * hw - camUp * hh;
                corners[k++] = centre - right * hw + camUp * hh;
                corners[k++] = centre + right * hw + camUp * hh;
            }

            // Bounding sphere of the slice corners.
            var center = Vector3.Zero;
            for (var i = 0; i < 8; i++) center += corners[i];
            center /= 8f;
            var radius = 0f;
            for (var i = 0; i < 8; i++) radius = MathF.Max(radius, Vector3.Distance(corners[i], center));
            radius = MathF.Ceiling(radius);

            // Texel-snap the sphere centre in light space so the ortho footprint
            // lands on a stable grid (kills the shimmer under camera motion).
            var eye = center - L * (shadows.SunDistance + radius);
            var lightView = Matrix4x4.CreateLookAt(eye, center, sunUp);
            var texelSize = (2f * radius) / ShadowMapSizes[c];
            var centreLight = Vector3.Transform(center, lightView);
            centreLight.X = MathF.Round(centreLight.X / texelSize) * texelSize;
            centreLight.Y = MathF.Round(centreLight.Y / texelSize) * texelSize;
            Matrix4x4.Invert(lightView, out var invLightView);
            var snapped = Vector3.Transform(centreLight, invLightView);

            var eye2 = snapped - L * (shadows.SunDistance + radius);
            var lightView2 = Matrix4x4.CreateLookAt(eye2, snapped, sunUp);
            var farPlane = 2f * (shadows.SunDistance + radius);
            var ortho = CreateOrthoVulkan(2f * radius, 2f * radius, 0.1f, farPlane);
            cascadeViewProj[c] = lightView2 * ortho;

            // Base depth bias = BiasTexels shadow-texels of world offset,
            // converted to this cascade's NDC depth units (ortho z is linear,
            // so world→NDC depth scale is 1/farPlane). Keeps the bias visually
            // constant across cascades despite their very different extents.
            cascadeDepthBias[c] = (shadows.BiasTexels * texelSize) / farPlane;
        }
    }

    // Symmetric orthographic projection for Vulkan clip space: x/y in [-1,1],
    // z in [0,1], Y flipped (matches VulkanLit's shadow ortho + CreatePerspectiveVulkan).
    private static Matrix4x4 CreateOrthoVulkan(float width, float height, float near, float far)
    {
        var fn = far - near;
        return new Matrix4x4(
            2f / width, 0f,          0f,         0f,
            0f,        -2f / height, 0f,         0f,
            0f,         0f,         -1f / fn,    0f,
            0f,         0f,         -near / fn,  1f);
    }

    public void OnResize(int width, int height)
    {
        if (height > 0)
        {
            aspect = width / (float)height;
            renderHeightPx = height;
            UpdateCamera();
        }
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        if (!sceneLoaded)
        {
            // Nothing to draw yet. The graph still needs an Execute so the
            // swapchain image transitions and the present pass clears.
            graph.Pass(litPassHandle, _ => { },
                clearColor: new GraphicsColor(0.05f, 0.07f, 0.10f, 1f));
            graph.Execute(commandList);
            RecordPresentPass(commandList);
            return;
        }

        // Stress hook: flip fog every 90 frames to exercise the on/off barrier
        // transitions under validation (no effect without --fog-stress).
        if (fogStress && (++fogStressFrame % 90 == 0)) fog.Enabled = !fog.Enabled;

        var perFrameList = new List<ShaderUniform>
        {
            new("uViewProjection",   new Matrix4x4Uniform(viewProj)),
            new("uSunDirection",     new Vector3Uniform(sunDirection)),
            new("uAmbientColor",     new Vector3Uniform(ambientColor)),
            new("uCameraPos",        new Vector3Uniform(cameraPosition)),
            new("uEnvMipCount",      new FloatUniform(iblPrefilterMips)),
            new("uCameraForward",    new Vector3Uniform(cameraForward)),
            new("uShadowStrength",   new FloatUniform(shadows.Enabled ? 1f : 0f)),
            new("uCascadeViewProj",  new Matrix4x4ArrayUniform(cascadeViewProj)),
            // .xyz = far view-depth bound of cascade 0,1,2 (Splits[1..3]).
            new("uCascadeSplits",    new Vector4Uniform(
                new Vector4(cascadeSplits[1], cascadeSplits[2], cascadeSplits[3], 0f))),
            new("uCascadeBias",      new Vector4Uniform(
                new Vector4(cascadeDepthBias[0], cascadeDepthBias[1], cascadeDepthBias[2], 0f))),
            new("uFog",              new Vector4Uniform(
                new Vector4(frame.Width, frame.Height, fog.Far, fog.Enabled ? 1f : 0f))),
        };
        // The //@tune shader uniforms (uSunIntensity, uIblIntensity, the un-packed
        // material/shadow scalars, uVisualizeCascades) are appended by name from
        // the overlay panel — reflection lands each at its offset.
        tunePanel.AppendUniforms(perFrameList);
        var perFrame = perFrameList.ToArray();

        // Per-pass set-1 bindings (passBindings) and the identity model push
        // are built once at load (constant handles / identity transform) and
        // reused here — see OnLoad.

        // --- Streamed-load preview: flat geometry while textures upload ------
        // Geometry is built but its textures are still streaming into their
        // (allocated, undefined) handles, so we render the opaque set flat
        // (lit.vert + flat.frag, no material/IBL/shadow sampling) over the
        // pre-pass depth. No shadow cascades or fog yet — those start once
        // fullyLoaded. The pre-pass treats everything as solid opaque (no mask
        // discard against the not-yet-uploaded albedo).
        if (!fullyLoaded)
        {
            FillIndirect(opaqueDrawables, opaqueLodMargins, opaqueIndirect, cull: null, margin: 0f);
            graph.Pass(depthPrepassHandle, scope =>
            {
                foreach (var g in opaqueGroups)
                    scope.DrawIndexedIndirect(
                        vertexBuffer: sharedVb, indexBuffer: g.IsU32 ? sharedIbU32 : sharedIbU16,
                        pipeline: prepassOpaquePipeline, indirectBuffer: opaqueIndirect,
                        indirectByteOffset: g.Start * VulkanGraphicsDevice.IndirectCommandStride, drawCount: g.Count,
                        uniforms: perFrame, textures: Array.Empty<ShaderTextureBinding>(),
                        material: null, pushConstants: identityPush);
            });
            graph.Pass(litPassHandle, scope =>
            {
                foreach (var g in opaqueGroups)
                    scope.DrawIndexedIndirect(
                        vertexBuffer: sharedVb, indexBuffer: g.IsU32 ? sharedIbU32 : sharedIbU16,
                        pipeline: flatPipeline, indirectBuffer: opaqueIndirect,
                        indirectByteOffset: g.Start * VulkanGraphicsDevice.IndirectCommandStride, drawCount: g.Count,
                        uniforms: perFrame, textures: Array.Empty<ShaderTextureBinding>(),
                        material: null, pushConstants: identityPush);
            }, clearColor: new GraphicsColor(0.05f, 0.07f, 0.10f, 1f));
            graph.Execute(commandList);
            RecordPresentPass(commandList);
            return;
        }

        // --- Shadow passes: opaque/mask occluders into each cascade ---------
        // Each cascade frustum-culls the opaque set against its ortho box, so
        // the near cascade only redraws nearby geometry instead of the whole
        // scene ×3. Blend drawables (windows) are skipped — translucent
        // surfaces shouldn't cast solid shadows. Casters route by alpha mode:
        // OPAQUE → push-only pipeline (no descriptor set); MASK → alpha-cutout
        // pipeline binding the albedo, so foliage casts leaf-shaped shadows.
        var cull = cullEnabled;
        var margin = cullMargin;
        // Reset the mask-push pool cursor; the cascade scopes rent monotonically
        // during graph.Execute, so each draw this frame gets a distinct buffer.
        maskPushCursor = 0;
        for (var c = 0; c < CascadeCount; c++)
        {
            var ci = c;
            // Shadows off: skip the pass (the lit shader early-outs without
            // sampling) and invalidate the cache so re-enabling forces a redraw.
            if (!shadows.Enabled)
            {
                cachedCascadeViewProj[ci] = default;
                cascadeRendered[ci] = false;
                continue;
            }
            var vp = cascadeViewProj[ci];
            // Cached: this cascade's VP is unchanged since it was last rendered,
            // so its depth target still holds the right result — skip the pass
            // entirely (no begin-render-pass → contents persist in ShaderReadOnly,
            // which is exactly what the lit pass samples).
            if (vp == cachedCascadeViewProj[ci])
            {
                cascadeRendered[ci] = false;
                continue;
            }
            cachedCascadeViewProj[ci] = vp;
            cascadeRendered[ci] = true;
            // Frustum.FromViewProjection expects a column-vector clip matrix
            // (clip = M·world); our cascade VP is the System.Numerics
            // row-vector form (clip = Vector4.Transform(world, M)), so transpose
            // to hand it the clip-coordinate generators as rows.
            var cascadeFrustum = Frustum.FromViewProjection(Matrix4x4.Transpose(vp));
            // Fill this cascade's indirect buffer (per-cascade frustum cull → 0
            // instanceCount; same SSE LOD as the lit/pre-pass so shadow depth
            // matches the shaded silhouette). Then one indirect draw per group.
            cascadeDrawCounts[ci] = FillIndirect(opaqueDrawables, opaqueLodMargins, cascadeIndirect[ci], cull ? cascadeFrustum : null, margin);
            // Opaque casters all push the same bytes (identity model + this
            // cascade's VP); mask casters push per-material alpha params, so one
            // mask push per group (constant within a material).
            var cascadeOpaquePush = ShadowOpaquePushBytes(Matrix4x4.Identity, vp);
            var cascadeBuf = cascadeIndirect[ci];
            graph.Pass(cascadePassHandles[ci], scope =>
            {
                foreach (var g in opaqueGroups)
                {
                    var ib = g.IsU32 ? sharedIbU32 : sharedIbU16;
                    var byteOffset = g.Start * VulkanGraphicsDevice.IndirectCommandStride;
                    if (g.IsMask)
                    {
                        var rep = opaqueDrawables[g.Start];
                        scope.DrawIndexedIndirect(
                            vertexBuffer: sharedVb, indexBuffer: ib, pipeline: shadowMaskPipeline,
                            indirectBuffer: cascadeBuf, indirectByteOffset: byteOffset, drawCount: g.Count,
                            uniforms: Array.Empty<ShaderUniform>(), textures: rep.ShadowAlbedoBinding,
                            material: null,
                            pushConstants: RentMaskPush(Matrix4x4.Identity, vp, rep.AlphaCutoff, rep.BaseColorAlpha));
                    }
                    else
                    {
                        scope.DrawIndexedIndirect(
                            vertexBuffer: sharedVb, indexBuffer: ib, pipeline: shadowOpaquePipeline,
                            indirectBuffer: cascadeBuf, indirectByteOffset: byteOffset, drawCount: g.Count,
                            uniforms: Array.Empty<ShaderUniform>(), textures: Array.Empty<ShaderTextureBinding>(),
                            material: null, pushConstants: cascadeOpaquePush);
                    }
                }
            });
        }

        // Froxel fog: fill the 3D scattering grid. Recorded here — after the
        // shadow scopes, before the lit scope — so this code reads in frame
        // order. (Execution is by graph declaration order regardless: the
        // froxel ComputePass is declared between the cascades and the lit pass,
        // so it dispatches after the shadow maps render and before the lit pass
        // samples the grid.) Skipped when fog is off — and the lit shader gates
        // on uFog.w, so when disabled the grid is never sampled (its contents
        // are undefined/stale; "disabled" must mean "never sample").
        if (fog.Enabled)
        {
            Matrix4x4.Invert(viewProj, out var invViewProj);
            var froxelUniforms = new ShaderUniform[]
            {
                new("uInvViewProj",   new Matrix4x4Uniform(invViewProj)),
                new("uCamPos",        new Vector4Uniform(new Vector4(cameraPosition, fog.Far))),
                new("uCamForward",    new Vector4Uniform(new Vector4(cameraForward, fog.Density))),
                new("uSunDir",        new Vector4Uniform(new Vector4(sunDirection, tunePanel.Value("uSunIntensity")))),
                new("uSunColor",      new Vector4Uniform(new Vector4(1f, 1f, 1f, fog.Scatter))),
                new("uFogParams",     new Vector4Uniform(new Vector4(fog.PhaseG, fog.Ambient, 0f, 0f))),
                new("uCascadeVP",     new Matrix4x4ArrayUniform(cascadeViewProj)),
                new("uCascadeSplits", new Vector4Uniform(
                    new Vector4(cascadeSplits[1], cascadeSplits[2], cascadeSplits[3], 0f))),
            };
            graph.Dispatch(froxelPassHandle, new DispatchCommand(
                froxelPipeline,
                (FroxelGridX + 7) / 8, (FroxelGridY + 7) / 8, 1,
                froxelUniforms, froxelBindings));
        }

        // Fill the camera opaque indirect commands once per frame (LOD by SSE, no
        // cull); both the depth pre-pass and the lit pass consume this buffer —
        // they draw the identical opaque set at identical LODs.
        FillIndirect(opaqueDrawables, opaqueLodMargins, opaqueIndirect, cull: null, margin: 0f);
        if (blendDrawables.Count > 0) FillIndirect(blendDrawables, blendLodMargins, blendIndirect, cull: null, margin: 0f);

        // Depth pre-pass: same non-blend set as the lit pass (no cull, so the
        // depth the lit pass loads covers exactly what it shades), depth only.
        // Per group: mask binds the material (set 2 albedo) for the alpha
        // discard + the mask pipeline; opaque needs only set 0 + model push.
        graph.Pass(depthPrepassHandle, scope =>
        {
            foreach (var g in opaqueGroups)
            {
                scope.DrawIndexedIndirect(
                    vertexBuffer: sharedVb,
                    indexBuffer: g.IsU32 ? sharedIbU32 : sharedIbU16,
                    pipeline: g.IsMask ? prepassMaskPipeline : prepassOpaquePipeline,
                    indirectBuffer: opaqueIndirect,
                    indirectByteOffset: g.Start * VulkanGraphicsDevice.IndirectCommandStride,
                    drawCount: g.Count,
                    uniforms: perFrame,
                    textures: Array.Empty<ShaderTextureBinding>(),
                    material: g.IsMask ? g.Material : null,
                    pushConstants: identityPush);
            }
        });

        graph.Pass(litPassHandle, scope =>
        {
            // Opaque + Mask first (depth-test, no write — pre-pass wrote depth),
            // then Blend (depth-test only), so translucent surfaces composite
            // over the resolved opaque depth without writing into it.
            //
            // GPU-driven Stage 1: one indirect draw per (pipeline, material)
            // group over the buffer filled above — ~800 per-object draws collapse
            // to ~one per material. All draws in a group share set0 + set2
            // (material) + identity push (the static importer bakes transforms),
            // exactly the indirect-multidraw constraint.
            foreach (var g in opaqueGroups)
            {
                scope.DrawIndexedIndirect(
                    vertexBuffer: sharedVb,
                    indexBuffer: g.IsU32 ? sharedIbU32 : sharedIbU16,
                    pipeline: g.Pipeline,
                    indirectBuffer: opaqueIndirect,
                    indirectByteOffset: g.Start * VulkanGraphicsDevice.IndirectCommandStride,
                    drawCount: g.Count,
                    uniforms: perFrame,
                    textures: passBindings,
                    material: g.Material,
                    pushConstants: identityPush);
            }
            // Sky after opaque, before blend. Fullscreen triangle drawn
            // from the dummy VB; positions are synthesised in skybox.vert.
            scope.DrawIndexed(
                vertexBuffer: presentDummyVB,
                indexBuffer: presentDummyIB,
                pipeline: skyPipeline,
                indexCount: 3,
                uniforms: perFrame,
                textures: passBindings);
            // Blend (glass), depth-test only, after opaque + sky. One indirect
            // draw per (pipeline, material) group, same as opaque.
            foreach (var g in blendGroups)
            {
                scope.DrawIndexedIndirect(
                    vertexBuffer: sharedVb,
                    indexBuffer: g.IsU32 ? sharedIbU32 : sharedIbU16,
                    pipeline: g.Pipeline,
                    indirectBuffer: blendIndirect,
                    indirectByteOffset: g.Start * VulkanGraphicsDevice.IndirectCommandStride,
                    drawCount: g.Count,
                    uniforms: perFrame,
                    textures: passBindings,
                    material: g.Material,
                    pushConstants: identityPush);
            }
        }, clearColor: new GraphicsColor(0.05f, 0.07f, 0.10f, 1f));

        graph.Execute(commandList);

        RecordPresentPass(commandList);
    }

    // --- IDebuggable ------------------------------------------------------
    // Replaces the old Console cull-count log + hardcoded C/L key toggles.
    // Controls are read-back: the returned value feeds this frame's render.
    public void Debug(DebugContext debug)
    {
        // Cmd+C toggles this; Debug() runs unconditionally so it re-applies.
        debug.State.Enabled = overlayEnabled;
        // Overlay off → emit nothing. The panels gate on State.Enabled, but the
        // debug-line pass just renders whatever's queued, so we must skip the
        // Draw emissions too or the cascade/sun gizmos linger when hidden.
        if (!overlayEnabled) return;

        // Live tuning, grouped by scope. Controls are read-back: the returned
        // value feeds this frame's render (Debug() runs before OnRender).
        using (debug.Scope("Sun"))
        {
            var deg = 180f / MathF.PI;
            sunYaw   = debug.Controls.Float("Yaw (deg)", sunYaw * deg, -180f, 180f) / deg;
            sunPitch = debug.Controls.Float("Pitch (deg)", sunPitch * deg, -89f, -1f) / deg;
            UpdateSunDirection();
        }
        // Shadows + Render scopes are now [Tune]-tagged settings objects
        // (ShadowsSettings / RenderSettings), auto-paneled by tuneObjects below.
        // Shader-uniform tunables (//@tune in lit.frag): sun/ambient intensity,
        // metallic/normal/slope/glass thresholds, indirect-shadow base/range,
        // cascade-viz. Auto-built + read back here — grouped by their UBO block.
        tunePanel.BuildControls(debug);
        tuneObjects.BuildControls(debug);   // [Tune]-tagged CPU settings (Fog, …)

        // --- Live selection (ephemeral) -------------------------------------
        // Left-click picks a primitive; Cmd-click adds. Drag LOD margin to
        // coarsen/sharpen the whole selection at once (set-all); nothing is
        // saved. The framework highlights the primary; tint the rest here.
        if (selection.Count > 0)
        {
            using (debug.Scope("Selection"))
            {
                debug.Values.Value("count", selection.Count);
                var repPath = primarySelection ?? selection.First();
                var cur = TryResolveMargin(repPath, out var rArr, out var rIdx) ? rArr[rIdx] : 1f;
                var next = debug.Controls.Float("LOD margin (×px)", cur, 0f, 8f);
                if (next != cur)
                {
                    foreach (var p in selection)
                        if (TryResolveMargin(p, out var a, out var ix)) a[ix] = next;
                }
            }
            foreach (var p in selection)
            {
                if (p == primarySelection) continue;
                if (sceneSelection.TryGetBounds(p, out var b))
                    debug.Draw.Aabb($"sel/{p}", b.Min, b.Max, MultiSelectColor);
            }
        }

        using (debug.Scope("Cascades"))
        {
            cullEnabled   = debug.Controls.Toggle("Frustum cull", cullEnabled);
            cullMargin    = debug.Controls.Float("Cull margin", cullMargin, 0f, 5f);
            // Far distance of each cascade; clamped into ascending-ish bands so
            // the splits stay ordered as you drag.
            cascadeSplits[1] = debug.Controls.Float("Far: cascade 0", cascadeSplits[1], 1f, 20f);
            cascadeSplits[2] = debug.Controls.Float("Far: cascade 1", cascadeSplits[2], cascadeSplits[1], 50f);
            cascadeSplits[3] = debug.Controls.Float("Far: cascade 2", cascadeSplits[3], cascadeSplits[2], 150f);
        }
        // Exposure / tonemap / fly-speed / LOD-error budget are [Tune] on
        // RenderSettings (auto-paneled above). Vsync stays manual — it's a
        // device property, not a field: FIFO (capped, no tearing) vs Mailbox
        // (uncapped; tears on MoltenVK). Toggling recreates the swapchain.
        using (debug.Scope("Render"))
        {
            vk.VsyncEnabled = debug.Controls.Toggle("Vsync", vk.VsyncEnabled);
        }

        debug.Values.Value("shadow-map", $"{ShadowMapSizes[0]}/{ShadowMapSizes[1]}/{ShadowMapSizes[2]}");
        debug.Values.Value("splits-m", $"{cascadeSplits[1]:0}/{cascadeSplits[2]:0}/{cascadeSplits[3]:0}");
        // Per-cascade caster counts after frustum cull (one frame stale — set
        // during the previous OnRender's graph.Execute).
        debug.Values.Value("cascade-casters", $"{cascadeDrawCounts[0]}/{cascadeDrawCounts[1]}/{cascadeDrawCounts[2]} of {opaqueDrawables.Count}");
        // Shadow-map cache hits: R = re-rendered this frame, · = served cached.
        debug.Values.Value("cascade-cache", $"{(cascadeRendered[0] ? 'R' : '·')}{(cascadeRendered[1] ? 'R' : '·')}{(cascadeRendered[2] ? 'R' : '·')}");
        debug.Values.Value("blend-draws", blendDrawables.Count);
        debug.Values.Value("cam-pos", cameraPosition);
        // LOD diagnostic: max levels available + histogram of selected levels
        // across opaque drawables at the current camera + pixel-error budget.
        var maxLevels = 0;
        var hist = new int[8];
        for (var i = 0; i < opaqueDrawables.Count; i++)
        {
            var d = opaqueDrawables[i];
            maxLevels = Math.Max(maxLevels, d.LodIndexCounts.Length);
            var lv = d.PickLod(cameraPosition, LodErrorScale, render.LodErrorPixels * opaqueLodMargins[i]);
            if (lv < hist.Length) hist[lv]++;
        }
        debug.Values.Value("lod-maxlevels", maxLevels);
        debug.Values.Value("lod-hist", $"{hist[0]}/{hist[1]}/{hist[2]}/{hist[3]} (err={render.LodErrorPixels:0.0}px)");

        // --- Perf instrumentation: weigh where the frame actually goes -------
        // CPU-phase split of the bundled `execute` timer. encode is the only
        // phase draw-COUNT moves (recording vkCmds → Metal encoder calls), so
        // it's the number A (batching) / B (GPU-driven indirect) would change;
        // wait is the GPU/vsync throttle (high = GPU-bound, can't be cut by
        // batching); submit is queue submit + present enqueue.
        //
        // The other half — per-pass GPU ms — is surfaced by the runtime under
        // the `gpu/passes` timer scope (Window drains ConsumeAvailableGpuTimings
        // each frame); the periodic console sink prints it. We deliberately do
        // NOT drain it here too — that would race the runtime and steal frames.
        var cpu = vk.LastCpuFrameTiming;
        debug.Values.Value("cpu-wait", $"{cpu.WaitMs:0.00}ms");
        debug.Values.Value("cpu-encode", $"{cpu.EncodeMs:0.00}ms");
        debug.Values.Value("cpu-submit", $"{cpu.SubmitPresentMs:0.00}ms");

        // Spatial gizmos: sun direction + the three cascade ortho boxes.
        debug.Draw.ViewProjection = viewProj;
        debug.Draw.Arrow("sun/dir", -sunDirection * 6f, Vector3.Zero,
            new GraphicsColor(1f, 0.92f, 0.3f, 1f));
        var cascadeTints = new[]
        {
            new GraphicsColor(1f, 0.35f, 0.35f, 0.8f),
            new GraphicsColor(0.35f, 1f, 0.35f, 0.8f),
            new GraphicsColor(0.4f, 0.5f, 1f, 0.8f),
        };
        for (var c = 0; c < CascadeCount; c++)
        {
            debug.Draw.Frustum($"cascade/{c}", cascadeViewProj[c], cascadeTints[c]);
        }
    }

    private void RecordPresentPass(RenderCommandList commandList)
    {
        var hdrTex = graph.GetColorTexture(hdrHandle);
        var push = new byte[8];
        var exposure = render.Exposure;   // local: MemoryMarshal.Write needs an `in` ref
        MemoryMarshal.Write(push.AsSpan(0, 4), in exposure);
        var tonemap = (uint)render.Tonemap;
        MemoryMarshal.Write(push.AsSpan(4, 4), in tonemap);
        commandList.Pass(
            "present",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0, 0, 0, 1) },
                ClearDepth: true),
            pass =>
            {
                pass.DrawIndexed(
                    vertexBuffer: presentDummyVB,
                    indexBuffer: presentDummyIB,
                    pipeline: presentPipeline,
                    indexCount: 3,
                    uniforms: Array.Empty<ShaderUniform>(),
                    textures: new[]
                    {
                        new ShaderTextureBinding("uHdr", hdrTex, Slot: 0),
                    },
                    pushConstants: push);
            });
    }

    private static byte[] ModelPushBytes(Matrix4x4 model)
    {
        var bytes = new byte[64];
        MemoryMarshal.Write(bytes.AsSpan(0, 64), in model);
        return bytes;
    }

    // Opaque shadow caster push: [model | cascadeViewProj] = 128 bytes,
    // matching shadow.vert's PushConstants block.
    private static byte[] ShadowOpaquePushBytes(Matrix4x4 model, Matrix4x4 cascadeViewProj)
    {
        var bytes = new byte[128];
        MemoryMarshal.Write(bytes.AsSpan(0, 64), in model);
        MemoryMarshal.Write(bytes.AsSpan(64, 64), in cascadeViewProj);
        return bytes;
    }

    // Mask shadow caster push: [model | cascadeViewProj | alphaParams] = 144
    // bytes, matching shadow_mask's PushConstants block. alphaParams.xy =
    // (alphaCutoff, baseColorAlpha). Rents a pooled buffer (reset per frame via
    // maskPushCursor) rather than allocating, since these differ per draw.
    private byte[] RentMaskPush(
        Matrix4x4 model, Matrix4x4 cascadeViewProj, float alphaCutoff, float baseColorAlpha)
    {
        if (maskPushCursor >= maskPushPool.Count) maskPushPool.Add(new byte[144]);
        var bytes = maskPushPool[maskPushCursor++];
        MemoryMarshal.Write(bytes.AsSpan(0, 64), in model);
        MemoryMarshal.Write(bytes.AsSpan(64, 64), in cascadeViewProj);
        var alphaParams = new Vector4(alphaCutoff, baseColorAlpha, 0f, 0f);
        MemoryMarshal.Write(bytes.AsSpan(128, 16), in alphaParams);
        return bytes;
    }

    // --- IInputHandler ----------------------------------------------------

    public void OnKeyDown(Key key)
    {
        heldKeys.Add(key);
        switch (key)
        {
            // Cmd+C toggles the diagnostics overlay. Plain C does nothing.
            case Key.C:
                if (heldKeys.Contains(Key.LeftSuper) || heldKeys.Contains(Key.RightSuper))
                {
                    overlayEnabled = !overlayEnabled;
                }
                break;
            // Exposure / sun / shadow tuning live in the overlay Controls now;
            // arrow keys drive the camera (see OnUpdate).
            case Key.Escape:
                host.RequestClose();
                break;
        }
    }

    public void OnKeyUp(Key key) => heldKeys.Remove(key);

    public void OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        lastMouseX = x;
        lastMouseY = y;
        if (!mouseLook) return;
        const float sensitivity = 0.0035f;
        camYaw += deltaX * sensitivity;
        camPitch -= deltaY * sensitivity;
        var limit = MathF.PI / 2f - 0.01f;
        camPitch = Math.Clamp(camPitch, -limit, limit);
        UpdateCamera();
    }

    public void OnMouseDown(MouseButton button)
    {
        if (button == MouseButton.Right)
        {
            mouseLook = true;
            host.SetCursorCaptured(true);
        }
        else if (button == MouseButton.Left && !mouseLook)
        {
            PickAt(lastMouseX, lastMouseY);
        }
    }

    // Click-to-pick: build a geometric pick ray (backend-agnostic — no Vulkan-NDC
    // unproject needed), ray-test every registered selectable's AABB, and select
    // the smallest-volume hit ("the most specific thing under the cursor", as the
    // GL demo does). Misses clear the selection.
    private void PickAt(float mx, float my)
    {
        if (debugSystem is null) return;
        var w = host.LogicalSize.Width;
        var h = host.LogicalSize.Height;
        if (w <= 0 || h <= 0) return;

        var nx = 2f * mx / w - 1f;
        var ny = 1f - 2f * my / h;
        var right = Vector3.Normalize(Vector3.Cross(cameraForward, Vector3.UnitY));
        var up = Vector3.Cross(right, cameraForward);
        var tanV = MathF.Tan(fovYRadians * 0.5f);
        var dir = Vector3.Normalize(cameraForward + right * (nx * tanV * aspect) + up * (ny * tanV));
        var ray = new Blix.Geometry.Ray(cameraPosition, dir);

        var selectables = debugSystem.CollectSelectables();
        DebugSelectable? best = null;
        var bestVolume = float.PositiveInfinity;
        for (var i = 0; i < selectables.Count; i++)
        {
            var s = selectables[i];
            if (Blix.Geometry.Intersection.Raycast(ray, s.Bounds) is null) continue;
            var ext = s.Bounds.Max - s.Bounds.Min;
            var vol = ext.X * ext.Y * ext.Z;
            if (vol < bestVolume) { bestVolume = vol; best = s; }
        }

        // Cmd-click adds/toggles; plain click replaces. The framework tracks one
        // SelectedPath (the primary, last-picked) for its highlight + inspector;
        // the full multi-select set lives here and is drawn/edited in Debug().
        var add = heldKeys.Contains(Key.LeftSuper) || heldKeys.Contains(Key.RightSuper);
        if (best is { } pick)
        {
            if (add)
            {
                if (!selection.Remove(pick.EntityPath)) selection.Add(pick.EntityPath);
                primarySelection = selection.Contains(pick.EntityPath) ? pick.EntityPath
                    : (selection.Count > 0 ? selection.First() : null);
            }
            else
            {
                selection.Clear();
                selection.Add(pick.EntityPath);
                primarySelection = pick.EntityPath;
            }
        }
        else if (!add)
        {
            selection.Clear();
            primarySelection = null;
        }

        if (primarySelection is { } pp && sceneSelection.TryGetBounds(pp, out var pb))
            debugSystem.Select(pp, pb);
        else
            debugSystem.ClearSelection();
    }

    public void OnMouseUp(MouseButton button)
    {
        if (button == MouseButton.Right)
        {
            mouseLook = false;
            host.SetCursorCaptured(false);
        }
    }

    public void OnMouseWheel(float offsetX, float offsetY)
    {
        render.MoveSpeed = Math.Clamp(render.MoveSpeed * (offsetY > 0 ? 1.25f : 0.8f), 0.3f, 60f);
    }

    public void Dispose() { }

    // Procedural-sky IBL fallback (no cooked .blixprobe). The synthesis lives in
    // the demo-owned ProceduralSky helper; this just binds the result.
    private void BakeProceduralIbl()
    {
        var sky = ProceduralSky.Bake(vk, SkyBakeSunDirection);
        envCubeTexture = sky.EnvCube;
        irradianceCubeTexture = sky.Irradiance;
        brdfLutTexture = sky.BrdfLut;
        iblPrefilterMips = sky.PrefilterMips;
    }
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
