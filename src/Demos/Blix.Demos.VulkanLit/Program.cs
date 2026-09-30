using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Demos.VulkanLit.Debug;
using Blix.Diagnostics;
using Blix.Graphics;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanLit;

// PBR lit scene end-to-end on the Vulkan backend: cube + skinned glTF +
// PBR spheres + ground plane, lit by sun (cascade-less) + two spot lights
// + one point light, with PCF shadows on each, procedural-sky IBL, HDR
// bloom, and ACES tonemap.
//
// Render graph: shadow passes (sun + spots + 6 point-cube faces) →
// lit-scene → bloom (bright/blurH/blurV) → present.
//
// Skinning rides the per-frame bone-palette SSBO path: a IMaterialBindings
// at set 3 with framesInFlight = MaxFramesInFlight, written into
// CurrentFrameSlot every frame.
//
// ── Executable spec for (engine primitives this demo proves) ──
//   • Full PBR + procedural-sky IBL + PCF shadows (sun + 2 spot + point-cube)
//   • Skeletal skinning on the per-frame bone-palette SSBO path
//   • HDR bloom (bright/blurH/blurV) + ACES tonemap through the RenderGraph
//   • SPIR-V-reflected binding model end-to-end (set-by-lifetime, push constants)
// ── Intentionally owns (stays local; don't extract until a 2nd consumer needs it) ──
//   • the specific test scene: PBR sphere rig, light placement, camera
public static class Program
{
    public static int Main(string[] args) => BlixApps.Main(args, Run);

    private static int Run(AppArgs args)
    {
        var loop = new LitLoop();
        // Opens with the overlay showing: its knobs are what this demo is for. ` hides it.
        using var window = new Window(loop, WindowOptions.FromArgs(args,
            new WindowOptions("Blix — Vulkan Lit + Shadow + Skinned glTF", 1280, 720) { Diagnostics = true }));
        window.Run();
        return 0;
    }
}

internal sealed class LitLoop : IGameLoop, IDebuggable, IDisposable
{
    public string DebugName => "vulkan-lit";

    // Cube + ground geometry.
    private VertexBufferHandle cubeVB;
    private IndexBufferHandle cubeIB;
    private VertexBufferHandle groundVB;
    private IndexBufferHandle groundIB;

    // PBR sphere test rig: 2 rows (metallic + dielectric) × SphereCount,
    // sweeping roughness. One shared white material; per-draw metallic/roughness.
    private const int SphereCount = 6;
    private VertexBufferHandle sphereVB;
    private IndexBufferHandle sphereIB;
    private int sphereIndexCount;
    private MaterialHandle sphereMaterial;
    private TextureHandle whiteTexture;
    private TextureHandle flatNormalTexture;   // (0,0,1) — no perturbation
    private TextureHandle groundNormalTexture; // procedural bumps
    private readonly Matrix4x4[] sphereModels = new Matrix4x4[2 * SphereCount];

    // IBL: procedural sky environment (mipped, doubles as the prefiltered
    // specular source), diffuse irradiance cube, and split-sum BRDF LUT.
    private TextureHandle envCubeTexture;
    private TextureHandle irradianceCubeTexture;
    private TextureHandle brdfLutTexture;
    private const int EnvFaceSize = 64;
    private const int EnvMips = 7;             // log2(64)+1
    private const int IrradianceFaceSize = 16;
    private const int BrdfLutSize = 128;

    // Albedo textures + materials.
    private TextureHandle albedoTexture;
    private TextureHandle cesiumAlbedoTexture;
    // The engine's texture loader, which knows BOTH shapes a GltfTexture arrives in. See the
    // comment at its one call site for why this demo stopped resolving its own.
    private GltfTextureLoader textureLoader = null!;
    private MaterialHandle cubeMaterial;
    private MaterialHandle groundMaterial;
    private MaterialHandle cesiumSkinMaterial;   // set 2 (per-material)
    private MaterialHandle cesiumBoneMaterial;   // set 3 (per-draw, framesInFlight replicated)
    private IMaterialBindings cesiumBonePalette = null!;  // direct ref for per-frame WriteBuffer

    // Static lit pipeline (cube + ground).
    private ShaderProgramHandle litShaderProgram;
    private PipelineHandle litPipeline;
    private ShaderProgramHandle shadowShaderProgram;
    private PipelineHandle shadowPipeline;
    private ShaderProgramHandle presentShaderProgram;
    private PipelineHandle presentPipeline;
    private ShaderProgramHandle presentDepthProgram;
    private PipelineHandle presentDepthPipeline;

    // Skinned pipelines (cesium man).
    private ShaderProgramHandle skinnedLitProgram;
    private PipelineHandle skinnedLitPipeline;
    private ShaderProgramHandle skinnedShadowProgram;
    private PipelineHandle skinnedShadowPipeline;

    // Skinned model state.
    private Mesh cesiumMesh = null!;
    private Skeleton cesiumSkeleton = null!;
    private Pose cesiumPose = null!;
    private Pose cesiumRestPose = null!;
    private BonePalette cesiumPalette = null!;
    private AnimationClip cesiumAnimation = null!;
    private Matrix4x4 cesiumMeshNodeTransform;
    private Matrix4x4 cesiumUserTransform;
    private byte[] cesiumPalettePayload = null!;
    private double cesiumAnimTime;
    private IGraphicsDevice vkDevice = null!;

    // Graph + per-pass resources.
    private RenderGraph graph = null!;
    private GraphResourceHandle hdrHandle;
    private GraphResourceHandle sunShadowHandle;
    private GraphResourceHandle spot0ShadowHandle;
    private GraphResourceHandle spot1ShadowHandle;
    private DepthCubeHandle pointShadowCube;
    private PassHandle shadowPassHandle;
    private PassHandle spot0ShadowPassHandle;
    private PassHandle spot1ShadowPassHandle;
    private readonly PassHandle[] pointFacePassHandles = new PassHandle[6];
    private PassHandle litPassHandle;

    // Point shadow pipelines (linear-distance cube caster).
    private ShaderProgramHandle pointShadowProgram;
    private PipelineHandle pointShadowPipeline;
    private ShaderProgramHandle pointSkinnedShadowProgram;
    private PipelineHandle pointSkinnedShadowPipeline;
    private readonly Matrix4x4[] pointFaceVP = new Matrix4x4[6];

    // Bloom: bright extract → separable Gaussian (H+V) at quarter res, as a PostChain.
    private PostChain bloom = null!;
    private bool bloomEnabled = true;
    private const float BloomScale = 0.25f; // const — sizes graph render targets
    internal float BloomIntensity { get; set; } = 0.7f;

    // Present.
    private FullscreenPass fullscreen = null!;

    // Per-frame state.
    private int frameCount;
    private Matrix4x4 viewProj;
    private Matrix4x4 sunShadowVP;
    private Matrix4x4 spot0ViewProj;
    private Matrix4x4 spot1ViewProj;
    private Matrix4x4 cubeModel;
    private Matrix4x4 groundModel;
    private Matrix4x4 cesiumWorldModel;

    // Two spot lights, sampled through a Count=2 shadow-map array. Each is a
    // cone aimed at the scene with a perspective shadow map. Color is
    // pre-multiplied by intensity for the shader.
    internal static readonly Vector3 Spot0Position = new(-2.6f, 3.2f, 1.8f);
    private static readonly Vector3 Spot0Target = new(0.2f, -0.4f, 0.2f);
    private static readonly Vector3 Spot0Color = new(1.0f, 0.45f, 0.2f);  // warm orange
    private const float Spot0Intensity = 6.0f;
    private const float Spot0Range = 9.0f;
    private const float Spot0InnerDeg = 14f;
    private const float Spot0OuterDeg = 22f;

    internal static readonly Vector3 Spot1Position = new(2.8f, 3.0f, -1.6f);
    private static readonly Vector3 Spot1Target = new(0.4f, -0.4f, 0.0f);
    private static readonly Vector3 Spot1Color = new(0.4f, 1.0f, 0.55f);  // green
    private const float Spot1Intensity = 6.0f;
    private const float Spot1Range = 9.0f;
    private const float Spot1InnerDeg = 13f;
    private const float Spot1OuterDeg = 20f;

    // Point light. Cool cyan, sits low between cube and cesium to throw
    // omnidirectional shadows. Cube shadow stores linear distance / far.
    internal static readonly Vector3 PointPosition = new(0.9f, 0.7f, 1.4f);
    private static readonly Vector3 PointColor = new(0.25f, 0.6f, 1.0f); // cool
    private const float PointIntensity = 4.0f;
    private const float PointRange = 6.0f;
    private const float PointFar = 8.0f;
    private const int PointShadowSize = 512;
    private float currentRotY;
    private float currentRotX;

    // The engine's camera controller (look, orbit, fly, zoom); the renderer reads the two names below.
    private IRenderHost host = null!;
    private readonly CameraController camera = new(new Camera3D { VerticalFieldOfView = MathF.PI / 3f, NearPlane = 0.1f, FarPlane = 100f })
    {
        MoveSpeed = 3.5f,
    };
    private Vector3 cameraPosition => camera.Position;
    private Vector3 cameraForward => camera.Forward;
    private float aspect = 16f / 9f;

