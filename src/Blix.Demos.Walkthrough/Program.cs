using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Graphics.Primitives;
using Blix.Render;
using Blix.Runtime.OpenTK;

using var window = new Window(
    new WalkthroughGame(),
    new WindowOptions("Blix . Sponza Walkthrough", 1440, 810));
window.Run();

internal sealed class WalkthroughGame : Game, IInputHandler, IDebuggable
{
    private const float PitchClamp = MathF.PI * 0.49f;   // just under 90 degrees
    private const int EnvCubeFaceSize = 256;
    private const int ShadowMapSize = 2048;
    private const int CascadeCount = 3;
    // View-space split distances. shadowSurfaces[i] covers depth
    // [cascadeSplits[i], cascadeSplits[i+1]]. Beyond cascadeSplits[N] no
    // shadow is applied (lit shader returns 1.0). Tuned to Sponza's
    // ~25m atrium plus some margin.
    private static readonly float[] cascadeSplits = { 0.1f, 4.0f, 15.0f, 40.0f };
    // Debug: tint fragments by cascade in the lit shader output.
    private bool visualizeCascades = false;
    // Debug: bypass CSM and use a single big ortho frustum that wraps the
    // whole atrium. Diagnostic A/B against cascades -- if shadows are correct
    // here but wrong with cascades, the bug is in cascade VP construction;
    // if wrong both ways, the bug is elsewhere.
    private bool disableCascades = false;
    private const int PointShadowFaceSize = 512;
    private const int MaxPointShadows = 4;
    private static readonly Vector3 SceneCenter = new(0.0f, 5.5f, 0.0f);

    // --- Tunable fields (backing debug sliders) ----------------------------
    // FP camera + controls
    private float walkSpeed = 3.5f;
    private float sprintMultiplier = 3.0f;
    private float mouseLookSensitivity = 0.0025f;
    private float cameraFov = MathF.PI / 3.0f;
    private float cameraNearPlane = 0.1f;
    private float cameraFarPlane = 100.0f;

    // Sun & exposure. The bake-time sun direction is held separately from the
    // live `sunDirection` so the user can scrub the sliders without forcing
    // an IBL re-bake every tick — the rebake button commits.
    private float sunYaw = MathF.Atan2(-0.30f, -0.45f);   // matches initial dir
    private float sunPitch = MathF.Asin(-0.85f);
    private float sunStrength = 3.2f;
    private float exposure = 1.0f;
    private Vector3 sunDirection = Vector3.Normalize(new Vector3(-0.45f, -0.85f, -0.30f));
    private Vector3 bakedSunDirection = Vector3.Normalize(new Vector3(-0.45f, -0.85f, -0.30f));

    // Shadow projection
    private float shadowOrthoExtent = 18.0f;
    private float shadowSunDistance = 22.0f;
    private float shadowNearPlane = 0.1f;
    private float shadowFarPlane = 50.0f;

    // IBL / shader tuning (uniforms)
    private float emissiveBoost = 2.5f;
    private float iblSpecAttenuation = 0.8f;
    private float iblDiffuseBoost = 1.0f;
    private float metalFloor = 0.18f;
    private float indirectShadowBase = 0.60f;
    private float indirectShadowRange = 0.40f;
    // Horizon fade kills the white-halo rim at silhouette edges of shiny /
    // metallic surfaces (Schlick/Lazarov Fresnel pushing F -> 1 as
    // NdotV -> 0). Strength 0 = off; 1 = full silhouette specular kill.
    private float horizonFadeStrength = 0.85f;
    private float horizonFadeStart = 0.15f;
    // Sky tint multiplier. Applied to the visible skybox AND to the
    // lit shader's IBL samples so the env probe agrees with the backdrop.
    // White = identity (procedural sky as baked); deep blue = night;
    // cool gray = overcast/storm.
    private Vector3 skyTint = Vector3.One;

    // Pending rebake — set by the Debug button, consumed in OnUpdate.
    private bool rebakeRequested;

    // Floor override (the asset authors Sponza's marble as semi-rough ~0.66,
    // which reads as matte under IBL. Override on detected floor prims so we
    // can dial in polished-marble looks without re-authoring textures.)
    private float floorRoughness = 0.18f;
    private float floorMetallic = 0.0f;
    private bool floorOverrideEnabled = true;
    // Diagnostic: when on, force tagged floor prims to bright magenta albedo
    // and zero roughness so they're visually unmistakable. Catches detection
    // misses (floor not tagged) and false positives (curtains tagged).
    private bool floorVisualize = false;

    // Point lights. Defaults: 4 warm hanging-lamp lights along the atrium
    // centerline at chain-height. Shader has MAX_POINT_LIGHTS=8; the
    // remaining 4 slots are spare for later (e.g., the fountain). Uniforms
    // are repacked each frame from these arrays so the debug sliders take
    // effect live.
    private const int MaxPointLights = 8;
    // Default positions sit on the four standing braziers identified in the
    // Sponza glTF by metallic-material analysis (mat[20]/mat[21] centroids,
    // see python diagnostic). Y is bumped slightly above the lamp body so the
    // light "sits in the flame" rather than buried in the geometry.
    private readonly Vector3[] pointLightPositions =
    {
        new(-4.95f, 1.10f, -1.76f),
        new(-4.95f, 1.10f,  1.15f),
        new( 3.90f, 1.10f, -1.76f),
        new( 3.90f, 1.10f,  1.15f),
    };
    private float pointLightIntensity = 10.0f;
    private float pointLightRange = 6.5f;
    private Vector3 pointLightColor = new(1.0f, 0.55f, 0.25f);  // warm amber
    private bool pointLightsEnabled = true;
    // Flicker drives a per-lamp time-varying multiplier on intensity so the
    // warm pools waver with the flame instead of reading as static spots.
    // Three octaves of sine -> "candle-y" feel without needing a noise texture.
    private float pointLightFlickerAmount = 0.18f;
    private float pointLightFlickerSpeed = 1.0f;
    // Tone down the point lights' specular term separately from diffuse.
    // View-dependent specular on small metallic lantern geometry migrates
    // across the surface as the camera moves and reads as "the geometry
    // is swinging" -- dropping this kills the migration without losing
    // the warm diffuse pools the lights cast.
    private float pointLightSpecScale = 0.35f;

    // Volumetric flame (phase A). Ray-marched analytic 3D noise inside a
    // unit-cube bounding box per lamp. Validates the volume-rendering
    // pipeline so volume.frag's density function can later be swapped for a
    // sampler3D lookup against EmberGen/Houdini-baked data with no other
    // plumbing changes.
    private Material volumeMaterial = null!;
    private Mesh volumeCubeMesh = null!;
    private TextureHandle volumeVdbTexture;
    private int volumeVdbFrames = 0;       // populated when .bvol loads
    private bool useFlameVolume = false;
    // Two-layer mix: VDB campfire-style sim at the cube's base + analytic
    // narrow teardrop on top. Both layers contribute to density+temperature
    // with smooth vertical weighting in the [-0.2, 0.3] local-y band.
    private float volumeVdbWeight = 1.0f;
    private float volumeProcWeight = 1.2f;
    private float volumeSize = 0.75f;
    private float volumeSteps = 36.0f;
    private float volumeDensity = 9.0f;    // VDB temperatures are 0..1 but typical voxel ~0.2; needs boost for visible opacity
    private float volumeRise = 0.85f;
    private float volumeIntensity = 2.2f;
    private float volumeFps = 24.0f;
    // Temperature boost: VDB peak ~1.013 was global-max normalised to 1.0
    // before quantisation, so a typical hot voxel comes back as ~0.2 -- well
    // below the fire colour ramp's orange threshold. 3x lifts the body into
    // visible orange/yellow without pumping low-density wisps into hot core.
    private float volumeTempBoost = 3.0f;

    // Flame quads -- one per active point light. Two materials share the same
    // billboard vertex shader; the toggle picks between them at render time.
    //   atlas:      samples BenHickling's CC0 64x64x60-frame fire atlas. Looks
    //               like real fire because it *is* real (well, rendered) fire.
    //   procedural: the FBM-based hand-written shader. Free of texture deps;
    //               handy fallback / comparison; stylised look.
    private Material flameMaterial = null!;
    private Material flameAtlasMaterial = null!;
    private TextureHandle flameAtlasTexture;
    private Mesh flameMesh = null!;
    private float flameSize = 0.45f;
    private float flameIntensity = 1.4f;
    private bool flamesEnabled = true;
    private bool useFlameAtlas = true;
    private float flameAtlasFps = 30.0f;
    // The Unity Flame02 atlas's 64 frames are a continuous sim where a flame
    // rises out the top of the cell while a new source forms at the bottom.
    // Reading as "two stacked flames" on a short brazier quad. Toggle this
    // on to fall back to playing only row 0 (16-frame clean candle loop).
    private bool flameAtlasSingleRow = false;
    // Flame's billboard origin sits at the BOTTOM of the quad (y=0..1 in
    // local coords), so to put the visible flame at absolute y=1 while
    // the light source sits at y=1.10, we anchor the quad at world y=1.0
    // -- an offset of -0.10 from the light position.
    private float flameYOffset = -0.10f;

    // Cube shadow maps for the first MaxPointShadows point lights. Static
    // scene, static lights -- bake once and only re-bake when a light moves
    // or the user mashes the Rebake button. Stored as one cube per light
    // plus six render surfaces (one per cube face) per light.
    private TextureHandle[] pointShadowCubes = Array.Empty<TextureHandle>();
    private RenderSurface[][] pointShadowSurfaces = Array.Empty<RenderSurface[]>();
    private Material cubeShadowMaterial = null!;
    private float pointShadowFarPlane = 12.0f;
    private float pointShadowBias = 0.005f;
    // PCF filter radius in world units. 0 = single-tap hard shadows.
    // Around 0.08 m gives a soft penumbra that matches the directional
    // shadow's 9-tap PCF feel.
    private float pointShadowFilterRadius = 0.08f;
    // Cached positions at last bake. A mismatch with the live array triggers
    // a re-bake at the top of the next frame.
    private Vector3[] bakedShadowLightPositions = Array.Empty<Vector3>();
    private bool pointShadowsBaked;

    // Cached scene resources -------------------------------------------------
    // IsFloor: tagged at load via a flat-and-wide heuristic so we can push
    // override uniforms only onto those materials each frame.
    // Built scene + the indices of submeshes flagged as "floor" by the
    // bounds heuristic below. floorIndices is intentionally separate from
    // GltfSceneInstance -- the heuristic is Sponza-specific (the marble
    // floor has a distinctive thin/wide bounds signature), and we don't
    // want that pattern smuggled into the generic scene type.
    private GltfSceneInstance scene = null!;
    private readonly List<int> floorIndices = new();

    private PipelineHandle litPipeline;
    private Material shadowMaterial = null!;
    // Engine-level helper that owns the per-submesh draw + per-frame
    // uniform packing for the lit / cascade-shadow / cube-shadow passes.
    // Demo still drives pass orchestration (which surface, what else
    // draws inside); renderer just gets called inside the open pass.
    private PbrSceneRenderer pbrRenderer = null!;
    private Material skyboxMaterial = null!;
    private Mesh skyMesh = null!;
    private TextureHandle whitePixel;
    private TextureHandle flatNormal;
    private TextureHandle neutralMetallicRoughness;
    private TextureHandle fullOcclusion;
    // CSM: per-cascade depth textures + render surfaces + light VPs.
    private TextureHandle[] cascadeShadowMaps = null!;
    // Legacy single-shadow alias (for the fog pass which still uses one
    // sampler2DShadow). Pointed at the far cascade (covers the most depth
    // so god-ray sampling looks right at the camera's distance).
    private TextureHandle shadowMapTexture;
    // Environment subsystem -- the profile (data) drives EnvironmentBaker
    // to produce a probe (baked GPU resources). Rebake-on-slider-change
    // mutates the profile and re-bakes. The BRDF LUT is env-independent
    // and lives outside the probe so slider rebakes don't redo the
    // expensive 1024-sample LUT integration on every tick.
    private EnvironmentProfile envProfile = null!;
    private EnvironmentProbe envProbe = null!;
    private TextureHandle brdfLut;
    // The sun direction extracted from the HDR equirect at load time, kept
    // so the "Sync sun to HDR" button can re-apply it without re-scanning.
    private Vector3? hdrSunDirectionFromEquirect = null;

