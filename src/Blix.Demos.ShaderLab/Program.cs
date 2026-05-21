using Blix.Assets;
using Blix.Audio;
using Blix.Core;
using Blix.Diagnostics;
using Blix;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Images;
using Blix.Graphics.Primitives;
using Blix.Render;
using Blix.Runtime.OpenTK;
using System.Numerics;
using PlaneMesh = Blix.Graphics.Primitives.PlaneMesh;
// Disambiguate from System.Numerics.Plane (same name, different type).
using Plane = Blix.Geometry.Plane;

using var window = new Window(new ShaderLabGame());
window.Run();

internal sealed class ShaderLabGame : Game, IInputHandler, IDebuggable
{
    private const int PresentModeColor = 0;
    private const int PresentModeSceneColor = 1;
    private const int PresentModeOpaqueColor = 2;
    private const int PresentModeLuminance = 3;
    private const int PresentModeNormals = 4;
    private const int PresentModeDepth = 5;
    private const int PresentModeShadowMap = 6;
    private const int PresentModeBloom0 = 7;
    private const int PresentModeBloom1 = 8;
    private const int PresentModeBloom2 = 9;
    private const int PresentModeCount = 10;
    private static readonly string[] PresentModeLabels =
    [
        "final",
        "scene",
        "opaque",
        "luminance",
        "normals",
        "depth",
        "shadowmap",
        "bloom0",
        "bloom1",
        "bloom2"
    ];

    private const int ShadowMapResolution = 2048;
    private const float CameraMoveSpeed = 3.0f;
    private const float CameraLookSpeed = 1.4f;
    private const float BrightnessMin = 0.0f;
    private const float BrightnessMax = 3.0f;
    private const float ExposureMin = 0.1f;
    private const float ExposureMax = 3.0f;
    private const float BloomMin = 0.0f;
    private const float BloomMax = 2.0f;
    private const float GlassThicknessMin = 0.0f;
    private const float GlassThicknessMax = 0.5f;
    private const float GlassF0Min = 0.0f;
    private const float GlassF0Max = 0.3f;
    private const float FurShellCountMin = 6.0f;
    private const float FurShellCountMax = 36.0f;
    private const float FurLengthMin = 0.0f;
    private const float FurLengthMax = 0.45f;
    private const float FurDensityMin = 0.2f;
    private const float FurDensityMax = 1.0f;
    private const float FurWindMin = 0.0f;
    private const float FurWindMax = 0.35f;
    private const float HologramOpacityMin = 0.05f;
    private const float HologramOpacityMax = 1.0f;
    private const float HologramRimMin = 0.0f;
    private const float HologramRimMax = 4.0f;
    private const float HologramScanlineMin = 8.0f;
    private const float HologramScanlineMax = 80.0f;
    private const float HologramGlitchMin = 0.0f;
    private const float HologramGlitchMax = 1.0f;
    // Camera mouse-look: rad/pixel applied to yaw/pitch when LMB is NOT held.
    private const float MouseLookSensitivity = 0.004f;
    // Light drag: rad/pixel applied while LMB is held. A bit lower than camera so the
    // light feels weightier to move - keeping a directional light steady matters more
    // than reaching the same yaw rate.
    private const float LightDragSensitivity = 0.0025f;

    // Initial state, recoverable via the R hotkey when the camera or light gets lost.
    private static readonly Vector3 InitialCameraPosition = new(0.15f, 0.75f, 4.0f);
    private const float InitialCameraYaw = -0.08f;
    private const float InitialCameraPitch = -0.16f;
    private static readonly Vector3 InitialLightDirection =
        Vector3.Normalize(new Vector3(-0.55f, 0.72f, 0.35f));
    private const float InitialAmbientBoost = 0.62f;
    private const float InitialSkyboxIntensity = 1.25f;
    private const float InitialLightIntensity = 1.0f;
    private const float InitialBloomStrength = 0.4f;
    private const float InitialExposure = 1.0f;
    private const float InitialGlassThickness = 0.22f;
    private const float InitialGlassF0 = 0.07f;
    private const float InitialFurShellCount = 18.0f;
    private const float InitialFurLength = 0.22f;
    private const float InitialFurDensity = 0.72f;
    private const float InitialFurWind = 0.09f;
    private const float InitialHologramOpacity = 0.42f;
    private const float InitialHologramRim = 1.65f;
    private const float InitialHologramScanlines = 36.0f;
    private const float InitialHologramGlitch = 0.18f;

    // mainLight + mainCamera are demo fields. Mounting them on Game (e.g.
    // `protected Camera3D? MainCamera { get; set; }`) is a follow-on once a second
    // game proves the shape — for now the convention lives by example.
    private readonly DirectionalLight mainLight = new()
    {
        Direction = InitialLightDirection,
        Intensity = InitialLightIntensity,
    };

    // A small set of additional lights to demonstrate the multi-light
    // path. Sun stays the only shadow caster; these add unshadowed fill.
    //   - Warm orange point near the bunny (mimics a hearth/firelight)
    //   - Cool blue point above the floor centre (mimics moon spillover)
    //   - Magenta spot from above CesiumMan (mimics a stage spot)
    // Tuned for the LDR pipeline -- intensities are larger than a "physical" 1.0
    // because the engine doesn't HDR-tonemap.
    private readonly List<PointLight> pointLights = new()
    {
        new PointLight
        {
            Position = new Vector3(1.4f, 0.2f, 0.5f),
            Color = new Vector3(1.0f, 0.55f, 0.25f),   // warm orange
            Intensity = 8.0f,
            Range = 4.0f,
            CastsShadow = true,                        // Cubemap shadow
        },
        new PointLight
        {
            Position = new Vector3(0.0f, 1.4f, -1.5f),
            Color = new Vector3(0.4f, 0.6f, 1.0f),     // cool blue
            Intensity = 6.0f,
            Range = 5.0f,
        },
    };
    private readonly List<SpotLight> spotLights = new()
    {
        new SpotLight
        {
            Position = new Vector3(-2.5f, 1.8f, 0.5f),
            Direction = new Vector3(0.0f, -1.0f, 0.0f),
            Color = new Vector3(0.9f, 0.5f, 0.95f),     // magenta-pink
            Intensity = 18.0f,
            Range = 6.0f,
            InnerConeAngle = MathF.PI / 9.0f,           // 20 deg
            OuterConeAngle = MathF.PI / 6.0f,           // 30 deg
            CastsShadow = true,                         // Shadow caster
        },
    };
    // Shadow caster caps — must match the shader's kMaxSpotShadowCasters and
    // kMaxPointShadowCasters in pbr_core.glsl. Surfaces are allocated for every
    // slot up to the cap regardless of how many casters the demo actually uses;
    // uSpotShadowCount / uPointShadowCount tell the shader which slots are live.
    private const int MaxSpotShadowCasters = 4;
    private const int MaxPointShadowCasters = 2;
    private const int SpotShadowMapSize = 1024;
    private const int PointShadowFaceSize = 512;
    // Spot shadow surfaces + their depth textures. Each surface is its own 1024^2
    // depth target; per-frame rendering loops once per active caster and writes
    // into the matching surface. Inactive slots keep stale depth — fine because
    // the shader gates sampling on uSpotShadowCount.
    private readonly RenderSurface?[] spotShadowSurfaces = new RenderSurface?[MaxSpotShadowCasters];
    private readonly TextureHandle[] spotShadowTextures = new TextureHandle[MaxSpotShadowCasters];
    // Point-light depth cubemaps + their six face surfaces. Indexed
    // [casterIndex][face]. Same allocate-all / render-only-active pattern as the
    // spot arrays above.
    private readonly TextureHandle[] pointShadowCubes = new TextureHandle[MaxPointShadowCasters];
    private readonly RenderSurface?[,] pointShadowFaceSurfaces = new RenderSurface?[MaxPointShadowCasters, 6];
    // Live multipliers driven from the debug UI. 1.0 = the tuned default for each
    // control; ranges are clamped to their corresponding min/max values.
    private float ambientBoost = InitialAmbientBoost;
    private float skyboxIntensity = InitialSkyboxIntensity;
    private float bloomStrength = InitialBloomStrength;
    private float exposure = InitialExposure;
    private float glassThickness = InitialGlassThickness;
    private float glassF0 = InitialGlassF0;
    private float furShellCount = InitialFurShellCount;
    private float furLength = InitialFurLength;
    private float furDensity = InitialFurDensity;
    private float furWind = InitialFurWind;
    private float hologramOpacity = InitialHologramOpacity;
    private float hologramRim = InitialHologramRim;
    private float hologramScanlines = InitialHologramScanlines;
    private float hologramGlitch = InitialHologramGlitch;
    private float cameraYaw = InitialCameraYaw;
    private float cameraPitch = InitialCameraPitch;
    private readonly HashSet<Key> heldKeys = new();
    private bool leftMouseDown;

    private Mesh cubeMesh = null!;
    private Mesh planeMesh = null!;
    private Mesh torusMesh = null!;
    private Mesh cylinderMesh = null!;
    private Mesh icosphereMesh = null!;
    private Mesh torusKnotMesh = null!;
    private Mesh capsuleMesh = null!;
    private Mesh teapotMesh = null!;
    private Mesh bunnyMesh = null!;
    private Mesh suzanneMesh = null!;
    private Mesh fullscreenMesh = null!;
    private Material shadowMaterial = null!;
    // Per-object lit materials. Identity (specular/shininess/reflectance/normalScale +
    // diffuse + normal map) lives in JSON under Assets/materials; the demo binds runtime-
    // only textures (uShadowMap, uEnvMap, fallback uNormalMap) post-resolve.
    private Material heroCubeMaterial = null!;
    private Material stoneFloorMaterial = null!;
    private Material fabricMaterial = null!;
    private Material uvGridMaterial = null!;
    private Material paintedMetalMaterial = null!;
    private Material marbleMaterial = null!;
    private Material woodMaterial = null!;
    private Material leatherMaterial = null!;
    private Material rustyMetalMaterial = null!;
    private Material concreteMaterial = null!;
    private Material presentColorMaterial = null!;
    private Material presentDepthMaterial = null!;
    private Material presentShadowMapMaterial = null!;
    private Material brightMaterial = null!;
    private Material blurMaterial = null!;
    private Material bloomCompositeMaterial = null!;
    private Material skyboxMaterial = null!;
    private Material copyMaterial = null!;
    private Material glassMaterial = null!;
    private Material furMaterial = null!;
    private Material hologramMaterial = null!;
    private Camera3D mainCamera = null!;
    // Procedural UV-grid texture is bound onto the torus material post-resolve; it is
    // not on disk so it can't be referenced via AssetId.
    private TextureHandle uvGridTexture;
    private TextureHandle furNoiseTexture;
    private TextureHandle environmentCubemap;
    // Flat tangent-space normal (R=128, G=128, B=255 -> decodes to (0, 0, 1)). Bound as
    // the material's uNormalMap default so objects without an authored normal map sample
    // back-to-surface-normal, regardless of uNormalScale.
    private TextureHandle flatNormalTexture;
    private TextureHandle neutralMetallicRoughnessTexture;

    // Scene composition: lists of GameObjects, separated by which pass renders them.
    // Opaque list feeds both the shadow pass and the scene pass; glass list feeds the
    // refractive pass that runs after the scene snapshot. The Blix layer deliberately
    // leaves pass routing to the demo.
    private readonly List<GameObject> opaqueObjects = new();
    private readonly List<GameObject> furObjects = new();
    private readonly List<GameObject> hologramObjects = new();
    private readonly List<GameObject> glassObjects = new();

    // Skinned game objects — the same compositional shape as opaqueObjects /
    // dropCubes, just with per-object Skeleton + Pose + Palette + AnimationHost.
    // OnUpdate ticks each one (reset pose to rest, sample animations, recompute
    // palette); the scene pass draws each with the skin pipeline + the per-object
    // bone-palette uniform pulled from SkinnedGameObject.Palette.
    private readonly List<SkinnedGameObject> skinnedObjects = new();
    // Fox's animation blend handle. OnUpdate auto-oscillates Weight
    // between 0 and 1 to demonstrate seamless crossfade between Walk and Run.
    private BlendedClipAnimation foxBlend = null!;
    // Shared shadow caster for skinned content. One material instance
    // across all skinned objects (the same pattern as the static shadowMaterial)
    // because the shadow pass only cares about depth — material-specific
    // properties don't apply.
    private Material skinShadowMaterial = null!;

    // Three dropping cubes against the static floor. Each ticks its physics at the
    // engine's fixed step (60Hz); ResolveCollisions runs once per fixed step and
    // queries the CollisionWorld3D for what each cube might be touching. The visual
    // `floor` GameObject is kept for rendering; its collider lives in the world as
    // an infinite Plane.
    private readonly List<PhysicsGameObject> dropCubes = new();
    private GameObject floor = null!;
    // The collision world holds every static collider. For this demo it's just the
    // floor plane, but the indirection (a) centralises iteration and (b) lets
    // ResolveCollisions stop knowing about specific collider identities.
    private readonly CollisionWorld3D<GameObject> collisionWorld = new();
    // Reused overlap-query buffer so the per-frame collision loop allocates zero.
    private readonly List<CollisionContact3D<GameObject>> contactScratch = new();

    // Picking state. Mouse position and viewport dimensions are tracked so right-click
    // can build a screen-space ray, raycast against the world + drop cubes, and store
    // the closest hit. Debug-draw renders a marker at the hit point so picks are
    // visible (with the B-key debug overlay on). Picking is gated on the cursor being
    // released (C key) — captured cursor doesn't have a meaningful screen position to
    // aim with.
    private float lastMouseX;
    private float lastMouseY;
    private CollisionContact3D<GameObject>? lastPick;
    // Coefficient of restitution: 0 = perfect stick, 1 = perfect bounce. 0.5 gives
    // a recognisably-bouncy cube that settles within a few impacts.
    private const float DropCubeRestitution = 0.5f;
    // NormalisedInverseMass for the drop cubes: 1.0 is the baseline "unit-mobile"
    // body — equivalent to using equal mass for all three. Demos that want one cube
    // heavier than another would track per-cube values (per-instance field, Dictionary,
    // game-side subclass, etc.); the engine deliberately doesn't put this on PhysicsHost3D.
    private const float DropCubeNormalisedInverseMass = 1.0f;
    // Cursor capture is the single dev-mode axis. Captured = camera mouselook on,
    // debug overlay off (game mode). Uncaptured = camera locked, debug overlay +
    // ImGui usable (debug mode). Toggled with Cmd/Ctrl+C; SetCursorCaptured flips
    // OpenTK's CursorState to match.
    private bool cursorCaptured = true;
    // Debug overlay visibility tracks cursor-release state: when the cursor is free
    // the user is in debug mode and every developer overlay (frustum, grid, AABBs)
    // lights up.
    private bool ShowDebug => !cursorCaptured;
    private RenderSurface sceneSurface =
        new(RenderSurfaceHandle.Default, Array.Empty<TextureHandle>(), DepthTexture: null);
    // HDR snapshot of the opaque scene right before the glass pass. The glass shader
    // needs to sample "what's behind this glass" and we can't read from sceneSurface
    // while we're also writing to it - this is the read-side buffer.
    private RenderSurface sceneCopySurface =
        new(RenderSurfaceHandle.Default, Array.Empty<TextureHandle>(), DepthTexture: null);
    private RenderSurface shadowSurface =
        new(RenderSurfaceHandle.Default, Array.Empty<TextureHandle>(), DepthTexture: null);
    // Bloom pipeline: three resolution levels, each with a "bright" target (downsample
    // destination, holds the final blurred bloom for that level) and a "temp" target
    // (intermediate for ping-pong separable Gaussian).
    private const int BloomLevelCount = 3;
    private readonly RenderSurface[] bloomBrightSurfaces = new RenderSurface[BloomLevelCount];
    private readonly RenderSurface[] bloomTempSurfaces = new RenderSurface[BloomLevelCount];
    private int presentMode;
    private string lastInput = "ready";

