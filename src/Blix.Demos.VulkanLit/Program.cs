using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanLit;

// Vector B step 6.a + 6.b + 6.c — ShaderLab lit shader port,
// sun shadow map, and skinned glTF.
//
// Two graph passes:
//   1. "sun-shadow" (depth-only) — cube + skinned cesium → 1024×1024
//      shadow map from sun POV.
//   2. "lit-scene"  (color+depth) — cube + ground plane + skinned cesium
//      → hdr (Rgba16F), reading the shadow map via set 1 binding 0.
//
// Imperative present pass samples hdr → swapchain.
//
// The skinned model rides the per-draw bone-palette path:
//   - Set 3 binding 0: readonly SSBO mat4 m[] — one entry per bone.
//   - MaterialBindings with framesInFlight = MaxFramesInFlight; each
//     frame the demo writes the slot matching CurrentFrameSlot.
//   - SkinnedGameObject-equivalent state: GltfModel + Pose + BonePalette
//     + AnimationClip, sampled at frame time then composed into the SSBO.
//
// Visual gate: a lit cube + a walking humanoid casting moving shadows
// on a checker ground plane. Window resize works.
public static class Program
{
    public static void Main()
    {
        var loop = new LitLoop();
        using var window = new Window(loop, new WindowOptions("Blix — Vulkan Lit + Shadow + Skinned glTF", 1280, 720));
        window.Run();
    }
}

internal sealed class LitLoop : IGameLoop, IInputHandler, IDebuggable, IDisposable
{
    public string DebugName => "vulkan-lit";

    // Cube + ground geometry.
    private VertexBufferHandle cubeVB;
    private IndexBufferHandle cubeIB;
    private VertexBufferHandle groundVB;
    private IndexBufferHandle groundIB;

    // Albedo textures + materials.
    private TextureHandle albedoTexture;
    private TextureHandle cesiumAlbedoTexture;
    private MaterialHandle cubeMaterial;
    private MaterialHandle groundMaterial;
    private MaterialHandle cesiumSkinMaterial;   // set 2 (per-material)
    private MaterialHandle cesiumBoneMaterial;   // set 3 (per-draw, framesInFlight replicated)
    private MaterialBindings cesiumBonePalette = null!;  // direct ref for per-frame WriteBuffer

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
    private VertexBufferHandle cesiumVB;
    private IndexBufferHandle cesiumIB;
    private int cesiumIndexCount;
    private Skeleton cesiumSkeleton = null!;
    private Pose cesiumPose = null!;
    private Pose cesiumRestPose = null!;
    private BonePalette cesiumPalette = null!;
    private AnimationClip cesiumAnimation = null!;
    private Matrix4x4 cesiumMeshNodeTransform;
    private Matrix4x4 cesiumUserTransform;
    private byte[] cesiumPalettePayload = null!;
    private readonly byte[] cesiumModelPushBytes = new byte[64];
    private double cesiumAnimTime;
    private VulkanGraphicsDevice vkDevice = null!;

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

    // Present.
    private VertexBufferHandle fullscreenVB;
    private IndexBufferHandle fullscreenIB;

    // Per-frame state.
    private readonly byte[] cubeModelPushBytes = new byte[64];
    private readonly byte[] groundModelPushBytes = new byte[64];
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
    private static readonly Vector3 Spot0Position = new(-2.6f, 3.2f, 1.8f);
    private static readonly Vector3 Spot0Target = new(0.2f, -0.4f, 0.2f);
    private static readonly Vector3 Spot0Color = new(1.0f, 0.45f, 0.2f);  // warm orange
    private const float Spot0Intensity = 6.0f;
    private const float Spot0Range = 9.0f;
    private const float Spot0InnerDeg = 14f;
    private const float Spot0OuterDeg = 22f;

    private static readonly Vector3 Spot1Position = new(2.8f, 3.0f, -1.6f);
    private static readonly Vector3 Spot1Target = new(0.4f, -0.4f, 0.0f);
    private static readonly Vector3 Spot1Color = new(0.4f, 1.0f, 0.55f);  // green
    private const float Spot1Intensity = 6.0f;
    private const float Spot1Range = 9.0f;
    private const float Spot1InnerDeg = 13f;
    private const float Spot1OuterDeg = 20f;