    private RenderSurface sceneSurface = null!;
    // Offscreen Rgba16F target the lit/skybox/flame/volume passes draw into.
    // A final composite pass (composite.frag) tonemaps from here to the
    // default swap-chain surface, which lets us do alpha-blended flame and
    // volume passes in linear HDR space (correct) and centralises ACES +
    // gamma encoding into a single place.
    private RenderSurface hdrSceneSurface = null!;
    // Post-process pipelines + intermediate surfaces (fog, SSR, dual-filter
    // bloom chain, composite). Boxed in PostProcessStack so the boilerplate
    // doesn't sprawl across the demo's setup; orchestration (which pass
    // runs when, what reads what) still lives in OnRender below since the
    // composition order is a demo-level choice.
    private PostProcessStack post = null!;
    private const int BloomMipCount = 4;
    private bool bloomEnabled = true;
    private float bloomStrength = 0.10f;

    // Composite-stage colour grading + tonemap selector. Operates in linear
    // HDR space before the tonemap so adjustments have access to the full
    // dynamic range. Defaults are identity-ish.
    private int tonemapMode = 0;            // 0=ACES, 1=AgX, 2=Reinhard, 3=Neutral
    private Vector3 colorTemp = Vector3.One;
    private float saturation = 1.0f;
    private float contrast = 1.0f;

    // Screen-space reflections + volumetric fog knobs. Surfaces + materials
    // live on the PostProcessStack above; these are the per-frame slider-
    // driven settings the demo packs into uniforms.
    private bool ssrEnabled = true;
    private bool ssrShowOnly = false;
    private bool ssrFlipV = false;
    private float ssrIntensity = 0.6f;
    private float ssrMaxDistance = 30.0f;
    private float ssrSteps = 40.0f;
    private float ssrThickness = 0.005f;  // NDC.z units now, not world units
    private float ssrRoughnessCutoff = 0.4f;
    private bool fogEnabled = true;
    private float fogDensity = 0.04f;
    private float fogScatter = 0.20f;
    private float fogSteps = 28.0f;
    private float fogMaxDistance = 40.0f;
    // Volumetric point-light in-scatter through the fog. Scales by lamp
    // HUE (not intensity) and by fog density, so the slider expresses
    // "warm haze strength" in roughly [0, 1] -- 0.5 is moderate, 1.0 is
    // dense atmospheric tint.
    private float fogPointScatter = 0.5f;
    private RenderSurface[] cascadeShadowSurfaces = null!;
    private Camera3D camera = null!;
    private SpriteBatch spriteBatch = null!;
    private Font? hudFont;

    private Matrix4x4[] cascadeLightVPs = new Matrix4x4[CascadeCount];

    // Input state ------------------------------------------------------------
    private readonly HashSet<Key> heldKeys = new();
    private float yaw = -MathF.PI * 0.5f;   // start looking down -X (Sponza's long axis)
    private float pitch;
    // Captured = mouselook on, debug overlay off (game mode).
    // Uncaptured = camera locked, ImGui sliders interactable (debug mode).
    // Toggled with Cmd/Ctrl+C, matching the ShaderLab demo's convention.
    private bool cursorCaptured = true;
    private float fpsSmoothed;
    private bool ShowDebug => !cursorCaptured;

    public string DebugName => "Walkthrough";

