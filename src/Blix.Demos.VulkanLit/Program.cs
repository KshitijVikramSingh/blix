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
    private PassHandle shadowPassHandle;
    private PassHandle litPassHandle;

    // Present.
    private VertexBufferHandle fullscreenVB;
    private IndexBufferHandle fullscreenIB;

    // Per-frame state.
    private readonly byte[] cubeModelPushBytes = new byte[64];
    private readonly byte[] groundModelPushBytes = new byte[64];
    private int frameCount;
    private Matrix4x4 viewProj;
    private Matrix4x4 sunShadowVP;
    private Matrix4x4 cubeModel;
    private Matrix4x4 groundModel;
    private Matrix4x4 cesiumWorldModel;
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
    private enum View { Final = 0, ShadowMap = 1, SceneDepth = 2 }
    private static readonly string[] ViewLabels = { "Final (HDR)", "Sun shadow map", "Scene depth" };
    private int viewMode;          // index into View
    private bool animPaused;
    private GraphResourceHandle sceneDepthHandle;

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
        hdrHandle = graph.ColorTarget("hdr", TextureFormat.Rgba16F, fullSize);
        sceneDepthHandle = graph.DepthTarget("scene-depth", fullSize);

        // --- Shader interfaces ------------------------------------------
        var frameUbo = new UniformBlockLayout(
            TotalSize: 160,
            Members: new[]
            {
                new UniformBlockMember("uViewProjection",   0,  64),
                new UniformBlockMember("uSunDirection",     64, 12),
                new UniformBlockMember("uSunIntensity",     76, 4),
                new UniformBlockMember("uAmbientColor",     80, 12),
                new UniformBlockMember("uAmbientIntensity", 92, 4),
                new UniformBlockMember("uSunShadowVP",      96, 64),
            });
        var tintUbo = new UniformBlockLayout(
            TotalSize: 16,
            Members: new[] { new UniformBlockMember("uTint", 0, 16) });

        // Static lit interface (no SSBO).
        var shadowInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer,
                    ShaderStages.Vertex, BlockLayout: frameUbo),
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });

        var litInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer,
                    ShaderStages.Vertex | ShaderStages.Fragment, BlockLayout: frameUbo),
                new DescriptorSetSlot(1, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
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
                new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer,
                    ShaderStages.Vertex, BlockLayout: frameUbo),
                new DescriptorSetSlot(3, 0, ShaderResourceType.StorageBuffer,
                    ShaderStages.Vertex, BlockLayout: boneSsboLayout),
            },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });

        var skinnedLitInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(0, 0, ShaderResourceType.UniformBuffer,
                    ShaderStages.Vertex | ShaderStages.Fragment, BlockLayout: frameUbo),
                new DescriptorSetSlot(1, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
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
        // Both shadowInterface AND skinnedShadowInterface get declared on
        // the sun-shadow pass — the pass needs to host BOTH static and
        // skinned shadow casters, and the descriptor pool gets sized for
        // whichever set 0/1 slots are union'd across declared shaders.
        shadowPassHandle = graph.GraphicsPass("sun-shadow")
            .Depth(sunShadowHandle, LoadOp.Clear, StoreOp.Store)
            .Shader(shadowInterface, skinnedShadowInterface)
            .Handle;
        litPassHandle = graph.GraphicsPass("lit-scene")
            .Target(hdrHandle, LoadOp.Clear, StoreOp.Store)
            .Depth(sceneDepthHandle, LoadOp.Clear, StoreOp.Store)
            .Read(sunShadowHandle)
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

        var perFrame = new ShaderUniform[]
        {
            new("uViewProjection",   new Matrix4x4Uniform(viewProj)),
            new("uSunDirection",     new Vector3Uniform(SunDirection)),
            new("uSunIntensity",     new FloatUniform(SunIntensity)),
            new("uAmbientColor",     new Vector3Uniform(AmbientColor)),
            new("uAmbientIntensity", new FloatUniform(AmbientIntensity)),
            new("uSunShadowVP",      new Matrix4x4Uniform(sunShadowVP)),
        };

        // Shadow pass: cube (static) + cesium (skinned).
        graph.Pass(shadowPassHandle, scope =>
        {
            scope.DrawIndexed(
                vertexBuffer: cubeVB,
                indexBuffer: cubeIB,
                pipeline: shadowPipeline,
                indexCount: 36,
                uniforms: perFrame,
                textures: Array.Empty<ShaderTextureBinding>(),
                pushConstants: cubeModelPushBytes);
            scope.DrawIndexedSkinnedShadow(
                vertexBuffer: cesiumVB,
                indexBuffer: cesiumIB,
                pipeline: skinnedShadowPipeline,
                indexCount: cesiumIndexCount,
                uniforms: perFrame,
                textures: Array.Empty<ShaderTextureBinding>(),
                perDrawMaterial: cesiumBoneMaterial,
                pushConstants: cesiumModelPushBytes);
        });

        // Lit pass: ground + cube + cesium.
        var sunShadowTex = graph.GetDepthTexture(sunShadowHandle);
        var shadowBinding = new ShaderTextureBinding("uSunShadowMap", sunShadowTex, Slot: 0);
        graph.Pass(litPassHandle, scope =>
        {
            scope.DrawIndexed(
                vertexBuffer: groundVB,
                indexBuffer: groundIB,
                pipeline: litPipeline,
                indexCount: 6,
                uniforms: perFrame,
                textures: new[] { shadowBinding },
                material: groundMaterial,
                pushConstants: groundModelPushBytes);
            scope.DrawIndexed(
                vertexBuffer: cubeVB,
                indexBuffer: cubeIB,
                pipeline: litPipeline,
                indexCount: 36,
                uniforms: perFrame,
                textures: new[] { shadowBinding },
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
                textures: new[] { shadowBinding },
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
            View.ShadowMap => (graph.GetDepthTexture(sunShadowHandle), presentDepthPipeline),
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

        // Sun direction arrow at the scene origin (points the way light travels).
        debug.Draw.Arrow("sun/dir", Vector3.Zero, SunDirection * 2.0f,
            new GraphicsColor(1f, 0.6f, 0.1f, 1f));

        // Ground reference grid.
        debug.Draw.Grid("ground/grid", new Vector3(0, -0.6f, 0), 6f, 12,
            new GraphicsColor(0.4f, 0.45f, 0.55f, 0.5f));
    }

    public void Dispose()
    {
        graph?.Dispose();
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