    // Point light. Cool cyan, sits low between cube and cesium to throw
    // omnidirectional shadows. Cube shadow stores linear distance / far.
    private static readonly Vector3 PointPosition = new(0.9f, 0.7f, 1.4f);
    private static readonly Vector3 PointColor = new(0.25f, 0.6f, 1.0f); // cool
    private const float PointIntensity = 4.0f;
    private const float PointRange = 6.0f;
    private const float PointFar = 8.0f;
    private const int PointShadowSize = 512;
    private float currentRotY;
    private float currentRotX;

    // Debug state.
    private GraphicsDeviceInfo? gpuInfo;
    private Vector3 cameraPosition;
    private float fovYRadians;

    // Free-fly camera + input.
    private IRenderHost host = null!;
    private readonly HashSet<Key> heldKeys = new();
    private float camYaw;       // radians, around world +Y
    private float camPitch;     // radians, around camera right
    private bool mouseLook;     // true while RMB held (cursor captured)
    private float moveSpeed = 3.5f;
    private float aspect = 16f / 9f;
    private Vector3 cameraForward = -Vector3.UnitZ;

    // Debug views: which texture the present pass shows.
    private enum View { Final = 0, SunShadow = 1, SpotShadow = 2, SceneDepth = 3 }
    private static readonly string[] ViewLabels = { "Final (HDR)", "Sun shadow map", "Spot shadow map", "Scene depth" };
    private int viewMode;          // index into View
    private bool animPaused;
    private GraphResourceHandle sceneDepthHandle;

    // Per-light isolation toggles (debug) — switch lights off independently
    // to attribute artifacts to a specific light.
    private bool sunEnabled = true;
    private bool spotEnabled = true;
    private bool pointEnabled = true;