    protected override void OnLoad()
    {
        Host.SetTitle("Blix . Sponza Walkthrough");
        Host.SetCursorCaptured(true);

        // --- Load Sponza ------------------------------------------------
        Console.WriteLine("Loading Sponza...");
        var assets = new AssetDatabase()
            .RegisterImporter(new GltfStaticImporter())
            .RegisterImporter(new FontImporter())
            .LoadManifest(Path.Combine(AppContext.BaseDirectory, "Assets", "manifest.json"));

        var sponza = assets.Load<GltfModel>(AssetId.Parse("models/sponza"));
        try
        {
            var fontData = assets.Load<FontData>(AssetId.Parse("fonts/bowlby"));
            hudFont = Font.Upload(GraphicsDevice, fontData);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Font unavailable: {ex.Message}");
        }
        Console.WriteLine($"Sponza loaded: {sponza.Primitives.Length} primitives.");

        // --- Shadow surface (depth-only, sampler2DShadow-ready) -------
        // Compare=true enables the hardware sampler2DShadow PCF path: each
        // texture() call returns a 0..1 occlusion via bilinear-interpolated
        // depth-vs-reference comparisons. Linear filter is required for the
        // bilinear part to do real work.
        var shadowSampler = new SamplerDescription(
            MinFilter: TextureFilter.Linear,
            MagFilter: TextureFilter.Linear,
            WrapU: TextureWrap.ClampToEdge,
            WrapV: TextureWrap.ClampToEdge,
            GenerateMipmaps: false,
            Compare: true);
        cascadeShadowSurfaces = new RenderSurface[CascadeCount];
        cascadeShadowMaps = new TextureHandle[CascadeCount];
        for (int c = 0; c < CascadeCount; c++)
        {
            cascadeShadowSurfaces[c] = GraphicsDevice.CreateRenderSurface(new RenderSurfaceDescription(
                Name: $"walk.shadow.cascade{c}",
                Size: new FixedRenderSurfaceSize(ShadowMapSize, ShadowMapSize),
                ColorAttachments: Array.Empty<ColorAttachmentDescription>(),
                Depth: new DepthTexture(shadowSampler)));
            cascadeShadowMaps[c] = cascadeShadowSurfaces[c].DepthTexture
                ?? throw new InvalidOperationException($"Cascade {c} shadow surface has no depth texture.");
        }
        // The fog pass still uses a single sampler2DShadow; alias the farthest
        // cascade so its rays through the atrium hit a sensible shadow lookup.
        shadowMapTexture = cascadeShadowMaps[CascadeCount - 1];

        // --- Pipeline + fallback textures -------------------------------
        whitePixel = GraphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            new byte[] { 255, 255, 255, 255 },
            name: "walk.white");
        // Tangent-space "no perturbation": (128, 128, 255) decodes to (0, 0, 1).
        flatNormal = GraphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            new byte[] { 128, 128, 255, 255 },
            name: "walk.flat_normal");
        // glTF MR convention: G = roughness, B = metallic. Default to full
        // rough, no metal so factors alone drive the BRDF when no MR texture.
        neutralMetallicRoughness = GraphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            new byte[] { 0, 255, 0, 255 },
            name: "walk.neutral_mr");
        // AO = 1.0 (no occlusion). Gate uniform uHasOcclusionMap toggles
        // sampling on/off in the lit shader; the texture is always bound.
        fullOcclusion = GraphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            new byte[] { 255, 255, 255, 255 },
            name: "walk.full_ao");

        // --- Environment (HDR sky probe + IBL bakes) -------------------
        // Profile is the data-only authoring side: which source, sizes,
        // firefly clamp. EnvironmentBaker turns it into a probe with the
        // env cube + IBL probes; the BRDF LUT lives outside the probe
        // because it's env-independent and re-baking it on every sun-
        // direction slider tick would cost ~100ms per change for nothing.
        var hdrPath = Path.Combine(AppContext.BaseDirectory, "Assets", "textures", "sky_hdr.hdr");
        if (File.Exists(hdrPath))
        {
            Console.WriteLine($"Loading HDR sky: {hdrPath}");
            var hdrSource = ImageLoader.LoadRgba32F(hdrPath);
            Console.WriteLine($"  equirect {hdrSource.Width}x{hdrSource.Height}");
            envProfile = new EnvironmentProfile
            {
                Source = new HdrEnvironmentSource(hdrSource),
                EnvCubeFaceSize = EnvCubeFaceSize,
            };
        }
        else
        {
            Console.WriteLine("No HDR sky found; falling back to procedural sky.");
            envProfile = new EnvironmentProfile
            {
                Source = new ProceduralEnvironmentSource(bakedSunDirection),
                EnvCubeFaceSize = EnvCubeFaceSize,
            };
        }

        Console.WriteLine("Baking environment probe (env cube + IBL)...");
        envProbe = EnvironmentBaker.Bake(GraphicsDevice, envProfile, "walk");

        // Auto-align the directional sun to wherever the HDR's brightest
        // pixel sits in the sky. Without this the visible sun in the
        // backdrop and the directional-light direction disagree, and
        // shadows fall in visually-wrong directions for any HDRI dropped in.
        if (envProbe.SunDirectionFromEquirect is { } hdrSun)
        {
            hdrSunDirectionFromEquirect = hdrSun;
            sunDirection = hdrSun;
            bakedSunDirection = sunDirection;
            sunPitch = MathF.Asin(Math.Clamp(sunDirection.Y, -1.0f, 1.0f));
            sunYaw = MathF.Atan2(sunDirection.Z, sunDirection.X);
            Console.WriteLine($"  auto-aligned sun direction to HDR brightest pixel: {sunDirection}");
        }

        Console.WriteLine("Baking 256x256 BRDF LUT...");
        brdfLut = EnvironmentBaker.BakeBrdfLut(GraphicsDevice, 256, "walk.brdf_lut");

        var litShader = GraphicsDevice.CreateShaderProgram(LoadShader("lit.vert", "lit.frag"));
        // Two ColorBlends entries because hdrSceneSurface has two attachments
        // (HDR colour + material/roughness G-buffer). Both disabled: the lit
        // pass overwrites whatever was there. Same for every other pipeline
        // targeting hdrSceneSurface below -- they all need len-2 ColorBlends.
        litPipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                litShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                new[] { BlendState.Disabled, BlendState.Disabled }),
            name: "walk.lit");

        // Depth-only pipeline for the shadow pass. Uses the same vertex layout
        // (Sponza primitives are bound with this layout) but the shadow.vert
        // only reads aPosition; normal/uv slots are silently ignored.
        var shadowShader = GraphicsDevice.CreateShaderProgram(LoadShader("shadow.vert", "shadow.frag"));
        var shadowPipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                shadowShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                BlendState.Disabled),
            name: "walk.shadow");
        shadowMaterial = new Material("walk.shadow", shadowPipeline);

        // --- Point-light cube shadow infra -----------------------------
        // One depth cubemap per shadow-casting point light, plus six render
        // surfaces per cube (one per face). All six surfaces share the same
        // underlying cube via DepthCubeFace -- attaching a single face as the
        // depth target of an otherwise color-less framebuffer.
        var cubeShadowSampler = new SamplerDescription(
            MinFilter: TextureFilter.Linear,
            MagFilter: TextureFilter.Linear,
            WrapU: TextureWrap.ClampToEdge,
            WrapV: TextureWrap.ClampToEdge,
            GenerateMipmaps: false,
            Compare: true);
        pointShadowCubes = new TextureHandle[MaxPointShadows];
        pointShadowSurfaces = new RenderSurface[MaxPointShadows][];
        for (var i = 0; i < MaxPointShadows; i++)
        {
            pointShadowCubes[i] = GraphicsDevice.CreateTextureCubeDepth(
                PointShadowFaceSize, cubeShadowSampler, name: $"walk.pshadow.cube{i}");
            pointShadowSurfaces[i] = new RenderSurface[6];
            for (var face = 0; face < 6; face++)
            {
                pointShadowSurfaces[i][face] = GraphicsDevice.CreateRenderSurface(
                    new RenderSurfaceDescription(
                        Name: $"walk.pshadow.cube{i}.f{face}",
                        Size: new FixedRenderSurfaceSize(PointShadowFaceSize, PointShadowFaceSize),
                        ColorAttachments: Array.Empty<ColorAttachmentDescription>(),
                        Depth: new DepthCubeFace(pointShadowCubes[i], face)));
            }
        }

        var cubeShadowShader = GraphicsDevice.CreateShaderProgram(
            LoadShader("shadow_cube.vert", "shadow_cube.frag"));
        var cubeShadowPipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                cubeShadowShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                // Same culling as the directional shadow pass. Sponza has
                // single-sided drapes/arches that would self-occlude badly
                // under front-face culling; we rely on uPointShadowBias to
                // hide self-shadow acne instead.
                RasterizerState.BackFaceCulling,
                BlendState.Disabled),
            name: "walk.shadow_cube");
        cubeShadowMaterial = new Material("walk.shadow_cube", cubeShadowPipeline);

        pbrRenderer = new PbrSceneRenderer("walk", shadowMaterial, cubeShadowMaterial);

        // --- Flame quad + pipeline --------------------------------------
        // Single 1x1 quad rebuilt as a billboard in flame.vert. Alpha-blended,
        // depth-tested but no depth write so flames don't write occluder depth
        // for geometry behind them. NoCulling because the billboard normal is
        // computed in the vertex shader and we don't want orientation gotchas.
        var flameShader = GraphicsDevice.CreateShaderProgram(
            LoadShader("flame.vert", "flame.frag"));
        var flamePipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                flameShader,
                VertexPositionTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualNoWrite,
                RasterizerState.NoCulling,
                new[] { BlendState.AlphaBlend, BlendState.AlphaBlend }),
            name: "walk.flame");
        flameMaterial = new Material("walk.flame", flamePipeline);

        // Quad corners in local pos.xy in [-0.5..0.5], uv in [0..1]. Y up so
        // the noise scrolls toward the top of the flame.
        var flameVerts = new[]
        {
            new VertexPositionTexture(new(-0.5f, 0.0f), new(0.0f, 0.0f)),
            new VertexPositionTexture(new( 0.5f, 0.0f), new(1.0f, 0.0f)),
            new VertexPositionTexture(new( 0.5f, 1.0f), new(1.0f, 1.0f)),
            new VertexPositionTexture(new(-0.5f, 1.0f), new(0.0f, 1.0f)),
        };
        var flameIndices = new ushort[] { 0, 1, 2, 0, 2, 3 };
        var flameVb = GraphicsDevice.CreateVertexBuffer(
            VertexPositionTexture.CreateBufferData(flameVerts), name: "walk.flame.verts");
        var flameIb = GraphicsDevice.CreateIndexBuffer(flameIndices, name: "walk.flame.indices");
        flameMesh = new Mesh("walk.flame", flameVb, flameIb, flameIndices.Length, Bounds3.Empty);

        // --- Fire atlas + atlas-flavoured flame pipeline ----------------
        // Load Unity Labs Paris's CC-licensed Flame02-temperature atlas
        // (2048x1024, 16x4 grid of 128x256 frames; grayscale temperature
        // scalar). The atlas shader maps temperature through a blackbody
        // colour ramp at runtime so we keep the simulation's energy and
        // pick the hue ourselves. See LICENSE.txt next to the TGA for
        // provenance.
        var flameAtlasPath = Path.Combine(AppContext.BaseDirectory, "Assets", "textures", "fire_atlas.tga");
        var flameAtlasImage = ImageLoader.LoadRgba32(flameAtlasPath);
        flameAtlasTexture = GraphicsDevice.CreateTexture2D(
            new TextureDescription(
                flameAtlasImage.Width,
                flameAtlasImage.Height,
                TextureFormat.Rgba8,
                SamplerDescription.LinearClamp),
            flameAtlasImage.Pixels,
            name: "walk.flame.atlas");

        var flameAtlasShader = GraphicsDevice.CreateShaderProgram(
            LoadShader("flame.vert", "flame_atlas.frag"));
        var flameAtlasPipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                flameAtlasShader,
                VertexPositionTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualNoWrite,
                RasterizerState.NoCulling,
                new[] { BlendState.AlphaBlend, BlendState.AlphaBlend }),
            name: "walk.flame_atlas");
        flameAtlasMaterial = new Material("walk.flame_atlas", flameAtlasPipeline);
        flameAtlasMaterial.SetTexture("uFlameAtlas", flameAtlasTexture, 0);

        // --- Volumetric flame (ray-marched bounding box) ----------------
        // Unit cube in local [-0.5, 0.5]. Front-face-culled so we render the
        // BACK of the box -- guarantees a fragment to shade whether the
        // camera is outside (sees back through front) or inside (sees back
        // directly with front behind). Depth test LessEqual no-write so the
        // box is occluded by closer geometry but doesn't write its own
        // depth (volumes don't have a single Z; ray-march decides per-pixel).
        var cubeVerts = new VertexPosition3NormalTexture[]
        {
            new(new(-0.5f, -0.5f, -0.5f), new(0,0,0), new(0,0)),
            new(new( 0.5f, -0.5f, -0.5f), new(0,0,0), new(0,0)),
            new(new( 0.5f,  0.5f, -0.5f), new(0,0,0), new(0,0)),
            new(new(-0.5f,  0.5f, -0.5f), new(0,0,0), new(0,0)),
            new(new(-0.5f, -0.5f,  0.5f), new(0,0,0), new(0,0)),
            new(new( 0.5f, -0.5f,  0.5f), new(0,0,0), new(0,0)),
            new(new( 0.5f,  0.5f,  0.5f), new(0,0,0), new(0,0)),
            new(new(-0.5f,  0.5f,  0.5f), new(0,0,0), new(0,0)),
        };
        // CCW winding from OUTSIDE the cube. With CullMode.Front, the faces
        // pointing toward camera get culled, leaving back faces visible.
        var cubeIndices = new ushort[]
        {
            0, 1, 2,  0, 2, 3,    // -Z (front)
            5, 4, 7,  5, 7, 6,    // +Z (back)
            4, 0, 3,  4, 3, 7,    // -X
            1, 5, 6,  1, 6, 2,    // +X
            4, 5, 1,  4, 1, 0,    // -Y
            3, 2, 6,  3, 6, 7,    // +Y
        };
        var cubeVb = GraphicsDevice.CreateVertexBuffer(
            new VertexBufferData(
                new VertexBufferDescription(
                    VertexPosition3NormalTexture.Layout,
                    cubeVerts.Length,
                    GraphicsBufferUsage.Static),
                VertexPosition3NormalTexture.Pack(cubeVerts)),
            name: "walk.volume.cube.verts");
        var cubeIb = GraphicsDevice.CreateIndexBuffer(cubeIndices, name: "walk.volume.cube.indices");
        volumeCubeMesh = new Mesh("walk.volume.cube", cubeVb, cubeIb, cubeIndices.Length, Bounds3.Empty);

        var volumeShader = GraphicsDevice.CreateShaderProgram(
            LoadShader("volume.vert", "volume.frag"));
        var volumePipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                volumeShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualNoWrite,
                new RasterizerState(CullMode.Front, FrontFace.CounterClockwise),
                new[] { BlendState.AlphaBlend, BlendState.AlphaBlend }),
            name: "walk.volume");
        volumeMaterial = new Material("walk.volume", volumePipeline);

        // --- Load baked VDB volume (BVOL format) ------------------------
        // tools/vdb_to_blix_volume.py renders the JangaFX Small Campfire
        // VDB pack down to a single packed R8 3D texture: width x height
        // x (depth_per_frame * frame_count). Header is 40 bytes; body is
        // the raw voxel data laid out X-fastest.
        var bvolPath = Path.Combine(AppContext.BaseDirectory, "Assets", "textures", "fire_volume.bvol");
        if (File.Exists(bvolPath))
        {
            var bvolBytes = File.ReadAllBytes(bvolPath);
            // BVOL header is 32 bytes: 4-byte magic + 7 x uint32 fields
            // (version, width, height, depth_per_frame, frame_count,
            // channels, reserved).
            if (bvolBytes.Length < 32 ||
                bvolBytes[0] != (byte)'B' || bvolBytes[1] != (byte)'V' ||
                bvolBytes[2] != (byte)'O' || bvolBytes[3] != (byte)'L')
            {
                throw new InvalidDataException("fire_volume.bvol header is not 'BVOL'.");
            }
            uint version = BitConverter.ToUInt32(bvolBytes, 4);
            int vw   = (int)BitConverter.ToUInt32(bvolBytes, 8);
            int vh   = (int)BitConverter.ToUInt32(bvolBytes, 12);
            int vd   = (int)BitConverter.ToUInt32(bvolBytes, 16);   // depth per frame
            int vfr  = (int)BitConverter.ToUInt32(bvolBytes, 20);   // frame count
            int vch  = (int)BitConverter.ToUInt32(bvolBytes, 24);
            if (version != 1u)
                throw new InvalidDataException($"fire_volume.bvol version {version} unsupported (expected 1).");
            if (vch != 1)
                throw new InvalidDataException($"fire_volume.bvol channel count {vch} unsupported (expected 1).");
            int expectedBytes = vw * vh * vd * vfr * vch;
            int bodyBytes = bvolBytes.Length - 32;
            if (bodyBytes != expectedBytes)
                throw new InvalidDataException(
                    $"fire_volume.bvol body is {bodyBytes} bytes; expected {expectedBytes}.");
            // Sampler: linear in all 3 axes, clamp on every edge (the flame
            // shouldn't repeat across cube boundaries), no mipmaps.
            var volumeSampler = new SamplerDescription(
                MinFilter: TextureFilter.Linear,
                MagFilter: TextureFilter.Linear,
                WrapU: TextureWrap.ClampToEdge,
                WrapV: TextureWrap.ClampToEdge,
                GenerateMipmaps: false,
                WrapW: TextureWrap.ClampToEdge);
            volumeVdbTexture = GraphicsDevice.CreateTexture3D(
                vw, vh, vd * vfr,
                TextureFormat.R8,
                volumeSampler,
                new ReadOnlySpan<byte>(bvolBytes, 32, bodyBytes),
                name: "walk.volume.vdb");
            volumeVdbFrames = vfr;
            volumeMaterial.SetTexture("uVolumeData", volumeVdbTexture, 0);
            Console.WriteLine($"Loaded VDB volume: {vw}x{vh}x{vd} per frame, {vfr} frames.");
        }
        else
        {
            Console.WriteLine($"WARN: {bvolPath} not found; volumetric flame will fall back to analytic noise only.");
            // volumeVdbFrames stays 0 -> effectiveVdbWeight drops to 0 at draw time.
        }

        // --- Skybox pipeline + mesh -------------------------------------
        // Rendered AFTER geometry inside the scene pass with LessEqual depth +
        // no depth write. The vertex shader puts the quad at NDC z = 1, so
        // only pixels that geometry didn't already cover get the sky shader.
        var skyShader = GraphicsDevice.CreateShaderProgram(
            LoadShader("skybox.vert", "skybox.frag"));
        var skyPipeline = GraphicsDevice.CreatePipeline(
            new PipelineDescription(
                skyShader,
                VertexPositionTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualNoWrite,
                RasterizerState.NoCulling,
                new[] { BlendState.Disabled, BlendState.Disabled }),
            name: "walk.sky");
        skyboxMaterial = new Material("walk.sky", skyPipeline);
        skyboxMaterial.SetTexture("uEnvMap", envProbe.EnvCubemap, 0);
        var skyVerts = GraphicsDevice.CreateVertexBuffer(
            VertexPositionTexture.CreateBufferData(FullscreenQuad.Vertices),
            name: "walk.sky.verts");
        var skyIndices = GraphicsDevice.CreateIndexBuffer(FullscreenQuad.Indices, name: "walk.sky.indices");
        skyMesh = new Mesh("walk.sky", skyVerts, skyIndices,
            FullscreenQuad.Indices.Length, Bounds3.Empty);

        // --- HDR scene render target + composite pipeline ---------------
        // Scene passes now render into hdrSceneSurface (Rgba16F + depth) so
        // alpha blending happens in linear HDR space. The composite pass
        // samples this surface, applies ACES + gamma, and writes to the
        // default surface. Tonemap was previously baked into lit.frag and
        // skybox.frag; those now output raw HDR.
        // Depth is a SAMPLEABLE texture (Compare=false) rather than a
        // renderbuffer so the volumetric fog pass can read it to find each
        // pixel's scene-space far point for the ray march.
        var sceneDepthSampler = new SamplerDescription(
            MinFilter: TextureFilter.Nearest,
            MagFilter: TextureFilter.Nearest,
            WrapU: TextureWrap.ClampToEdge,
            WrapV: TextureWrap.ClampToEdge,
            GenerateMipmaps: false,
            Compare: false);
        hdrSceneSurface = GraphicsDevice.CreateRenderSurface(new RenderSurfaceDescription(
            Name: "walk.hdr_scene",
            Size: new MatchDefaultRenderSurfaceSize(1.0f),
            ColorAttachments: new[]
            {
                new ColorAttachmentDescription(TextureFormat.Rgba16F, SamplerDescription.LinearClamp),
                // Attachment 1: per-fragment material info. R = roughness;
                // GBA spare for future material/ID writes. Sampled by SSR
                // to gate matte surfaces (cloth, plaster). Lit pass writes
                // roughness directly; sky/volume/fog/flame passes write a
                // value that combines correctly with their blend mode so
                // the lit-pass roughness is preserved where appropriate.
                new ColorAttachmentDescription(TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            },
            Depth: new DepthTexture(sceneDepthSampler)));

        // --- Post-process resources (fog, SSR, bloom chain, composite) ---
        // PostProcessStack owns the pipelines + intermediate surfaces + the
        // fullscreen quad mesh. Demo still drives the per-frame pass
        // orchestration; the stack is just where the boilerplate lives.
        post = PostProcessStack.Create(
            GraphicsDevice,
            Path.Combine(AppContext.BaseDirectory, "Shaders"),
            namePrefix: "walk",
            bloomMipCount: BloomMipCount);
        // Shadow map binding stays static (single directional light). Scene
        // depth gets rebound each frame in OnRender.
        post.FogMaterial.SetTexture("uShadowMap", shadowMapTexture, 1);

        // --- Upload Sponza primitives + build materials -----------------
        // GltfSceneInstance does the per-primitive Mesh upload + per-material
        // construction; the OnMaterialBuilt hook is where this demo's shared
        // scene-wide bindings (cascade shadow maps, point cube shadows, IBL
        // probes, env cube, BRDF LUT) get attached. The hook lets the demo
        // hold all of that without GltfSceneInstance needing to know.
        scene = GltfSceneInstance.Build(GraphicsDevice, sponza, new GltfSceneOptions
        {
            Opaque = litPipeline,
            Defaults = new GltfDefaultTextures(
                WhitePixel: whitePixel,
                FlatNormal: flatNormal,
                NeutralMetallicRoughness: neutralMetallicRoughness,
                FullOcclusion: fullOcclusion),
            Sampler = new SamplerDescription(
                MinFilter: TextureFilter.Linear,
                MagFilter: TextureFilter.Linear,
                WrapU: TextureWrap.Repeat,
                WrapV: TextureWrap.Repeat,
                GenerateMipmaps: true,
                Compare: false),
            Prefix = "sponza",
            OnMaterialBuilt = (material, _) =>
            {
                // CSM: bind all three cascade shadow maps. Slot 4 = cascade 0
                // (closest), slot 13 = cascade 1, slot 14 = cascade 2.
                material.SetTexture("uShadowMap", cascadeShadowMaps[0], 4);
                material.SetTexture("uShadowMap1", cascadeShadowMaps[1], 13);
                material.SetTexture("uShadowMap2", cascadeShadowMaps[2], 14);
                material.SetTexture("uEnvMap", envProbe.EnvCubemap, 5);
                // Slots 6..9 are the four point-light cube shadow maps. Bound on
                // every lit material; the shader's MAX_POINT_SHADOWS gate
                // decides which ones contribute per fragment.
                material.SetTexture("uPointShadowMap0", pointShadowCubes[0], 6);
                material.SetTexture("uPointShadowMap1", pointShadowCubes[1], 7);
                material.SetTexture("uPointShadowMap2", pointShadowCubes[2], 8);
                material.SetTexture("uPointShadowMap3", pointShadowCubes[3], 9);
                material.SetTexture("uDiffuseIrradiance", envProbe.DiffuseIrradiance, 10);
                material.SetTexture("uSpecularPrefilter", envProbe.PrefilteredSpecular, 11);
                material.SetTexture("uBrdfLut", brdfLut, 12);
                material.SetUniform("uEnvMapMipCount", new FloatUniform((float)envProbe.EnvCubeMipCount));
            },
        });

        // Sponza-specific floor detection: thin Y span, wide XZ footprint,
        // near y=0. Catches the marble floor primitives and nothing else.
        // Recorded indices have per-frame uniform overrides applied below
        // (roughness/metallic/baseColor from the Floor debug sliders).
        for (var i = 0; i < scene.Submeshes.Count; i++)
        {
            var b = scene.Submeshes[i].WorldBounds;
            var ySpan = b.Max.Y - b.Min.Y;
            var xSpan = b.Max.X - b.Min.X;
            var zSpan = b.Max.Z - b.Min.Z;
            var isFloor = ySpan < 0.2f && b.Min.Y < 0.5f && (xSpan > 4.0f || zSpan > 4.0f);
            if (isFloor)
            {
                floorIndices.Add(i);
                Console.WriteLine($"  Floor: {scene.Submeshes[i].Name} y=[{b.Min.Y:0.00}..{b.Max.Y:0.00}] x=[{b.Min.X:0.0}..{b.Max.X:0.0}] z=[{b.Min.Z:0.0}..{b.Max.Z:0.0}] (xspan={xSpan:0.0}m zspan={zSpan:0.0}m)");
            }
        }
        Console.WriteLine($"Uploaded {scene.Submeshes.Count} submeshes, {scene.Materials.All.Count} unique materials.");

        // --- Camera -----------------------------------------------------
        camera = new Camera3D
        {
            Transform = new Transform3D { Position = new Vector3(7.0f, 1.7f, 0.0f) },
            VerticalFieldOfView = cameraFov,
            NearPlane = cameraNearPlane,
            FarPlane = cameraFarPlane
        };

        // --- HUD --------------------------------------------------------
        spriteBatch = new SpriteBatch(GraphicsDevice);
        sceneSurface = new RenderSurface(RenderSurfaceHandle.Default, Array.Empty<TextureHandle>(), null);
    }

    void IInputHandler.OnKeyDown(Key key)
    {
        heldKeys.Add(key);
        if (key == Key.Escape) Host.RequestClose();
        else if (key == Key.C &&
                 (heldKeys.Contains(Key.LeftControl) ||
                  heldKeys.Contains(Key.RightControl) ||
                  heldKeys.Contains(Key.LeftSuper) ||
                  heldKeys.Contains(Key.RightSuper)))
        {
            // Cmd/Ctrl+C: single dev-mode toggle. Cursor capture controls
            // both mouselook (off when uncaptured) and ImGui interaction
            // (mouse forwarded to ImGui when uncaptured). ShowDebug is
            // derived from !cursorCaptured so the overlay follows.
            cursorCaptured = !cursorCaptured;
            Host.SetCursorCaptured(cursorCaptured);
        }
    }

    void IInputHandler.OnKeyUp(Key key) => heldKeys.Remove(key);

    void IInputHandler.OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        if (!cursorCaptured) return;
        yaw -= deltaX * mouseLookSensitivity;
        pitch -= deltaY * mouseLookSensitivity;
        pitch = Math.Clamp(pitch, -PitchClamp, PitchClamp);
    }

    public override void OnUpdate(Time time)
    {
        var dt = (float)time.Delta;
        if (dt <= 0) return;

        // Derive live sun direction from yaw/pitch sliders. The IBL cubemap
        // only re-bakes when the user hits the Rebake button — moving the
        // sliders previews direct lighting without the full upload cost.
        sunDirection = SunDirectionFromYawPitch(sunYaw, sunPitch);
        if (rebakeRequested)
        {
            RebakeSky();
            rebakeRequested = false;
        }

        // Sync slider-driven camera params.
        camera.VerticalFieldOfView = cameraFov;
        camera.NearPlane = cameraNearPlane;
        camera.FarPlane = cameraFarPlane;

        // Push floor overrides onto tagged materials. Sponza's floor MR
        // texture averages roughness ~0.66 — multiplying by a factor can't
        // get us truly polished, so when override is on we also flip
        // uHasMetallicMap to 0, which makes the shader read the factors
        // directly and ignore the texture entirely.
        var floorRough = floorOverrideEnabled ? floorRoughness : 1.0f;
        var floorMetal = floorOverrideEnabled ? floorMetallic : 1.0f;
        var floorUseMap = floorOverrideEnabled ? 0.0f : 1.0f;
        // Visualize: solid magenta on detected floor prims so they're
        // unmistakable in the viewport. Off: restore the asset's authored
        // baseColorFactor (mat[5] is 0.588 gray, not white).
        foreach (var i in floorIndices)
        {
            var sub = scene.Submeshes[i];
            sub.Material.SetUniform("uRoughnessFactor", new FloatUniform(floorRough));
            sub.Material.SetUniform("uMetallicFactor", new FloatUniform(floorMetal));
            sub.Material.SetUniform("uHasMetallicMap", new FloatUniform(floorUseMap));
            var original = sub.Source?.BaseColorFactor ?? new Vector4(1.0f);
            var albedo = floorVisualize ? new Vector4(1.0f, 0.0f, 1.0f, 1.0f) : original;
            sub.Material.SetUniform("uBaseColorFactor", new Vector4Uniform(albedo));
        }

        // Per-cascade VPs are computed in OnRender (we need view aspect),
        // not here. ComputeLightViewProjection is still used as a legacy
        // single-frustum fallback for the fog pass.

        // Yaw rotates around world-Y so the camera's "forward" stays horizontal
        // when WASD-walking. Pitch is applied on top for mouse-look. WASD moves
        // on the horizontal plane (no flying via W/S); Space/Ctrl ascend/descend.
        var forwardHoriz = new Vector3(MathF.Sin(yaw), 0, MathF.Cos(yaw));
        var rightHoriz = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));

        var move = Vector3.Zero;
        if (heldKeys.Contains(Key.W)) move -= forwardHoriz;
        if (heldKeys.Contains(Key.S)) move += forwardHoriz;
        if (heldKeys.Contains(Key.A)) move -= rightHoriz;
        if (heldKeys.Contains(Key.D)) move += rightHoriz;
        if (heldKeys.Contains(Key.Space)) move += Vector3.UnitY;
        if (heldKeys.Contains(Key.LeftControl) || heldKeys.Contains(Key.RightControl)) move -= Vector3.UnitY;

        if (move.LengthSquared() > 1e-6f) move = Vector3.Normalize(move);
        var speed = walkSpeed;
        if (heldKeys.Contains(Key.LeftSuper) || heldKeys.Contains(Key.RightSuper)) speed *= sprintMultiplier;

        camera.Transform.Position += move * speed * dt;

        // Apply yaw + pitch to the camera's rotation. Identity rotation looks
        // down -Z; we build pitch about local X then yaw about world Y.
        var pitchQ = Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch);
        var yawQ = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        camera.Transform.Rotation = yawQ * pitchQ;

        // FPS HUD smoothing
        var instantFps = 1.0f / dt;
        fpsSmoothed = fpsSmoothed == 0.0f ? instantFps : fpsSmoothed * 0.9f + instantFps * 0.1f;
    }

    public override void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return;
        var aspect = (float)frame.Width / frame.Height;

        var view = camera.GetView();
        var proj = camera.GetProjection(aspect);

        // Point-light cube shadow re-bake (lazy: only when light positions
        // differ from the last bake, or on first frame). Sponza is a static
        // scene with static lights, so this typically runs once at startup
        // and only again on user-driven changes.
        var activeLights = pointLightsEnabled
            ? Math.Min(pointLightPositions.Length, MaxPointLights)
            : 0;
        var shadowCount = Math.Min(activeLights, MaxPointShadows);
        if (PointShadowsDirty(shadowCount))
        {
            BakePointShadows(commandList, shadowCount);
        }

        // --- Per-cascade light VPs --------------------------------------
        // Each cascade fits its view-frustum slice in light space, snug,
        // independent of the others. Recomputed each frame because they
        // depend on camera position/orientation. With disableCascades, all
        // three cascades use the single legacy single-frustum VP -- a
        // diagnostic A/B for narrowing down whether shadow issues live in
        // the cascade code or elsewhere.
        if (disableCascades)
        {
            var legacyVP = ComputeLightViewProjection(sunDirection);
            for (int c = 0; c < CascadeCount; c++) cascadeLightVPs[c] = legacyVP;
        }
        else
        {
            for (int c = 0; c < CascadeCount; c++)
            {
                cascadeLightVPs[c] = ComputeCascadeLightViewProjection(
                    sunDirection,
                    camera.Transform.Position,
                    camera.Transform.Rotation,
                    camera.VerticalFieldOfView,
                    aspect,
                    cascadeSplits[c],
                    cascadeSplits[c + 1]);
            }
        }

        // --- Shadow passes: one per cascade -----------------------------
        for (int c = 0; c < CascadeCount; c++)
        {
            var cascadeIndex = c;       // capture for closure
            var cascadeVP = cascadeLightVPs[c];
            commandList.Pass(
                $"walk.shadow.cascade{cascadeIndex}",
                new RenderPassDescription(
                    cascadeShadowSurfaces[cascadeIndex].Handle,
                    ClearColors: Array.Empty<GraphicsColor?>(),
                    ClearDepth: true),
                pass => pbrRenderer.DrawCascadeShadow(pass, scene, cascadeVP));
        }

        // Pack point lights into uniform arrays sized to the shader's
        // MAX_POINT_LIGHTS. Slots past the active count are filled with zeros
        // for safety but the shader's count gate skips them anyway.
        var plPositions = new Vector3[MaxPointLights];
        var plColors = new Vector3[MaxPointLights];
        var plRanges = new float[MaxPointLights];
        var activeCount = pointLightsEnabled
            ? Math.Min(pointLightPositions.Length, MaxPointLights)
            : 0;
        var packedColor = pointLightColor * pointLightIntensity;
        var flickerTime = (float)time.Total * pointLightFlickerSpeed;
        for (var i = 0; i < activeCount; i++)
        {
            // Per-lamp phase offset so the four braziers don't flicker in
            // lockstep. Three octaves of sine compose into a "candle-y" 1D
            // signal in roughly [1 - amount, 1 + 0.6 * amount]; we clamp
            // to keep extreme dips/spikes bounded.
            var phase = i * 1.73f;
            var f = MathF.Sin(flickerTime *  7.3f + phase)
                  + MathF.Sin(flickerTime * 13.7f + phase * 0.6f) * 0.6f
                  + MathF.Sin(flickerTime * 27.0f + phase * 1.4f) * 0.35f;
            // f is now in roughly [-2.0, 2.0]; normalise to a centred
            // multiplier around 1.0 weighted by amount.
            var flicker = 1.0f + (f / 2.0f) * pointLightFlickerAmount;
            flicker = Math.Clamp(flicker, 0.55f, 1.35f);

            plPositions[i] = pointLightPositions[i];
            plColors[i] = packedColor * flicker;
            plRanges[i] = pointLightRange;
        }

        var cascadeVpArray = new Matrix4x4[CascadeCount];
        for (int c = 0; c < CascadeCount; c++) cascadeVpArray[c] = cascadeLightVPs[c];
        // Float-array splits sized [CascadeCount + 1] so the lit shader can
        // do simple `if (viewDepth < splits[i+1])` lookups. Splits beyond
        // CascadeCount cascadeSplits[N] return no shadow (lit returns 1.0).
        var splitFloats = new float[CascadeCount + 1];
        Array.Copy(cascadeSplits, splitFloats, CascadeCount + 1);

        // Per-frame inputs handed to PbrSceneRenderer. Physics-input fields
        // (camera, sun, cascades, point lights, env probe, exposure) live
        // on PbrFrameContext directly; demo-tuned shader knobs flow
        // through ExtraUniforms as a flat list.
        var demoTuning = new ShaderUniform[]
        {
            new("uEmissiveBoost", new FloatUniform(emissiveBoost)),
            new("uIblSpecAttenuation", new FloatUniform(iblSpecAttenuation)),
            new("uIblDiffuseBoost", new FloatUniform(iblDiffuseBoost)),
            new("uMetalFloor", new FloatUniform(metalFloor)),
            new("uIndirectShadowBase", new FloatUniform(indirectShadowBase)),
            new("uIndirectShadowRange", new FloatUniform(indirectShadowRange)),
            new("uHorizonFadeStrength", new FloatUniform(horizonFadeStrength)),
            new("uHorizonFadeStart", new FloatUniform(horizonFadeStart)),
            new("uSkyTint", new Vector3Uniform(skyTint)),
            new("uPointLightSpecScale", new FloatUniform(pointLightSpecScale)),
            new("uPointShadowFarPlane", new FloatUniform(pointShadowFarPlane)),
            new("uPointShadowBias", new FloatUniform(pointShadowBias)),
            new("uPointShadowFilterRadius", new FloatUniform(pointShadowFilterRadius)),
        };
        var frameContext = new PbrFrameContext
        {
            View = view,
            Projection = proj,
            CameraPosition = camera.Transform.Position,
            SunDirection = sunDirection,
            SunColor = new Vector3(1.0f, 0.94f, 0.82f) * sunStrength,
            Environment = envProbe,
            Cascades = new CascadeShadowState(cascadeVpArray, splitFloats, visualizeCascades),
            PointLights = new PbrPointLightState(plPositions, plColors, plRanges, activeCount),
            Exposure = exposure,
            ExtraUniforms = demoTuning,
        };
        var sharedUniforms = pbrRenderer.PackSceneUniforms(frameContext);

        // Inverse view-projection for the skybox: lets its vertex shader
        // unproject NDC corners back into world space to compute the view ray
        // per fragment. The fragment shader samples the env cubemap (the same
        // cube bound for IBL) so backdrop + reflections show the same sun.
        var viewProjection = proj * view;
        Matrix4x4.Invert(viewProjection, out var invViewProj);
        var skyUniforms = new ShaderUniform[]
        {
            new("uInvViewProj", new Matrix4x4Uniform(invViewProj)),
            new("uCameraPosition", new Vector3Uniform(camera.Transform.Position)),
            new("uExposure", new FloatUniform(exposure)),
            new("uSkyTint", new Vector3Uniform(skyTint))
        };

        commandList.Pass(
            "walk.scene",
            new RenderPassDescription(
                hdrSceneSurface.Handle,
                // Clear colour is overwritten by the skybox fill pass at the
                // end. Kept as a sane sky-blue so the very first frame before
                // the skybox draws doesn't flash black.
                ClearColors: new GraphicsColor?[] { new(0.55f, 0.66f, 0.82f, 1.0f) },
                ClearDepth: true),
            pass =>
            {
                pbrRenderer.DrawScene(pass, scene, sharedUniforms);
                // Sky fills the un-drawn pixels (depth=1 from the clear) using
                // LessEqualNoWrite. Drawn last so it costs only sky pixels and
                // doesn't waste fragment work behind opaque geometry.
                pass.DrawMesh(skyMesh, skyboxMaterial,
                    perDrawUniforms: skyUniforms, perDrawTextures: null);

                // Flame quads OR volumes -- one per active point light.
                // Alpha-blended, depth-tested but no depth write, so they sit
                // on top of the lamp body while still being occluded by
                // columns/walls in front. Drawn after the sky so the sky
                // never overwrites a flame pixel.
                if (flamesEnabled && useFlameVolume)
                {
                    var volumeTime = (float)time.Total;
                    for (var i = 0; i < activeLights; i++)
                    {
                        var perFlamePhaseSeconds = i * 0.57f;
                        // Place the volume centred on the lamp position plus
                        // half the volumeSize upward so the box's BASE sits
                        // at the lamp's anchor world Y (matching how flame
                        // quads anchor at base).
                        var lampPos = pointLightPositions[i] + new Vector3(0, flameYOffset, 0);
                        var volumeCenter = lampPos + new Vector3(0, volumeSize * 0.5f, 0);
                        // VDB playback: per-lamp phase offset puts each
                        // brazier at a different point in the 34-frame loop
                        // so they don't pulse in sync.
                        var vdbHasData = volumeVdbFrames > 0;
                        var vdbFrameCount = vdbHasData ? (float)volumeVdbFrames : 1.0f;
                        var vdbPhase = perFlamePhaseSeconds * volumeFps;
                        var effectiveVdbWeight = vdbHasData ? volumeVdbWeight : 0.0f;
                        var perDrawV = new ShaderUniform[]
                        {
                            new("uView", new Matrix4x4Uniform(view)),
                            new("uProjection", new Matrix4x4Uniform(proj)),
                            new("uCameraPosition", new Vector3Uniform(camera.Transform.Position)),
                            new("uVolumeCenter", new Vector3Uniform(volumeCenter)),
                            new("uVolumeSize", new FloatUniform(volumeSize)),
                            new("uTime", new FloatUniform(volumeTime + perFlamePhaseSeconds)),
                            new("uFlameColor", new Vector3Uniform(pointLightColor)),
                            new("uFlameIntensity", new FloatUniform(volumeIntensity)),
                            new("uExposure", new FloatUniform(exposure)),
                            new("uVolumeSteps", new FloatUniform(volumeSteps)),
                            new("uVolumeDensity", new FloatUniform(volumeDensity)),
                            new("uVolumeRise", new FloatUniform(volumeRise)),
                            new("uVolumeFrames", new FloatUniform(vdbFrameCount)),
                            new("uVolumeFps", new FloatUniform(volumeFps)),
                            new("uFramePhase", new FloatUniform(vdbPhase)),
                            new("uVolumeVdbWeight", new FloatUniform(effectiveVdbWeight)),
                            new("uVolumeProcWeight", new FloatUniform(volumeProcWeight)),
                            new("uVolumeTempBoost", new FloatUniform(volumeTempBoost))
                        };
                        pass.DrawMesh(volumeCubeMesh, volumeMaterial,
                            perDrawUniforms: perDrawV, perDrawTextures: null);
                    }
                }
                else if (flamesEnabled)
                {
                    var flameTime = (float)time.Total;
                    var chosenMat = useFlameAtlas ? flameAtlasMaterial : flameMaterial;
                    for (var i = 0; i < activeLights; i++)
                    {
                        // Per-flame phase offset so the four braziers don't
                        // animate in lockstep. Multiplied by atlas-fps for the
                        // atlas path so the integer-frame offset translates
                        // into a meaningful time-domain offset for procedural.
                        var perFlamePhaseSeconds = i * 0.57f;
                        var atlasPhaseFrames = perFlamePhaseSeconds * flameAtlasFps;
                        // Atlas frames are 128x204.8 in a 16x5 layout (the
                        // file's "16x4" name is wrong -- straddling rows in a
                        // 16x4 sampler picks up the top of the next frame as
                        // a second bright region per cell). Aspect = 128/204.8.
                        // Procedural uses square (1.0) -- it was tuned that way.
                        var aspect = useFlameAtlas ? 0.625f : 1.0f;
                        var perDraw = new List<ShaderUniform>
                        {
                            new("uView", new Matrix4x4Uniform(view)),
                            new("uProjection", new Matrix4x4Uniform(proj)),
                            new("uFlamePosition", new Vector3Uniform(
                                pointLightPositions[i] + new Vector3(0.0f, flameYOffset, 0.0f))),
                            new("uFlameSize", new FloatUniform(flameSize)),
                            new("uFlameAspect", new FloatUniform(aspect)),
                            new("uFlameColor", new Vector3Uniform(pointLightColor)),
                            new("uFlameIntensity", new FloatUniform(flameIntensity)),
                            new("uExposure", new FloatUniform(exposure)),
                            new("uTime", new FloatUniform(flameTime + perFlamePhaseSeconds * 30.0f))
                        };
                        if (useFlameAtlas)
                        {
                            // Real layout is 16 cols x 5 rows, 80 frames total.
                            // Each frame is 128 x 204.8 pixels (1024/5). The
                            // file's "16x4" name is wrong and produced the
                            // "two flames per cell" artefact -- sampling 256-
                            // tall cells straddled two real frames vertically.
                            // 80 = play the full loop; 16 = first-row fallback.
                            var totalFrames = flameAtlasSingleRow ? 16.0f : 80.0f;
                            perDraw.Add(new("uAtlasCols", new FloatUniform(16.0f)));
                            perDraw.Add(new("uAtlasRows", new FloatUniform(5.0f)));
                            perDraw.Add(new("uAtlasFrames", new FloatUniform(totalFrames)));
                            perDraw.Add(new("uAtlasFps", new FloatUniform(flameAtlasFps)));
                            perDraw.Add(new("uFramePhase", new FloatUniform(atlasPhaseFrames)));
                        }
                        pass.DrawMesh(flameMesh, chosenMat,
                            perDrawUniforms: perDraw, perDrawTextures: null);
                    }
                }
            });

        // --- Volumetric fog (god-rays from directional sun) -------------
        // Full-screen pass that ray-marches each pixel from camera to the
        // scene's depth, sampling the directional shadow map at each step
        // and accumulating in-scattered sun light. Additive into the HDR
        // scene buffer so the fog is part of what bloom processes.
        if (fogEnabled && hdrSceneSurface.DepthTexture is { } sceneDepthHandle)
        {
            Matrix4x4.Invert(proj * view, out var invVP);
            post.FogMaterial.SetTexture("uSceneDepth", sceneDepthHandle, 0);
            var fogUniforms = new ShaderUniform[]
            {
                new("uInvViewProj", new Matrix4x4Uniform(invVP)),
                new("uLightViewProjection", new Matrix4x4Uniform(cascadeLightVPs[CascadeCount - 1])),
                new("uCameraPosition", new Vector3Uniform(camera.Transform.Position)),
                new("uSunDirection", new Vector3Uniform(sunDirection)),
                new("uSunColor", new Vector3Uniform(new Vector3(1.0f, 0.94f, 0.82f) * sunStrength)),
                new("uFogDensity", new FloatUniform(fogDensity)),
                new("uFogScatter", new FloatUniform(fogScatter)),
                new("uFogSteps", new FloatUniform(fogSteps)),
                new("uFogMaxDistance", new FloatUniform(fogMaxDistance)),
                // Reuse the same packed point-light arrays the lit pass uses;
                // they're already filled in earlier in OnRender.
                new("uPointLightPositions", new Vector3ArrayUniform(plPositions)),
                new("uPointLightColors", new Vector3ArrayUniform(plColors)),
                new("uPointLightRanges", new FloatArrayUniform(plRanges)),
                new("uPointLightCount", new FloatUniform(activeCount)),
                new("uFogPointScatter", new FloatUniform(fogPointScatter))
            };
            commandList.Pass(
                "walk.fog",
                new RenderPassDescription(
                    hdrSceneSurface.Handle,
                    ClearColors: Array.Empty<GraphicsColor?>(),  // keep scene contents
                    ClearDepth: false),
                pass =>
                {
                    pass.DrawMesh(post.FullscreenQuad, post.FogMaterial,
                        perDrawUniforms: fogUniforms, perDrawTextures: null);
                });
        }

        // --- Screen-space reflections -----------------------------------
        // Runs after fog so its god-rays end up reflected too. Reads the
        // (now fog-augmented) HDR scene buffer and depth; writes into a
        // separate post.SsrSurface so we dodge the read-write hazard of writing
        // to a buffer we're also sampling. Composite pulls post.SsrSurface into
        // the final result.
        var hdrColorTex = hdrSceneSurface.ColorAttachments[0];
        var hdrRoughnessTex = hdrSceneSurface.ColorAttachments[1];
        if (ssrEnabled && hdrSceneSurface.DepthTexture is { } ssrDepthHandle)
        {
            Matrix4x4.Invert(proj * view, out var ssrInvVP);
            post.SsrMaterial.SetTexture("uHdrScene", hdrColorTex, 0);
            post.SsrMaterial.SetTexture("uSceneDepth", ssrDepthHandle, 1);
            post.SsrMaterial.SetTexture("uRoughnessMap", hdrRoughnessTex, 2);
            commandList.Pass(
                "walk.ssr",
                new RenderPassDescription(
                    post.SsrSurface.Handle,
                    ClearColors: new GraphicsColor?[] { new(0, 0, 0, 0) },
                    ClearDepth: false),
                pass =>
                {
                    pass.DrawMesh(post.FullscreenQuad, post.SsrMaterial,
                        perDrawUniforms: new ShaderUniform[]
                        {
                            new("uView", new Matrix4x4Uniform(view)),
                            new("uProjection", new Matrix4x4Uniform(proj)),
                            new("uInvViewProj", new Matrix4x4Uniform(ssrInvVP)),
                            new("uCameraPosition", new Vector3Uniform(camera.Transform.Position)),
                            new("uMaxDistance", new FloatUniform(ssrMaxDistance)),
                            new("uSteps", new FloatUniform(ssrSteps)),
                            new("uThickness", new FloatUniform(ssrThickness)),
                            new("uIntensity", new FloatUniform(ssrIntensity)),
                            new("uRoughnessCutoff", new FloatUniform(ssrRoughnessCutoff))
                        },
                        perDrawTextures: null);
                });
        }

        // --- Bloom: downsample chain then upsample chain ----------------
        // Downsample: mip0 ← HDR scene; mip(n+1) ← down(mip(n)). Each pass
        // overwrites its destination (no blend). Upsample: starting from
        // the coarsest mip, additively blend a tent-filtered copy of it
        // into the next-finer mip. After the upsample chain, mip0 contains
        // the sum of all mip contributions and is sampled by composite.
        if (bloomEnabled)
        {
            // Down: mip[0] ← downsample(HDR), then chain.
            for (int i = 0; i < BloomMipCount; i++)
            {
                var src = (i == 0) ? hdrColorTex : post.BloomMips[i - 1].ColorAttachments[0];
                // Source texel size for the kernel offsets. Source resolution
                // is the previous step's resolution.
                var srcScale = (i == 0) ? 1.0f : (1.0f / MathF.Pow(2.0f, i));
                var srcW = frame.Width * srcScale;
                var srcH = frame.Height * srcScale;
                var srcTexel = new Vector2(1.0f / srcW, 1.0f / srcH);
                var localBloomMip = post.BloomMips[i];
                var localSrc = src;
                var localTexel = srcTexel;
                commandList.Pass(
                    $"walk.bloom.down{i}",
                    new RenderPassDescription(
                        localBloomMip.Handle,
                        ClearColors: new GraphicsColor?[] { new(0.0f, 0.0f, 0.0f, 0.0f) },
                        ClearDepth: false),
                    pass =>
                    {
                        post.BloomDownMaterial.SetTexture("uSrc", localSrc, 0);
                        pass.DrawMesh(post.FullscreenQuad, post.BloomDownMaterial,
                            perDrawUniforms: new ShaderUniform[]
                            {
                                new("uSrcTexel", new Vector2Uniform(localTexel))
                            },
                            perDrawTextures: null);
                    });
            }
            // Up: from coarsest to finest, additively blend up(mip(n+1)) into mip(n).
            // No clear -- the additive blend needs the existing downsample
            // contents to remain so the upsample tap accumulates onto it.
            for (int i = BloomMipCount - 2; i >= 0; i--)
            {
                var srcMip = post.BloomMips[i + 1];
                var srcScale = 1.0f / MathF.Pow(2.0f, i + 2);  // mip i+1's scale
                var srcW = frame.Width * srcScale;
                var srcH = frame.Height * srcScale;
                var srcTexel = new Vector2(1.0f / srcW, 1.0f / srcH);
                var localDstMip = post.BloomMips[i];
                var localSrcTex = srcMip.ColorAttachments[0];
                var localTexel = srcTexel;
                commandList.Pass(
                    $"walk.bloom.up{i}",
                    new RenderPassDescription(
                        localDstMip.Handle,
                        ClearColors: Array.Empty<GraphicsColor?>(),  // keep dst contents
                        ClearDepth: false),
                    pass =>
                    {
                        post.BloomUpMaterial.SetTexture("uSrc", localSrcTex, 0);
                        pass.DrawMesh(post.FullscreenQuad, post.BloomUpMaterial,
                            perDrawUniforms: new ShaderUniform[]
                            {
                                new("uSrcTexel", new Vector2Uniform(localTexel))
                            },
                            perDrawTextures: null);
                    });
            }
        }

        // --- Composite + tonemap pass -----------------------------------
        // Samples the HDR scene buffer + the half-res bloom mip and writes
        // ACES-tonemapped sRGB to the swap chain.
        post.CompositeMaterial.SetTexture("uHdrScene", hdrColorTex, 0);
        post.CompositeMaterial.SetTexture("uBloom",
            bloomEnabled ? post.BloomMips[0].ColorAttachments[0] : hdrColorTex,
            1);
        post.CompositeMaterial.SetTexture("uSsr",
            ssrEnabled ? post.SsrSurface.ColorAttachments[0] : hdrColorTex,
            2);
        var bloomStrengthValue = bloomEnabled ? bloomStrength : 0.0f;
        var ssrStrengthValue = ssrEnabled ? 1.0f : 0.0f;
        commandList.Pass(
            "walk.composite",
            new RenderPassDescription(
                RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new(0.0f, 0.0f, 0.0f, 1.0f) },
                ClearDepth: true),
            pass =>
            {
                pass.DrawMesh(post.FullscreenQuad, post.CompositeMaterial,
                    perDrawUniforms: new ShaderUniform[]
                    {
                        new("uBloomStrength", new FloatUniform(bloomStrengthValue)),
                        new("uSsrStrength", new FloatUniform(ssrStrengthValue)),
                        new("uSsrOnly", new FloatUniform(ssrShowOnly ? 1.0f : 0.0f)),
                        new("uSsrFlipV", new FloatUniform(ssrFlipV ? 1.0f : 0.0f)),
                        new("uTonemapMode", new FloatUniform(tonemapMode)),
                        new("uColorTemp", new Vector3Uniform(colorTemp)),
                        new("uSaturation", new FloatUniform(saturation)),
                        new("uContrast", new FloatUniform(contrast))
                    },
                    perDrawTextures: null);
            });

        DrawHud(time, frame, commandList);
    }

    // Light view-projection from the current sun direction. Eye sits opposite
    // Legacy single-frustum light VP (kept for the fog pass which still
    // wants one sun matrix). Sized to wrap the whole scene around
    // SceneCenter; the cascade-aware version below is what the lit pass
    // actually consumes.
    private Matrix4x4 ComputeLightViewProjection(Vector3 sunDir)
    {
        var L = Vector3.Normalize(-sunDir);
        var eye = SceneCenter + L * shadowSunDistance;
        var up = MathF.Abs(L.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var view = GraphicsMatrices.CreateLookAt(eye, SceneCenter, up);
        var projection = GraphicsMatrices.CreateOrthographic(
            shadowOrthoExtent * 2.0f, shadowOrthoExtent * 2.0f,
            shadowNearPlane, shadowFarPlane);
        return projection * view;
    }

    // Per-cascade light view-projection -- "stable cascade" formulation.
    // Bounds each slice with a SPHERE (centroid + max-radius) rather than an
    // AABB in light space. The sphere is view-direction-independent: the
    // radius only depends on the slice's near/far distances and the camera
    // FoV, not on how the camera is oriented. That means the ortho extent
    // stays constant as the camera rotates, eliminating the AABB-stretching
    // artefacts that produce wrong-looking shadow projections at oblique
    // angles. Texel-snapping the projection (rounding centroid translation
    // to a whole-texel grid in light space) also stops the shadow edges
    // shimmering as the camera moves -- a property the AABB version had
    // no way to achieve.
    private Matrix4x4 ComputeCascadeLightViewProjection(
        Vector3 sunDir,
        Vector3 cameraPos,
        Quaternion cameraRotation,
        float verticalFov,
        float aspect,
        float nearDist,
        float farDist)
    {
        // 8 view-space frustum corners for the slice.
        float tanHalf = MathF.Tan(verticalFov * 0.5f);
        float nearTop = nearDist * tanHalf, nearRight = nearTop * aspect;
        float farTop  = farDist  * tanHalf, farRight  = farTop  * aspect;
        var cornersView = new Vector3[]
        {
            new(-nearRight, -nearTop, -nearDist),
            new( nearRight, -nearTop, -nearDist),
            new(-nearRight,  nearTop, -nearDist),
            new( nearRight,  nearTop, -nearDist),
            new(-farRight,  -farTop,  -farDist),
            new( farRight,  -farTop,  -farDist),
            new(-farRight,   farTop,  -farDist),
            new( farRight,   farTop,  -farDist),
        };

        // Compute centroid + sphere radius in VIEW SPACE first. The
        // frustum slice's shape is rotation-invariant: it has the same
        // 8 corners at the same relative distances regardless of where
        // the camera points. Running the radius calc on view-space
        // corners gives a value that's truly constant per cascade,
        // unlike the same calc in world space (which floating-point-
        // drifts as the rotated corners shuffle around and breaks the
        // texel snap below, producing visible per-frame swings on
        // small objects).
        var viewCentroid = Vector3.Zero;
        for (int i = 0; i < 8; i++) viewCentroid += cornersView[i];
        viewCentroid /= 8.0f;

        float radius = 0.0f;
        for (int i = 0; i < 8; i++)
        {
            var d = (cornersView[i] - viewCentroid).Length();
            if (d > radius) radius = d;
        }
        radius = MathF.Ceiling(radius);

        // Transform the view-space centroid to world. World corners aren't
        // needed beyond this; the snug-sphere bound has all the info we
        // need.
        var centroid = cameraPos + Vector3.Transform(viewCentroid, cameraRotation);

        var L = Vector3.Normalize(-sunDir);
        var up = MathF.Abs(L.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        // Eye sits behind the centroid in the sun direction, far enough that
        // tall occluders above the slice still get rasterised into the
        // shadow map. shadowSunDistance + radius is a safe bound.
        var eye = centroid + L * (shadowSunDistance + radius);
        var view = GraphicsMatrices.CreateLookAt(eye, centroid, up);

        // Texel snap: round the centroid's light-space position to whole-texel
        // increments so the shadow texel grid stays aligned with the world
        // grid as the camera moves. Without this, edges of shadow occluders
        // would shimmer pixel-by-pixel under tiny camera motions.
        var centroidLight = GraphicsMatrices.TransformPoint(view, centroid);
        float texelSize = (2.0f * radius) / ShadowMapSize;
        centroidLight.X = MathF.Round(centroidLight.X / texelSize) * texelSize;
        centroidLight.Y = MathF.Round(centroidLight.Y / texelSize) * texelSize;
        // Reconstruct snapped centroid in world; build a fresh view from it.
        var snappedCentroid = GraphicsMatrices.TransformPoint(InvertOrIdentity(view), centroidLight);
        eye = snappedCentroid + L * (shadowSunDistance + radius);
        view = GraphicsMatrices.CreateLookAt(eye, snappedCentroid, up);

        // Square ortho sized to the slice's sphere. Far plane goes well past
        // the centroid so anything behind the slice (in light-Z) still casts.
        var projection = GraphicsMatrices.CreateOrthographic(
            2.0f * radius, 2.0f * radius,
            nearPlane: 0.1f,
            farPlane: 2.0f * (shadowSunDistance + radius) + 60.0f);
        return projection * view;
    }

    private static Matrix4x4 InvertOrIdentity(Matrix4x4 m)
    {
        return Matrix4x4.Invert(m, out var inv) ? inv : Matrix4x4.Identity;
    }

    // Resolves a (vert, frag) pair under Shaders/ into a preprocessed
    // ShaderSources. Library includes resolve next to the .frag itself
    // (Shaders/lib/* lands next to the .frag at build time -- see csproj),
    // so callers just write `#include "lib/tonemap.glsl"` with no extra
    // search-path config. Use this in place of raw File.ReadAllText calls
    // so the GLSL preprocessor + source-map plumbing kicks in.
    private static ShaderSources LoadShader(string vertName, string fragName)
    {
        var shadersDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        return ShaderLoader.LoadVertexFragment(
            Path.Combine(shadersDir, vertName),
            Path.Combine(shadersDir, fragName));
    }

    // Spherical-coords sun direction. Yaw = azimuth around +Y axis, pitch =
    // altitude (positive = up). Returned vector points FROM the sun INTO the
    // scene, so a yaw=0 pitch=-pi/2 means a sun directly overhead pointing
    // straight down.
    private static Vector3 SunDirectionFromYawPitch(float yawRad, float pitchRad)
    {
        var cp = MathF.Cos(pitchRad);
        return Vector3.Normalize(new Vector3(
            cp * MathF.Cos(yawRad),
            MathF.Sin(pitchRad),
            cp * MathF.Sin(yawRad)));
    }

    // Has any tracked point-light position drifted from its last-baked value?
    // Also true if we haven't baked yet, or the active shadow count changed.
    private bool PointShadowsDirty(int shadowCount)
    {
        if (!pointShadowsBaked) return true;
        if (bakedShadowLightPositions.Length != shadowCount) return true;
        for (var i = 0; i < shadowCount; i++)
        {
            var diff = bakedShadowLightPositions[i] - pointLightPositions[i];
            if (diff.LengthSquared() > 1e-6f) return true;
        }
        return false;
    }

    // Re-bake the cube shadow maps for the first `shadowCount` point lights.
    // For each light, render six depth-only passes (one per cube face) at
    // 90-degree perspective into PointShadowFaceSize^2 surfaces. shadow_cube.frag
    // writes linear distance-to-light so the lit shader's samplerCubeShadow
    // compare matches regardless of face.
    private void BakePointShadows(RenderCommandList commandList, int shadowCount)
    {
        if (shadowCount <= 0)
        {
            // Even with zero shadow-casters, mark baked so we don't spin on
            // dirty checks. The cube textures retain whatever was there.
            bakedShadowLightPositions = Array.Empty<Vector3>();
            pointShadowsBaked = true;
            return;
        }

        var projection = GraphicsMatrices.CreatePerspective(
            MathF.PI * 0.5f, 1.0f, 0.1f, pointShadowFarPlane);

        // OpenGL cubemap face order: +X, -X, +Y, -Y, +Z, -Z. Y-axis faces use
        // a Z-axis "up" because the cube's vertical axis is the look direction.
        Span<Vector3> faceForward = stackalloc Vector3[]
        {
            new( 1.0f,  0.0f,  0.0f),
            new(-1.0f,  0.0f,  0.0f),
            new( 0.0f,  1.0f,  0.0f),
            new( 0.0f, -1.0f,  0.0f),
            new( 0.0f,  0.0f,  1.0f),
            new( 0.0f,  0.0f, -1.0f),
        };
        Span<Vector3> faceUp = stackalloc Vector3[]
        {
            new(0.0f, -1.0f,  0.0f),
            new(0.0f, -1.0f,  0.0f),
            new(0.0f,  0.0f,  1.0f),
            new(0.0f,  0.0f, -1.0f),
            new(0.0f, -1.0f,  0.0f),
            new(0.0f, -1.0f,  0.0f),
        };

        for (var li = 0; li < shadowCount; li++)
        {
            var lightPos = pointLightPositions[li];
            for (var face = 0; face < 6; face++)
            {
                var faceView = GraphicsMatrices.CreateLookAt(
                    lightPos, lightPos + faceForward[face], faceUp[face]);
                var faceVP = projection * faceView;
                var surface = pointShadowSurfaces[li][face];
                commandList.Pass(
                    $"walk.pshadow.l{li}.f{face}",
                    new RenderPassDescription(
                        surface.Handle,
                        ClearColors: Array.Empty<GraphicsColor?>(),
                        ClearDepth: true),
                    pass => pbrRenderer.DrawCubeShadowFace(
                        pass, scene, faceVP, lightPos, pointShadowFarPlane));
            }
        }

        bakedShadowLightPositions = new Vector3[shadowCount];
        for (var i = 0; i < shadowCount; i++) bakedShadowLightPositions[i] = pointLightPositions[i];
        pointShadowsBaked = true;
    }

    // Re-bake the environment probe from the current sun direction and
    // rebind every material's IBL slot. Called by the debug "Rebake Sky"
    // button and by time-of-day presets. No-op for HDR sources (the HDR is
    // authoritative). Old probe handles are left to leak -- this is a debug
    // action; the engine doesn't expose public texture-delete on the device.
    private void RebakeSky()
    {
        if (envProfile.Source is HdrEnvironmentSource) return;
        bakedSunDirection = sunDirection;
        envProfile = envProfile with { Source = new ProceduralEnvironmentSource(bakedSunDirection) };
        Console.WriteLine("Rebaking environment probe...");
        envProbe = EnvironmentBaker.Bake(GraphicsDevice, envProfile, "walk");
        foreach (var mat in scene.Materials.All)
        {
            mat.SetTexture("uEnvMap", envProbe.EnvCubemap, 5);
            mat.SetTexture("uDiffuseIrradiance", envProbe.DiffuseIrradiance, 10);
            mat.SetTexture("uSpecularPrefilter", envProbe.PrefilteredSpecular, 11);
            mat.SetUniform("uEnvMapMipCount", new FloatUniform((float)envProbe.EnvCubeMipCount));
        }
        skyboxMaterial.SetTexture("uEnvMap", envProbe.EnvCubemap, 0);
    }

    public void Debug(DebugContext debug)
    {
        debug.State.Enabled = ShowDebug;
        debug.State.ShowOverlay = ShowDebug;
        debug.State.ShowDebugDraw = ShowDebug;

        using (debug.Scope("Frame"))
        {
            debug.Values.Value("FPS", $"{fpsSmoothed:0}");
            debug.Values.Value("Position", camera.Transform.Position);
            debug.Values.Value("Yaw deg", yaw * 180.0f / MathF.PI);
            debug.Values.Value("Pitch deg", pitch * 180.0f / MathF.PI);
            debug.Values.Value("Submeshes", scene.Submeshes.Count);
            debug.Values.Value("Materials", scene.Materials.All.Count);
        }

        using (debug.Scope("Camera"))
        {
            cameraFov = debug.Controls.Float("FoV (rad)", cameraFov, MathF.PI / 8.0f, MathF.PI / 1.5f);
            cameraNearPlane = debug.Controls.Float("Near", cameraNearPlane, 0.01f, 1.0f);
            cameraFarPlane = debug.Controls.Float("Far", cameraFarPlane, 20.0f, 500.0f);
        }

        using (debug.Scope("Controls"))
        {
            walkSpeed = debug.Controls.Float("Walk speed", walkSpeed, 0.5f, 20.0f);
            sprintMultiplier = debug.Controls.Float("Sprint x", sprintMultiplier, 1.0f, 10.0f);
            mouseLookSensitivity = debug.Controls.Float("Mouse sens", mouseLookSensitivity, 0.0005f, 0.01f);
        }

        using (debug.Scope("Presets"))
        {
            // One-click cinematic lighting: low warm golden-hour sun raking
            // down the atrium's long axis, deeper shadows (IBL diffuse cut),
            // brighter lanterns so they compete with the sun, slightly
            // underexposed for richer blacks. Triggers a sky rebake so IBL
            // reflections track the new sun position.
            if (debug.Controls.Button("Golden hour (dramatic)"))
            {
                sunYaw = 0.15f;
                sunPitch = -0.32f;
                sunStrength = 4.2f;
                iblDiffuseBoost = 0.35f;
                iblSpecAttenuation = 0.55f;
                indirectShadowBase = 0.45f;
                indirectShadowRange = 0.55f;
                exposure = 0.85f;
                emissiveBoost = 3.5f;
                pointLightIntensity = 22.0f;
                pointLightRange = 8.0f;
                pointLightColor = new Vector3(1.0f, 0.48f, 0.18f);
                skyTint = new Vector3(1.0f, 0.85f, 0.65f);   // warm sunset wash
                horizonFadeStrength = 0.85f;
                rebakeRequested = true;
            }
            // Night: sun is essentially extinguished and the sky is a deep
            // moonlit blue. Lanterns become the dominant light source --
            // intensity and range crank up, IBL contribution collapses,
            // exposure floats up to read the dim scene without losing the
            // lanterns' HDR core.
            if (debug.Controls.Button("Night (lanterns dominate)"))
            {
                sunYaw = 0.20f;
                sunPitch = -0.95f;                            // sun below horizon
                sunStrength = 0.12f;                          // hint of moonlight
                iblDiffuseBoost = 0.18f;
                iblSpecAttenuation = 0.95f;
                indirectShadowBase = 0.20f;
                indirectShadowRange = 0.55f;
                exposure = 1.30f;
                emissiveBoost = 6.0f;
                pointLightIntensity = 38.0f;
                pointLightRange = 10.5f;
                pointLightColor = new Vector3(1.0f, 0.50f, 0.20f);
                skyTint = new Vector3(0.04f, 0.07f, 0.16f);  // deep moonlit blue
                horizonFadeStrength = 0.85f;
                rebakeRequested = true;
            }
            // Storm: heavy overcast. Sun is occluded -> diffuse sky-light
            // only, cool-gray cast. No specular sparkle (atten cranked up),
            // emissives muted, lanterns just barely glow. Reads cold and
            // moody.
            if (debug.Controls.Button("Storm (cold overcast)"))
            {
                sunYaw = -0.40f;
                sunPitch = -0.55f;
                sunStrength = 1.10f;                          // diffuse-only feel
                iblDiffuseBoost = 1.50f;                      // sky is the light
                iblSpecAttenuation = 1.00f;
                indirectShadowBase = 0.65f;
                indirectShadowRange = 0.35f;
                exposure = 0.75f;
                emissiveBoost = 1.80f;
                pointLightIntensity = 12.0f;
                pointLightRange = 5.5f;
                pointLightColor = new Vector3(0.95f, 0.60f, 0.35f);
                skyTint = new Vector3(0.55f, 0.60f, 0.65f);  // cool desaturated
                horizonFadeStrength = 0.85f;
                rebakeRequested = true;
            }
            if (debug.Controls.Button("Reset (midday default)"))
            {
                sunYaw = MathF.Atan2(-0.30f, -0.45f);
                sunPitch = MathF.Asin(-0.85f);
                sunStrength = 3.2f;
                iblDiffuseBoost = 1.0f;
                iblSpecAttenuation = 0.8f;
                indirectShadowBase = 0.60f;
                indirectShadowRange = 0.40f;
                exposure = 1.0f;
                emissiveBoost = 2.5f;
                pointLightIntensity = 10.0f;
                pointLightRange = 6.5f;
                pointLightColor = new Vector3(1.0f, 0.55f, 0.25f);
                skyTint = Vector3.One;
                horizonFadeStrength = 0.85f;
                rebakeRequested = true;
            }
        }

        using (debug.Scope("Sun"))
        {
            // Yaw is wrapped; pitch clamped to keep the sun above the horizon
            // bias the cubemap was tuned for. Direct lighting follows live;
            // IBL only updates on Rebake Sky.
            sunYaw = debug.Controls.Float("Yaw (rad)", sunYaw, -MathF.PI, MathF.PI);
            sunPitch = debug.Controls.Float("Pitch (rad)", sunPitch, -MathF.PI / 2.0f + 0.1f, -0.05f);
            sunStrength = debug.Controls.Float("Strength", sunStrength, 0.0f, 12.0f);
            debug.Values.Value("Live dir", sunDirection);
            debug.Values.Value("Baked dir", bakedSunDirection);
            if (debug.Controls.Button("Rebake Sky"))
            {
                rebakeRequested = true;
            }
            // With HDR loaded, the IBL probes and visible sky are baked from
            // the HDR's actual sun position; the slider sun is independent
            // and drifts out of alignment once touched. This button snaps
            // the sliders back to the HDR-extracted direction so direct
            // light + visible sky agree again.
            if (hdrSunDirectionFromEquirect.HasValue
                && debug.Controls.Button("Sync sun to HDR"))
            {
                var d = hdrSunDirectionFromEquirect.Value;
                sunDirection = d;
                sunPitch = MathF.Asin(Math.Clamp(d.Y, -1.0f, 1.0f));
                sunYaw = MathF.Atan2(d.Z, d.X);
            }
        }

        using (debug.Scope("Shadow"))
        {
            shadowOrthoExtent = debug.Controls.Float("Ortho extent", shadowOrthoExtent, 4.0f, 60.0f);
            shadowSunDistance = debug.Controls.Float("Sun distance", shadowSunDistance, 5.0f, 80.0f);
            shadowNearPlane = debug.Controls.Float("Near", shadowNearPlane, 0.01f, 1.0f);
            shadowFarPlane = debug.Controls.Float("Far", shadowFarPlane, 10.0f, 200.0f);
            visualizeCascades = debug.Controls.Toggle("Visualize CSM (R/G/B by cascade)", visualizeCascades);
            disableCascades = debug.Controls.Toggle("Disable cascades (single VP fallback)", disableCascades);
        }

        using (debug.Scope("Tonemap"))
        {
            exposure = debug.Controls.Float("Exposure", exposure, 0.1f, 4.0f);
            bloomEnabled = debug.Controls.Toggle("Bloom", bloomEnabled);
            bloomStrength = debug.Controls.Float("Bloom strength", bloomStrength, 0.0f, 0.5f);
            tonemapMode = debug.Controls.Enum("Operator", tonemapMode,
                new[] { "ACES", "AgX", "Reinhard", "Neutral" });
            saturation = debug.Controls.Float("Saturation", saturation, 0.0f, 2.0f);
            contrast = debug.Controls.Float("Contrast", contrast, 0.5f, 2.0f);
            colorTemp.X = debug.Controls.Float("Temp R", colorTemp.X, 0.5f, 1.5f);
            colorTemp.Y = debug.Controls.Float("Temp G", colorTemp.Y, 0.5f, 1.5f);
            colorTemp.Z = debug.Controls.Float("Temp B", colorTemp.Z, 0.5f, 1.5f);
        }

        using (debug.Scope("SSR"))
        {
            ssrEnabled = debug.Controls.Toggle("Enabled", ssrEnabled);
            ssrShowOnly = debug.Controls.Toggle("Show SSR only", ssrShowOnly);
            ssrFlipV = debug.Controls.Toggle("Flip V", ssrFlipV);
            ssrIntensity = debug.Controls.Float("Intensity", ssrIntensity, 0.0f, 3.0f);
            ssrMaxDistance = debug.Controls.Float("Max distance", ssrMaxDistance, 1.0f, 80.0f);
            ssrSteps = debug.Controls.Float("Steps", ssrSteps, 4.0f, 64.0f);
            ssrThickness = debug.Controls.Float("Hit thickness (NDC.z)", ssrThickness, 0.0005f, 0.05f);
            ssrRoughnessCutoff = debug.Controls.Float("Roughness cutoff", ssrRoughnessCutoff, 0.0f, 1.0f);
        }

        using (debug.Scope("Fog"))
        {
            fogEnabled = debug.Controls.Toggle("Enabled", fogEnabled);
            fogDensity = debug.Controls.Float("Density", fogDensity, 0.0f, 0.2f);
            fogScatter = debug.Controls.Float("Scatter strength (sun)", fogScatter, 0.0f, 1.0f);
            fogPointScatter = debug.Controls.Float("Scatter strength (point)", fogPointScatter, 0.0f, 2.0f);
            fogSteps = debug.Controls.Float("Steps", fogSteps, 8.0f, 64.0f);
            fogMaxDistance = debug.Controls.Float("Max distance", fogMaxDistance, 5.0f, 100.0f);
        }

        using (debug.Scope("IBL"))
        {
            iblSpecAttenuation = debug.Controls.Float("Spec atten", iblSpecAttenuation, 0.0f, 1.5f);
            iblDiffuseBoost = debug.Controls.Float("Diffuse boost", iblDiffuseBoost, 0.0f, 4.0f);
            metalFloor = debug.Controls.Float("Metal floor", metalFloor, 0.0f, 1.0f);
            indirectShadowBase = debug.Controls.Float("Shadow base", indirectShadowBase, 0.0f, 1.0f);
            indirectShadowRange = debug.Controls.Float("Shadow range", indirectShadowRange, 0.0f, 1.0f);
            horizonFadeStrength = debug.Controls.Float("Horizon fade", horizonFadeStrength, 0.0f, 1.0f);
            horizonFadeStart = debug.Controls.Float("Horizon fade start", horizonFadeStart, 0.01f, 0.5f);
            debug.Values.Value("Env mip count", envProbe.EnvCubeMipCount);
            debug.Values.Value("Prefilter mips", envProbe.PrefilteredSpecularMipCount);
        }

        using (debug.Scope("Emissive"))
        {
            emissiveBoost = debug.Controls.Float("Boost", emissiveBoost, 0.0f, 10.0f);
        }

        using (debug.Scope("Lights"))
        {
            pointLightsEnabled = debug.Controls.Toggle("Enabled", pointLightsEnabled);
            pointLightIntensity = debug.Controls.Float("Intensity", pointLightIntensity, 0.0f, 80.0f);
            pointLightRange = debug.Controls.Float("Range", pointLightRange, 1.0f, 20.0f);
            pointLightFlickerAmount = debug.Controls.Float("Flicker amount", pointLightFlickerAmount, 0.0f, 0.6f);
            pointLightFlickerSpeed = debug.Controls.Float("Flicker speed", pointLightFlickerSpeed, 0.1f, 4.0f);
            pointLightSpecScale = debug.Controls.Float("Specular scale", pointLightSpecScale, 0.0f, 2.0f);
            // Warmth as RGB sliders — cheap; a color-temperature mapping
            // is a future polish if we want fewer knobs.
            pointLightColor.X = debug.Controls.Float("R", pointLightColor.X, 0.0f, 1.0f);
            pointLightColor.Y = debug.Controls.Float("G", pointLightColor.Y, 0.0f, 1.0f);
            pointLightColor.Z = debug.Controls.Float("B", pointLightColor.Z, 0.0f, 1.0f);
            // Per-light Y so we can dial chain height. Positions x/z are
            // baked defaults — easy to add sliders if we need them.
            for (var i = 0; i < pointLightPositions.Length; i++)
            {
                var p = pointLightPositions[i];
                p.Y = debug.Controls.Float($"L{i} Y", p.Y, 0.5f, 10.0f);
                pointLightPositions[i] = p;
            }
            pointShadowFarPlane = debug.Controls.Float("Shadow far", pointShadowFarPlane, 2.0f, 30.0f);
            pointShadowBias = debug.Controls.Float("Shadow bias", pointShadowBias, 0.0f, 0.05f);
            pointShadowFilterRadius = debug.Controls.Float("Shadow PCF radius", pointShadowFilterRadius, 0.0f, 0.3f);
            if (debug.Controls.Button("Rebake Shadows"))
            {
                pointShadowsBaked = false;
            }
            flamesEnabled = debug.Controls.Toggle("Flames", flamesEnabled);
            useFlameAtlas = debug.Controls.Toggle("Atlas (vs procedural)", useFlameAtlas);
            flameSize = debug.Controls.Float("Flame size", flameSize, 0.05f, 1.5f);
            flameIntensity = debug.Controls.Float("Flame brightness", flameIntensity, 0.1f, 8.0f);
            flameYOffset = debug.Controls.Float("Flame Y offset", flameYOffset, -0.5f, 0.5f);
            flameAtlasFps = debug.Controls.Float("Atlas fps", flameAtlasFps, 5.0f, 60.0f);
            flameAtlasSingleRow = debug.Controls.Toggle("Atlas single row (row 0)", flameAtlasSingleRow);
            // Volumetric flame (phase A: procedural noise ray-march).
            // Overrides the quad path entirely when on.
            useFlameVolume = debug.Controls.Toggle("Volume flame (override)", useFlameVolume);
            volumeVdbWeight = debug.Controls.Float("VDB base weight", volumeVdbWeight, 0.0f, 3.0f);
            volumeProcWeight = debug.Controls.Float("Procedural tongue weight", volumeProcWeight, 0.0f, 3.0f);
            volumeSize = debug.Controls.Float("Volume size", volumeSize, 0.2f, 2.0f);
            volumeSteps = debug.Controls.Float("Volume steps", volumeSteps, 8.0f, 64.0f);
            volumeDensity = debug.Controls.Float("Volume density", volumeDensity, 0.2f, 24.0f);
            volumeTempBoost = debug.Controls.Float("Volume temp boost (VDB)", volumeTempBoost, 0.5f, 8.0f);
            volumeRise = debug.Controls.Float("Volume rise (procedural)", volumeRise, 0.0f, 3.0f);
            volumeFps = debug.Controls.Float("Volume fps (VDB)", volumeFps, 4.0f, 60.0f);
            volumeIntensity = debug.Controls.Float("Volume intensity", volumeIntensity, 0.1f, 6.0f);
            debug.Values.Value("VDB frames", volumeVdbFrames);
        }

        using (debug.Scope("Floor"))
        {
            // The marble floor's authored roughness is ~0.66 — too matte to
            // catch reflections. Override toggles MR texture sampling off on
            // detected floor prims and uses these factors directly.
            floorOverrideEnabled = debug.Controls.Toggle("Override", floorOverrideEnabled);
            floorVisualize = debug.Controls.Toggle("Visualize (magenta)", floorVisualize);
            floorRoughness = debug.Controls.Float("Roughness", floorRoughness, 0.0f, 1.0f);
            floorMetallic = debug.Controls.Float("Metallic", floorMetallic, 0.0f, 1.0f);
            debug.Values.Value("Tagged prims", floorIndices.Count);
        }
    }

    private void DrawHud(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        if (hudFont is null) return;
        var (logicalW, logicalH) = Host.LogicalSize;
        if (logicalW <= 0 || logicalH <= 0) return;
        var dpiScale = frame.Width / (float)logicalW;
        var ortho = GraphicsMatrices.CreateOrthographicOffCenter(
            0, logicalW, logicalH, 0, -1, 1);

        commandList.Pass(
            "walk.hud",
            new RenderPassDescription(
                RenderSurfaceHandle.Default,
                ClearColors: Array.Empty<GraphicsColor?>(),
                ClearDepth: false),
            pass =>
            {
                spriteBatch.Begin(ortho);

                var pos = camera.Transform.Position;
                var info =
                    $"FPS  {fpsSmoothed:0}\n" +
                    $"POS  {pos.X:0.0} {pos.Y:0.0} {pos.Z:0.0}";
                spriteBatch.DrawText(hudFont, 18.0f, info,
                    new Vector2(20, 20),
                    new GraphicsColor(0.85f, 0.90f, 1.00f, 0.85f),
                    dpiScale: dpiScale);

                const string controls = "WASD MOVE   SPACE/CTRL UP/DOWN   CMD SPRINT   C RELEASE MOUSE   ESC";
                const float controlsSize = 14.0f;
                var cm = SpriteBatchUiExtensions.MeasureText(hudFont, controlsSize, controls, dpiScale);
                spriteBatch.DrawText(hudFont, controlsSize, controls,
                    new Vector2(logicalW * 0.5f - cm.X * 0.5f, logicalH - 28.0f),
                    new GraphicsColor(0.55f, 0.65f, 0.85f, 0.70f),
                    dpiScale: dpiScale);

                spriteBatch.End(pass);
            });
    }
}