    // UI/HUD path. SpriteBatch + Font are created lazily in OnLoad so the
    // demo continues to render even if the font asset is missing. `hudFont` stays
    // null when the load fails; the HUD pass quietly skips text in that case.
    private SpriteBatch? spriteBatch;
    private Font? hudFont;
    private TextureHandle hudWhitePixel;
    private float fpsSmoothed;

    // Positional audio. Listener follows the main camera; source is
    // anchored to the warm-orange point light's world position so the sound
    // appears to emit from a visible point in the scene. Both stay null when
    // the audio backend or asset is unavailable — the demo runs muted.
    private AudioListener? audioListener;
    private AudioSource? sceneAudioSource;
    private AudioClipHandle audioClipHandle = AudioClipHandle.Invalid;

    public string DebugName => "ShaderLab";

    public void Debug(DebugContext debug)
    {
        debug.State.Enabled = ShowDebug;
        debug.State.ShowOverlay = ShowDebug;
        debug.State.ShowDebugDraw = ShowDebug;

        using (debug.Scope("Camera"))
        {
            debug.Values.Value("Position", mainCamera.Transform.Position);
            debug.Values.Value("Yaw", cameraYaw);
            debug.Values.Value("Pitch", cameraPitch);
        }

        using (debug.Scope("Lighting"))
        {
            debug.Values.Value("Direction", mainLight.Direction);
            mainLight.Intensity = debug.Controls.Float("Direct", mainLight.Intensity, BrightnessMin, BrightnessMax);
            ambientBoost = debug.Controls.Float("Ambient", ambientBoost, BrightnessMin, BrightnessMax);
            skyboxIntensity = debug.Controls.Float("Sky Env", skyboxIntensity, BrightnessMin, BrightnessMax);
        }

        using (debug.Scope("Post"))
        {
            presentMode = debug.Controls.Enum("Present", presentMode, PresentModeLabels);
            bloomStrength = debug.Controls.Float("Bloom", bloomStrength, BloomMin, BloomMax);
            exposure = debug.Controls.Float("Exposure", exposure, ExposureMin, ExposureMax);
        }

        using (debug.Scope("Glass"))
        {
            glassThickness = debug.Controls.Float("Thickness", glassThickness, GlassThicknessMin, GlassThicknessMax);
            glassF0 = debug.Controls.Float("F0", glassF0, GlassF0Min, GlassF0Max);
        }

        using (debug.Scope("Fur"))
        {
            furShellCount = debug.Controls.Float("Shells", furShellCount, FurShellCountMin, FurShellCountMax);
            furLength = debug.Controls.Float("Length", furLength, FurLengthMin, FurLengthMax);
            furDensity = debug.Controls.Float("Density", furDensity, FurDensityMin, FurDensityMax);
            furWind = debug.Controls.Float("Wind", furWind, FurWindMin, FurWindMax);
        }

        using (debug.Scope("Hologram"))
        {
            hologramOpacity = debug.Controls.Float("Opacity", hologramOpacity, HologramOpacityMin, HologramOpacityMax);
            hologramRim = debug.Controls.Float("Rim", hologramRim, HologramRimMin, HologramRimMax);
            hologramScanlines = debug.Controls.Float("Scanlines", hologramScanlines, HologramScanlineMin, HologramScanlineMax);
            hologramGlitch = debug.Controls.Float("Glitch", hologramGlitch, HologramGlitchMin, HologramGlitchMax);
        }

        using (debug.Scope("Actions"))
        {
            if (debug.Controls.Button("Reset View"))
            {
                ResetView();
            }
        }

        debug.Values.Value("Last Input", lastInput);
    }