    // Scene constants.
    private static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(-0.55f, -1.0f, -0.45f));
    private const float SunIntensity = 1.0f;
    private static readonly Vector3 AmbientColor = new(0.65f, 0.7f, 0.85f);
    private const float AmbientIntensity = 0.22f;

    private const int ShadowMapSize = 1024;

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        host.SetTitle("Blix — Vulkan Lit + Shadow + Skinned glTF");
        this.host = host;
        var vk = (VulkanGraphicsDevice)graphicsDevice;
        vkDevice = vk;
        gpuInfo = graphicsDevice.Info;

        // --- Geometry: cube + ground -------------------------------------
        var (cubeVerts, cubeIndices) = BuildCube();
        cubeVB = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(cubeVerts), "cube.vb");
        cubeIB = vk.CreateIndexBuffer(cubeIndices, name: "cube.ib");

        var (groundVerts, groundIndices) = BuildGround(extent: 3.0f, y: -0.6f, uvTile: 4.0f);
        groundVB = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(groundVerts), "ground.vb");
        groundIB = vk.CreateIndexBuffer(groundIndices, name: "ground.ib");

        var checker = BuildCheckerboard(256, 8);
        albedoTexture = vk.CreateTexture2D(
            new TextureDescription(256, 256, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            checker,
            "albedo");

        // --- Skinned model: cesium_man.glb -------------------------------
        var assetPath = Path.Combine(AppContext.BaseDirectory, "Assets", "models", "cesium_man.glb");
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
        var prim = cesiumModel.Primitives[0];
        var vbData = new VertexBufferData(
            new VertexBufferDescription(prim.Mesh.Layout, prim.Mesh.VertexCount, GraphicsBufferUsage.Static),
            prim.Mesh.VertexBytes);
        cesiumVB = vk.CreateVertexBuffer(vbData, name: "cesium.vb");
        cesiumIB = vk.CreateIndexBuffer(prim.Mesh.Indices, name: "cesium.ib");
        cesiumIndexCount = prim.Mesh.Indices.Length;

        // Cesium albedo: prefer the glTF's BaseColorTexture; fall back to a
        // neutral white if the material strips out images for some reason.
        cesiumAlbedoTexture = LoadGltfAlbedo(vk, prim.Material, fallbackName: "cesium.albedo.fallback");

        // --- Render graph ------------------------------------------------
        graph = new RenderGraph(vk);

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
            TotalSize: 432,
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
            });
        var tintUbo = new UniformBlockLayout(
            TotalSize: 16,
            Members: new[] { new UniformBlockMember("uTint", 0, 16) });

        // Shadow caster interface — light-agnostic. No set 0; the shadow VP
        // rides in the push constant (mat4 uModel @0, mat4 uShadowViewProj
        // @64 = 128 bytes) so one pipeline serves sun + spot + cube faces.
        var shadowInterface = new ShaderInterface(
            Slots: Array.Empty<DescriptorSetSlot>(),
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 128) });

        // Point cube shadow caster — writes linear distance via gl_FragDepth,
        // so the frag stage also reads the push (light pos + far). Push is
        // [model | faceVP | lightPosFar] = 144 bytes, Vertex+Fragment.
        var pointShadowInterface = new ShaderInterface(
            Slots: Array.Empty<DescriptorSetSlot>(),
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex | ShaderStages.Fragment, 0, 144) });

        var litInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer,
                    ShaderStages.Vertex | ShaderStages.Fragment, BlockLayout: frameUbo),
                new DescriptorSetSlot(1, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                new DescriptorSetSlot(1, 1, ShaderResourceType.SampledImage, ShaderStages.Fragment, Count: 2),
                new DescriptorSetSlot(1, 2, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                new DescriptorSetSlot(2, 0, ShaderResourceType.UniformBuffer,
                    ShaderStages.Fragment, BlockLayout: tintUbo),
                new DescriptorSetSlot(2, 1, ShaderResourceType.SampledImage, ShaderStages.Fragment),
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });

        // Skinned interfaces: add set 3 binding 0 = readonly SSBO bone palette.
        var boneSsboLayout = new UniformBlockLayout(
            TotalSize: cesiumSkeleton.BoneCount * 64,
            Members: new[]
            {
                new UniformBlockMember("bones", 0, cesiumSkeleton.BoneCount * 64,
                    ElementStride: 64),
            });
        var skinnedShadowInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(3, 0, ShaderResourceType.StorageBuffer,
                    ShaderStages.Vertex, BlockLayout: boneSsboLayout),
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 128) });

        // Skinned point cube caster: set 3 SSBO + 144-byte Vertex|Fragment push.
        var pointSkinnedShadowInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(3, 0, ShaderResourceType.StorageBuffer,
                    ShaderStages.Vertex, BlockLayout: boneSsboLayout),
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex | ShaderStages.Fragment, 0, 144) });

        var skinnedLitInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer,
                    ShaderStages.Vertex | ShaderStages.Fragment, BlockLayout: frameUbo),
                new DescriptorSetSlot(1, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                new DescriptorSetSlot(1, 1, ShaderResourceType.SampledImage, ShaderStages.Fragment, Count: 2),
                new DescriptorSetSlot(1, 2, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                new DescriptorSetSlot(2, 0, ShaderResourceType.UniformBuffer,
                    ShaderStages.Fragment, BlockLayout: tintUbo),
                new DescriptorSetSlot(2, 1, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                new DescriptorSetSlot(3, 0, ShaderResourceType.StorageBuffer,
                    ShaderStages.Vertex, BlockLayout: boneSsboLayout),
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });

        var presentInterface = new ShaderInterface(new[]
        {
            new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
        });

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
        graph.Compile();

        // --- Pipelines --------------------------------------------------
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");

        var shadowVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow.vert.spv"));
        var shadowFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "shadow.frag.spv"));
        shadowShaderProgram = vk.CreateShaderProgramFromSpv(shadowVertSpv, shadowFragSpv, shadowInterface, "shadow");
        shadowPipeline = vk.CreatePipeline(new PipelineDescription(
            shadowShaderProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(shadowPassHandle)), "shadow");

        var skinnedShadowVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "skinned_shadow.vert.spv"));
        skinnedShadowProgram = vk.CreateShaderProgramFromSpv(skinnedShadowVertSpv, shadowFragSpv, skinnedShadowInterface, "skinned_shadow");
        skinnedShadowPipeline = vk.CreatePipeline(new PipelineDescription(
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
        pointShadowProgram = vk.CreateShaderProgramFromSpv(pointShadowVertSpv, pointShadowFragSpv, pointShadowInterface, "point_shadow");
        pointShadowPipeline = vk.CreatePipeline(new PipelineDescription(
            pointShadowProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(pointFacePassHandles[0])), "point_shadow");

        var pointSkinnedShadowVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "point_skinned_shadow.vert.spv"));
        pointSkinnedShadowProgram = vk.CreateShaderProgramFromSpv(pointSkinnedShadowVertSpv, pointShadowFragSpv, pointSkinnedShadowInterface, "point_skinned_shadow");
        pointSkinnedShadowPipeline = vk.CreatePipeline(new PipelineDescription(
            pointSkinnedShadowProgram,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(pointFacePassHandles[0])), "point_skinned_shadow");

        var litVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "lit.vert.spv"));
        var litFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "lit.frag.spv"));
        litShaderProgram = vk.CreateShaderProgramFromSpv(litVertSpv, litFragSpv, litInterface, "lit");
        litPipeline = vk.CreatePipeline(new PipelineDescription(
            litShaderProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPassHandle)), "lit");

        var skinnedLitVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "skinned_lit.vert.spv"));
        skinnedLitProgram = vk.CreateShaderProgramFromSpv(skinnedLitVertSpv, litFragSpv, skinnedLitInterface, "skinned_lit");
        skinnedLitPipeline = vk.CreatePipeline(new PipelineDescription(
            skinnedLitProgram,
            VertexPosition3NormalTextureSkin4Tangent.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(litPassHandle)), "skinned_lit");

        var presentVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.vert.spv"));
        var presentFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.frag.spv"));
        presentShaderProgram = vk.CreateShaderProgramFromSpv(presentVertSpv, presentFragSpv, presentInterface, "present");
        presentPipeline = vk.CreatePipeline(new PipelineDescription(
            presentShaderProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.Disabled,
            RasterizerState.NoCulling,
            BlendState.Disabled), "present");

        // Depth-visualizer present pipeline (debug views). Same fullscreen
        // vertex shader + present interface, different fragment shader.
        var presentDepthFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present_depth.frag.spv"));
        presentDepthProgram = vk.CreateShaderProgramFromSpv(presentVertSpv, presentDepthFragSpv, presentInterface, "present_depth");
        presentDepthPipeline = vk.CreatePipeline(new PipelineDescription(
            presentDepthProgram,
            VertexPosition3NormalTexture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.Disabled,
            RasterizerState.NoCulling,
            BlendState.Disabled), "present_depth");

        // --- Materials --------------------------------------------------
        cubeMaterial = vk.CreateMaterial(litShaderProgram, name: "cube.material")
            .SetUniform(binding: 0, "uTint", new Vector4(1.0f, 1.0f, 1.0f, 1.0f))
            .SetTexture(binding: 1, albedoTexture)
            .Handle;

        groundMaterial = vk.CreateMaterial(litShaderProgram, name: "ground.material")
            .SetUniform(binding: 0, "uTint", new Vector4(0.55f, 0.62f, 0.78f, 1.0f))
            .SetTexture(binding: 1, albedoTexture)
            .Handle;

        // Cesium per-material set (set 2). Tint from BaseColorFactor when
        // available; the importer hands us the linear-space tint.
        var cesiumTint = prim.Material?.BaseColorFactor ?? new Vector4(1, 1, 1, 1);
        cesiumSkinMaterial = vk.CreateMaterial(skinnedLitProgram, name: "cesium.skin.material")
            .SetUniform(binding: 0, "uTint", cesiumTint)
            .SetTexture(binding: 1, cesiumAlbedoTexture)
            .Handle;

        // Cesium bone palette (set 3, per-draw SSBO, replicated across
        // frames so per-frame writes don't race with in-flight GPU work).
        cesiumBonePalette = vk.CreateMaterial(
            skinnedLitProgram,
            setIndex: 3,
            framesInFlight: vk.MaxFramesInFlightCount,
            name: "cesium.bonepalette");
        cesiumBoneMaterial = cesiumBonePalette.Handle;

        // --- Dummy fullscreen quad --------------------------------------
        var dummyVerts = new VertexPosition3NormalTexture[]
        {
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0)),
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0)),
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0)),
        };
        fullscreenVB = vk.CreateVertexBuffer(
            VertexPosition3NormalTexture.CreateBufferData(dummyVerts), "fullscreen.vb");
        fullscreenIB = vk.CreateIndexBuffer(new ushort[] { 0, 1, 2 }, name: "fullscreen.ib");

        // --- Camera + transforms ----------------------------------------
        cameraPosition = new Vector3(3.5f, 2.4f, 4.4f);
        fovYRadians = MathF.PI / 3f;
        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;

        // Derive initial yaw/pitch from the look-at direction so the
        // free-fly camera starts pointed at the scene.
        var dir = Vector3.Normalize(new Vector3(0.3f, 0.4f, 0) - cameraPosition);
        camYaw = MathF.Atan2(dir.X, -dir.Z);
        camPitch = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
        UpdateCamera();

        // Sun shadow VP — static (sun + scene bounds don't move).
        var lightEye = -SunDirection * 8.0f;
        var sunUp = MathF.Abs(SunDirection.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var sunView = Matrix4x4.CreateLookAt(lightEye, Vector3.Zero, sunUp);
        var sunOrtho = CreateOrthoVulkan(width: 7.5f, height: 7.5f, near: 0.1f, far: 16f);
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
        // occluder distance (a mirrored false shadow). Undo the flip while
        // keeping Vulkan's [0,1] depth range (can't use the GL-style
        // CreatePerspective — its z ∈ [-1,1] would clip near geometry in
        // Vulkan). See ShaderLab's point-shadow faces (GL CreatePerspective).
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
        System.Runtime.InteropServices.MemoryMarshal.Write(groundModelPushBytes, in groundModel);

        // Cesium user transform: stand the figure next to the cube on the
        // ground. CesiumMan is roughly 1.5 units tall in mesh-local space;
        // MeshNodeTransform handles the Z-up → Y-up axis correction the
        // asset's parent node applies.
        cesiumUserTransform =
            Matrix4x4.CreateTranslation(1.7f, -0.6f, -0.6f);
    }

    public void OnUpdate(Time time)
    {
        var dt = (float)time.Delta;

        // WASD = horizontal-plane move along view forward/right; Space /
        // LeftControl = world up/down. Full-3D forward (W follows pitch).
        var move = Vector3.Zero;
        if (heldKeys.Contains(Key.W)) move += cameraForward;
        if (heldKeys.Contains(Key.S)) move -= cameraForward;
        var right = Vector3.Normalize(Vector3.Cross(cameraForward, Vector3.UnitY));
        if (heldKeys.Contains(Key.D)) move += right;
        if (heldKeys.Contains(Key.A)) move -= right;
        if (heldKeys.Contains(Key.Space)) move += Vector3.UnitY;
        if (heldKeys.Contains(Key.LeftControl)) move -= Vector3.UnitY;

        if (move != Vector3.Zero)
        {
            cameraPosition += Vector3.Normalize(move) * moveSpeed * dt;
        }
        UpdateCamera();
    }

    // Recompute forward + viewProj from yaw/pitch/position/aspect.
    private void UpdateCamera()
    {
        var cp = MathF.Cos(camPitch);
        cameraForward = Vector3.Normalize(new Vector3(
            cp * MathF.Sin(camYaw),
            MathF.Sin(camPitch),
            -cp * MathF.Cos(camYaw)));
        var view = Matrix4x4.CreateLookAt(cameraPosition, cameraPosition + cameraForward, Vector3.UnitY);
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(fovYRadians, aspect, 0.1f, 100f);
        viewProj = view * proj;
    }

    public void OnResize(int width, int height)
    {
        if (height > 0)
        {
            aspect = width / (float)height;
            UpdateCamera();
        }
    }

    // --- IInputHandler ----------------------------------------------------

    public void OnKeyDown(Key key)
    {
        heldKeys.Add(key);
        switch (key)
        {
            case Key.V:
                viewMode = (viewMode + 1) % ViewLabels.Length;
                break;
            case Key.P:
                animPaused = !animPaused;
                break;
            // Per-light isolation (the Silk runtime has no clickable HUD, so
            // these live on the keyboard). State echoes to the console diag.
            case Key.Z:
                sunEnabled = !sunEnabled;
                break;
            case Key.X:
                spotEnabled = !spotEnabled;
                break;
            case Key.C:
                pointEnabled = !pointEnabled;
                break;
            case Key.Escape:
                host.RequestClose();
                break;
        }
    }

    public void OnKeyUp(Key key) => heldKeys.Remove(key);

    public void OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        if (!mouseLook) return;
        const float sensitivity = 0.0035f;
        camYaw += deltaX * sensitivity;
        camPitch -= deltaY * sensitivity;
        // Clamp pitch just shy of vertical to avoid gimbal flip.
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
        // Scroll adjusts fly speed (1.5×/0.66× per notch), clamped sane.
        moveSpeed = Math.Clamp(moveSpeed * (offsetY > 0 ? 1.25f : 0.8f), 0.3f, 40f);
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;
        currentRotY = (float)time.Total * 0.8f;
        currentRotX = (float)time.Total * 0.3f;
        cubeModel = Matrix4x4.CreateRotationY(currentRotY)
                  * Matrix4x4.CreateRotationX(currentRotX)
                  * Matrix4x4.CreateTranslation(0, 0.2f, 0);
        System.Runtime.InteropServices.MemoryMarshal.Write(cubeModelPushBytes, in cubeModel);

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
        System.Runtime.InteropServices.MemoryMarshal.Write(cesiumModelPushBytes, in cesiumWorldModel);

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
            new ShaderTextureBinding("uSunShadowMap", sunShadowTex, Slot: 0),
            new ShaderTextureBinding("uSpotShadowMaps[0]", spot0ShadowTex, Slot: 1, ArrayIndex: 0),
            new ShaderTextureBinding("uSpotShadowMaps[1]", spot1ShadowTex, Slot: 1, ArrayIndex: 1),
            new ShaderTextureBinding("uPointShadowCube", pointShadowTex, Slot: 2),
        };
        graph.Pass(litPassHandle, scope =>
        {
            scope.DrawIndexed(
                vertexBuffer: groundVB,
                indexBuffer: groundIB,
                pipeline: litPipeline,
                indexCount: 6,
                uniforms: perFrame,
                textures: shadowBindings,
                material: groundMaterial,
                pushConstants: groundModelPushBytes);
            scope.DrawIndexed(
                vertexBuffer: cubeVB,
                indexBuffer: cubeIB,
                pipeline: litPipeline,
                indexCount: 36,
                uniforms: perFrame,
                textures: shadowBindings,
                material: cubeMaterial,
                pushConstants: cubeModelPushBytes);
            // Skinned cesium: set 2 (skin material — tint + albedo) AND
            // set 3 (bone palette SSBO, framesInFlight-replicated). The
            // two-material DrawIndexed overload binds both at their
            // declared SetIndex values.
            scope.DrawIndexed(
                vertexBuffer: cesiumVB,
                indexBuffer: cesiumIB,
                pipeline: skinnedLitPipeline,
                indexCount: cesiumIndexCount,
                uniforms: perFrame,
                textures: shadowBindings,
                material: cesiumSkinMaterial,
                perDrawMaterial: cesiumBoneMaterial,
                pushConstants: cesiumModelPushBytes);
        }, clearColor: new GraphicsColor(0.04f, 0.06f, 0.10f, 1.0f));

        graph.Execute(commandList);

        // Imperative present — pick the texture + pipeline for the current
        // debug view. Final shows the lit HDR; the depth views show the
        // shadow map / scene depth through the grayscale visualizer.
        var (presentTex, presentPipe) = (View)viewMode switch
        {
            View.SunShadow => (graph.GetDepthTexture(sunShadowHandle), presentDepthPipeline),
            View.SpotShadow => (graph.GetDepthTexture(spot0ShadowHandle), presentDepthPipeline),
            View.SceneDepth => (graph.GetDepthTexture(sceneDepthHandle), presentDepthPipeline),
            _ => (graph.GetColorTexture(hdrHandle), presentPipeline),
        };
        commandList.Pass(
            "present",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0, 0, 0, 1) },
                ClearDepth: true),
            pass =>
            {
                pass.DrawIndexed(
                    vertexBuffer: fullscreenVB,
                    indexBuffer: fullscreenIB,
                    pipeline: presentPipe,
                    indexCount: 3,
                    uniforms: Array.Empty<ShaderUniform>(),
                    textures: new[] { new ShaderTextureBinding("uOffscreen", presentTex, Slot: 0) });
            });
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
                vertexBuffer: cesiumVB,
                indexBuffer: cesiumIB,
                pipeline: skinnedShadowPipeline,
                indexCount: cesiumIndexCount,
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
                vertexBuffer: cesiumVB,
                indexBuffer: cesiumIB,
                pipeline: pointSkinnedShadowPipeline,
                indexCount: cesiumIndexCount,
                uniforms: Array.Empty<ShaderUniform>(),
                textures: Array.Empty<ShaderTextureBinding>(),
                perDrawMaterial: cesiumBoneMaterial,
                pushConstants: cesiumPush);
        });
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

        // Interactive controls (overlay; click when RMB-look isn't active).
        // The returned value reflects either keyboard state (V / P keys) or
        // a click on the widget — whichever changed this frame.
        using (debug.Scope("controls"))
        {
            viewMode = debug.Controls.Enum("View [V]", viewMode, ViewLabels);
            animPaused = debug.Controls.Toggle("Pause anim [P]", animPaused);
            moveSpeed = debug.Controls.Float("Fly speed", moveSpeed, 0.3f, 40f);
        }
        using (debug.Scope("lights"))
        {
            // Toggle keys Z/X/C (no clickable HUD in the Silk runtime); state
            // echoed here so the console diag shows what's on.
            debug.Values.Value("sun [Z]", sunEnabled);
            debug.Values.Value("spot [X]", spotEnabled);
            debug.Values.Value("point [C]", pointEnabled);
            debug.Values.Value("spot0-pos", Spot0Position);
            debug.Values.Value("spot1-pos", Spot1Position);
            debug.Values.Value("point-pos", PointPosition);
        }

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
        using (debug.Scope("sun"))
        {
            debug.Values.Value("direction", SunDirection);
            debug.Values.Value("intensity", SunIntensity);
            debug.Values.Value("ambient", AmbientColor);
            debug.Values.Value("ambient-intensity", AmbientIntensity);
        }
        using (debug.Scope("camera"))
        {
            debug.Values.Value("position", cameraPosition);
            debug.Values.Value("forward", cameraForward);
            debug.Values.Value("yaw-rad", camYaw);
            debug.Values.Value("pitch-rad", camPitch);
            debug.Values.Value("fovY-rad", fovYRadians);
            debug.Values.Value("view", ViewLabels[viewMode]);
        }
        if (gpuInfo is { } info)
        {
            using (debug.Scope("gpu"))
            {
                debug.Values.Value("vendor", info.Vendor);
                debug.Values.Value("renderer", info.Renderer);
            }
        }

        // --- Debug draw overlays (toggle with backtick) -----------------
        debug.Draw.ViewProjection = viewProj;

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

    private static Matrix4x4 CreateOrthoVulkan(float width, float height, float near, float far)
    {
        var fn = far - near;
        return new Matrix4x4(
            2f / width, 0f,           0f,          0f,
            0f,        -2f / height,  0f,          0f,
            0f,         0f,          -1f / fn,     0f,
            0f,         0f,          -near / fn,   1f);
    }

    // Pack BonePalette.Matrices into byte[] for the GLSL std430 SSBO.
    // Direct row-major write (same convention as every other matrix upload
    // in the engine): GLSL reads column-major → sees the transpose →
    // `mat * v_col` is the row-vector operation `v_row * mat`. See F-008.
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

    private static TextureHandle LoadGltfAlbedo(VulkanGraphicsDevice vk, GltfMaterial? material, string fallbackName)
    {
        if (material?.BaseColorTexture is { MipBytes: { Count: > 0 } mipBytes } tex
            && tex.Format == TextureFormat.Rgba8)
        {
            // Promote to sRGB at sample time: BaseColor is sRGB-encoded per
            // glTF spec. The engine's SamplerDescription.LinearRepeat covers
            // the wrap+filter we want for character textures.
            return vk.CreateTexture2D(
                new TextureDescription(tex.Width, tex.Height, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
                mipBytes[0],
                $"cesium.albedo");
        }
        // Fallback: 4×4 neutral-white texture.
        var fallback = new byte[4 * 4 * 4];
        for (var i = 0; i < fallback.Length; i += 4)
        {
            fallback[i] = 230; fallback[i + 1] = 220; fallback[i + 2] = 200; fallback[i + 3] = 255;
        }
        return vk.CreateTexture2D(
            new TextureDescription(4, 4, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            fallback, fallbackName);
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