    // Debug views: which texture the present pass shows.
    private enum View { Final = 0, SunShadow = 1, SpotShadow = 2, SceneDepth = 3 }
    private static readonly string[] ViewLabels = { "Final (HDR)", "Sun shadow map", "Spot shadow map", "Scene depth" };
    private int viewMode;          // index into View
    // Per-pixel shader debug channel — overrides lit.frag's output with one
    // shading term so you can inspect the math. Index matches the branch in
    // lit.frag main(); 0 = normal shading.
    private static readonly string[] ShaderChannelLabels =
    {
        "Shaded", "Albedo", "World normal", "Geometric normal", "Roughness",
        "Metallic", "NdotV", "Ambient (IBL)", "Specular (IBL)", "Diffuse (IBL)",
        "Sun shadow", "UVs",
    };
    private int shaderDebugMode;   // index into ShaderChannelLabels
    private bool animPaused;
    private float exposure = 1.0f; // tonemap exposure multiplier (Up/Down keys)
    private GraphResourceHandle sceneDepthHandle;

    // Per-light isolation toggles (debug) — switch lights off independently
    // to attribute artifacts to a specific light.
    private bool sunEnabled = true;
    private bool spotEnabled = true;
    private bool pointEnabled = true;

    // Scene "constants" — promoted from static-readonly / const to
    // internal instance properties so the SunControls debug contributor
    // can drive them. Default values match the original consts.
    internal Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(-0.55f, -1.0f, -0.45f));
    internal float SunIntensity { get; set; } = 1.0f;
    internal Vector3 AmbientColor { get; set; } = new(0.65f, 0.7f, 0.85f);
    internal float AmbientIntensity { get; set; } = 1.0f;  // IBL strength (real ambient now)

    // Sun direction baked into the procedural sky / IBL env cube. The IBL
    // cubes are generated once at init (BuildEnvCubeWithMips +
    // BuildIrradianceCube), so a live SunDirection change won't relight
    // them — direct sun shading updates, IBL stays frozen until re-baked.
    // Held statically so the static sky-bake helpers can reach it.
    private static readonly Vector3 SkyBakeSunDirection =
        Vector3.Normalize(new Vector3(-0.55f, -1.0f, -0.45f));

    private const int ShadowMapSize = 1024;

    // Surface exposed to debug-overlay contributors. PascalCase pass-throughs
    // so we don't have to rename every internal use site when a knob graduates
    // from hardcoded to tunable.
    internal float Exposure          { get => exposure;        set => exposure = value; }
    internal bool  BloomEnabled      { get => bloomEnabled;    set => bloomEnabled = value; }
    internal bool  SunEnabled        { get => sunEnabled;      set => sunEnabled = value; }
    internal bool  SpotEnabled       { get => spotEnabled;     set => spotEnabled = value; }
    internal bool  PointEnabled      { get => pointEnabled;    set => pointEnabled = value; }
    internal float MoveSpeed         { get => camera.MoveSpeed; set => camera.MoveSpeed = value; }
    internal bool  AnimPaused        { get => animPaused;      set => animPaused = value; }
    internal int   ViewMode          { get => viewMode;        set => viewMode = value; }
    internal int   ShaderDebugMode   { get => shaderDebugMode; set => shaderDebugMode = value; }
    internal float FovYRadians       { get => camera.FieldOfView; set => camera.FieldOfView = value; }

    internal static IReadOnlyList<string> ViewModeLabels       => ViewLabels;
    internal static IReadOnlyList<string> ShaderChannelOptions => ShaderChannelLabels;

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        host.SetTitle("Blix — Vulkan Lit + Shadow + Skinned glTF");
        this.host = host;
        var device = graphicsDevice;
        vkDevice = device;

        // --- Geometry: cube + ground -------------------------------------
        var (cubeVerts, cubeIndices) = BuildCube();
        cubeVB = device.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(cubeVerts), "cube.vb");
        cubeIB = device.CreateIndexBuffer(cubeIndices, name: "cube.ib");

        var (groundVerts, groundIndices) = BuildGround(extent: 3.0f, y: -0.6f, uvTile: 4.0f);
        groundVB = device.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(groundVerts), "ground.vb");
        groundIB = device.CreateIndexBuffer(groundIndices, name: "ground.ib");

        var checker = BuildCheckerboard(256, 8);
        albedoTexture = device.CreateTexture2D(
            new TextureDescription(256, 256, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            checker,
            "albedo");

        // PBR sphere rig: one UV sphere, two rows of materials driven by
        // per-draw metallic/roughness. White albedo so the BRDF response is
        // unambiguous (silver metal vs white dielectric).
        var (sphereVerts, sphereIndices) = BuildSphere(radius: 0.42f, rings: 32, sectors: 48);
        sphereVB = device.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(sphereVerts), "sphere.vb");
        sphereIB = device.CreateIndexBuffer(sphereIndices, name: "sphere.ib");
        sphereIndexCount = sphereIndices.Length;
        var white = new byte[4 * 4 * 4];
        Array.Fill(white, (byte)255);
        whiteTexture = device.CreateTexture2D(
            new TextureDescription(4, 4, TextureFormat.Rgba8Srgb, SamplerDescription.LinearClamp),
            white, "white");

        // Normal maps are LINEAR data (directions), not sRGB. Flat = (0,0,1)
        // encoded as (128,128,255). Ground gets a procedural ripple pattern.
        flatNormalTexture = device.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            new byte[] { 128, 128, 255, 255 }, "normal.flat");
        groundNormalTexture = device.CreateTexture2D(
            new TextureDescription(256, 256, TextureFormat.Rgba8, SamplerDescription.LinearRepeat),
            BuildRippleNormalMap(256, freq: 6f, strength: 1.4f), "normal.ground");

        // --- IBL precompute (procedural sky) ----------------------------
        // Env cube + box-filter mips (the mip chain stands in for prefiltered
        // specular at increasing roughness). Diffuse irradiance via cosine-
        // weighted hemisphere convolution. BRDF split-sum LUT.
        envCubeTexture = device.CreateTextureCube(
            EnvFaceSize, TextureFormat.Rgba8, EnvMips,
            BuildEnvCubeWithMips(EnvFaceSize, EnvMips),
            SamplerDescription.LinearClamp, "ibl.env");
        irradianceCubeTexture = device.CreateTextureCube(
            IrradianceFaceSize, TextureFormat.Rgba8, 1,
            BuildIrradianceCube(IrradianceFaceSize),
            SamplerDescription.LinearClamp, "ibl.irradiance");
        brdfLutTexture = device.CreateTexture2D(
            new TextureDescription(BrdfLutSize, BrdfLutSize, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            BuildBrdfLut(BrdfLutSize), "ibl.brdfLut");
        // Two rows of SphereCount, spread in x; metallic row higher.
        for (var i = 0; i < SphereCount; i++)
        {
            var x = -3.0f + i * (6.0f / (SphereCount - 1));
            sphereModels[i]               = Matrix4x4.CreateTranslation(x, 1.7f, -1.8f);
            sphereModels[SphereCount + i] = Matrix4x4.CreateTranslation(x, 0.7f, -1.8f);
        }

        // --- Skinned model: cesium_man.glb -------------------------------
        var assetPath = AppFiles.Asset("models", "cesium_man.glb");
        var importer = new GltfImporter();
        var importCtx = new AssetImportContext(AssetId.Parse("models/cesium_man"), assetPath);
        var cesiumModel = importer.Import(importCtx);
        if (cesiumModel.Animations.Length == 0)
        {
            throw new InvalidOperationException("cesium_man.glb has no animations.");
        }
        cesiumSkeleton = cesiumModel.Skeleton;
        cesiumRestPose = cesiumSkeleton.CreateRestPose();
        cesiumPose = cesiumSkeleton.CreateRestPose();
        cesiumPalette = new BonePalette(cesiumSkeleton.BoneCount);
        cesiumAnimation = cesiumModel.Animations[0];
        cesiumMeshNodeTransform = cesiumModel.MeshNodeTransform;
        cesiumPalettePayload = new byte[cesiumSkeleton.BoneCount * 64];

        // First primitive only — CesiumMan is a single-primitive mesh.
        //
        // This upload used to read prim.Mesh.Indices directly and take its Length as the count,
        // which is correct for CesiumMan and silently wrong for any asset large enough to carry
        // 32-bit indices. CreateMesh makes that branch the engine's rather than each caller's.
        var prim = cesiumModel.Primitives[0];
        cesiumMesh = device.CreateMesh(prim.Mesh, "cesium");

        // Cesium albedo: prefer the glTF's BaseColorTexture; fall back to a
        // neutral white if the material strips out images for some reason.
        // <b>This demo used to resolve its own albedo, and lost the character's texture the first
        // time anything cooked its assets.</b> The csproj declares CookMesh over Assets/models, so
        // a build writes cesium_man.textures/image_0.blixtex beside the .glb; GltfShared then
        // PREFERS that cooked sibling and hands back a GltfTexture carrying a LazyHandle rather
        // than MipBytes, in a BC format rather than Rgba8. The local loader tested for MipBytes
        // and Rgba8, matched neither, and returned its neutral fallback -- so Cesium Man went
        // quietly cream-coloured, with no error and nothing in the log.
        //
        // GltfTextureLoader already handles both shapes: MipBytes uploads directly, LazyHandle
        // allocates the chain and queues per-mip uploads to drain across later frames. Resolving
        // textures is the asset layer's job and this demo had no business having an opinion about
        // it, least of all a partial one.
        textureLoader = new GltfTextureLoader(device);
        cesiumAlbedoTexture = textureLoader.Load(prim.Material).Albedo;

        // --- Render graph ------------------------------------------------
        graph = new RenderGraph(device);

        var fullSize = new MatchSwapchainGraphSize(1.0f);
        var shadowSize = new FixedGraphSize(ShadowMapSize, ShadowMapSize);

        sunShadowHandle = graph.DepthTarget("sun-shadow", shadowSize);
        spot0ShadowHandle = graph.DepthTarget("spot0-shadow", shadowSize);
        spot1ShadowHandle = graph.DepthTarget("spot1-shadow", shadowSize);
        pointShadowCube = graph.DepthCube("point-shadow", PointShadowSize);
        hdrHandle = graph.ColorTarget("hdr", TextureFormat.Rgba16F, fullSize);
        sceneDepthHandle = graph.DepthTarget("scene-depth", fullSize);

        // --- Shader interfaces ------------------------------------------
        // Per-frame UBO grows with each light. vec4-packed light blocks
        // dodge the std140 vec3+float padding fragility. See lit.vert.
        var frameUbo = new UniformBlockLayout(
            TotalSize: 464,
            Members: new[]
            {
                new UniformBlockMember("uViewProjection",     0,   64),
                new UniformBlockMember("uSunDirection",       64,  12),
                new UniformBlockMember("uSunIntensity",       76,  4),
                new UniformBlockMember("uAmbientColor",       80,  12),
                new UniformBlockMember("uAmbientIntensity",   92,  4),
                new UniformBlockMember("uSunShadowVP",        96,  64),
                new UniformBlockMember("uSpot0ViewProj",      160, 64),
                new UniformBlockMember("uSpot0PosRange",      224, 16),
                new UniformBlockMember("uSpot0DirCosInner",   240, 16),
                new UniformBlockMember("uSpot0ColorCosOuter", 256, 16),
                new UniformBlockMember("uSpot1ViewProj",      272, 64),
                new UniformBlockMember("uSpot1PosRange",      336, 16),
                new UniformBlockMember("uSpot1DirCosInner",   352, 16),
                new UniformBlockMember("uSpot1ColorCosOuter", 368, 16),
                new UniformBlockMember("uPointPosFar",        384, 16),
                new UniformBlockMember("uPointColorRange",    400, 16),
                new UniformBlockMember("uLightEnable",        416, 16),
                new UniformBlockMember("uCameraPos",          432, 16),
                // Debug channel selector (x = mode). 0 = normal shading; >0
                // overrides outColor with one shading term for inspection.
                new UniformBlockMember("uDebug",              448, 16),
            });
        var tintUbo = new UniformBlockLayout(
            TotalSize: 16,
            Members: new[] { new UniformBlockMember("uTint", 0, 16) });

        // Shadow caster interface — light-agnostic. No set 0; the shadow VP
        // rides in the push constant (mat4 uModel @0, mat4 uShadowViewProj
        // @64 = 128 bytes) so one pipeline serves sun + spot + cube faces.
        // --- Shader interfaces, read from the shaders -------------------------
        // Ten programs. What stood here restated every set, binding, type, stage, array count
        // and push size that the shaders already declare -- including five distinct push sizes
        // (128, 144, 80, 8, 8) and a two-element sampler array, none of which anything checked.
        //
        // The shader is the source of truth for all of it EXCEPT the length of the bone palette:
        // it is declared unsized, so the count is this demo's (`cesiumSkeleton.BoneCount`) and
        // is stated once, where it is known.
        var shaderDir = AppFiles.Shaders;
        ShaderInterface Reflect(params string[] stages) => ShaderReflection.ForProgram(shaderDir, stages);
        ShaderInterface Skinned(ShaderInterface iface) =>
            iface.WithBlockSize(set: 3, binding: 0, cesiumSkeleton.BoneCount * 64);

        var shadowInterface = Reflect("shadow.vert", "shadow.frag");
        var pointShadowInterface = Reflect("point_shadow.vert", "point_shadow.frag");
        var litInterface = Reflect("lit.vert", "lit.frag");
        var skinnedShadowInterface = Skinned(Reflect("skinned_shadow.vert", "shadow.frag"));
        var pointSkinnedShadowInterface = Skinned(Reflect("point_skinned_shadow.vert", "point_shadow.frag"));
        var skinnedLitInterface = Skinned(Reflect("skinned_lit.vert", "lit.frag"));
        var presentInterface = Reflect("present.vert", "present_depth.frag");
        var presentTonemapInterface = Reflect("present.vert", "present.frag");
        var bloomBrightInterface = Reflect("present.vert", "bloom_bright.frag");
        var bloomBlurInterface = Reflect("present.vert", "bloom_blur.frag");

        // --- Declare graph passes (shadow casters via both interfaces) --
        // sun + 2 spot depth passes; all host the static + skinned casters.
        // The lit pass reads every shadow map.
        shadowPassHandle = graph.GraphicsPass("sun-shadow")
            .Depth(sunShadowHandle, LoadOp.Clear, StoreOp.Store)
            .Shader(shadowInterface, skinnedShadowInterface)
            .Handle;
        spot0ShadowPassHandle = graph.GraphicsPass("spot0-shadow")
            .Depth(spot0ShadowHandle, LoadOp.Clear, StoreOp.Store)
            .Shader(shadowInterface, skinnedShadowInterface)
            .Handle;
        spot1ShadowPassHandle = graph.GraphicsPass("spot1-shadow")
            .Depth(spot1ShadowHandle, LoadOp.Clear, StoreOp.Store)
            .Shader(shadowInterface, skinnedShadowInterface)
            .Handle;
        // Point light: one depth pass per cube face, each targeting a face
        // view. Casters write linear distance via the point-shadow shaders.
        for (var f = 0; f < 6; f++)
        {
            pointFacePassHandles[f] = graph.GraphicsPass($"point-shadow-face{f}")
                .Depth(pointShadowCube.Face(f), LoadOp.Clear, StoreOp.Store)
                .Shader(pointShadowInterface, pointSkinnedShadowInterface)
                .Handle;
        }
        litPassHandle = graph.GraphicsPass("lit-scene")
            .Target(hdrHandle, LoadOp.Clear, StoreOp.Store)
            .Depth(sceneDepthHandle, LoadOp.Clear, StoreOp.Store)
            .Read(sunShadowHandle)
            .Read(spot0ShadowHandle)
            .Read(spot1ShadowHandle)
            .Read(pointShadowCube)
            .Shader(litInterface, skinnedLitInterface)
            .Handle;
        // Bloom: bright(hdr) → blurH → blurV at quarter res, as a PostChain. It
        // declares its own targets + passes after the lit scene pass (so it runs
        // after the scene writes hdr) and stops at a blurred-bright texture; the
        // present pass below composites bloom.Output back over hdr.
        var bloomSize = new MatchSwapchainGraphSize(BloomScale);
        bloom = new PostChain(device, graph, hdrHandle, new[]
        {
            new PostStage("bloom-bright", TextureFormat.Rgba16F, bloomSize, bloomBrightInterface, "uHdr"),
            new PostStage("bloom-blurH", TextureFormat.Rgba16F, bloomSize, bloomBlurInterface, "uSrc"),
            new PostStage("bloom-blurV", TextureFormat.Rgba16F, bloomSize, bloomBlurInterface, "uSrc"),
        }, "bloom");
        graph.Compile();

        // --- Pipelines --------------------------------------------------
        var shadowVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow.vert.spv"));
        var shadowFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow.frag.spv"));
        shadowShaderProgram = device.CreateShaderProgramFromSpv(shadowVertSpv, shadowFragSpv, shadowInterface, "shadow");
        shadowPipeline = device.CreatePipeline(new PipelineDescription(
            shadowShaderProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(shadowPassHandle)), "shadow");

        var skinnedShadowVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "skinned_shadow.vert.spv"));
        skinnedShadowProgram = device.CreateShaderProgramFromSpv(skinnedShadowVertSpv, shadowFragSpv, skinnedShadowInterface, "skinned_shadow");
        skinnedShadowPipeline = device.CreatePipeline(new PipelineDescription(
            skinnedShadowProgram,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(shadowPassHandle)), "skinned_shadow");

        // Point cube shadow pipelines. Created against face-0's render pass;
        // reused for all 6 faces (the per-face render passes are identical
        // depth-only setups, so they're render-pass compatible).
        var pointShadowVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "point_shadow.vert.spv"));
        var pointShadowFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "point_shadow.frag.spv"));
        pointShadowProgram = device.CreateShaderProgramFromSpv(pointShadowVertSpv, pointShadowFragSpv, pointShadowInterface, "point_shadow");
        pointShadowPipeline = device.CreatePipeline(new PipelineDescription(
            pointShadowProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(pointFacePassHandles[0])), "point_shadow");

        var pointSkinnedShadowVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "point_skinned_shadow.vert.spv"));
        pointSkinnedShadowProgram = device.CreateShaderProgramFromSpv(pointSkinnedShadowVertSpv, pointShadowFragSpv, pointSkinnedShadowInterface, "point_skinned_shadow");
        pointSkinnedShadowPipeline = device.CreatePipeline(new PipelineDescription(
            pointSkinnedShadowProgram,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(pointFacePassHandles[0])), "point_skinned_shadow");

        var litVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "lit.vert.spv"));
        var litFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "lit.frag.spv"));
        litShaderProgram = device.CreateShaderProgramFromSpv(litVertSpv, litFragSpv, litInterface, "lit");
        litPipeline = device.CreatePipeline(new PipelineDescription(
            litShaderProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPassHandle)), "lit");

        var skinnedLitVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "skinned_lit.vert.spv"));
        skinnedLitProgram = device.CreateShaderProgramFromSpv(skinnedLitVertSpv, litFragSpv, skinnedLitInterface, "skinned_lit");
        skinnedLitPipeline = device.CreatePipeline(new PipelineDescription(
            skinnedLitProgram,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPassHandle)), "skinned_lit");

        var presentVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.vert.spv"));
        var presentFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.frag.spv"));
        presentShaderProgram = device.CreateShaderProgramFromSpv(presentVertSpv, presentFragSpv, presentTonemapInterface, "present");
        presentPipeline = device.CreatePipeline(new PipelineDescription(
            presentShaderProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.Disabled,
            RasterizerState.NoCulling,
            BlendState.Disabled), "present");

        // Depth-visualizer present pipeline (debug views). Same fullscreen
        // vertex shader + present interface, different fragment shader.
        var presentDepthFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present_depth.frag.spv"));
        presentDepthProgram = device.CreateShaderProgramFromSpv(presentVertSpv, presentDepthFragSpv, presentInterface, "present_depth");
        presentDepthPipeline = device.CreatePipeline(new PipelineDescription(
            presentDepthProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.Disabled,
            RasterizerState.NoCulling,
            BlendState.Disabled), "present_depth");

        // Bloom pipelines (fullscreen): the caller brings each stage's pipeline,
        // built against that stage's surface. Bright + blur share present.vert; the
        // blur program is reused for blurH and blurV (the transient descriptor pool
        // gives each draw its own uSrc set, so the two blur draws don't clobber).
        bloom.BuildPipelines((stage, surface) =>
        {
            var frag = stage.Name == "bloom-bright" ? "bloom_bright.frag.spv" : "bloom_blur.frag.spv";
            var program = device.CreateShaderProgramFromSpv(
                presentVertSpv, File.ReadAllBytes(Path.Combine(shaderDir, frag)), stage.Interface, stage.Name);
            return device.CreatePipeline(new PipelineDescription(
                program,
                VertexPosition3NormalTexture.Layout,
                PrimitiveTopology.Triangles,
                DepthState.Disabled,
                RasterizerState.NoCulling,
                new[] { BlendState.Disabled },
                RenderTarget: surface), stage.Name);
        });

        // --- Materials --------------------------------------------------
        cubeMaterial = device.CreateMaterial(litShaderProgram, name: "cube.material")
            .SetUniform(binding: 0, "uTint", new Vector4(1.0f, 1.0f, 1.0f, 1.0f))
            .SetTexture(binding: 1, albedoTexture)
            .SetTexture(binding: 2, flatNormalTexture)
            .Handle;

        groundMaterial = device.CreateMaterial(litShaderProgram, name: "ground.material")
            .SetUniform(binding: 0, "uTint", new Vector4(0.55f, 0.62f, 0.78f, 1.0f))
            .SetTexture(binding: 1, albedoTexture)
            .SetTexture(binding: 2, groundNormalTexture)
            .Handle;

        sphereMaterial = device.CreateMaterial(litShaderProgram, name: "sphere.material")
            .SetUniform(binding: 0, "uTint", new Vector4(0.95f, 0.95f, 0.95f, 1.0f))
            .SetTexture(binding: 1, whiteTexture)
            .SetTexture(binding: 2, flatNormalTexture)
            .Handle;

        // Cesium per-material set (set 2). Tint from BaseColorFactor when
        // available; the importer hands us the linear-space tint.
        var cesiumTint = prim.Material?.BaseColorFactor ?? new Vector4(1, 1, 1, 1);
        cesiumSkinMaterial = device.CreateMaterial(skinnedLitProgram, name: "cesium.skin.material")
            .SetUniform(binding: 0, "uTint", cesiumTint)
            .SetTexture(binding: 1, cesiumAlbedoTexture)
            .SetTexture(binding: 2, flatNormalTexture)
            .Handle;

        // Cesium bone palette (set 3, per-draw SSBO, replicated across
        // frames so per-frame writes don't race with in-flight GPU work).
        cesiumBonePalette = device.CreateMaterial(
            skinnedLitProgram,
            setIndex: 3,
            framesInFlight: device.MaxFramesInFlightCount,
            name: "cesium.bonepalette");
        cesiumBoneMaterial = cesiumBonePalette.Handle;

        // --- Fullscreen triangle (present + bloom passes) ----------------
        fullscreen = new FullscreenPass(device, "fullscreen");

        // --- Camera + transforms ----------------------------------------
        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;

        // Starts pointed at the scene, which also makes the scene the orbit pivot.
        camera.LookAt(new Vector3(3.5f, 2.4f, 4.4f), new Vector3(0.3f, 0.4f, 0f));
        UpdateCamera();

        // Sun shadow VP — static (sun + scene bounds don't move).
        var lightEye = -SunDirection * 8.0f;
        var sunUp = MathF.Abs(SunDirection.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var sunView = Matrix4x4.CreateLookAt(lightEye, Vector3.Zero, sunUp);
        // Fit the ortho to the whole ground seen from the sun's slant: the
        // 6×6 floor (±3) has an ~8.5-unit diagonal, and the light looks across
        // it at an angle, so 7.5 left the far corners outside the shadow map —
        // past its edge sampleShadow returns "lit", a hard seam across the far
        // floor when you look down. 11 covers the diagonal + actor height; the
        // longer far plane keeps the full depth range in view.
        var sunOrtho = GraphicsMatrices.CreateOrthographicVulkan(width: 11f, height: 11f, nearPlane: 0.1f, farPlane: 22f);
        sunShadowVP = sunView * sunOrtho;

        // Spot shadow VP — perspective from the spot, FOV covering the outer
        // cone (×2.2 for a margin so the cone edge isn't clipped by the map).
        spot0ViewProj = MakeSpotVP(Spot0Position, Spot0Target, Spot0OuterDeg, Spot0Range);
        spot1ViewProj = MakeSpotVP(Spot1Position, Spot1Target, Spot1OuterDeg, Spot1Range);

        // Point cube face VPs — 90° perspective per face, canonical cubemap
        // axes (+X,-X,+Y,-Y,+Z,-Z) with the standard up vectors. Static
        // because the point light doesn't move.
        //
        // Cube faces must NOT use the screen Y-flip. Cubemap sampling follows
        // a fixed convention that assumes Y-up face rendering; the screen
        // perspective's Y-flip (M22 < 0) vertically mirrors each stored face,
        // so a direction samples a mirrored texel and reads the wrong
        // occluder distance (a mirrored false shadow). Undo the flip (below)
        // while keeping Vulkan's [0,1] depth range.
        var pointProj = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 2f, 1.0f, 0.1f, PointFar);
        pointProj.M22 = -pointProj.M22;
        var faceDirs = new[]
        {
            ( new Vector3( 1, 0, 0), new Vector3(0, -1,  0) ), // +X
            ( new Vector3(-1, 0, 0), new Vector3(0, -1,  0) ), // -X
            ( new Vector3( 0, 1, 0), new Vector3(0,  0,  1) ), // +Y
            ( new Vector3( 0,-1, 0), new Vector3(0,  0, -1) ), // -Y
            ( new Vector3( 0, 0, 1), new Vector3(0, -1,  0) ), // +Z
            ( new Vector3( 0, 0,-1), new Vector3(0, -1,  0) ), // -Z
        };
        for (var f = 0; f < 6; f++)
        {
            var (faceDir, faceUp) = faceDirs[f];
            var faceView = Matrix4x4.CreateLookAt(PointPosition, PointPosition + faceDir, faceUp);
            pointFaceVP[f] = faceView * pointProj;
        }

        groundModel = Matrix4x4.Identity;

        // Cesium user transform: stand the figure next to the cube on the
        // ground. CesiumMan is roughly 1.5 units tall in mesh-local space;
        // MeshNodeTransform handles the Z-up → Y-up axis correction the
        // asset's parent node applies.
        cesiumUserTransform =
            Matrix4x4.CreateTranslation(1.7f, -0.6f, -0.6f);

        // Register the per-knob debug contributors. Each owns a focused
        // slice of the overlay's Controls/Values surface so LitLoop.Debug()
        // doesn't have to. The device's own contributor is the host's;
        // these game-specific ones are registered here.
        if (host is IDebugHost debugHost && debugHost.System is { } debugSystem)
        {
            debugSystem.Register(new SunControls(this));
            debugSystem.Register(new ToneMapControls(this));
            debugSystem.Register(new LightControls(this));
            debugSystem.Register(new CameraTuningControls(this));
            debugSystem.Register(new DebugViewControls(this));
        }
    }

    public void OnUpdate(Time time)
    {
        ReadInput();

        var dt = (float)time.Delta;

        // Cooked textures arrive over several frames; until the queue drains the character holds
        // whatever levels have landed. Nothing else in this demo is deferred, so a small budget is
        // ample and the cost disappears once PendingCount reaches zero.
        if (textureLoader is not null && textureLoader.PendingCount > 0) textureLoader.Drain(budgetMillis: 4.0);

        // The engine's layout: right-drag looks, WASD with Space and Ctrl flies, left-drag orbits the
        // scene, the wheel zooms (or sets the flying speed while looking).
        camera.DriveDefault(host, dt);
        UpdateCamera();
    }

    private void UpdateCamera() => viewProj = camera.ViewProjection(aspect);

    public void OnResize(int width, int height)
    {
        if (height > 0)
        {
            aspect = width / (float)height;
            UpdateCamera();
        }
    }

    // --- input -------------------------------------------------------------

    /// <summary>Read the devices, at a point this loop chose rather than whenever an event landed.</summary>
    /// <remarks>
    /// Every toggle here is <c>Pressed</c>. Run from the callback they were once per key-down
    /// event, so a backend that repeats while a key is held would have flickered each of them —
    /// a tick transition cannot, whatever the backend does.
    /// </remarks>
    private void ReadInput()
    {
        var input = host.Input;

        // V, P, Z, X, C and B are bound on the controls they drive (Debug/), which the engine flips.
        // Exposure steps and quitting stay here, described in Debug() so the key list is whole.
        if (input[Key.Up].Pressed) exposure = Math.Clamp(exposure * 1.25f, 0.05f, 16f);
        if (input[Key.Down].Pressed) exposure = Math.Clamp(exposure * 0.8f, 0.05f, 16f);
        if (input[Key.Escape].Pressed) host.RequestClose();

        // The mouse, the cursor capture and the wheel are the camera controller's (Drive, in OnUpdate).
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;
        currentRotY = (float)time.Total * 0.8f;
        currentRotX = (float)time.Total * 0.3f;
        cubeModel = Matrix4x4.CreateRotationY(currentRotY)
                  * Matrix4x4.CreateRotationX(currentRotX)
                  * Matrix4x4.CreateTranslation(0, 0.2f, 0);

        // --- Animate skinned model -------------------------------------
        // Standard reset-sample-palette sequence (matches SkinnedGameObject.Update):
        //   1. Pose ← RestPose so partial clips overlay onto a known base.
        //      Skipping this means non-animated channels keep last frame's
        //      sampled values — contortion comes from a clip's track shape
        //      being interpreted as "this is the WHOLE local transform"
        //      when it's actually "delta on top of rest".
        //   2. Sample the clip at looped time into the working pose.
        //   3. Push pose through skeleton to get the GPU bone palette.
        if (!animPaused) cesiumAnimTime += time.Delta;
        var loopedTime = cesiumAnimation.Duration > 0
            ? cesiumAnimTime % cesiumAnimation.Duration
            : 0.0;
        cesiumPose.CopyFrom(cesiumRestPose);
        cesiumAnimation.Sample(loopedTime, cesiumPose);
        cesiumSkeleton.ComputeBonePalette(cesiumPose, cesiumPalette);

        // Compose the demo's user transform with the asset's mesh-node
        // axis correction (Z-up → Y-up for CesiumMan). The bone palette
        // is mesh-local; uModel takes mesh-local → world.
        cesiumWorldModel = cesiumMeshNodeTransform * cesiumUserTransform;

        // Pack the palette into the SSBO payload bytes (16 floats × N bones)
        // and upload to the slot matching this frame.
        PackPalette(cesiumPalette, cesiumPalettePayload);
        var frameSlot = vkDevice.CurrentFrameSlot;
        cesiumBonePalette.WriteBuffer(frameSlot, binding: 0, cesiumPalettePayload);

        // Two spot lights, vec4-packed (color pre-multiplied by intensity).
        var perFrame = new ShaderUniform[]
        {
            new("uViewProjection",     new Matrix4x4Uniform(viewProj)),
            new("uSunDirection",       new Vector3Uniform(SunDirection)),
            new("uSunIntensity",       new FloatUniform(SunIntensity)),
            new("uAmbientColor",       new Vector3Uniform(AmbientColor)),
            new("uAmbientIntensity",   new FloatUniform(AmbientIntensity)),
            new("uSunShadowVP",        new Matrix4x4Uniform(sunShadowVP)),
            new("uSpot0ViewProj",      new Matrix4x4Uniform(spot0ViewProj)),
            new("uSpot0PosRange",      new Vector4Uniform(new Vector4(Spot0Position, Spot0Range))),
            new("uSpot0DirCosInner",   new Vector4Uniform(new Vector4(
                Vector3.Normalize(Spot0Target - Spot0Position), MathF.Cos(Spot0InnerDeg * (MathF.PI / 180f))))),
            new("uSpot0ColorCosOuter", new Vector4Uniform(new Vector4(
                Spot0Color * Spot0Intensity, MathF.Cos(Spot0OuterDeg * (MathF.PI / 180f))))),
            new("uSpot1ViewProj",      new Matrix4x4Uniform(spot1ViewProj)),
            new("uSpot1PosRange",      new Vector4Uniform(new Vector4(Spot1Position, Spot1Range))),
            new("uSpot1DirCosInner",   new Vector4Uniform(new Vector4(
                Vector3.Normalize(Spot1Target - Spot1Position), MathF.Cos(Spot1InnerDeg * (MathF.PI / 180f))))),
            new("uSpot1ColorCosOuter", new Vector4Uniform(new Vector4(
                Spot1Color * Spot1Intensity, MathF.Cos(Spot1OuterDeg * (MathF.PI / 180f))))),
            new("uPointPosFar",        new Vector4Uniform(new Vector4(PointPosition, PointFar))),
            new("uPointColorRange",    new Vector4Uniform(new Vector4(PointColor * PointIntensity, PointRange))),
            new("uLightEnable",        new Vector4Uniform(new Vector4(
                sunEnabled ? 1f : 0f, spotEnabled ? 1f : 0f, pointEnabled ? 1f : 0f, 0f))),
            new("uCameraPos",          new Vector4Uniform(new Vector4(cameraPosition, 1f))),
            new("uDebug",              new Vector4Uniform(new Vector4(shaderDebugMode, 0f, 0f, 0f))),
        };

        // Sun + 2 spot shadow passes. Each draws the same casters (cube +
        // skinned cesium) but with its own shadow view-projection pushed
        // per-draw alongside the model matrix (128-byte push).
        RecordShadowPass(shadowPassHandle, sunShadowVP);
        RecordShadowPass(spot0ShadowPassHandle, spot0ViewProj);
        RecordShadowPass(spot1ShadowPassHandle, spot1ViewProj);

        // Point light: 6 cube face passes, each writing linear distance.
        for (var f = 0; f < 6; f++)
        {
            RecordPointShadowFace(pointFacePassHandles[f], pointFaceVP[f]);
        }

        // Lit pass: ground + cube + cesium, sampling every shadow map. The
        // two spot maps bind into the Count=2 array at (Slot 1, ArrayIndex 0/1).
        var sunShadowTex = graph.GetDepthTexture(sunShadowHandle);
        var spot0ShadowTex = graph.GetDepthTexture(spot0ShadowHandle);
        var spot1ShadowTex = graph.GetDepthTexture(spot1ShadowHandle);
        var pointShadowTex = graph.GetDepthCubeTexture(pointShadowCube);
        var shadowBindings = new[]
        {
            new ShaderTextureBinding("uSunShadowMap", sunShadowTex),
            new ShaderTextureBinding("uSpotShadowMaps[0]", spot0ShadowTex),
            new ShaderTextureBinding("uSpotShadowMaps[1]", spot1ShadowTex),
            new ShaderTextureBinding("uPointShadowCube", pointShadowTex),
            new ShaderTextureBinding("uIrradiance", irradianceCubeTexture),
            new ShaderTextureBinding("uPrefilteredEnv", envCubeTexture),
            new ShaderTextureBinding("uBrdfLut", brdfLutTexture),
        };
        graph.Pass(litPassHandle, scope =>
        {
            // Each lit draw pushes [model | matParams(metallic, roughness)].
            scope.DrawIndexed(
                vertexBuffer: groundVB,
                indexBuffer: groundIB,
                pipeline: litPipeline,
                indexCount: 6,
                uniforms: perFrame,
                textures: shadowBindings,
                material: groundMaterial,
                pushConstants: LitPush(groundModel, metallic: 0.0f, roughness: 0.85f, normalScale: 1.0f));
            scope.DrawIndexed(
                vertexBuffer: cubeVB,
                indexBuffer: cubeIB,
                pipeline: litPipeline,
                indexCount: 36,
                uniforms: perFrame,
                textures: shadowBindings,
                material: cubeMaterial,
                pushConstants: LitPush(cubeModel, metallic: 0.1f, roughness: 0.35f));
            // PBR sphere test rig: row of metallic, row of dielectric, each
            // sweeping roughness left→right. One shared white material; the
            // metallic/roughness vary per draw via the push constant.
            for (var i = 0; i < SphereCount; i++)
            {
                var rough = SphereCount > 1 ? i / (float)(SphereCount - 1) : 0.5f;
                rough = 0.05f + rough * 0.95f;
                scope.DrawIndexed(
                    vertexBuffer: sphereVB, indexBuffer: sphereIB, pipeline: litPipeline,
                    indexCount: sphereIndexCount, uniforms: perFrame, textures: shadowBindings,
                    material: sphereMaterial,
                    pushConstants: LitPush(sphereModels[i], metallic: 1.0f, roughness: rough));
                scope.DrawIndexed(
                    vertexBuffer: sphereVB, indexBuffer: sphereIB, pipeline: litPipeline,
                    indexCount: sphereIndexCount, uniforms: perFrame, textures: shadowBindings,
                    material: sphereMaterial,
                    pushConstants: LitPush(sphereModels[SphereCount + i], metallic: 0.0f, roughness: rough));
            }
            // Skinned cesium: set 2 (skin material) + set 3 (bone palette SSBO).
            scope.DrawIndexed(
                vertexBuffer: cesiumMesh.VertexBuffer,
                indexBuffer: cesiumMesh.IndexBuffer,
                pipeline: skinnedLitPipeline,
                indexCount: cesiumMesh.IndexCount,
                uniforms: perFrame,
                textures: shadowBindings,
                material: cesiumSkinMaterial,
                perDrawMaterial: cesiumBoneMaterial,
                pushConstants: LitPush(cesiumWorldModel, metallic: 0.0f, roughness: 0.6f));
        }, clearColor: new GraphicsColor(0.04f, 0.06f, 0.10f, 1.0f));

        // Bloom chain: bright(hdr) → blurH → blurV at quarter res. The chain resolves
        // each stage's input from its wiring; bright reads a const threshold in-shader
        // (no push), each blur takes a texel step (direction / bloom-target-resolution).
        var bloomW = MathF.Max(1f, frame.Width * BloomScale);
        var bloomH = MathF.Max(1f, frame.Height * BloomScale);
        bloom.Record((i, stage) => i switch
        {
            0 => null,
            1 => Vec2Bytes(1f / bloomW, 0f),
            _ => Vec2Bytes(0f, 1f / bloomH),
        });

        graph.Execute(commandList);

        // Imperative present. Final composites bloom + tonemaps the HDR
        // (exposure, bloomIntensity via push); depth views are grayscale.
        var isFinal = (View)viewMode == View.Final;
        var (presentTex, presentPipe) = (View)viewMode switch
        {
            View.SunShadow => (graph.GetDepthTexture(sunShadowHandle), presentDepthPipeline),
            View.SpotShadow => (graph.GetDepthTexture(spot0ShadowHandle), presentDepthPipeline),
            View.SceneDepth => (graph.GetDepthTexture(sceneDepthHandle), presentDepthPipeline),
            _ => (graph.GetColorTexture(hdrHandle), presentPipeline),
        };
        var bloomTex = graph.GetColorTexture(bloom.Output);
        var presentPush = new byte[8];
        System.Runtime.InteropServices.MemoryMarshal.Write(presentPush.AsSpan(0, 4), in exposure);
        var bloomI = bloomEnabled ? BloomIntensity : 0f;
        System.Runtime.InteropServices.MemoryMarshal.Write(presentPush.AsSpan(4, 4), in bloomI);
        commandList.Pass(
            "present",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0, 0, 0, 1) },
                ClearDepth: true),
            pass =>
            {
                if (isFinal)
                {
                    fullscreen.Draw(
                        pass, presentPipe,
                        new[]
                        {
                            new ShaderTextureBinding("uHdr", presentTex),
                            new ShaderTextureBinding("uBloom", bloomTex),
                        },
                        presentPush);
                }
                else
                {
                    fullscreen.Draw(
                        pass, presentPipe,
                        new[] { new ShaderTextureBinding("uOffscreen", presentTex) });
                }
            });
    }

    // Record a fullscreen graph pass: draw the fullscreen triangle sampling
    // one input texture, optional push payload. Used by the bloom chain.
    private static byte[] Vec2Bytes(float x, float y)
    {
        var bytes = new byte[8];
        var v = new Vector2(x, y);
        System.Runtime.InteropServices.MemoryMarshal.Write(bytes, in v);
        return bytes;
    }

    // Record a depth-only shadow pass for one light: draw the cube + skinned
    // cesium, each pushing [model | shadowViewProj] (128 bytes). The same
    // shadow pipeline serves every light because the VP rides in the push
    // constant, not a per-light shader.
    private void RecordShadowPass(PassHandle pass, Matrix4x4 shadowVp)
    {
        var cubePush = ShadowPushBytes(cubeModel, shadowVp);
        var cesiumPush = ShadowPushBytes(cesiumWorldModel, shadowVp);
        graph.Pass(pass, scope =>
        {
            scope.DrawIndexed(
                vertexBuffer: cubeVB,
                indexBuffer: cubeIB,
                pipeline: shadowPipeline,
                indexCount: 36,
                uniforms: Array.Empty<ShaderUniform>(),
                textures: Array.Empty<ShaderTextureBinding>(),
                pushConstants: cubePush);
            scope.DrawIndexedSkinnedShadow(
                vertexBuffer: cesiumMesh.VertexBuffer,
                indexBuffer: cesiumMesh.IndexBuffer,
                pipeline: skinnedShadowPipeline,
                indexCount: cesiumMesh.IndexCount,
                uniforms: Array.Empty<ShaderUniform>(),
                textures: Array.Empty<ShaderTextureBinding>(),
                perDrawMaterial: cesiumBoneMaterial,
                pushConstants: cesiumPush);
        });
    }

    // [model (64 bytes) | shadowViewProj (64 bytes)] — matches the shadow
    // shaders' push-constant block. Fresh buffer per call so each deferred
    // graph draw captures its own bytes (the graph replays scopes at Execute).
    private static byte[] ShadowPushBytes(Matrix4x4 model, Matrix4x4 shadowVp)
    {
        var bytes = new byte[128];
        System.Runtime.InteropServices.MemoryMarshal.Write(bytes.AsSpan(0, 64), in model);
        System.Runtime.InteropServices.MemoryMarshal.Write(bytes.AsSpan(64, 64), in shadowVp);
        return bytes;
    }

    // Record one cube-face depth pass for the point light. Casters write
    // linear distance (point shadow shaders) via a 144-byte push:
    // [model | faceVP | lightPosFar].
    private void RecordPointShadowFace(PassHandle pass, Matrix4x4 faceVp)
    {
        var lightPosFar = new Vector4(PointPosition, PointFar);
        var cubePush = PointShadowPushBytes(cubeModel, faceVp, lightPosFar);
        var cesiumPush = PointShadowPushBytes(cesiumWorldModel, faceVp, lightPosFar);
        graph.Pass(pass, scope =>
        {
            scope.DrawIndexed(
                vertexBuffer: cubeVB,
                indexBuffer: cubeIB,
                pipeline: pointShadowPipeline,
                indexCount: 36,
                uniforms: Array.Empty<ShaderUniform>(),
                textures: Array.Empty<ShaderTextureBinding>(),
                pushConstants: cubePush);
            scope.DrawIndexedSkinnedShadow(
                vertexBuffer: cesiumMesh.VertexBuffer,
                indexBuffer: cesiumMesh.IndexBuffer,
                pipeline: pointSkinnedShadowPipeline,
                indexCount: cesiumMesh.IndexCount,
                uniforms: Array.Empty<ShaderUniform>(),
                textures: Array.Empty<ShaderTextureBinding>(),
                perDrawMaterial: cesiumBoneMaterial,
                pushConstants: cesiumPush);
        });
    }

    // [model (64) | matParams (16: metallic, roughness, normalScale, _)] = 80 bytes.
    // The lit/skinned-lit push: vertex reads uModel, fragment reads uMatParams.
    private static byte[] LitPush(Matrix4x4 model, float metallic, float roughness, float normalScale = 1f)
    {
        var bytes = new byte[80];
        System.Runtime.InteropServices.MemoryMarshal.Write(bytes.AsSpan(0, 64), in model);
        var p = new Vector4(metallic, roughness, normalScale, 0f);
        System.Runtime.InteropServices.MemoryMarshal.Write(bytes.AsSpan(64, 16), in p);
        return bytes;
    }

    // [model (64) | faceViewProj (64) | lightPosFar (16)] = 144 bytes.
    private static byte[] PointShadowPushBytes(Matrix4x4 model, Matrix4x4 faceVp, Vector4 lightPosFar)
    {
        var bytes = new byte[144];
        System.Runtime.InteropServices.MemoryMarshal.Write(bytes.AsSpan(0, 64), in model);
        System.Runtime.InteropServices.MemoryMarshal.Write(bytes.AsSpan(64, 64), in faceVp);
        System.Runtime.InteropServices.MemoryMarshal.Write(bytes.AsSpan(128, 16), in lightPosFar);
        return bytes;
    }

    public void Debug(DebugContext debug)
    {
        debug.Values.Value("frame", frameCount);

        // The keys this loop handles itself (ReadInput, OnUpdate); the rest are bound on controls.
        camera.DescribeDefaultKeys(debug);
        debug.Keys.Describe("Up / Down", "exposure up / down");
        debug.Keys.Describe(Key.Escape, "quit");

        // Tunable knobs (view mode, shader channel, sun, tone map, lights,
        // camera tuning) live in dedicated debug contributors in the Debug/
        // folder — they register themselves on the DebugSystem in OnLoad
        // and emit their own Controls/Values under top-level scopes
        // ("sun", "tonemap", "lights", "camera-tuning", "view").

        using (debug.Scope("cube"))
        {
            debug.Values.Value("rotY-rad", currentRotY);
            debug.Values.Value("rotX-rad", currentRotX);
        }
        using (debug.Scope("cesium"))
        {
            debug.Values.Value("bones", cesiumSkeleton.BoneCount);
            debug.Values.Value("anim", cesiumAnimation.Name);
            debug.Values.Value("anim-time", (float)cesiumAnimTime);
            debug.Values.Value("anim-duration", (float)cesiumAnimation.Duration);
        }
        using (debug.Scope("graph"))
        {
            debug.Values.Value("passes", "sun-shadow → lit-scene → present");
            debug.Values.Value("hdr-handle", hdrHandle.Id);
            debug.Values.Value("shadow-handle", sunShadowHandle.Id);
            debug.Values.Value("shadow-size", ShadowMapSize);
        }
        using (debug.Scope("camera"))
        {
            // Read-only camera observability. The tunable knobs (fly speed,
            // FOV, anim pause) live in CameraTuningControls under its own
            // "camera-tuning" scope.
            debug.Values.Value("position", cameraPosition);
            debug.Values.Value("forward", cameraForward);
            debug.Values.Value("yaw-deg", camera.Yaw);
            debug.Values.Value("pitch-deg", camera.Pitch);
            debug.Values.Value("fovY-rad", camera.FieldOfView);
            debug.Values.Value("view", ViewLabels[viewMode]);
        }

        // --- Debug draw overlays (toggle with backtick) -----------------
        // Every primitive below belongs to this view. Scoped rather than assigned: the old
        // per-channel matrix meant a frame could only ever be one world seen one way.
        using var view = debug.Draw.In("main", viewProj);

        // Cube oriented bounding box.
        var obb = Matrix4x4.CreateScale(0.51f) * cubeModel;
        debug.Draw.Obb("cube/obb", obb, new GraphicsColor(1f, 1f, 1f, 0.85f));

        // Sun shadow frustum — shows the orthographic volume the shadow
        // pass renders. If the scene pokes outside this box, shadows clip.
        debug.Draw.Frustum("sun/frustum", sunShadowVP, new GraphicsColor(1f, 0.85f, 0.2f, 0.8f));

        // Sun (directional): an incoming arrow from up-sun toward the origin,
        // so you can see which way the sunlight travels. Yellow = sun.
        debug.Draw.Arrow("sun/incoming", -SunDirection * 4.0f, Vector3.Zero,
            new GraphicsColor(1f, 0.92f, 0.3f, 1f));

        // Spot light: its perspective shadow frustum + an aim arrow. The
        // frustum shows exactly the cone volume the spot shadow covers.
        debug.Draw.Frustum("spot0/frustum", spot0ViewProj, new GraphicsColor(1f, 0.5f, 0.2f, 0.8f));
        debug.Draw.Arrow("spot0/dir", Spot0Position, Spot0Target, new GraphicsColor(1f, 0.4f, 0.15f, 1f));
        debug.Draw.Sphere("spot0/pos", Spot0Position, 0.12f, new GraphicsColor(1f, 0.5f, 0.2f, 1f));
        debug.Draw.Frustum("spot1/frustum", spot1ViewProj, new GraphicsColor(0.3f, 1f, 0.45f, 0.8f));
        debug.Draw.Arrow("spot1/dir", Spot1Position, Spot1Target, new GraphicsColor(0.25f, 0.9f, 0.4f, 1f));
        debug.Draw.Sphere("spot1/pos", Spot1Position, 0.12f, new GraphicsColor(0.3f, 1f, 0.45f, 1f));

        // Point light position + range sphere (cool cyan).
        debug.Draw.Sphere("point/pos", PointPosition, 0.12f, new GraphicsColor(0.3f, 0.6f, 1f, 1f));
        debug.Draw.Sphere("point/range", PointPosition, PointRange, new GraphicsColor(0.25f, 0.5f, 0.9f, 0.25f));

        // Ground reference grid.
        debug.Draw.Grid("ground/grid", new Vector3(0, -0.6f, 0), 6f, 12,
            new GraphicsColor(0.4f, 0.45f, 0.55f, 0.5f));
    }

    public void Dispose()
    {
        graph?.Dispose();
        bloom?.Dispose();
        fullscreen?.Dispose();
    }

    // Perspective shadow VP for a spot light. FOV covers the outer cone with
    // a small margin so the cone edge isn't clipped by the shadow map.
    private static Matrix4x4 MakeSpotVP(Vector3 position, Vector3 target, float outerDeg, float range)
    {
        var view = Matrix4x4.CreateLookAt(position, target, Vector3.UnitY);
        var fov = 2f * outerDeg * (MathF.PI / 180f) * 1.1f;
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(fov, 1.0f, 0.2f, range + 4f);
        return view * proj;
    }

    // Direct row-major write — GLSL std430 reads column-major so the
    // transpose happens implicitly and `mat * v_col` in the shader equals
    // `v_row * mat` here. Same convention as every other matrix upload.
    private static void PackPalette(BonePalette palette, byte[] dst)
    {
        if (dst.Length != palette.BoneCount * 64)
        {
            throw new ArgumentException(
                $"Palette payload size mismatch: {palette.BoneCount} bones expects {palette.BoneCount * 64} bytes, got {dst.Length}.",
                nameof(dst));
        }
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(dst);
        for (var i = 0; i < palette.BoneCount; i++)
        {
            var m = palette.Matrices[i];
            var off = i * 16;
            floats[off + 0]  = m.M11; floats[off + 1]  = m.M12; floats[off + 2]  = m.M13; floats[off + 3]  = m.M14;
            floats[off + 4]  = m.M21; floats[off + 5]  = m.M22; floats[off + 6]  = m.M23; floats[off + 7]  = m.M24;
            floats[off + 8]  = m.M31; floats[off + 9]  = m.M32; floats[off + 10] = m.M33; floats[off + 11] = m.M34;
            floats[off + 12] = m.M41; floats[off + 13] = m.M42; floats[off + 14] = m.M43; floats[off + 15] = m.M44;
        }
    }

    private static (VertexPosition3NormalTexture[] Vertices, ushort[] Indices) BuildCube()
    {
        VertexPosition3NormalTexture V(float x, float y, float z, float nx, float ny, float nz, float u, float v) =>
            new(new GraphicsVector3(x, y, z), new GraphicsVector3(nx, ny, nz), new GraphicsVector2(u, v));

        var vertices = new VertexPosition3NormalTexture[]
        {
            V(-0.5f, -0.5f, -0.5f,  0, 0, -1,  0, 1),
            V( 0.5f, -0.5f, -0.5f,  0, 0, -1,  1, 1),
            V( 0.5f,  0.5f, -0.5f,  0, 0, -1,  1, 0),
            V(-0.5f,  0.5f, -0.5f,  0, 0, -1,  0, 0),
            V(-0.5f, -0.5f,  0.5f,  0, 0,  1,  0, 1),
            V( 0.5f, -0.5f,  0.5f,  0, 0,  1,  1, 1),
            V( 0.5f,  0.5f,  0.5f,  0, 0,  1,  1, 0),
            V(-0.5f,  0.5f,  0.5f,  0, 0,  1,  0, 0),
            V(-0.5f, -0.5f, -0.5f, -1, 0,  0,  0, 1),
            V(-0.5f, -0.5f,  0.5f, -1, 0,  0,  1, 1),
            V(-0.5f,  0.5f,  0.5f, -1, 0,  0,  1, 0),
            V(-0.5f,  0.5f, -0.5f, -1, 0,  0,  0, 0),
            V( 0.5f, -0.5f, -0.5f,  1, 0,  0,  0, 1),
            V( 0.5f,  0.5f, -0.5f,  1, 0,  0,  0, 0),
            V( 0.5f,  0.5f,  0.5f,  1, 0,  0,  1, 0),
            V( 0.5f, -0.5f,  0.5f,  1, 0,  0,  1, 1),
            V(-0.5f, -0.5f, -0.5f,  0, -1, 0,  0, 1),
            V( 0.5f, -0.5f, -0.5f,  0, -1, 0,  1, 1),
            V( 0.5f, -0.5f,  0.5f,  0, -1, 0,  1, 0),
            V(-0.5f, -0.5f,  0.5f,  0, -1, 0,  0, 0),
            V(-0.5f,  0.5f, -0.5f,  0,  1, 0,  0, 1),
            V(-0.5f,  0.5f,  0.5f,  0,  1, 0,  0, 0),
            V( 0.5f,  0.5f,  0.5f,  0,  1, 0,  1, 0),
            V( 0.5f,  0.5f, -0.5f,  0,  1, 0,  1, 1),
        };
        var indices = new ushort[]
        {
            0,  1,  2,   0,  2,  3,
            4,  5,  6,   4,  6,  7,
            8,  9, 10,   8, 10, 11,
            12, 13, 14,  12, 14, 15,
            16, 17, 18,  16, 18, 19,
            20, 21, 22,  20, 22, 23,
        };
        return (vertices, indices);
    }

    // UV sphere centered at origin. Normals = normalized position (unit
    // sphere), UVs from spherical coords. rings = latitude bands, sectors =
    // longitude segments.
    private static (VertexPosition3NormalTexture[] Vertices, ushort[] Indices) BuildSphere(
        float radius, int rings, int sectors)
    {
        var verts = new List<VertexPosition3NormalTexture>((rings + 1) * (sectors + 1));
        for (var r = 0; r <= rings; r++)
        {
            var phi = MathF.PI * r / rings;          // 0..π latitude
            var y = MathF.Cos(phi);
            var sinPhi = MathF.Sin(phi);
            for (var s = 0; s <= sectors; s++)
            {
                var theta = 2f * MathF.PI * s / sectors;  // 0..2π longitude
                var x = sinPhi * MathF.Cos(theta);
                var z = sinPhi * MathF.Sin(theta);
                var n = new GraphicsVector3(x, y, z);
                verts.Add(new VertexPosition3NormalTexture(
                    new GraphicsVector3(x * radius, y * radius, z * radius),
                    n,
                    new GraphicsVector2(s / (float)sectors, r / (float)rings)));
            }
        }
        var indices = new List<ushort>(rings * sectors * 6);
        var stride = sectors + 1;
        for (var r = 0; r < rings; r++)
        for (var s = 0; s < sectors; s++)
        {
            var a = (ushort)(r * stride + s);
            var b = (ushort)((r + 1) * stride + s);
            indices.Add(a); indices.Add(b); indices.Add((ushort)(a + 1));
            indices.Add((ushort)(a + 1)); indices.Add(b); indices.Add((ushort)(b + 1));
        }
        return (verts.ToArray(), indices.ToArray());
    }

    private static (VertexPosition3NormalTexture[] Vertices, ushort[] Indices) BuildGround(
        float extent, float y, float uvTile)
    {
        var n = new GraphicsVector3(0, 1, 0);
        var vertices = new VertexPosition3NormalTexture[]
        {
            new(new GraphicsVector3(-extent, y, -extent), n, new GraphicsVector2(0,      0)),
            new(new GraphicsVector3( extent, y, -extent), n, new GraphicsVector2(uvTile, 0)),
            new(new GraphicsVector3( extent, y,  extent), n, new GraphicsVector2(uvTile, uvTile)),
            new(new GraphicsVector3(-extent, y,  extent), n, new GraphicsVector2(0,      uvTile)),
        };
        var indices = new ushort[] { 0, 2, 1,  0, 3, 2 };
        return (vertices, indices);
    }

    // --- IBL procedural-sky generation ---------------------------------

    // Analytic sky radiance (linear) for a world direction. Zenith→horizon
    // gradient, dim ground below, plus a soft warm glow toward the sun.
    private static Vector3 SkyColor(Vector3 d)
    {
        d = Vector3.Normalize(d);
        var zenith = new Vector3(0.22f, 0.42f, 0.82f);
        var horizon = new Vector3(0.70f, 0.78f, 0.90f);
        var ground = new Vector3(0.16f, 0.15f, 0.14f);
        Vector3 baseCol;
        if (d.Y >= 0f)
        {
            var k = MathF.Pow(Math.Clamp(d.Y, 0f, 1f), 0.5f);
            baseCol = Vector3.Lerp(horizon, zenith, k);
        }
        else
        {
            var k = Math.Clamp(-d.Y, 0f, 1f);
            baseCol = Vector3.Lerp(horizon, ground, k);
        }
        // Sun glow: light travels along SkyBakeSunDirection, so it comes
        // FROM the opposite direction. Uses the bake-time constant, not
        // the live SunDirection (see SkyBakeSunDirection comment).
        var toSun = -SkyBakeSunDirection;
        var glow = MathF.Pow(MathF.Max(Vector3.Dot(d, toSun), 0f), 32f);
        baseCol += new Vector3(0.5f, 0.42f, 0.30f) * glow;
        return Vector3.Clamp(baseCol, Vector3.Zero, Vector3.One);
    }

    // Cubemap face (u,v)∈[-1,1] → world direction. Canonical Vulkan/GL cube
    // convention (matches samplerCube lookup), so generation and sampling agree.
    private static Vector3 CubeDir(int face, float u, float v) => face switch
    {
        0 => new Vector3( 1f, -v, -u),   // +X
        1 => new Vector3(-1f, -v,  u),   // -X
        2 => new Vector3( u,  1f,  v),   // +Y
        3 => new Vector3( u, -1f, -v),   // -Y
        4 => new Vector3( u, -v,  1f),   // +Z
        _ => new Vector3(-u, -v, -1f),   // -Z
    };

    private static byte LinByte(float c) => (byte)(Math.Clamp(c, 0f, 1f) * 255f + 0.5f);

    // Render one env mip level (face-major within the level handled by caller).
    private static void WriteEnvFace(byte[] dst, int offset, int face, int size)
    {
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var u = 2f * ((x + 0.5f) / size) - 1f;
            var v = 2f * ((y + 0.5f) / size) - 1f;
            var c = SkyColor(CubeDir(face, u, v));
            var i = offset + (y * size + x) * 4;
            dst[i] = LinByte(c.X); dst[i + 1] = LinByte(c.Y); dst[i + 2] = LinByte(c.Z); dst[i + 3] = 255;
        }
    }

    // Env cube, face-major then mip-major. Each mip re-evaluates the analytic
    // sky at that resolution (smooth sky → equivalent to box-downsampling, but
    // cleaner). The mip chain is the prefiltered-specular stand-in.
    private static byte[] BuildEnvCubeWithMips(int faceSize, int mips)
    {
        long total = 0;
        for (var f = 0; f < 6; f++)
            for (var m = 0; m < mips; m++) { var s = faceSize >> m; total += s * s * 4; }
        var data = new byte[total];
        var offset = 0;
        for (var face = 0; face < 6; face++)
        for (var m = 0; m < mips; m++)
        {
            var s = faceSize >> m;
            WriteEnvFace(data, offset, face, s);
            offset += s * s * 4;
        }
        return data;
    }

    // Diffuse irradiance cube: for each output direction N, cosine-weighted
    // average of the sky over the hemisphere around N.
    private static byte[] BuildIrradianceCube(int faceSize)
    {
        var data = new byte[6 * faceSize * faceSize * 4];
        var faceBytes = faceSize * faceSize * 4;
        for (var face = 0; face < 6; face++)
        for (var y = 0; y < faceSize; y++)
        for (var x = 0; x < faceSize; x++)
        {
            var u = 2f * ((x + 0.5f) / faceSize) - 1f;
            var v = 2f * ((y + 0.5f) / faceSize) - 1f;
            var N = Vector3.Normalize(CubeDir(face, u, v));
            // Tangent basis around N.
            var up = MathF.Abs(N.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
            var tangent = Vector3.Normalize(Vector3.Cross(up, N));
            var bitangent = Vector3.Cross(N, tangent);

            var sum = Vector3.Zero;
            var weight = 0f;
            const int phiSteps = 24;
            const int thetaSteps = 12;
            for (var pi = 0; pi < phiSteps; pi++)
            for (var ti = 0; ti < thetaSteps; ti++)
            {
                var phi = 2f * MathF.PI * (pi + 0.5f) / phiSteps;
                var theta = 0.5f * MathF.PI * (ti + 0.5f) / thetaSteps;
                var st = MathF.Sin(theta);
                var local = new Vector3(st * MathF.Cos(phi), st * MathF.Sin(phi), MathF.Cos(theta));
                var dir = local.X * tangent + local.Y * bitangent + local.Z * N;
                var w = MathF.Cos(theta) * MathF.Sin(theta); // cosine × solid-angle
                sum += SkyColor(dir) * w;
                weight += w;
            }
            var irr = sum / MathF.Max(weight, 1e-4f);
            var idx = face * faceBytes + (y * faceSize + x) * 4;
            data[idx] = LinByte(irr.X); data[idx + 1] = LinByte(irr.Y); data[idx + 2] = LinByte(irr.Z); data[idx + 3] = 255;
        }
        return data;
    }

    // Split-sum BRDF integration LUT. R = scale on F0, G = bias. Encoded in
    // the red/green channels of an Rgba8 texture (values are in [0,1]).
    private static byte[] BuildBrdfLut(int size)
    {
        var data = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var NdotV = (x + 0.5f) / size;
            var roughness = (y + 0.5f) / size;
            var (a, b) = IntegrateBrdf(NdotV, roughness);
            var i = (y * size + x) * 4;
            data[i] = LinByte(a); data[i + 1] = LinByte(b); data[i + 2] = 0; data[i + 3] = 255;
        }
        return data;
    }

    private static (float A, float B) IntegrateBrdf(float NdotV, float roughness)
    {
        var V = new Vector3(MathF.Sqrt(1f - NdotV * NdotV), 0f, NdotV);
        float a = 0f, b = 0f;
        const int samples = 256;
        var N = new Vector3(0, 0, 1);
        for (var i = 0; i < samples; i++)
        {
            var xi = Hammersley(i, samples);
            var H = ImportanceSampleGgx(xi, roughness, N);
            var L = Vector3.Normalize(2f * Vector3.Dot(V, H) * H - V);
            var NdotL = MathF.Max(L.Z, 0f);
            var NdotH = MathF.Max(H.Z, 0f);
            var VdotH = MathF.Max(Vector3.Dot(V, H), 0f);
            if (NdotL > 0f)
            {
                var g = GeometrySmithIbl(NdotV, NdotL, roughness);
                var gVis = g * VdotH / MathF.Max(NdotH * NdotV, 1e-5f);
                var fc = MathF.Pow(1f - VdotH, 5f);
                a += (1f - fc) * gVis;
                b += fc * gVis;
            }
        }
        return (a / samples, b / samples);
    }

    private static Vector2 Hammersley(int i, int n)
    {
        uint bits = (uint)i;
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
        var rdi = bits * 2.3283064365386963e-10f;
        return new Vector2(i / (float)n, rdi);
    }

    private static Vector3 ImportanceSampleGgx(Vector2 xi, float roughness, Vector3 N)
    {
        var a = roughness * roughness;
        var phi = 2f * MathF.PI * xi.X;
        var cosT = MathF.Sqrt((1f - xi.Y) / (1f + (a * a - 1f) * xi.Y));
        var sinT = MathF.Sqrt(1f - cosT * cosT);
        // N is +Z in tangent space here, so the half-vector is direct.
        return new Vector3(MathF.Cos(phi) * sinT, MathF.Sin(phi) * sinT, cosT);
    }

    private static float GeometrySmithIbl(float NdotV, float NdotL, float roughness)
    {
        var k = (roughness * roughness) / 2f;
        float GeomG(float c) => c / (c * (1f - k) + k);
        return GeomG(NdotV) * GeomG(NdotL);
    }

    // Procedural tangent-space normal map: two crossed sine ripples whose
    // height gradient becomes the perturbed normal. Tileable, linear-encoded
    // (RGB = normal*0.5+0.5). strength scales the xy slope.
    private static byte[] BuildRippleNormalMap(int size, float freq, float strength)
    {
        var data = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var u = x / (float)size * MathF.PI * 2f * freq;
            var v = y / (float)size * MathF.PI * 2f * freq;
            // height h = sin(u) + sin(v); slope = dh/du, dh/dv.
            var dhdu = MathF.Cos(u) * strength;
            var dhdv = MathF.Cos(v) * strength;
            var n = Vector3.Normalize(new Vector3(-dhdu, -dhdv, 1f));
            var idx = (y * size + x) * 4;
            data[idx]     = (byte)((n.X * 0.5f + 0.5f) * 255f);
            data[idx + 1] = (byte)((n.Y * 0.5f + 0.5f) * 255f);
            data[idx + 2] = (byte)((n.Z * 0.5f + 0.5f) * 255f);
            data[idx + 3] = 255;
        }
        return data;
    }

    private static byte[] BuildCheckerboard(int size, int cellCount)
    {
        var data = new byte[size * size * 4];
        var cellSize = size / cellCount;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dark = ((x / cellSize) + (y / cellSize)) % 2 == 0;
            var idx = (y * size + x) * 4;
            var c = dark ? (byte)80 : (byte)220;
            data[idx] = c; data[idx + 1] = c; data[idx + 2] = c; data[idx + 3] = 255;
        }
        return data;
    }
}