    protected override void OnLoad()
    {
        // Local alias keeps the existing OnLoad body terse; Host is the base-class
        // property captured during the IGameLoop bootstrap.
        var graphicsDevice = GraphicsDevice;
        Host.SetTitle("Blix · ShaderLab");
        // FPS-style cursor capture: the cursor is hidden and locked to the window, so
        // mouse motion is delivered as deltas indefinitely (cursor never wanders off).
        // Press Esc to quit (the cursor is released automatically on window close).
        Host.SetCursorCaptured(true);
        Console.WriteLine($"Graphics: {graphicsDevice.Info.Vendor} | {graphicsDevice.Info.Renderer}");
        Console.WriteLine($"OpenGL: {graphicsDevice.Info.Version} | GLSL: {graphicsDevice.Info.ShadingLanguageVersion}");

        var assets = new AssetDatabase()
            .RegisterImporter(new TextureImporter())
            .RegisterImporter(new ObjImporter())
            .RegisterImporter(new MaterialImporter())
            .RegisterImporter(new GltfImporter())
            .RegisterImporter(new WavImporter())
            .RegisterImporter(new FontImporter())
            .LoadManifest(Path.Combine(AppContext.BaseDirectory, "Assets", "manifest.json"));

        var cubeData = assets.Load<MeshData>(AssetId.Parse("models/cube"));
        cubeMesh = graphicsDevice.CreateMesh(cubeData, name: "cube");

        // Downloaded OBJ assets: teapot (v//n, no UVs), bunny (v only, no normals or
        // UVs), Suzanne (v//n). The OBJ importer now synthesizes the missing streams.
        var teapotData = assets.Load<MeshData>(AssetId.Parse("models/teapot"));
        teapotMesh = graphicsDevice.CreateMesh(teapotData, name: "teapot");
        Console.WriteLine($"Loaded teapot: bounds={teapotData.Bounds.Min}..{teapotData.Bounds.Max}");

        var bunnyData = assets.Load<MeshData>(AssetId.Parse("models/bunny"));
        bunnyMesh = graphicsDevice.CreateMesh(bunnyData, name: "bunny");
        Console.WriteLine($"Loaded bunny: bounds={bunnyData.Bounds.Min}..{bunnyData.Bounds.Max}");

        var suzanneData = assets.Load<MeshData>(AssetId.Parse("models/suzanne"));
        suzanneMesh = graphicsDevice.CreateMesh(suzanneData, name: "suzanne");
        Console.WriteLine($"Loaded suzanne: bounds={suzanneData.Bounds.Min}..{suzanneData.Bounds.Max}");

        var planeVertices = graphicsDevice.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(PlaneMesh.Vertices), name: "plane.vertices");
        var planeIndices = graphicsDevice.CreateIndexBuffer(PlaneMesh.Indices, name: "plane.indices");
        var torusVertices = graphicsDevice.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(Torus.Vertices), name: "torus.vertices");
        var torusIndices = graphicsDevice.CreateIndexBuffer(Torus.Indices, name: "torus.indices");
        var cylinderVertices = graphicsDevice.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(Cylinder.Vertices), name: "cylinder.vertices");
        var cylinderIndices = graphicsDevice.CreateIndexBuffer(Cylinder.Indices, name: "cylinder.indices");
        var icosphereVertices = graphicsDevice.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(Icosphere.Vertices), name: "icosphere.vertices");
        var icosphereIndices = graphicsDevice.CreateIndexBuffer(Icosphere.Indices, name: "icosphere.indices");
        var torusKnotVertices = graphicsDevice.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(TorusKnot.Vertices), name: "torusknot.vertices");
        var torusKnotIndices = graphicsDevice.CreateIndexBuffer(TorusKnot.Indices, name: "torusknot.indices");
        var capsuleVertices = graphicsDevice.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(CapsuleMesh.Vertices), name: "capsule.vertices");
        var capsuleIndices = graphicsDevice.CreateIndexBuffer(CapsuleMesh.Indices, name: "capsule.indices");
        var fullscreenVertices = graphicsDevice.CreateVertexBuffer(VertexPositionTexture.CreateBufferData(FullscreenQuad.Vertices), name: "fullscreen.vertices");
        var fullscreenIndices = graphicsDevice.CreateIndexBuffer(FullscreenQuad.Indices, name: "fullscreen.indices");

        planeMesh = new Mesh("plane", planeVertices, planeIndices, PlaneMesh.Indices.Length, CreateBounds(PlaneMesh.Vertices));
        torusMesh = new Mesh("torus", torusVertices, torusIndices, Torus.Indices.Length, CreateBounds(Torus.Vertices));
        cylinderMesh = new Mesh("cylinder", cylinderVertices, cylinderIndices, Cylinder.Indices.Length, CreateBounds(Cylinder.Vertices));
        icosphereMesh = new Mesh("icosphere", icosphereVertices, icosphereIndices, Icosphere.Indices.Length, CreateBounds(Icosphere.Vertices));
        torusKnotMesh = new Mesh("torusknot", torusKnotVertices, torusKnotIndices, TorusKnot.Indices.Length, CreateBounds(TorusKnot.Vertices));
        capsuleMesh = new Mesh("capsule", capsuleVertices, capsuleIndices, CapsuleMesh.Indices.Length, CreateBounds(CapsuleMesh.Vertices));
        fullscreenMesh = new Mesh("fullscreen", fullscreenVertices, fullscreenIndices, FullscreenQuad.Indices.Length, Bounds3.Empty);

        sceneSurface = graphicsDevice.CreateRenderSurface(new RenderSurfaceDescription(
            Name: "scene",
            Size: new MatchDefaultRenderSurfaceSize(),
            ColorAttachments:
            [
                // HDR color buffer: lit-pass output can exceed 1.0 (specular highlights
                // routinely do). The bright-pass for bloom reads this directly, and the
                // final tone-mapping shader compresses it back into LDR for display.
                new ColorAttachmentDescription(TextureFormat.Rgba16F, SamplerDescription.LinearClamp),
                // Luminance attachment kept at Rgba8 - it's only used for debug viewing
                // and doesn't need the dynamic range.
                new ColorAttachmentDescription(TextureFormat.Rgba8, SamplerDescription.LinearClamp),
                // Debug shading normals. The lit shader writes the final perturbed normal
                // (after normal maps) encoded from [-1,1] to [0,1].
                new ColorAttachmentDescription(TextureFormat.Rgba8, SamplerDescription.LinearClamp)
            ],
            Depth: new DepthTexture(SamplerDescription.LinearClamp)));

        // Single HDR attachment - just a copy target. No depth needed (the glass pass
        // uses sceneSurface's existing depth buffer for occlusion).
        sceneCopySurface = graphicsDevice.CreateRenderSurface(new RenderSurfaceDescription(
            Name: "scene.copy",
            Size: new MatchDefaultRenderSurfaceSize(),
            ColorAttachments:
            [
                new ColorAttachmentDescription(TextureFormat.Rgba16F, SamplerDescription.LinearClamp)
            ],
            Depth: null));

        // Bloom levels at 1/2, 1/4, 1/8 of the default surface. Each level needs a
        // "bright" target (downsample destination + final blurred output) and a "temp"
        // target (intermediate for separable Gaussian: H pass writes here, V pass reads).
        // All HDR (Rgba16F) so values >1 survive through the chain.
        for (var i = 0; i < BloomLevelCount; i++)
        {
            var scale = 1.0f / (1 << (i + 1));   // 0.5, 0.25, 0.125
            bloomBrightSurfaces[i] = graphicsDevice.CreateRenderSurface(new RenderSurfaceDescription(
                Name: $"bloom.bright[{i}]",
                Size: new MatchDefaultRenderSurfaceSize(scale),
                ColorAttachments:
                [
                    new ColorAttachmentDescription(TextureFormat.Rgba16F, SamplerDescription.LinearClamp)
                ],
                Depth: null));
            bloomTempSurfaces[i] = graphicsDevice.CreateRenderSurface(new RenderSurfaceDescription(
                Name: $"bloom.temp[{i}]",
                Size: new MatchDefaultRenderSurfaceSize(scale),
                ColorAttachments:
                [
                    new ColorAttachmentDescription(TextureFormat.Rgba16F, SamplerDescription.LinearClamp)
                ],
                Depth: null));
        }

        shadowSurface = graphicsDevice.CreateRenderSurface(new RenderSurfaceDescription(
            Name: "shadow",
            Size: new FixedRenderSurfaceSize(ShadowMapResolution, ShadowMapResolution),
            ColorAttachments: Array.Empty<ColorAttachmentDescription>(),
            // Linear filter + compare mode: the lit shader uses sampler2DShadow, so each
            // texture() call does a hardware 2x2 bilinear PCF compare (LEQUAL on stored
            // back-of-geometry depth). Linear is required for the bilinear part to fire.
            // Clamping keeps out-of-frustum lookups from wrapping; the shader also
            // early-outs on out-of-range UVs as belt-and-suspenders.
            Depth: new DepthTexture(new SamplerDescription(
                MinFilter: TextureFilter.Linear,
                MagFilter: TextureFilter.Linear,
                WrapU: TextureWrap.ClampToEdge,
                WrapV: TextureWrap.ClampToEdge,
                GenerateMipmaps: false,
                Compare: true))));

        var shadowMapTexture = shadowSurface.DepthTexture
            ?? throw new InvalidOperationException("Shadow surface has no depth texture.");

        // Spot + point shadow surfaces. Same depth/compare sampler config as the
        // sun's surface so the shader's PCSS path works identically across all
        // shadow types. Every cap slot is allocated upfront; per-frame
        // rendering only fills the slots that have casters this frame.
        var spotDepthSampler = new SamplerDescription(
            MinFilter: TextureFilter.Linear,
            MagFilter: TextureFilter.Linear,
            WrapU: TextureWrap.ClampToEdge,
            WrapV: TextureWrap.ClampToEdge,
            GenerateMipmaps: false,
            Compare: true);

        for (var i = 0; i < MaxSpotShadowCasters; i++)
        {
            var surface = graphicsDevice.CreateRenderSurface(new RenderSurfaceDescription(
                Name: $"shadow.spot.{i}",
                Size: new FixedRenderSurfaceSize(SpotShadowMapSize, SpotShadowMapSize),
                ColorAttachments: Array.Empty<ColorAttachmentDescription>(),
                Depth: new DepthTexture(spotDepthSampler)));
            spotShadowSurfaces[i] = surface;
            spotShadowTextures[i] = surface.DepthTexture
                ?? throw new InvalidOperationException($"Spot shadow surface {i} has no depth texture.");
        }

        for (var i = 0; i < MaxPointShadowCasters; i++)
        {
            pointShadowCubes[i] = graphicsDevice.CreateTextureCubeDepth(
                faceSize: PointShadowFaceSize,
                sampler: spotDepthSampler,
                name: $"shadow.point.{i}.cube");
            for (var face = 0; face < 6; face++)
            {
                pointShadowFaceSurfaces[i, face] = graphicsDevice.CreateRenderSurface(new RenderSurfaceDescription(
                    Name: $"shadow.point.{i}.face{face}",
                    Size: new FixedRenderSurfaceSize(PointShadowFaceSize, PointShadowFaceSize),
                    ColorAttachments: Array.Empty<ColorAttachmentDescription>(),
                    Depth: new DepthCubeFace(pointShadowCubes[i], face)));
            }
        }

        // 1x1 flat tangent-space normal (decoded value (0, 0, 1)). Bound as the
        // material default so any draw without a normal-map override samples a
        // "no-op" and the surface normal passes through unchanged.
        var flatNormalPixels = new byte[] { 128, 128, 255, 255 };
        flatNormalTexture = graphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            flatNormalPixels,
            name: "flat_normal");

        // 1x1 neutral metallic-roughness: G = 255 (roughness 1.0), B = 255
        // (metallic 1.0). Materials without an explicit MR texture sample this
        // baseline of 1.0 and let their uMetallicFactor / uRoughnessFactor JSON
        // values map directly to effective metallic/roughness (factor * 1 = factor).
        var neutralMrPixels = new byte[] { 0, 255, 255, 255 };
        neutralMetallicRoughnessTexture = graphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            neutralMrPixels,
            name: "mr_neutral");

        // UV-grid calibration pattern bound onto the torus' material post-resolve. Not
        // an on-disk asset, so it can't be referenced by AssetId from a material JSON.
        uvGridTexture = CreateProceduralTexture(graphicsDevice, "uv_grid", 256, GenerateUvGridPixels);
        furNoiseTexture = CreateProceduralTexture(
            graphicsDevice,
            "fur_noise",
            512,
            GenerateFurNoisePixels,
            SamplerDescription.LinearRepeat);

        // Procedurally generate a 6-face cubemap with a gradient sky + sun. Baked once
        // at startup using the initial light direction; doesn't follow the runtime
        // mainLight.Direction. Living-light environments would need a per-frame regenerate
        // or a separately-driven sky direction.
        // HDR procedural environment cubemap. Linear half-float storage
        // lets the sun reach ~12x and reflections show real punch instead of
        // flat-white clipping. Sampled directly as linear in skybox + pbr_core
        // (no sRGB decode, since the data isn't encoded that way).
        var cubemapHdr = GenerateProceduralCubemapHdr(256, InitialLightDirection);
        environmentCubemap = graphicsDevice.CreateTextureCubeHdr(256, cubemapHdr, SamplerDescription.LinearClampMipmap, name: "environment");

        var litShader = graphicsDevice.CreateShaderProgram(CubeShaderSources.LoadShaders());
        var shadowShader = graphicsDevice.CreateShaderProgram(CubeShaderSources.LoadShadowShaders());
        var presentColorShader = graphicsDevice.CreateShaderProgram(FullscreenQuadShaderSources.LoadPresentShaders());
        var presentDepthShader = graphicsDevice.CreateShaderProgram(FullscreenQuadShaderSources.LoadDepthPresentShaders());
        var presentShadowMapShader = graphicsDevice.CreateShaderProgram(FullscreenQuadShaderSources.LoadShadowMapPresentShaders());
        var brightShader = graphicsDevice.CreateShaderProgram(FullscreenQuadShaderSources.LoadBrightShaders());
        var blurShader = graphicsDevice.CreateShaderProgram(FullscreenQuadShaderSources.LoadBlurShaders());
        var bloomCompositeShader = graphicsDevice.CreateShaderProgram(FullscreenQuadShaderSources.LoadBloomCompositeShaders());
        var skyboxShader = graphicsDevice.CreateShaderProgram(FullscreenQuadShaderSources.LoadSkyboxShaders());
        var copyShader = graphicsDevice.CreateShaderProgram(FullscreenQuadShaderSources.LoadCopyShaders());
        var glassShader = graphicsDevice.CreateShaderProgram(FullscreenQuadShaderSources.LoadGlassShaders());
        var furShader = graphicsDevice.CreateShaderProgram(CubeShaderSources.LoadFurShaders());
        var hologramShader = graphicsDevice.CreateShaderProgram(CubeShaderSources.LoadHologramShaders());

        var litPipeline = graphicsDevice.CreatePipeline(
            new PipelineDescription(
                litShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                BlendState.Disabled),
            name: "lit");

        // Back-face cull in the shadow pass so we capture the FRONT face of each caster
        // from the light's POV — the actual silhouette. Front-face culling would write
        // the back face's depth and produce a "Peter Pan" offset on cast shadows by
        // roughly the object's thickness, which is very visible on nearby receivers.
        // Self-shadow acne on the casters is handled by the slope-scaled bias in cube.frag.
        var shadowPipeline = graphicsDevice.CreatePipeline(
            new PipelineDescription(
                shadowShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                BlendState.Disabled),
            name: "shadow");

        var presentColorPipeline = graphicsDevice.CreatePipeline(
            new PipelineDescription(
                presentColorShader,
                VertexPositionTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.Disabled,
                RasterizerState.NoCulling,
                BlendState.Disabled),
            name: "present.color");
        var presentDepthPipeline = graphicsDevice.CreatePipeline(
            new PipelineDescription(
                presentDepthShader,
                VertexPositionTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.Disabled,
                RasterizerState.NoCulling,
                BlendState.Disabled),
            name: "present.depth");
        var presentShadowMapPipeline = graphicsDevice.CreatePipeline(
            new PipelineDescription(
                presentShadowMapShader,
                VertexPositionTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.Disabled,
                RasterizerState.NoCulling,
                BlendState.Disabled),
            name: "present.shadowmap");
        // Bloom pipelines reuse the fullscreen-quad vertex layout and disable depth -
        // they're all full-screen post-process passes.
        var brightPipeline = graphicsDevice.CreatePipeline(
            new PipelineDescription(
                brightShader,
                VertexPositionTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.Disabled,
                RasterizerState.NoCulling,
                BlendState.Disabled),
            name: "bloom.bright");
        var blurPipeline = graphicsDevice.CreatePipeline(
            new PipelineDescription(
                blurShader,
                VertexPositionTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.Disabled,
                RasterizerState.NoCulling,
                BlendState.Disabled),
            name: "bloom.blur");
        var bloomCompositePipeline = graphicsDevice.CreatePipeline(
            new PipelineDescription(
                bloomCompositeShader,
                VertexPositionTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.Disabled,
                RasterizerState.NoCulling,
                BlendState.Disabled),
            name: "bloom.composite");
        // Skybox runs with depth-test on but write off, and no culling. The vertex shader
        // forces clip.z=w so skybox fragments are at the far plane; any scene fragments
        // already at depth<1 occlude them. No culling because the camera is "inside" the
        // unit cube and either side could be visible depending on the camera orientation.
        var skyboxPipeline = graphicsDevice.CreatePipeline(
            new PipelineDescription(
                skyboxShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualNoWrite,
                RasterizerState.NoCulling,
                BlendState.Disabled),
            name: "skybox");
        // Copy pipeline: trivial HDR -> HDR pass-through for the snapshot pass.
        var copyPipeline = graphicsDevice.CreatePipeline(
            new PipelineDescription(
                copyShader,
                VertexPositionTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.Disabled,
                RasterizerState.NoCulling,
                BlendState.Disabled),
            name: "scene.copy");
        // Glass pipeline: same vertex format as the lit pass. It writes depth even
        // though it is refractive, because the demo has a single glass mesh and the
        // torus knot overlaps itself heavily while rotating. Without depth writes,
        // self-overlapping fragments can draw in index order and appear to cut through
        // their own surface.
        var glassPipeline = graphicsDevice.CreatePipeline(
            new PipelineDescription(
                glassShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                BlendState.Disabled),
            name: "glass");
        var furPipeline = graphicsDevice.CreatePipeline(
            new PipelineDescription(
                furShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualNoWrite,
                RasterizerState.NoCulling,
                [BlendState.AlphaBlend, BlendState.AlphaBlend, BlendState.AlphaBlend]),
            name: "fur.shell");
        var hologramPipeline = graphicsDevice.CreatePipeline(
            new PipelineDescription(
                hologramShader,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualNoWrite,
                RasterizerState.NoCulling,
                [BlendState.AlphaBlend, BlendState.AlphaBlend, BlendState.AlphaBlend]),
            name: "hologram");

        shadowMaterial = new Material("shadow", shadowPipeline);

        presentColorMaterial = new Material("present.color", presentColorPipeline);
        presentDepthMaterial = new Material("present.depth", presentDepthPipeline);
        presentShadowMapMaterial = new Material("present.shadowmap", presentShadowMapPipeline);
        brightMaterial = new Material("bloom.bright", brightPipeline);
        blurMaterial = new Material("bloom.blur", blurPipeline);
        bloomCompositeMaterial = new Material("bloom.composite", bloomCompositePipeline);
        skyboxMaterial = new Material("skybox", skyboxPipeline)
            .SetTexture("uSkybox", environmentCubemap, slot: 0)
            .SetUniform("uSkyboxIntensity", new FloatUniform(InitialSkyboxIntensity));
        copyMaterial = new Material("scene.copy", copyPipeline);

        // Per-object lit materials and the glass material are JSON-defined under
        // Assets/materials. Resolver loads pipeline+uniforms+textures from disk; the
        // configureDefaults lambda binds runtime-only textures (shadow map, env cubemap,
        // flat normal fallback) before JSON values apply, so per-material normal maps
        // cleanly override the flat default while shared runtime bindings are common.
        var resolver = new MaterialResolver(graphicsDevice, assets, SamplerDescription.LinearClamp)
            .RegisterPipeline("scene.lit", litPipeline)
            .RegisterPipeline("glass", glassPipeline);

        Material ResolveLit(string id) => resolver.Resolve(AssetId.Parse(id), m =>
        {
            m.SetTexture("uShadowMap", shadowMapTexture, slot: 1);
            m.SetTexture("uEnvMap", environmentCubemap, slot: 2);
            m.SetTexture("uNormalMap", flatNormalTexture, slot: 3);
            // Metallic-roughness fallback for materials that don't bind
            // their own MR texture. Per-material uMetallicFactor / uRoughnessFactor
            // (set via the JSON) sample-multiply against this and drive the BRDF.
            m.SetTexture("uMetallicRoughnessMap", neutralMetallicRoughnessTexture, slot: 4);
            // Spot + point shadow maps. Every cap slot is bound so the sampler
            // arrays are always valid; uSpotShadowCount / uPointShadowCount
            // (per-frame) tell the shader which slots actually contain a live
            // caster's depth.
            for (var i = 0; i < MaxSpotShadowCasters; i++)
            {
                m.SetTexture($"uSpotShadowMaps[{i}]", spotShadowTextures[i], slot: 5 + i);
            }
            for (var i = 0; i < MaxPointShadowCasters; i++)
            {
                m.SetTexture($"uPointShadowCubes[{i}]", pointShadowCubes[i], slot: 5 + MaxSpotShadowCasters + i);
            }
            m.SetUniform("uEnvMapMipCount", new FloatUniform(9.0f));
            m.SetUniform("uNormalScale", new FloatUniform(0.0f));
            // Default baseColor multiplier to white. Critical: the PBR shader
            // samples `texture(uTexture, uv) * uBaseColorFactor`; if no material
            // sets the uniform it defaults to vec4(0) and EVERYTHING goes black.
            // JSON-authored values override; the existing static materials don't
            // tint via this uniform, so the default (1,1,1,1) leaves the texture
            // unchanged.
            m.SetUniform("uBaseColorFactor", new Vector4Uniform(Vector4.One));
        });

        heroCubeMaterial     = ResolveLit("materials/hero_cube");
        stoneFloorMaterial   = ResolveLit("materials/stone_floor");
        fabricMaterial       = ResolveLit("materials/fabric");
        uvGridMaterial       = ResolveLit("materials/uv_grid");
        paintedMetalMaterial = ResolveLit("materials/painted_metal");
        marbleMaterial       = ResolveLit("materials/marble");
        woodMaterial         = ResolveLit("materials/wood");
        leatherMaterial      = ResolveLit("materials/leather");
        rustyMetalMaterial   = ResolveLit("materials/rusty_metal");
        concreteMaterial     = ResolveLit("materials/concrete");

        // Torus' diffuse is procedural (not on disk, not an AssetId). Bind it onto the
        // resolved material here so material identity (specular/shininess/reflectance)
        // still flows from JSON.
        uvGridMaterial.SetTexture("uTexture", uvGridTexture, slot: 0);

        glassMaterial = resolver.Resolve(AssetId.Parse("materials/glass"), m =>
        {
            m.SetTexture("uScene", sceneCopySurface.ColorAttachments[0], slot: 0);
            m.SetTexture("uEnvMap", environmentCubemap, slot: 1);
        });

        furMaterial = new Material("fur", furPipeline)
            .SetTexture("uFurNoise", furNoiseTexture, slot: 0);
        hologramMaterial = new Material("hologram", hologramPipeline);

        // Skinned-content pipelines. skin.lit.vert + skin.lit.frag does the full
        // PBR + Cook-Torrance + IBL path with per-vertex tangents; the shadow
        // caster does depth-only with the same bone palette. Both use the
        // Tangent-augmented vertex layout — even meshes that don't provide
        // TANGENT carry a zero-vec4 sentinel that the fragment shader recognises
        // and falls back to derivative synthesis.
        var skinLitShader = graphicsDevice.CreateShaderProgram(CubeShaderSources.LoadSkinLitShaders());
        var skinLitPipeline = graphicsDevice.CreatePipeline(
            new PipelineDescription(
                skinLitShader,
                VertexPosition3NormalTextureSkin4Tangent.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                BlendState.Disabled),
            name: "skin.lit");

        var skinShadowShader = graphicsDevice.CreateShaderProgram(CubeShaderSources.LoadShadowSkinShaders());
        var skinShadowPipeline = graphicsDevice.CreatePipeline(
            new PipelineDescription(
                skinShadowShader,
                VertexPosition3NormalTextureSkin4Tangent.Layout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                BlendState.Disabled),
            name: "skin.shadow");
        // 1x1 white default texture for materials that don't reference a base-color
        // texture; the skin shader unconditionally samples uTexture and multiplies
        // by uBaseColorFactor, so untextured materials just bind white and let the
        // factor carry the colour.
        var whiteFallbackPixels = new byte[] { 255, 255, 255, 255 };
        var whiteFallbackTexture = graphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            whiteFallbackPixels,
            name: "skin.white_fallback");

        // Mip count for textureLod-driven IBL specular. 256x256 cubemap with
        // auto-generated mip chain has log2(256)+1 = 9 levels.
        const float envMapMipCount = 9.0f;

        // Shadow caster shared across every skinned object. Created
        // alongside the lit material setup so it can reference skinShadowPipeline.
        skinShadowMaterial = new Material("skin.shadow", skinShadowPipeline);

        // Load a rigged glTF and compose it as a SkinnedGameObject.
        // Returns (rig, animations[]) so the caller can attach whatever animation
        // shape it wants -- a plain looping ClipAnimation, a BlendedClipAnimation,
        // a future state-machine controller. The mesh/material/skeleton/palette
        // construction is the shared concern.
        (SkinnedGameObject rig, AnimationClip[] animations) LoadRig(string assetId, Transform3D transform)
        {
            var src = assets.Load<GltfModel>(AssetId.Parse(assetId));
            if (src.Animations.Length == 0)
            {
                throw new InvalidOperationException($"glTF '{assetId}' has no animations to play.");
            }
            Console.WriteLine($"Loaded {assetId}: {src.Primitives.Length} primitive(s), {src.Skeleton.BoneCount} bones, {src.Animations.Length} animation(s)");

            var assetSubmeshes = new Submesh[src.Primitives.Length];
            for (var i = 0; i < src.Primitives.Length; i++)
            {
                var prim = src.Primitives[i];
                var mesh = graphicsDevice.CreateMesh(prim.Mesh, name: $"{assetId}.{i}");
                var material = BuildSkinLitMaterial(
                    graphicsDevice, skinLitPipeline, prim.Material,
                    whiteFallbackTexture, flatNormalTexture, neutralMetallicRoughnessTexture,
                    shadowMapTexture, environmentCubemap,
                    spotShadowTextures, pointShadowCubes, envMapMipCount,
                    $"{assetId}.{i}");
                assetSubmeshes[i] = new Submesh(mesh, material);
            }
            var rig = new SkinnedGameObject(assetId, assetSubmeshes, src.Skeleton, transform,
                meshNodeTransform: src.MeshNodeTransform);
            return (rig, src.Animations);
        }

        // CesiumMan: humanoid, walks in place. Single-clip playback via plain
        // ClipAnimation -- nothing to blend here, one animation in the file.
        var (cesiumMan, cesiumAnims) = LoadRig("models/cesium_man",
            new Transform3D
            {
                Position = new Vector3(-2.5f, -1.0f, 0.0f),
            });
        cesiumMan.AddAnimation(new ClipAnimation
        {
            Clip = cesiumAnims[0],
            Target = cesiumMan.Pose,
            Loop = true,
        });
        skinnedObjects.Add(cesiumMan);

        // Fox: four-legged rig with Survey/Walk/Run clips. We wire a
        // BlendedClipAnimation between Walk (index 1) and Run (index 2); the
        // demo's OnUpdate oscillates Weight between 0 and 1 to show seamless
        // blending. Fox's glTF authors at ~120-unit scale, so the user transform
        // scales it down to fit the scene.
        var (fox, foxAnims) = LoadRig("models/fox",
            new Transform3D
            {
                Position = new Vector3(2.8f, -1.0f, -1.5f),
                Scale = new Vector3(0.01f, 0.01f, 0.01f),
            });
        foxBlend = new BlendedClipAnimation
        {
            ClipA = foxAnims[1],   // Walk
            ClipB = foxAnims[2],   // Run
            Target = fox.Pose,
            RestPose = fox.RestPose,
            Loop = true,
            Weight = 0.0f,         // start fully on Walk
        };
        fox.AddAnimation(foxBlend);
        skinnedObjects.Add(fox);

        // Scene composition. Each GameObject pairs a mesh + material + initial
        // Transform3D. Objects that spin are AnimatedGameObjects with an attached
        // EulerRotationAnimation; OnUpdate ticks them via the IUpdateable cast in the
        // pass-list iteration. Plain GameObjects are not IUpdateable and skip silently.
        var heroCube = new AnimatedGameObject("hero_cube", cubeMesh, heroCubeMaterial,
            new Transform3D { Position = new Vector3(-0.25f, -0.05f, -0.55f) });
        floor = new GameObject("floor", planeMesh, stoneFloorMaterial,
            new Transform3D { Position = new Vector3(0.0f, -1.0f, 0.0f), Scale = new Vector3(5.0f, 1.0f, 5.0f) });
        var smallCube = new GameObject("small_cube", cubeMesh, fabricMaterial,
            new Transform3D
            {
                Position = new Vector3(1.85f, -0.68f, 0.45f),
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4.0f),
                Scale = new Vector3(0.65f, 0.65f, 0.65f),
            });
        var torus = new AnimatedGameObject("torus", torusMesh, uvGridMaterial,
            new Transform3D { Position = new Vector3(0.45f, -0.8f, 1.25f), Scale = new Vector3(1.15f, 1.15f, 1.15f) });
        var cylinder = new GameObject("cylinder", cylinderMesh, paintedMetalMaterial,
            new Transform3D { Position = new Vector3(-1.45f, -0.5f, -1.15f) });
        var icosphere = new GameObject("icosphere", icosphereMesh, marbleMaterial,
            new Transform3D { Position = new Vector3(-1.25f, -0.5f, 1.65f) });
        var capsule = new GameObject("capsule", capsuleMesh, woodMaterial,
            new Transform3D
            {
                Position = new Vector3(1.35f, -0.4f, -1.1f),
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2.0f),
            });
        // Position.Y derived from each loaded mesh's bounds so "feet on the floor at
        // y=-1" works regardless of how the OBJ was authored: world bottom is
        // `position.Y + bounds.Min.Y * scale.Y`, so set position.Y = -1 - bounds.Min.Y * scale.Y.
        // Robust to both centred-at-origin and arbitrary-author-offset source meshes.
        const float teapotScale = 0.24f;
        var teapot = new AnimatedGameObject("teapot", teapotMesh, leatherMaterial,
            new Transform3D
            {
                Position = new Vector3(-0.85f, -1.0f - teapotData.Bounds.Min.Y * teapotScale, 0.45f),
                Scale = new Vector3(teapotScale, teapotScale, teapotScale),
            });
        const float bunnyScale = 6.0f;
        var bunny = new GameObject("bunny", bunnyMesh, rustyMetalMaterial,
            new Transform3D
            {
                Position = new Vector3(-1.9f, -1.0f - bunnyData.Bounds.Min.Y * bunnyScale, 1.05f),
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f),
                Scale = new Vector3(bunnyScale, bunnyScale, bunnyScale),
            });
        var suzanne = new GameObject("suzanne", suzanneMesh, concreteMaterial,
            new Transform3D
            {
                Position = new Vector3(1.15f, -0.55f, -0.25f),
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.12f),
                Scale = new Vector3(0.45f, 0.45f, 0.45f),
            });
        var glassKnot = new AnimatedGameObject("glass_knot", torusKnotMesh, glassMaterial,
            new Transform3D { Position = new Vector3(0.65f, 0.35f, 1.25f), Scale = new Vector3(1.15f, 1.15f, 1.15f) });
        var furBallA = new AnimatedGameObject("fur_ball_front", icosphereMesh, furMaterial,
            new Transform3D
            {
                Position = new Vector3(-0.15f, -0.78f, 1.95f),
                Scale = new Vector3(0.38f, 0.38f, 0.38f),
            });
        var furBallB = new AnimatedGameObject("fur_ball_left", icosphereMesh, furMaterial,
            new Transform3D
            {
                Position = new Vector3(-1.95f, -0.76f, -0.05f),
                Scale = new Vector3(0.34f, 0.34f, 0.34f),
            });
        var furBallC = new AnimatedGameObject("fur_ball_back", icosphereMesh, furMaterial,
            new Transform3D
            {
                Position = new Vector3(0.20f, -0.78f, -1.55f),
                Scale = new Vector3(0.30f, 0.30f, 0.30f),
            });
        var hologramSuzanne = new GameObject("hologram_suzanne", suzanneMesh, hologramMaterial,
            new Transform3D
            {
                Position = new Vector3(2.1f, -0.55f, 1.25f),
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.75f),
                Scale = new Vector3(0.36f, 0.36f, 0.36f),
            });
        var hologramBunny = new GameObject("hologram_bunny", bunnyMesh, hologramMaterial,
            new Transform3D
            {
                Position = new Vector3(-2.2f, -1.19f, 1.9f),
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.9f),
                Scale = new Vector3(4.6f, 4.6f, 4.6f),
            });

        // Three dropping cubes from different heights and X/Z offsets — exercises the
        // multi-body case against the CollisionWorld3D. They don't collide with each
        // other (dynamic-vs-dynamic isn't supported yet); each tests independently
        // against the static colliders in the world.
        PhysicsGameObject MakeDropCube(string name, Vector3 position, float scale)
        {
            var cube = new PhysicsGameObject(name, cubeMesh, heroCubeMaterial,
                new Transform3D
                {
                    Position = position,
                    Scale = new Vector3(scale, scale, scale),
                });
            cube.Physics.GravityScale = 1.0f;
            return cube;
        }
        // Stack the three cubes in a vertical column over a clear spot in the scene
        // so each one falls onto the previous and they actually interact. Same scale
        // and equal NormalisedInverseMass means symmetric two-body response — heavier-
        // top-on-lighter-bottom dynamics come later via per-cube mobility values.
        dropCubes.Add(MakeDropCube("drop_cube_a", new Vector3(0.4f, 1.0f, 0.4f), 0.30f));
        dropCubes.Add(MakeDropCube("drop_cube_b", new Vector3(0.4f, 2.5f, 0.4f), 0.30f));
        // Third cube falls onto the angled ramp added below — exercises AABB-vs-
        // triangle with a non-horizontal contact normal.
        dropCubes.Add(MakeDropCube("drop_cube_c", new Vector3(1.5f, 3.5f, 0.0f), 0.30f));

        // Tilted ramp adjacent to the floor — same PlaneMesh primitive, rotated 30°
        // around Z and shifted off to +X. Demonstrates AABB-vs-triangle with a
        // non-horizontal contact normal (cube C lands here and bounces off the
        // sloped face rather than the flat floor).
        var ramp = new GameObject("ramp", planeMesh, woodMaterial,
            new Transform3D
            {
                Position = new Vector3(1.5f, -0.5f, 0.0f),
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 6.0f),
                Scale = new Vector3(1.0f, 1.0f, 1.5f),
            });

        // Build triangle-mesh colliders for both static surfaces by extracting
        // PlaneMesh.Vertices, transforming through each owner's model matrix, and
        // packing into a TriangleMesh3D. Same shape for both — illustrates that the
        // same conversion path works for any procedural mesh + transform.
        collisionWorld.Add(floor, BuildPlaneTriangleCollider(floor.Transform.ToMatrix()));
        collisionWorld.Add(ramp,  BuildPlaneTriangleCollider(ramp.Transform.ToMatrix()));

        opaqueObjects.AddRange(new GameObject[]
        {
            heroCube, floor, ramp, smallCube, torus, cylinder, icosphere, capsule, teapot, bunny, suzanne,
        });
        opaqueObjects.AddRange(dropCubes);
        furObjects.AddRange(new[]
        {
            furBallA, furBallB, furBallC,
        });
        hologramObjects.AddRange(new[]
        {
            hologramSuzanne, hologramBunny,
        });
        glassObjects.Add(glassKnot);

        // EulerRotationAnimation composes Y * X * Z in column-vector order, so the Y
        // axis rate applies last from the local frame's perspective. Each animation
        // targets its host AnimatedGameObject's Transform — the object ticks its own
        // animations in Update.
        heroCube.AddAnimation(new EulerRotationAnimation { Target = heroCube.Transform,  RadiansPerSecond = new Vector3(0.35f, 0.8f,  0.0f) });
        glassKnot.AddAnimation(new EulerRotationAnimation { Target = glassKnot.Transform, RadiansPerSecond = new Vector3(0.25f, 0.45f, 0.0f) });
        teapot.AddAnimation(new EulerRotationAnimation { Target = teapot.Transform,       RadiansPerSecond = new Vector3(0.0f,  0.3f,  0.0f) });
        torus.AddAnimation(new EulerRotationAnimation { Target = torus.Transform,         RadiansPerSecond = new Vector3(0.0f,  0.5f,  0.0f) });
        furBallA.AddAnimation(new EulerRotationAnimation { Target = furBallA.Transform,   RadiansPerSecond = new Vector3(0.0f,  0.18f, 0.0f) });
        furBallB.AddAnimation(new EulerRotationAnimation { Target = furBallB.Transform,   RadiansPerSecond = new Vector3(0.0f, -0.13f, 0.0f) });
        furBallC.AddAnimation(new EulerRotationAnimation { Target = furBallC.Transform,   RadiansPerSecond = new Vector3(0.0f,  0.22f, 0.0f) });

        mainCamera = new Camera3D
        {
            Transform = new Transform3D
            {
                Position = InitialCameraPosition,
                Rotation = BuildCameraRotation(cameraYaw, cameraPitch),
            },
            VerticalFieldOfView = MathF.PI / 3.0f,
            NearPlane = 0.1f,
            FarPlane = 100.0f
        };

        // UI/HUD wiring. Bake Roboto at three sizes — 14/20/28 — and upload
        // each as its own atlas texture; DrawText picks the closest baked size. The
        // 1x1 white pixel is used by DrawSolidRect (and 9-slice fallbacks) so HUD
        // backings can tint freely via vertex color without a dedicated shader.
        spriteBatch = new SpriteBatch(graphicsDevice);
        hudWhitePixel = graphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.PixelatedRepeat),
            new byte[] { 255, 255, 255, 255 },
            name: "ui.white");

        // Positional audio wiring. Asset DB loads the WAV; the OpenAL backend
        // uploads the clip; a looping source attaches at the orange point light's
        // position. The listener tracks mainCamera so the sound pans / attenuates
        // as the camera moves through the scene. Inverse-distance with
        // ReferenceDistance=1m and MaxDistance=12m gives a roughly 1m "bubble" of
        // unity gain and a soft fall-off across the rest of the room.
        if (AudioDevice is { } audio)
        {
            var clipData = assets.Load<AudioClipData>(AssetId.Parse("audio/ambient_chord"));
            audioClipHandle = audio.CreateClip(clipData, name: "audio/ambient_chord");
            sceneAudioSource = AudioSource.Create(audio, audioClipHandle, name: "ambient_chord");
            sceneAudioSource.Position = pointLights[0].Position;
            sceneAudioSource.IsLooping = true;
            sceneAudioSource.ReferenceDistance = 1.0f;
            sceneAudioSource.MaxDistance = 12.0f;
            sceneAudioSource.Gain = 0.8f;
            sceneAudioSource.Sync(audio);
            sceneAudioSource.Play(audio);

            audioListener = new AudioListener(mainCamera.Transform);
            audioListener.Sync(audio);
            Console.WriteLine($"Audio: loaded {clipData.DurationSeconds:0.00}s clip, attached looping source at light#0.");
        }
        else
        {
            Console.WriteLine("Audio: no AudioDevice on host - demo will run silent.");
        }

        // HUD font. The font.json spec bakes Roboto at 14/20/28 (logical) plus their
        // 2x retina equivalents 40/56 — Font.NearestSize tie-breaks to the larger
        // size when distances match, so a 14-pt request at dpiScale=2 finds 28px
        // and a 28-pt request at dpiScale=1 finds 28px too. Five baked sizes cover
        // both density regimes without overlap math.
        var fontData = assets.Load<FontData>(AssetId.Parse("fonts/roboto"));
        hudFont = Font.Upload(graphicsDevice, fontData);
        Console.WriteLine($"Loaded font: {fontData.Name} ({fontData.Sizes.Count} sizes)");
    }

    public override void OnUpdate(Time time)
    {
        ApplyCameraInput((float)time.Delta);

        // Variable-rate tick: animations and anything else that should sample at the
        // display refresh. Plain GameObjects and PhysicsGameObjects skip silently —
        // physics ticks in OnFixedUpdate instead.
        foreach (var obj in opaqueObjects)   (obj as IUpdateable)?.Update(time);
        foreach (var obj in furObjects)      (obj as IUpdateable)?.Update(time);
        foreach (var obj in hologramObjects) (obj as IUpdateable)?.Update(time);
        foreach (var obj in glassObjects)    (obj as IUpdateable)?.Update(time);
        // Oscillate the Fox blend weight so the Walk <-> Run transition
        // is constantly visible. 6-second period (smoothstep of a sinusoid keeps
        // the transition reading naturally rather than mechanically linear).
        var raw = (MathF.Sin((float)time.Total * (MathF.PI * 2.0f / 6.0f)) + 1.0f) * 0.5f;
        var smooth = raw * raw * (3.0f - 2.0f * raw);  // smoothstep curve
        foxBlend.Weight = smooth;

        foreach (var obj in skinnedObjects)  obj.Update(time);

        // Push listener + source state to the audio backend each frame.
        // Listener pose comes from the camera transform (already updated above
        // via ApplyCameraInput); source position stays anchored to the light's
        // world position — even though the light is stationary in this demo,
        // re-syncing once per frame is the pattern future moving sources need.
        if (AudioDevice is { } audio)
        {
            audioListener?.Sync(audio);
            if (sceneAudioSource is not null)
            {
                sceneAudioSource.Position = pointLights[0].Position;
                sceneAudioSource.Sync(audio);
            }
        }
    }

    public override void OnUnload()
    {
        // Audio cleanup: stop + delete the source, then the clip, before the
        // OpenAL device tears down. Window's OnUnload disposes the device
        // itself, which would free everything anyway, but explicit cleanup
        // keeps the lifetimes clear and matches what real game code would do.
        if (AudioDevice is { } audio)
        {
            sceneAudioSource?.Dispose(audio);
            if (audioClipHandle.Id != AudioClipHandle.Invalid.Id)
            {
                audio.DeleteClip(audioClipHandle);
            }
        }
    }

    public override void OnFixedUpdate(Time time)
    {
        // Fixed-rate tick: physics integration runs at the engine's FixedStep cadence,
        // so trajectory math is stable regardless of frame rate. The IFixedUpdateable
        // cast picks up just the PhysicsGameObjects across all four scene lists.
        foreach (var obj in opaqueObjects)   (obj as IFixedUpdateable)?.FixedUpdate(time);
        foreach (var obj in furObjects)      (obj as IFixedUpdateable)?.FixedUpdate(time);
        foreach (var obj in hologramObjects) (obj as IFixedUpdateable)?.FixedUpdate(time);
        foreach (var obj in glassObjects)    (obj as IFixedUpdateable)?.FixedUpdate(time);

        // Collision response runs at the same cadence as physics integration so
        // depenetration and reflection see the post-integration position. Engine
        // provides the queries (CollisionWorld3D) and the math (CollisionResponse);
        // the demo decides "every cube against every collider, bounce on contact".
        ResolveCollisions();
    }

    private void ResolveCollisions()
    {
        // ---- Cube vs world (static colliders) -----------------------------------
        // Each drop cube tests against the CollisionWorld. The world's contacts are
        // treated as immovable: full depenetration on the cube side, single-body
        // ReflectVelocity for the bounce. No NormalisedInverseMass involved — the
        // world is a separate API from dynamic pairs, not a mobility-zero collider.
        foreach (var cube in dropCubes)
        {
            var cubeBounds = BoundsTransform.Transform(cube.Mesh.Bounds, cube.Transform.ToMatrix());
            collisionWorld.Overlap(cubeBounds, contactScratch);

            foreach (var contact in contactScratch)
            {
                if (ReferenceEquals(contact.Owner, cube)) continue;
                var hit = contact.Hit;
                cube.Transform.Position += hit.Normal * hit.Depth;
                cube.Physics.Velocity = CollisionResponse.ReflectVelocity(
                    cube.Physics.Velocity, hit.Normal, DropCubeRestitution);
            }
        }

        // ---- Cube vs cube (dynamic pairs) ---------------------------------------
        // Pairwise n² iteration over the drop cubes. Engine doesn't abstract this
        // pattern (no DynamicCollisionResolver) — pair iteration is short, and the
        // dynamic-pair concerns (layers, broadphase, navmesh) diverge from world-
        // collider concerns enough that they'd be a different abstraction anyway.
        //
        // Depenetration AND velocity response split by mobility share — equal-
        // mobility cubes each move half the overlap depth and absorb half the
        // impulse. A future heavier cube (lower NormalisedInverseMass) would move
        // less and the partner more, all from the same helper.
        for (var i = 0; i < dropCubes.Count; i++)
        {
            var a = dropCubes[i];
            var aBounds = BoundsTransform.Transform(a.Mesh.Bounds, a.Transform.ToMatrix());
            for (var j = i + 1; j < dropCubes.Count; j++)
            {
                var b = dropCubes[j];
                var bBounds = BoundsTransform.Transform(b.Mesh.Bounds, b.Transform.ToMatrix());
                if (Intersection.Test(aBounds, bBounds) is not { } hit) continue;

                var totalMobility = DropCubeNormalisedInverseMass + DropCubeNormalisedInverseMass;
                var aShare = DropCubeNormalisedInverseMass / totalMobility;
                var bShare = DropCubeNormalisedInverseMass / totalMobility;
                a.Transform.Position += hit.Normal * hit.Depth * aShare;
                b.Transform.Position -= hit.Normal * hit.Depth * bShare;

                var (newA, newB) = CollisionResponse.ReflectVelocities(
                    a.Physics.Velocity, DropCubeNormalisedInverseMass,
                    b.Physics.Velocity, DropCubeNormalisedInverseMass,
                    hit.Normal, DropCubeRestitution);
                a.Physics.Velocity = newA;
                b.Physics.Velocity = newB;
            }
        }
    }

    private void PerformPick()
    {
        // Build a world-space ray through the cursor's screen position, then test it
        // against everything we care about and keep the closest hit.
        //
        // Use the window's LOGICAL size (matching mouse coords' coordinate system),
        // NOT the framebuffer size from RenderFrameContext — on high-DPI displays
        // (Retina, Windows DPI scaling) those differ and mixing them sends the ray
        // into the wrong corner of the scene.
        var (logicalW, logicalH) = Host.LogicalSize;
        var ray = mainCamera.ScreenPointToRay(lastMouseX, lastMouseY, logicalW, logicalH);

        // Static world geometry: floor + ramp (registered as TriangleMesh3D colliders).
        var worldHit = collisionWorld.Raycast(ray);
        var bestDist = worldHit?.Hit.Time ?? float.PositiveInfinity;
        var bestPick = worldHit;

        // Drop cubes aren't in the CollisionWorld (dynamic-vs-dynamic stays demo-side),
        // so they get tested explicitly here. Each cube's world AABB is rebuilt for
        // the test since the transform may have moved since the last frame.
        foreach (var cube in dropCubes)
        {
            var cubeBounds = BoundsTransform.Transform(cube.Mesh.Bounds, cube.Transform.ToMatrix());
            if (Intersection.Raycast(ray, cubeBounds, bestDist) is { } hit && hit.Time < bestDist)
            {
                bestDist = hit.Time;
                bestPick = new CollisionContact3D<GameObject>(cube, CollisionLayer.Default, hit);
            }
        }

        lastPick = bestPick;
        lastInput = bestPick is { } pick
            ? $"picked {pick.Owner.Name} at {pick.Hit.Point}"
            : "pick missed";
    }

    private void ApplyCameraInput(float dt)
    {
        // Mouse handles rotation (when cursor captured). Keyboard only handles position.
        var forward = Vector3.Transform(-Vector3.UnitZ, mainCamera.Transform.Rotation);
        var right = Vector3.Transform(Vector3.UnitX, mainCamera.Transform.Rotation);

        var move = Vector3.Zero;
        if (heldKeys.Contains(Key.W)) move += forward;
        if (heldKeys.Contains(Key.S)) move -= forward;
        if (heldKeys.Contains(Key.D)) move += right;
        if (heldKeys.Contains(Key.A)) move -= right;

        if (move.LengthSquared() > 0.0f)
        {
            mainCamera.Transform.Position += Vector3.Normalize(move) * CameraMoveSpeed * dt;
        }
    }

    // FPS-style: yaw is applied around world Y first (right-most factor), then pitch around
    // the camera's local X. With column-vector composition, R = yaw * pitch.
    private static Quaternion BuildCameraRotation(float yaw, float pitch)
    {
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw) *
               Quaternion.CreateFromAxisAngle(Vector3.UnitX, pitch);
    }

    public override void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        var aspectRatio = frame.Height == 0 ? 1.0f : frame.Width / (float)frame.Height;

        var view = mainCamera.GetView();
        var projection = mainCamera.GetProjection(aspectRatio);
        var viewProjection = projection * view;

        var lightEye = mainLight.Direction * 6.0f;
        var lightView = GraphicsMatrices.CreateLookAt(lightEye, Vector3.Zero, Vector3.UnitY);
        // 7x7 ortho covers the 5x5 floor with margin for low-angle lights and any
        // geometry that pokes above the floor. Tighter than the previous 9x9 means
        // each shadow-map texel covers less world space - sharper shadows.
        var lightProjection = GraphicsMatrices.CreateOrthographic(width: 7.0f, height: 7.0f, nearPlane: 0.1f, farPlane: 16.0f);
        var lightViewProjection = lightProjection * lightView;

        // Per-spot shadow view-projection matrices. Shadow-casting spots are
        // collected once (up to the engine cap) and bound to shader uniforms at
        // fixed indices [0, count). Each spot's frustum is a perspective with
        // fov = 2 * outerCone so the lit cone fits the shadow frustum exactly.
        // Target = position + direction * range so the eye looks down the cone.
        var spotShadowVPs = new Matrix4x4[MaxSpotShadowCasters];
        var shadowCastingSpots = new List<SpotLight>();
        for (var i = 0; i < spotLights.Count && shadowCastingSpots.Count < MaxSpotShadowCasters; i++)
        {
            if (spotLights[i].CastsShadow) shadowCastingSpots.Add(spotLights[i]);
        }
        for (var i = 0; i < shadowCastingSpots.Count; i++)
        {
            var spot = shadowCastingSpots[i];
            var dir = Vector3.Normalize(spot.Direction);
            var up = MathF.Abs(Vector3.Dot(dir, Vector3.UnitY)) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
            var sView = GraphicsMatrices.CreateLookAt(spot.Position, spot.Position + dir * spot.Range, up);
            var sProj = GraphicsMatrices.CreatePerspective(
                verticalFieldOfView: 2.0f * spot.OuterConeAngle,
                aspectRatio: 1.0f,
                nearPlane: 0.05f,
                farPlane: spot.Range);
            spotShadowVPs[i] = sProj * sView;
        }

        var viewUniform = new ShaderUniform("uView", new Matrix4x4Uniform(view));
        var projectionUniform = new ShaderUniform("uProjection", new Matrix4x4Uniform(projection));
        var lightViewProjectionUniform = new ShaderUniform("uLightViewProjection", new Matrix4x4Uniform(lightViewProjection));
        var spotShadowCountUniform = new ShaderUniform("uSpotShadowCount", new FloatUniform(shadowCastingSpots.Count));

        // Collect shadow-casting point lights. Order matches the lit shader's
        // uPointShadowCubes[i] indexing convention -- shadow casters at the
        // front of uPointLights.
        var shadowCastingPoints = new List<PointLight>();
        for (var i = 0; i < pointLights.Count && shadowCastingPoints.Count < MaxPointShadowCasters; i++)
        {
            if (pointLights[i].CastsShadow) shadowCastingPoints.Add(pointLights[i]);
        }
        var pointShadowCountUniform = new ShaderUniform("uPointShadowCount", new FloatUniform(shadowCastingPoints.Count));

        // Per-frame uniforms shared by every lit draw — view/projection transforms,
        // lighting state, and the slider-controlled brightness multipliers. Bundled
        // once and passed via perDrawUniforms so each resolved per-object material
        // doesn't need per-frame mutation. mainLight.Direction points TO the light from a
        // surface (Lambert convention).
        //
        // Append the multi-light uniforms. Each point/spot light's fields
        // are bound by individual uniform name (`uPointLights[0].position`, etc.)
        // -- simple, no new uniform-value type. Light counts cap at 4 per type
        // (matches the shader's array size).
        var litFrameList = new List<ShaderUniform>
        {
            viewUniform,
            projectionUniform,
            lightViewProjectionUniform,
            new("uLightDirection", new Vector3Uniform(mainLight.Direction)),
            new("uCameraPosition", new Vector3Uniform(mainCamera.Transform.Position)),
            new("uLightIntensity", new FloatUniform(mainLight.Intensity)),
            new("uAmbientBoost", new FloatUniform(ambientBoost)),
            new("uSkyboxIntensity", new FloatUniform(skyboxIntensity)),
            new("uPointLightCount", new FloatUniform(Math.Min(pointLights.Count, 4))),
            new("uSpotLightCount",  new FloatUniform(Math.Min(spotLights.Count, 4))),
            spotShadowCountUniform,
            pointShadowCountUniform,
        };
        for (var i = 0; i < MaxSpotShadowCasters; i++)
        {
            litFrameList.Add(new($"uSpotShadowVPs[{i}]", new Matrix4x4Uniform(spotShadowVPs[i])));
        }
        for (var i = 0; i < MaxPointShadowCasters; i++)
        {
            var farPlane = i < shadowCastingPoints.Count ? shadowCastingPoints[i].Range : 1.0f;
            litFrameList.Add(new($"uPointShadowFarPlanes[{i}]", new FloatUniform(farPlane)));
        }
        for (var i = 0; i < pointLights.Count && i < 4; i++)
        {
            var pl = pointLights[i];
            var prefix = $"uPointLights[{i}]";
            litFrameList.Add(new($"{prefix}.position",  new Vector3Uniform(pl.Position)));
            litFrameList.Add(new($"{prefix}.color",     new Vector3Uniform(pl.Color)));
            litFrameList.Add(new($"{prefix}.intensity", new FloatUniform(pl.Intensity)));
            litFrameList.Add(new($"{prefix}.range",     new FloatUniform(pl.Range)));
        }
        for (var i = 0; i < spotLights.Count && i < 4; i++)
        {
            var sl = spotLights[i];
            var prefix = $"uSpotLights[{i}]";
            litFrameList.Add(new($"{prefix}.position",  new Vector3Uniform(sl.Position)));
            litFrameList.Add(new($"{prefix}.direction", new Vector3Uniform(Vector3.Normalize(sl.Direction))));
            litFrameList.Add(new($"{prefix}.color",     new Vector3Uniform(sl.Color)));
            litFrameList.Add(new($"{prefix}.intensity", new FloatUniform(sl.Intensity)));
            litFrameList.Add(new($"{prefix}.range",     new FloatUniform(sl.Range)));
            litFrameList.Add(new($"{prefix}.innerCos",  new FloatUniform(MathF.Cos(sl.InnerConeAngle))));
            litFrameList.Add(new($"{prefix}.outerCos",  new FloatUniform(MathF.Cos(sl.OuterConeAngle))));
        }
        var litFrameUniforms = litFrameList.ToArray();

        skyboxMaterial.SetUniform("uSkyboxIntensity", new FloatUniform(skyboxIntensity));

        ShaderUniform[] LitDraw(Matrix4x4 model, Matrix4x4 normalMatrix)
        {
            var result = new ShaderUniform[litFrameUniforms.Length + 2];
            Array.Copy(litFrameUniforms, result, litFrameUniforms.Length);
            result[litFrameUniforms.Length]     = new ShaderUniform("uModel", new Matrix4x4Uniform(model));
            result[litFrameUniforms.Length + 1] = new ShaderUniform("uNormalMatrix", new Matrix4x4Uniform(normalMatrix));
            return result;
        }

        commandList.Pass(
            "shadow",
            new RenderPassDescription(shadowSurface.Handle, ClearColors: Array.Empty<GraphicsColor?>(), ClearDepth: true),
            pass =>
            {
                // Opaque casters only. Glass is excluded — its self-overlapping geometry
                // pollutes the shadow map (see Shadow Maps section in docs/renderer.md).
                foreach (var obj in opaqueObjects)
                {
                    pass.DrawMesh(obj.Mesh, shadowMaterial, perDrawUniforms:
                    [
                        new ShaderUniform("uModel", new Matrix4x4Uniform(obj.Transform.ToMatrix())),
                        lightViewProjectionUniform
                    ]);
                }

                // Skinned shadow casters. Uses the skinShadowMaterial
                // (shadow.skin.vert + shadow.frag) with the rig's bone palette so
                // the cast shadow reflects the current pose, not the bind pose.
                foreach (var rig in skinnedObjects)
                {
                    var modelMatrix = rig.Transform.ToMatrix() * rig.MeshNodeTransform;
                    var modelUniform = new ShaderUniform("uModel", new Matrix4x4Uniform(modelMatrix));
                    var bonesUniform = new ShaderUniform("uBones", new Matrix4x4ArrayUniform(rig.Palette.Matrices));
                    foreach (var submesh in rig.Submeshes)
                    {
                        pass.DrawMesh(submesh.Mesh, skinShadowMaterial, perDrawUniforms:
                        [
                            modelUniform,
                            lightViewProjectionUniform,
                            bonesUniform,
                        ]);
                    }
                }
            });

        // Point-light cubemap shadow passes. Six passes per shadow-casting
        // point light, one per cubemap face. Each face has a fixed forward/up
        // pair following the standard OpenGL cubemap convention. Far plane =
        // light.Range so the cubemap depth and the shader's normalised reference
        // depth agree.
        //
        // (forward, up) per face: 0=+X, 1=-X, 2=+Y, 3=-Y, 4=+Z, 5=-Z
        var pointFaceBases = new[]
        {
            ( new Vector3( 1f,  0f,  0f), new Vector3(0f, -1f,  0f)),
            ( new Vector3(-1f,  0f,  0f), new Vector3(0f, -1f,  0f)),
            ( new Vector3( 0f,  1f,  0f), new Vector3(0f,  0f,  1f)),
            ( new Vector3( 0f, -1f,  0f), new Vector3(0f,  0f, -1f)),
            ( new Vector3( 0f,  0f,  1f), new Vector3(0f, -1f,  0f)),
            ( new Vector3( 0f,  0f, -1f), new Vector3(0f, -1f,  0f)),
        };
        for (var casterIndex = 0; casterIndex < shadowCastingPoints.Count; casterIndex++)
        {
            var pointLight = shadowCastingPoints[casterIndex];
            var pointProj = GraphicsMatrices.CreatePerspective(
                verticalFieldOfView: MathF.PI * 0.5f,   // 90 deg per face
                aspectRatio: 1.0f,
                nearPlane: 0.05f,
                farPlane: pointLight.Range);
            for (var face = 0; face < 6; face++)
            {
                var (fwd, up) = pointFaceBases[face];
                var faceView = GraphicsMatrices.CreateLookAt(pointLight.Position, pointLight.Position + fwd, up);
                var vp = pointProj * faceView;
                var surface = pointShadowFaceSurfaces[casterIndex, face]
                    ?? throw new InvalidOperationException($"Point shadow face surface {casterIndex}.{face} missing.");
                var vpUniform = new ShaderUniform("uLightViewProjection", new Matrix4x4Uniform(vp));
                commandList.Pass(
                    $"shadow.point.{casterIndex}.face{face}",
                    new RenderPassDescription(surface.Handle, ClearColors: Array.Empty<GraphicsColor?>(), ClearDepth: true),
                    pass =>
                    {
                        foreach (var obj in opaqueObjects)
                        {
                            pass.DrawMesh(obj.Mesh, shadowMaterial, perDrawUniforms:
                            [
                                new ShaderUniform("uModel", new Matrix4x4Uniform(obj.Transform.ToMatrix())),
                                vpUniform,
                            ]);
                        }
                        foreach (var rig in skinnedObjects)
                        {
                            var modelMatrix = rig.Transform.ToMatrix() * rig.MeshNodeTransform;
                            var modelUniform = new ShaderUniform("uModel", new Matrix4x4Uniform(modelMatrix));
                            var bonesUniform = new ShaderUniform("uBones", new Matrix4x4ArrayUniform(rig.Palette.Matrices));
                            foreach (var submesh in rig.Submeshes)
                            {
                                pass.DrawMesh(submesh.Mesh, skinShadowMaterial, perDrawUniforms:
                                [
                                    modelUniform,
                                    vpUniform,
                                    bonesUniform,
                                ]);
                            }
                        }
                    });
            }
        }

        // Spot shadow passes. One pass per shadow-casting spot, each rendering
        // into its own depth surface with the spot's view-projection bound as
        // uLightViewProjection. Reuses the existing shadow pipeline + skin
        // shadow material -- the only thing that changes per pass is the VP
        // matrix and the render-surface target.
        for (var casterIndex = 0; casterIndex < shadowCastingSpots.Count; casterIndex++)
        {
            var surface = spotShadowSurfaces[casterIndex]
                ?? throw new InvalidOperationException($"Spot shadow surface {casterIndex} missing.");
            var spotLightVPUniform = new ShaderUniform("uLightViewProjection", new Matrix4x4Uniform(spotShadowVPs[casterIndex]));
            commandList.Pass(
                $"shadow.spot.{casterIndex}",
                new RenderPassDescription(surface.Handle, ClearColors: Array.Empty<GraphicsColor?>(), ClearDepth: true),
                pass =>
                {
                    foreach (var obj in opaqueObjects)
                    {
                        pass.DrawMesh(obj.Mesh, shadowMaterial, perDrawUniforms:
                        [
                            new ShaderUniform("uModel", new Matrix4x4Uniform(obj.Transform.ToMatrix())),
                            spotLightVPUniform,
                        ]);
                    }
                    foreach (var rig in skinnedObjects)
                    {
                        var modelMatrix = rig.Transform.ToMatrix() * rig.MeshNodeTransform;
                        var modelUniform = new ShaderUniform("uModel", new Matrix4x4Uniform(modelMatrix));
                        var bonesUniform = new ShaderUniform("uBones", new Matrix4x4ArrayUniform(rig.Palette.Matrices));
                        foreach (var submesh in rig.Submeshes)
                        {
                            pass.DrawMesh(submesh.Mesh, skinShadowMaterial, perDrawUniforms:
                            [
                                modelUniform,
                                spotLightVPUniform,
                                bonesUniform,
                            ]);
                        }
                    }
                });
        }

        var sceneBackground = new GraphicsColor(0.08f, 0.10f, 0.16f, 1.0f);
        var luminanceBackground = new GraphicsColor(0.0f, 0.0f, 0.0f, 1.0f);
        var normalBackground = new GraphicsColor(0.5f, 0.5f, 1.0f, 1.0f);

        commandList.Pass(
            "scene",
            new RenderPassDescription(
                sceneSurface.Handle,
                ClearColors: [sceneBackground, luminanceBackground, normalBackground],
                ClearDepth: true),
            pass =>
            {
                // Material identity (specular/shininess/reflectance/normalScale + diffuse
                // and normal-map textures) lives in each material's JSON. Per-draw here
                // is just transforms + the shared per-frame uniforms via LitDraw.
                foreach (var obj in opaqueObjects)
                {
                    var model = obj.Transform.ToMatrix();
                    pass.DrawMesh(obj.Mesh, obj.Material,
                        LitDraw(model, GraphicsMatrices.CreateNormalMatrix(model)));
                }

                // Skinned game objects. Pose + palette were already
                // updated by SkinnedGameObject.Update in OnUpdate; the scene pass
                // just reads Palette.Matrices to bind the bone-palette uniform.
                // Lives outside opaqueObjects because the skin pipeline has its own
                // vertex layout (bone indices/weights) and uniform contract.
                foreach (var rig in skinnedObjects)
                {
                    // Skinned content draws through the lit pipeline, so
                    // the per-draw uniform block is LitDraw (shared scene state +
                    // model + normal matrix) plus the bone palette. uModel composes
                    // the user transform with the asset's intrinsic orientation
                    // matrix; uNormalMatrix derives from the same composition.
                    var modelMatrix = rig.Transform.ToMatrix() * rig.MeshNodeTransform;
                    var normalMatrix = GraphicsMatrices.CreateNormalMatrix(modelMatrix);
                    var bonesUniform = new ShaderUniform("uBones", new Matrix4x4ArrayUniform(rig.Palette.Matrices));
                    var litUniforms = LitDraw(modelMatrix, normalMatrix);
                    var perDraw = new ShaderUniform[litUniforms.Length + 1];
                    Array.Copy(litUniforms, perDraw, litUniforms.Length);
                    perDraw[^1] = bonesUniform;
                    foreach (var submesh in rig.Submeshes)
                    {
                        pass.DrawMesh(submesh.Mesh, submesh.Material, perDrawUniforms: perDraw);
                    }
                }

                // The torus knot is moved to the glass pass — renders after the scene
                // copy snapshot so it can sample what's behind it through refraction.

                // Skybox renders last so its pixels only land on uncovered (depth=1)
                // areas of the scene. Reuses the cube mesh as a unit cube centered on
                // the camera (the skybox vertex shader strips the view translation).
                pass.DrawMesh(cubeMesh, skyboxMaterial, perDrawUniforms:
                [
                    viewUniform,
                    projectionUniform
                ]);
            });

        commandList.Pass(
            "fur",
            new RenderPassDescription(sceneSurface.Handle, ClearColors: Array.Empty<GraphicsColor?>(), ClearDepth: false),
            pass =>
            {
                var shellCount = Math.Clamp((int)MathF.Round(furShellCount), (int)FurShellCountMin, (int)FurShellCountMax);

                foreach (var obj in furObjects)
                {
                    var model = obj.Transform.ToMatrix();
                    var normalMatrix = GraphicsMatrices.CreateNormalMatrix(model);
                    for (var shell = 0; shell < shellCount; shell++)
                    {
                        pass.DrawMesh(obj.Mesh, obj.Material, perDrawUniforms:
                        [
                            new ShaderUniform("uModel", new Matrix4x4Uniform(model)),
                            new ShaderUniform("uNormalMatrix", new Matrix4x4Uniform(normalMatrix)),
                            viewUniform,
                            projectionUniform,
                            new ShaderUniform("uShellIndex", new FloatUniform(shell)),
                            new ShaderUniform("uShellCount", new FloatUniform(shellCount)),
                            new ShaderUniform("uFurLength", new FloatUniform(furLength)),
                            new ShaderUniform("uFurDensity", new FloatUniform(furDensity)),
                            new ShaderUniform("uWindStrength", new FloatUniform(furWind)),
                            new ShaderUniform("uTime", new FloatUniform((float)time.Total)),
                            new ShaderUniform("uLightDirection", new Vector3Uniform(mainLight.Direction)),
                            new ShaderUniform("uCameraPosition", new Vector3Uniform(mainCamera.Transform.Position)),
                            new ShaderUniform("uLightIntensity", new FloatUniform(mainLight.Intensity)),
                            new ShaderUniform("uAmbientBoost", new FloatUniform(ambientBoost))
                        ]);
                    }
                }
            });

        commandList.Pass(
            "hologram",
            new RenderPassDescription(sceneSurface.Handle, ClearColors: Array.Empty<GraphicsColor?>(), ClearDepth: false),
            pass =>
            {
                foreach (var obj in hologramObjects)
                {
                    var model = obj.Transform.ToMatrix();
                    pass.DrawMesh(obj.Mesh, obj.Material, perDrawUniforms:
                    [
                        new ShaderUniform("uModel", new Matrix4x4Uniform(model)),
                        new ShaderUniform("uNormalMatrix", new Matrix4x4Uniform(GraphicsMatrices.CreateNormalMatrix(model))),
                        viewUniform,
                        projectionUniform,
                        new ShaderUniform("uCameraPosition", new Vector3Uniform(mainCamera.Transform.Position)),
                        new ShaderUniform("uHologramColor", new Vector3Uniform(new Vector3(0.18f, 0.9f, 1.0f))),
                        new ShaderUniform("uOpacity", new FloatUniform(hologramOpacity)),
                        new ShaderUniform("uRimStrength", new FloatUniform(hologramRim)),
                        new ShaderUniform("uScanlineDensity", new FloatUniform(hologramScanlines)),
                        new ShaderUniform("uGlitchAmount", new FloatUniform(hologramGlitch)),
                        new ShaderUniform("uTime", new FloatUniform((float)time.Total))
                    ]);
                }
            });

        // Snapshot of the opaque scene + skybox. The glass pass below samples this for
        // refraction; can't read directly from sceneSurface because we're about to write
        // into it again.
        commandList.Pass(
            "scene.copy",
            new RenderPassDescription(sceneCopySurface.Handle, ClearColors: Array.Empty<GraphicsColor?>(), ClearDepth: false),
            pass =>
            {
                pass.DrawMesh(fullscreenMesh, copyMaterial, perDrawUniforms: null, perDrawTextures:
                [
                    new ShaderTextureBinding("uSceneTexture", sceneSurface.ColorAttachments[0], Slot: 0)
                ]);
            });

        // Glass pass: refractive dielectric draws on top of the opaque scene, using the
        // snapshot for refraction. Writes back into sceneSurface so bloom and the final
        // present pick the glass highlights up naturally.
        commandList.Pass(
            "glass",
            new RenderPassDescription(sceneSurface.Handle, ClearColors: Array.Empty<GraphicsColor?>(), ClearDepth: false),
            pass =>
            {
                foreach (var obj in glassObjects)
                {
                    var model = obj.Transform.ToMatrix();
                    pass.DrawMesh(obj.Mesh, obj.Material, perDrawUniforms:
                    [
                        new ShaderUniform("uModel", new Matrix4x4Uniform(model)),
                        new ShaderUniform("uNormalMatrix", new Matrix4x4Uniform(GraphicsMatrices.CreateNormalMatrix(model))),
                        viewUniform,
                        projectionUniform,
                        new ShaderUniform("uCameraPosition", new Vector3Uniform(mainCamera.Transform.Position)),
                        new ShaderUniform("uSkyboxIntensity", new FloatUniform(skyboxIntensity)),
                        // Slider-driven; overrides material JSON defaults each frame.
                        new ShaderUniform("uThickness", new FloatUniform(glassThickness)),
                        new ShaderUniform("uF0", new FloatUniform(glassF0))
                    ]);
                }
            });

        // Bloom + composite path. The final beauty mode composites bloom; bloom debug
        // modes still run the bloom chain, then present one intermediate level directly.
        if (UsesBloomChain(presentMode))
        {
            var sceneHdr = sceneSurface.ColorAttachments[0];
            var horizontalDir = new ShaderUniform("uDirection", new Vector2Uniform(new Vector2(1.0f, 0.0f)));
            var verticalDir = new ShaderUniform("uDirection", new Vector2Uniform(new Vector2(0.0f, 1.0f)));

            for (var i = 0; i < BloomLevelCount; i++)
            {
                var brightSurface = bloomBrightSurfaces[i];
                var tempSurface = bloomTempSurfaces[i];

                // Bright pass at this level: read the full-res HDR scene, apply the
                // soft >1.0 threshold, write to brightSurface (which is smaller, so
                // bilinear sampling implicitly downsamples).
                commandList.Pass(
                    $"bloom.bright[{i}]",
                    new RenderPassDescription(brightSurface.Handle, ClearColors: Array.Empty<GraphicsColor?>(), ClearDepth: false),
                    pass =>
                    {
                        pass.DrawMesh(fullscreenMesh, brightMaterial, perDrawUniforms: null, perDrawTextures:
                        [
                            new ShaderTextureBinding("uSceneTexture", sceneHdr, Slot: 0)
                        ]);
                    });

                // Separable Gaussian: H pass writes the bright surface contents to temp,
                // V pass reads temp and writes back to bright. After both, bright holds
                // the fully blurred bloom at this level.
                commandList.Pass(
                    $"bloom.blurH[{i}]",
                    new RenderPassDescription(tempSurface.Handle, ClearColors: Array.Empty<GraphicsColor?>(), ClearDepth: false),
                    pass =>
                    {
                        pass.DrawMesh(fullscreenMesh, blurMaterial, perDrawUniforms: [horizontalDir], perDrawTextures:
                        [
                            new ShaderTextureBinding("uSceneTexture", brightSurface.ColorAttachments[0], Slot: 0)
                        ]);
                    });
                commandList.Pass(
                    $"bloom.blurV[{i}]",
                    new RenderPassDescription(brightSurface.Handle, ClearColors: Array.Empty<GraphicsColor?>(), ClearDepth: false),
                    pass =>
                    {
                        pass.DrawMesh(fullscreenMesh, blurMaterial, perDrawUniforms: [verticalDir], perDrawTextures:
                        [
                            new ShaderTextureBinding("uSceneTexture", tempSurface.ColorAttachments[0], Slot: 0)
                        ]);
                    });
            }

            if (presentMode == PresentModeColor)
            {
                // Composite scene + bloom levels with tone mapping + gamma encode to display.
                commandList.Pass(
                    "present.bloom",
                    new RenderPassDescription(
                        RenderSurfaceHandle.Default,
                        ClearColors: [new GraphicsColor(0.0f, 0.0f, 0.0f, 1.0f)],
                        ClearDepth: false),
                    pass =>
                    {
                        pass.DrawMesh(fullscreenMesh, bloomCompositeMaterial,
                            perDrawUniforms:
                            [
                                new ShaderUniform("uBloomStrength", new FloatUniform(bloomStrength)),
                                new ShaderUniform("uExposure", new FloatUniform(exposure))
                            ],
                            perDrawTextures:
                            [
                                new ShaderTextureBinding("uSceneTexture", sceneHdr, Slot: 0),
                                new ShaderTextureBinding("uBloom0", bloomBrightSurfaces[0].ColorAttachments[0], Slot: 1),
                                new ShaderTextureBinding("uBloom1", bloomBrightSurfaces[1].ColorAttachments[0], Slot: 2),
                                new ShaderTextureBinding("uBloom2", bloomBrightSurfaces[2].ColorAttachments[0], Slot: 3)
                            ]);
                    });
            }
            else
            {
                PresentDebugTexture(commandList);
            }
        }
        else
        {
            PresentDebugTexture(commandList);
        }

        void PresentDebugTexture(RenderCommandList commands)
        {
            var (presentMaterial, presentSource) = SelectPresent(presentMode);

            commands.Pass(
                "present",
                new RenderPassDescription(
                    RenderSurfaceHandle.Default,
                    ClearColors: [new GraphicsColor(0.0f, 0.0f, 0.0f, 1.0f)],
                    ClearDepth: false),
                pass =>
                {
                    pass.DrawMesh(
                        fullscreenMesh,
                        presentMaterial,
                        perDrawUniforms: null,
                        perDrawTextures:
                        [
                            new ShaderTextureBinding("uSceneTexture", presentSource, Slot: 0)
                        ]);
                });
        }

        if ((Host as IDebugHost)?.CurrentDebug is { State.ShowDebugDraw: true } debug)
        {
            debug.Draw.ViewProjection = viewProjection;
            debug.Draw.Grid("World Grid", center: Vector3.Zero, size: 4.0f, divisions: 8, color: new GraphicsColor(0.35f, 0.35f, 0.35f, 1.0f));
            debug.Draw.Frustum("Light Frustum", lightViewProjection, color: new GraphicsColor(1.0f, 0.9f, 0.2f, 1.0f));

            // Picked-object highlight — outline only the object the last right-click
            // landed on. Per-object debug AABBs are intentionally not drawn here
            // (was too noisy as the scene grew); the pick AABB is the only bounds
            // viz so what you see is unambiguously what picking selected.
            if (lastPick is { } pick)
            {
                var pickedBounds = BoundsTransform.Transform(
                    pick.Owner.Mesh.Bounds, pick.Owner.Transform.ToMatrix());
                debug.Draw.Aabb("Picked",
                    pickedBounds.Min, pickedBounds.Max,
                    new GraphicsColor(1.0f, 0.0f, 1.0f, 1.0f));
            }
        }

        DrawHud(time, frame, commandList);
    }

    // HUD overlay. Writes to the default framebuffer in a final pass after
    // present so the UI sits on top of every other layer. Coords are in LOGICAL
    // points (origin top-left, Y down) — the ortho uses LogicalSize and the
    // framebuffer/logical ratio is forwarded as dpiScale so text picks a baked
    // atlas at the physical-pixel resolution. EMA-smooths the per-frame delta so
    // the FPS number doesn't twitch frame to frame.
    private void DrawHud(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        if (spriteBatch is null || frame.Width <= 0 || frame.Height <= 0)
        {
            return;
        }

        var (logicalW, logicalH) = Host.LogicalSize;
        if (logicalW <= 0 || logicalH <= 0)
        {
            return;
        }
        var dpiScale = frame.Width / (float)logicalW;

        var dt = (float)time.Delta;
        if (dt > 0.0f)
        {
            var instantFps = 1.0f / dt;
            // Fast EMA: 0.1 weight on the new sample. Roughly a 10-frame moving average.
            fpsSmoothed = fpsSmoothed == 0.0f ? instantFps : fpsSmoothed * 0.9f + instantFps * 0.1f;
        }

        var screenOrtho = GraphicsMatrices.CreateOrthographicOffCenter(
            left: 0.0f, right: logicalW,
            bottom: logicalH, top: 0.0f,
            nearPlane: -1.0f, farPlane: 1.0f);

        commandList.Pass(
            "hud",
            new RenderPassDescription(
                RenderSurfaceHandle.Default,
                ClearColors: Array.Empty<GraphicsColor?>(),
                ClearDepth: false),
            pass =>
            {
                spriteBatch.Begin(screenOrtho);

                // Panel backing: semi-transparent dark box behind the text so the HUD
                // stays legible against any scene background. 12pt padding around the
                // text block; height grows to fit the three lines below.
                const float padding = 12.0f;
                const float topLeftX = 16.0f;
                const float topLeftY = 16.0f;
                const float panelWidth = 280.0f;
                const float textPointSize = 16.0f;
                float lineHeight;
                if (hudFont is null)
                {
                    lineHeight = textPointSize * 1.2f;
                }
                else
                {
                    // Atlas metrics are in atlas-pixels; rescale to logical points.
                    var bakedSize = hudFont.NearestSize(textPointSize * dpiScale);
                    lineHeight = bakedSize.LineHeight * (textPointSize / bakedSize.PixelSize);
                }
                var panelHeight = padding * 2.0f + lineHeight * 3.0f;

                spriteBatch.DrawSolidRect(
                    hudWhitePixel,
                    new Rect(topLeftX, topLeftY, panelWidth, panelHeight),
                    new GraphicsColor(0.0f, 0.0f, 0.0f, 0.55f));

                if (hudFont is not null)
                {
                    var camPos = mainCamera.Transform.Position;
                    var text =
                        $"FPS  {fpsSmoothed:0.0}\n" +
                        $"Cam  ({camPos.X:0.00}, {camPos.Y:0.00}, {camPos.Z:0.00})\n" +
                        $"View {PresentModeLabel(presentMode)}   Cmd/Ctrl+C debug  Esc quit";
                    spriteBatch.DrawText(
                        hudFont,
                        pixelSize: textPointSize,
                        text,
                        new Vector2(topLeftX + padding, topLeftY + padding),
                        new GraphicsColor(0.95f, 0.95f, 0.95f, 1.0f),
                        dpiScale: dpiScale);
                }

                spriteBatch.End(pass);
            });
    }

    // Extract PlaneMesh's local positions, transform through `worldTransform` (the
    // owning GameObject's model matrix), and pack into a TriangleMesh3D ready for
    // CollisionWorld3D.Add. Used for any GameObject whose visual is the procedural
    // PlaneMesh primitive — same conversion would work for any procedural primitive
    // with a known Vertices/Indices pair.
    private static TriangleMesh3D BuildPlaneTriangleCollider(Matrix4x4 worldTransform)
    {
        var positions = new Vector3[PlaneMesh.Vertices.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            var local = PlaneMesh.Vertices[i].Position;
            positions[i] = GraphicsMatrices.TransformPoint(
                worldTransform, new Vector3(local.X, local.Y, local.Z));
        }
        return TriangleMesh3D.FromIndexed(positions, PlaneMesh.Indices);
    }

    // Build a Material for one submesh from its glTF material against the
    // lit skin pipeline (skin.lit.vert + cube.frag, now PBR). Wires the PBR
    // uniform set the fragment shader expects:
    //   - uTexture (baseColor or white fallback) at slot 0
    //   - uShadowMap (scene-shared) at slot 1
    //   - uEnvMap (scene-shared cubemap, mipmapped) at slot 2
    //   - uNormalMap (glTF normal or flat-blue fallback) at slot 3
    //   - uMetallicRoughnessMap (glTF MR or neutral default) at slot 4
    //   - uBaseColorFactor / uMetallicFactor / uRoughnessFactor from the glTF
    //   - uNormalScale (1 when a map is bound, 0 for the fallback)
    //   - uEnvMapMipCount for textureLod-based IBL specular sampling
    private static Material BuildSkinLitMaterial(
        IGraphicsDevice device,
        PipelineHandle skinLitPipeline,
        GltfMaterial? gltfMaterial,
        TextureHandle whiteFallback,
        TextureHandle flatNormalFallback,
        TextureHandle neutralMetallicRoughnessFallback,
        TextureHandle shadowMap,
        TextureHandle envMap,
        TextureHandle[] spotShadowMaps,
        TextureHandle[] pointShadowCubes,
        float envMapMipCount,
        string namePrefix)
    {
        var material = new Material($"skin.lit.{gltfMaterial?.Name ?? "default"}", skinLitPipeline);

        TextureHandle baseColorHandle;
        if (gltfMaterial?.BaseColorTexture is { } baseColorTexture)
        {
            var image = new Blix.Graphics.Images.ImageData(
                baseColorTexture.Width, baseColorTexture.Height,
                TextureFormat.Rgba8, baseColorTexture.RgbaPixels);
            baseColorHandle = device.CreateTexture2D(image, SamplerDescription.LinearRepeat, name: $"{namePrefix}.{baseColorTexture.Name}");
        }
        else
        {
            baseColorHandle = whiteFallback;
        }
        material.SetTexture("uTexture", baseColorHandle, slot: 0);

        TextureHandle normalHandle;
        float normalScale;
        if (gltfMaterial?.NormalTexture is { } normalTexture)
        {
            var image = new Blix.Graphics.Images.ImageData(
                normalTexture.Width, normalTexture.Height,
                TextureFormat.Rgba8, normalTexture.RgbaPixels);
            normalHandle = device.CreateTexture2D(image, SamplerDescription.LinearRepeat, name: $"{namePrefix}.{normalTexture.Name}");
            normalScale = 1.0f;
        }
        else
        {
            normalHandle = flatNormalFallback;
            normalScale = 0.0f;
        }
        material.SetTexture("uNormalMap", normalHandle, slot: 3);

        TextureHandle metallicRoughnessHandle;
        if (gltfMaterial?.MetallicRoughnessTexture is { } mrTexture)
        {
            var image = new Blix.Graphics.Images.ImageData(
                mrTexture.Width, mrTexture.Height,
                TextureFormat.Rgba8, mrTexture.RgbaPixels);
            metallicRoughnessHandle = device.CreateTexture2D(image, SamplerDescription.LinearRepeat, name: $"{namePrefix}.{mrTexture.Name}");
        }
        else
        {
            metallicRoughnessHandle = neutralMetallicRoughnessFallback;
        }
        material.SetTexture("uMetallicRoughnessMap", metallicRoughnessHandle, slot: 4);

        material.SetTexture("uShadowMap", shadowMap, slot: 1);
        material.SetTexture("uEnvMap", envMap, slot: 2);
        // Spot + point shadow maps -- same slot layout as the lit materials so
        // the unified shader's expectations match. Every cap slot is bound; the
        // shader gates sampling on uSpotShadowCount / uPointShadowCount.
        for (var i = 0; i < spotShadowMaps.Length; i++)
        {
            material.SetTexture($"uSpotShadowMaps[{i}]", spotShadowMaps[i], slot: 5 + i);
        }
        for (var i = 0; i < pointShadowCubes.Length; i++)
        {
            material.SetTexture($"uPointShadowCubes[{i}]", pointShadowCubes[i], slot: 5 + spotShadowMaps.Length + i);
        }

        var factor = gltfMaterial?.BaseColorFactor ?? Vector4.One;
        material.SetUniform("uBaseColorFactor", new Vector4Uniform(factor));
        material.SetUniform("uMetallicFactor", new FloatUniform(gltfMaterial?.MetallicFactor ?? 1.0f));
        material.SetUniform("uRoughnessFactor", new FloatUniform(gltfMaterial?.RoughnessFactor ?? 1.0f));
        material.SetUniform("uNormalScale", new FloatUniform(normalScale));
        material.SetUniform("uEnvMapMipCount", new FloatUniform(envMapMipCount));

        return material;
    }

    public void OnKeyDown(Key key)
    {
        lastInput = $"key {key}";
        heldKeys.Add(key);

        switch (key)
        {
            case Key.Escape:
                Host.RequestClose();
                break;

            case Key.C:
                // Cmd/Ctrl+C is the single dev-mode toggle: flips cursor capture, and
                // ShowDebug (derived from !cursorCaptured) takes the debug overlay with
                // it. Bare C is intentionally unbound so the dev hotkey doesn't fire on
                // accidental presses during gameplay.
                if (heldKeys.Contains(Key.LeftControl) ||
                    heldKeys.Contains(Key.RightControl) ||
                    heldKeys.Contains(Key.LeftSuper) ||
                    heldKeys.Contains(Key.RightSuper))
                {
                    cursorCaptured = !cursorCaptured;
                    Host.SetCursorCaptured(cursorCaptured);
                }
                break;

            case Key.R:
                ResetView();
                break;
        }
    }

    private void ResetView()
    {
        mainCamera.Transform.Position = InitialCameraPosition;
        cameraYaw = InitialCameraYaw;
        cameraPitch = InitialCameraPitch;
        mainCamera.Transform.Rotation = BuildCameraRotation(cameraYaw, cameraPitch);
        mainLight.Direction = InitialLightDirection;
        mainLight.Intensity = InitialLightIntensity;
        ambientBoost = InitialAmbientBoost;
        skyboxIntensity = InitialSkyboxIntensity;
        bloomStrength = InitialBloomStrength;
        exposure = InitialExposure;
        glassThickness = InitialGlassThickness;
        glassF0 = InitialGlassF0;
        furShellCount = InitialFurShellCount;
        furLength = InitialFurLength;
        furDensity = InitialFurDensity;
        furWind = InitialFurWind;
        hologramOpacity = InitialHologramOpacity;
        hologramRim = InitialHologramRim;
        hologramScanlines = InitialHologramScanlines;
        hologramGlitch = InitialHologramGlitch;
        lastInput = "reset";
    }

    public void OnKeyUp(Key key)
    {
        heldKeys.Remove(key);
    }

    public void OnMouseDown(MouseButton button)
    {
        lastInput = $"mouse {button} down";
        if (button == MouseButton.Left)
        {
            // Left-click serves two purposes depending on mode:
            //   - debug overlay on (B) + cursor released (C): pick. The user can aim
            //     a cursor at scene geometry to inspect what the ray hits.
            //   - everything else (cursor captured, or debug off): light-drag, the
            //     existing gameplay-input behaviour. leftMouseDown stays in sync so
            //     OnMouseMove + OnMouseUp follow their normal flow.
            var debugActive = (Host as IDebugHost)?.CurrentDebug is { State.ShowDebugDraw: true };
            if (!cursorCaptured && debugActive)
            {
                PerformPick();
            }
            else
            {
                leftMouseDown = true;
            }
        }
    }

    public void OnMouseUp(MouseButton button)
    {
        lastInput = $"mouse {button} up";
        if (button == MouseButton.Left)
        {
            leftMouseDown = false;
        }
    }

    public void OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        // Track absolute screen position even when the cursor is captured — picking
        // (right-click) requires it when the cursor is later released.
        lastMouseX = x;
        lastMouseY = y;

        // Mouse-driven rotation is gated on cursor capture. When the cursor is
        // released (C key), the user is probably aiming for a pick or just wants to
        // stop accidentally moving the camera; suppress rotation.
        if (!cursorCaptured)
        {
            return;
        }

        if (leftMouseDown)
        {
            // LMB drag steers the light. Horizontal delta = yaw around world Y, vertical
            // delta = pitch around the axis perpendicular to mainLight.Direction and world up.
            if (deltaX != 0.0f)
            {
                mainLight.Direction = Vector3.Normalize(Vector3.Transform(
                    mainLight.Direction,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitY, -deltaX * LightDragSensitivity)));
            }
            if (deltaY != 0.0f)
            {
                var axis = Vector3.Cross(mainLight.Direction, Vector3.UnitY);
                if (axis.LengthSquared() > 1e-6f)
                {
                    mainLight.Direction = Vector3.Normalize(Vector3.Transform(
                        mainLight.Direction,
                        Quaternion.CreateFromAxisAngle(
                            Vector3.Normalize(axis),
                            -deltaY * LightDragSensitivity)));
                }
            }
            lastInput = $"light drag dx={deltaX:0} dy={deltaY:0}";
            return;
        }

        // No-LMB: mouse motion rotates the camera. Horizontal delta yaws, vertical pitches.
        cameraYaw -= deltaX * MouseLookSensitivity;
        cameraPitch -= deltaY * MouseLookSensitivity;
        cameraPitch = Math.Clamp(cameraPitch, -MathF.PI / 2.0f + 0.05f, MathF.PI / 2.0f - 0.05f);
        mainCamera.Transform.Rotation = BuildCameraRotation(cameraYaw, cameraPitch);
        lastInput = $"look dx={deltaX:0} dy={deltaY:0}";
    }

    public void OnMouseWheel(float offsetX, float offsetY)
    {
        // Wheel intentionally unbound in the current scheme - the user's spec only
        // assigns keyboard and mouse buttons.
        lastInput = $"wheel {offsetX:0.##},{offsetY:0.##}";
    }

    private (Material Material, TextureHandle Source) SelectPresent(int mode)
    {
        return mode switch
        {
            PresentModeColor => (presentColorMaterial, sceneSurface.ColorAttachments[0]),
            PresentModeSceneColor => (presentColorMaterial, sceneSurface.ColorAttachments[0]),
            PresentModeOpaqueColor => (presentColorMaterial, sceneCopySurface.ColorAttachments[0]),
            PresentModeLuminance => (presentColorMaterial, sceneSurface.ColorAttachments[1]),
            PresentModeNormals => (presentColorMaterial, sceneSurface.ColorAttachments[2]),
            PresentModeDepth => (presentDepthMaterial, sceneSurface.DepthTexture
                ?? throw new InvalidOperationException("Scene surface has no depth texture.")),
            PresentModeShadowMap => (presentShadowMapMaterial, shadowSurface.DepthTexture
                ?? throw new InvalidOperationException("Shadow surface has no depth texture.")),
            PresentModeBloom0 => (presentColorMaterial, bloomBrightSurfaces[0].ColorAttachments[0]),
            PresentModeBloom1 => (presentColorMaterial, bloomBrightSurfaces[1].ColorAttachments[0]),
            PresentModeBloom2 => (presentColorMaterial, bloomBrightSurfaces[2].ColorAttachments[0]),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown present mode.")
        };
    }

    private static bool UsesBloomChain(int mode)
    {
        return mode is PresentModeColor or PresentModeBloom0 or PresentModeBloom1 or PresentModeBloom2;
    }

    private static string PresentModeLabel(int mode)
    {
        return mode switch
        {
            PresentModeColor => "final",
            PresentModeSceneColor => "scene",
            PresentModeOpaqueColor => "opaque",
            PresentModeLuminance => "luminance",
            PresentModeNormals => "normals",
            PresentModeDepth => "depth",
            PresentModeShadowMap => "shadowmap",
            PresentModeBloom0 => "bloom0",
            PresentModeBloom1 => "bloom1",
            PresentModeBloom2 => "bloom2",
            _ => "?"
        };
    }

    private static Bounds3 CreateBounds(IReadOnlyList<VertexPosition3NormalTexture> vertices)
    {
        var positions = new Vector3[vertices.Count];
        for (var i = 0; i < vertices.Count; i++)
        {
            var position = vertices[i].Position;
            positions[i] = new Vector3(position.X, position.Y, position.Z);
        }

        return Bounds3.FromPoints(positions);
    }

    private static TextureHandle CreateProceduralTexture(
        IGraphicsDevice device,
        string name,
        int size,
        Action<byte[], int> fill,
        SamplerDescription? sampler = null)
    {
        var pixels = new byte[size * size * 4];
        fill(pixels, size);
        return device.CreateTexture2D(
            new TextureDescription(size, size, TextureFormat.Rgba8, sampler ?? SamplerDescription.LinearClamp),
            pixels,
            name: name);
    }

    private static void GenerateUvGridPixels(byte[] pixels, int size)
    {
        // 8x8 grid of colored cells with thin black borders. Cell colors cycle through
        // HSV hues so the UV mapping is immediately readable on any surface.
        var cellSize = size / 8;
        var borderWidth = Math.Max(1, size / 128);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var cellX = x / cellSize;
                var cellY = y / cellSize;
                var localX = x % cellSize;
                var localY = y % cellSize;
                var onBorder = localX < borderWidth || localY < borderWidth ||
                               localX >= cellSize - borderWidth || localY >= cellSize - borderWidth;

                byte r, g, b;
                if (onBorder)
                {
                    r = g = b = 30;
                }
                else
                {
                    var hue = ((cellY * 8 + cellX) % 64) / 64.0f;
                    HsvToRgb(hue, 0.7f, 0.95f, out r, out g, out b);
                }
                var i = (y * size + x) * 4;
                pixels[i] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
                pixels[i + 3] = 255;
            }
        }
    }

    private static void GenerateFurNoisePixels(byte[] pixels, int size)
    {
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var u = x / (float)size;
                var v = y / (float)size;

                // Layered deterministic value noise. Slightly clumpy is good here:
                // sparse shells read as strands instead of TV static.
                var fine = Hash01(x, y);
                var mid = Hash01(x / 4, y / 4);
                var broad = Hash01(x / 16, y / 16);
                var wave = MathF.Sin((u * 21.0f + v * 13.0f) * MathF.Tau) * 0.5f + 0.5f;
                var value = Math.Clamp(fine * 0.55f + mid * 0.25f + broad * 0.15f + wave * 0.05f, 0.0f, 1.0f);

                var b = (byte)MathF.Round(value * 255.0f);
                var i = (y * size + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = b;
                pixels[i + 2] = b;
                pixels[i + 3] = 255;
            }
        }
    }

    private static float Hash01(int x, int y)
    {
        unchecked
        {
            var n = (uint)(x * 374761393 + y * 668265263);
            n = (n ^ (n >> 13)) * 1274126177u;
            return ((n ^ (n >> 16)) & 0x00FFFFFF) / 16777215.0f;
        }
    }


    // Procedural sky cubemap: gradient horizon + zenith with a small bright sun spot in
    // the direction of sunDirection. Six faces are laid out contiguously in GL order
    // [+X, -X, +Y, -Y, +Z, -Z], each RGBA8 size*size, total 6*size*size*4 bytes.
    // HDR procedural cubemap generator. Returns linear half-float values
    // that get uploaded into an Rgba16F cubemap (no sRGB encoding stage). The sun
    // spot can exceed 1.0, so reflections in metallic surfaces show a real bright
    // highlight rather than a clipped flat white.
    private static Half[] GenerateProceduralCubemapHdr(int faceSize, Vector3 sunDirection)
    {
        var halfsPerFace = faceSize * faceSize * 4;
        var pixels = new Half[halfsPerFace * 6];
        for (var face = 0; face < 6; face++)
        {
            var faceOffset = face * halfsPerFace;
            for (var t = 0; t < faceSize; t++)
            {
                for (var s = 0; s < faceSize; s++)
                {
                    var u = 2.0f * (s + 0.5f) / faceSize - 1.0f;
                    var v = 1.0f - 2.0f * (t + 0.5f) / faceSize;
                    var dir = face switch
                    {
                        0 => new Vector3(1, v, -u),
                        1 => new Vector3(-1, v, u),
                        2 => new Vector3(u, 1, -v),
                        3 => new Vector3(u, -1, v),
                        4 => new Vector3(u, v, 1),
                        _ => new Vector3(-u, v, -1),
                    };
                    dir = Vector3.Normalize(dir);
                    var color = SampleProceduralSkyHdr(dir, sunDirection);
                    var index = faceOffset + (t * faceSize + s) * 4;
                    pixels[index + 0] = (Half)color.X;
                    pixels[index + 1] = (Half)color.Y;
                    pixels[index + 2] = (Half)color.Z;
                    pixels[index + 3] = (Half)1.0f;
                }
            }
        }
        return pixels;
    }

    private static Vector3 SampleProceduralSkyHdr(Vector3 dir, Vector3 sunDir)
    {
        // Sky / ground gradient in linear space. Slightly punchier than the LDR
        // version since HDR storage doesn't have the headroom-fairness problem
        // (one bright pixel doesn't crowd the precision of darker pixels).
        var zenith = new Vector3(0.04f, 0.08f, 0.20f);
        var horizon = new Vector3(0.32f, 0.40f, 0.52f);
        var ground = new Vector3(0.10f, 0.10f, 0.09f);

        Vector3 col;
        if (dir.Y >= 0.0f)
        {
            var t = Smoothstep(0.0f, 1.0f, dir.Y);
            col = Vector3.Lerp(horizon, zenith, t);
        }
        else
        {
            var t = Smoothstep(0.0f, 0.3f, -dir.Y);
            col = Vector3.Lerp(horizon, ground, t);
        }

        // HDR sun: spot peaks at ~12x (mid-summer sun-on-snow brightness in linear
        // units, modest compared to real-world ~10^4 but plenty for the LDR scene
        // pipeline's tone mapper). The glow is half as bright and much wider so
        // metal reflections show both a tight sun and a hazy aura around it.
        var sunCos = MathF.Max(Vector3.Dot(dir, sunDir), 0.0f);
        var sunSpot = MathF.Pow(sunCos, 256.0f) * 12.0f;
        var sunGlow = MathF.Pow(sunCos, 6.0f) * 1.8f;
        col += new Vector3(1.0f, 0.95f, 0.85f) * (sunSpot + sunGlow);

        // No clamp -- HDR storage is the whole point. Values are linear; the lit
        // shader / skybox shader sample directly without sRGB decode.
        return col;
    }


    private static float Smoothstep(float a, float b, float x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    private static void HsvToRgb(float h, float s, float v, out byte r, out byte g, out byte b)
    {
        var i = (int)(h * 6.0f) % 6;
        var f = h * 6.0f - MathF.Floor(h * 6.0f);
        var p = v * (1.0f - s);
        var q = v * (1.0f - f * s);
        var t = v * (1.0f - (1.0f - f) * s);
        (float rf, float gf, float bf) = i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        r = (byte)MathF.Round(rf * 255.0f);
        g = (byte)MathF.Round(gf * 255.0f);
        b = (byte)MathF.Round(bf * 255.0f);
    }
}
