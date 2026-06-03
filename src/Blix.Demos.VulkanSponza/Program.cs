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
public static class Program
{
    public static void Main()
    {
        var loop = new SponzaLoop();
        using var window = new Window(loop, new WindowOptions("Blix — Vulkan Sponza", 1440, 810));
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
    private readonly float[] cascadeDepthBias = new float[CascadeCount];
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
    private readonly Dictionary<GltfMaterial, (MaterialHandle Mat, TextureHandle Albedo, float Cutoff, float Alpha)> materialCache = new();
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
