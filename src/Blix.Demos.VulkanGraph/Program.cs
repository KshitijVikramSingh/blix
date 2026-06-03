using System.Numerics;
using Blix;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanGraph;

// Vector B step 5 (VB.vii) — render-graph validation demo.
//
// Three passes:
//   1. "scene"   (graph)      cube → graph-owned hdrOffscreen (Rgba16F)
//   2. "invert"  (graph)      fullscreen quad samples hdr → invertedOffscreen
//   3. "present" (imperative) fullscreen quad samples inverted → swapchain
//
// Pass 2 reads pass 1's output via a graph Read edge. If the edge is
// wired correctly, the cube appears INVERTED on the swapchain. If the
// edge is broken (e.g. layout transition missing), the present pass
// shows garbage.
//
// Pass 3 is imperative (commandList.Pass) because the swapchain isn't
// a graph-owned resource. The graph manages the offscreen passes; the
// demo bridges to swapchain via the existing routing.
//
// Visual proof of correctness: cube renders with checkerboard texture +
// per-face lighting + dark blue corner markers (red INVERTED → cyan;
// green INVERTED → magenta).
//
// ── Executable spec for (engine primitives this demo proves) ──
//   • RenderGraph topology + Read-edge correctness (graph-owned offscreen passes,
//     auto layout transitions between producer and consumer)
//   • The imperative-bridge seam: graph offscreen passes → swapchain present
// ── Intentionally owns (stays local) ──
//   • the 3-pass invert scene as a visual correctness check
public static class Program
{
    public static void Main()
    {
        var loop = new GraphLoop();
        using var window = new Window(loop, new WindowOptions("Blix — Vulkan RenderGraph", 1280, 720));
        window.Run();
    }
}

internal sealed class GraphLoop : IGameLoop, IDebuggable, IDisposable
{
    public string DebugName => "vulkan-graph";

    // Cube resources.
    private VertexBufferHandle cubeVB;
    private IndexBufferHandle cubeIB;
    private TextureHandle albedoTexture;
    private MaterialHandle cubeMaterial;
    private ShaderProgramHandle cubeShaderProgram;
    private PipelineHandle cubePipeline;

    // Graph + per-pass resources.
    private RenderGraph graph = null!;
    private GraphResourceHandle hdrHandle;
    private GraphResourceHandle invertedHandle;
    private PassHandle scenePassHandle;
    private PassHandle invertPassHandle;

    // Invert pass: shader + pipeline. Uses fullscreen-quad vertex inputs
    // that the shader ignores (gl_VertexIndex generates positions).
    private ShaderProgramHandle invertShaderProgram;
    private PipelineHandle invertPipeline;

    // Present pass (imperative): shader + pipeline + dummy vertex/index
    // buffers (shared with invert).
    private ShaderProgramHandle presentShaderProgram;
    private PipelineHandle presentPipeline;
    private FullscreenPass fullscreen = null!;

    // Per-frame state.
    private readonly byte[] modelPushBytes = new byte[64];
    private int frameCount;
    private Matrix4x4 viewProj;
    private Matrix4x4 currentModel;
    private float currentRotY;
    private float currentRotX;

    // Debug state.
    private GraphicsDeviceInfo? gpuInfo;
    private Vector3 cameraPosition;
    private Vector3 cameraTarget;
    private float fovYRadians;
    private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(0.55f, 1.0f, 0.45f));

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        host.SetTitle("Blix — Vulkan RenderGraph");
        var vk = (VulkanGraphicsDevice)graphicsDevice;
        gpuInfo = graphicsDevice.Info;

        // --- Cube geometry + texture ---------------------------------
        var (vertices, indices) = BuildCube();
        cubeVB = vk.CreateVertexBuffer(VertexPosition3Texture.CreateBufferData(vertices), "cube.vb");
        cubeIB = vk.CreateIndexBuffer(indices, name: "cube.ib");

        var checker = BuildUvAwareCheckerboard(256, 8);
        albedoTexture = vk.CreateTexture2D(
            new TextureDescription(256, 256, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            checker,
            "cube.albedo");

        // --- Render graph ---------------------------------------------
        // Two graph-owned offscreen color targets at half swapchain res.
        // Pass 1 writes hdr; pass 2 reads hdr and writes inverted; the
        // imperative present pass samples inverted into the swapchain.
        graph = new RenderGraph(vk);

        var halfSize = new MatchSwapchainGraphSize(0.5f);
        hdrHandle = graph.ColorTarget("hdr", TextureFormat.Rgba16F, halfSize);
        invertedHandle = graph.ColorTarget("inverted", TextureFormat.Rgba16F, halfSize);
        var sceneDepth = graph.DepthTarget("scene-depth", halfSize);

        // Cube shader interface — same shape as VulkanHello (set 0 frame
        // UBO + set 2 material UBO + sampler + push-constant uModel).
        var frameUbo = new UniformBlockLayout(
            TotalSize: 64,
            Members: new[] { new UniformBlockMember("uViewProjection", 0, 64) });
        var tintUbo = new UniformBlockLayout(
            TotalSize: 16,
            Members: new[] { new UniformBlockMember("uTint", 0, 16) });
        var cubeInterface = new ShaderInterface(
            Slots: new[]
            {
                new DescriptorSetSlot(
                    Set: 0, Binding: 0, Type: ShaderResourceType.UniformBuffer,
                    Stages: ShaderStages.Vertex | ShaderStages.Fragment, BlockLayout: frameUbo),
                new DescriptorSetSlot(
                    Set: 2, Binding: 0, Type: ShaderResourceType.UniformBuffer,
                    Stages: ShaderStages.Fragment, BlockLayout: tintUbo),
                new DescriptorSetSlot(
                    Set: 2, Binding: 1, Type: ShaderResourceType.SampledImage,
                    Stages: ShaderStages.Fragment),
            },
            PushConstants: new[]
            {
                new PushConstantRange(ShaderStages.Vertex, Offset: 0, Size: 64),
            });

        // Invert + present share the same interface: one SampledImage at
        // (set 0, binding 0).
        var samplerOnlyInterface = new ShaderInterface(new[]
        {
            new DescriptorSetSlot(
                Set: 0, Binding: 0, Type: ShaderResourceType.SampledImage,
                Stages: ShaderStages.Fragment),
        });

        // Declare graph passes. Scene targets hdr + depth. Invert reads hdr
        // and writes inverted.
        scenePassHandle = graph.GraphicsPass("scene")
            .Target(hdrHandle, LoadOp.Clear, StoreOp.Store)
            .Depth(sceneDepth, LoadOp.Clear, StoreOp.Store)
            .Shader(cubeInterface)
            .Handle;
        invertPassHandle = graph.GraphicsPass("invert")
            .Target(invertedHandle, LoadOp.Clear, StoreOp.Store)
            .Read(hdrHandle)
            .Shader(samplerOnlyInterface)
            .Handle;
        graph.Compile();

        // --- Pipelines (created AFTER Compile so render passes exist) -
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var cubeVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "cube.vert.spv"));
        var cubeFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "cube.frag.spv"));
        cubeShaderProgram = vk.CreateShaderProgramFromSpv(cubeVertSpv, cubeFragSpv, cubeInterface, "cube");
        cubePipeline = vk.CreatePipeline(new PipelineDescription(
            cubeShaderProgram,
            VertexPosition3Texture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(scenePassHandle)), "cube");

        var invertVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "invert.vert.spv"));
        var invertFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "invert.frag.spv"));
        invertShaderProgram = vk.CreateShaderProgramFromSpv(invertVertSpv, invertFragSpv, samplerOnlyInterface, "invert");
        invertPipeline = vk.CreatePipeline(new PipelineDescription(
            invertShaderProgram,
            VertexPosition3Texture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.Disabled,
            RasterizerState.NoCulling,
            new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(invertPassHandle)), "invert");

        var presentVertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.vert.spv"));
        var presentFragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "present.frag.spv"));
        presentShaderProgram = vk.CreateShaderProgramFromSpv(presentVertSpv, presentFragSpv, samplerOnlyInterface, "present");
        presentPipeline = vk.CreatePipeline(new PipelineDescription(
            presentShaderProgram,
            VertexPosition3Texture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.Disabled,
            RasterizerState.NoCulling,
            BlendState.Disabled), "present");

        // --- Cube material (set 2) ------------------------------------
        cubeMaterial = vk.CreateMaterial(cubeShaderProgram, name: "cube.material")
            .SetUniform(binding: 0, "uTint", new Vector4(1.0f, 1.0f, 1.0f, 1.0f))
            .SetTexture(binding: 1, albedoTexture)
            .Handle;

        // Fullscreen triangle shared by the invert + present passes.
        fullscreen = new FullscreenPass(vk, "fullscreen");

        // --- Camera ---------------------------------------------------
        var aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        cameraPosition = new Vector3(2.5f, 1.8f, 3.5f);
        cameraTarget = Vector3.Zero;
        fovYRadians = MathF.PI / 3f;
        var view = Matrix4x4.CreateLookAt(cameraPosition, cameraTarget, Vector3.UnitY);
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(fovYRadians, aspect, 0.1f, 100f);
        viewProj = view * proj;
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;
        currentRotY = (float)time.Total * 0.8f;
        currentRotX = (float)time.Total * 0.3f;
        currentModel = Matrix4x4.CreateRotationY(currentRotY)
                     * Matrix4x4.CreateRotationX(currentRotX);
        System.Runtime.InteropServices.MemoryMarshal.Write(modelPushBytes, in currentModel);

        var perFrame = new ShaderUniform[]
        {
            new("uViewProjection", new Matrix4x4Uniform(viewProj)),
        };

        // Pass 1 (graph): cube → hdr.
        graph.Pass(scenePassHandle, scope =>
        {
            scope.DrawIndexed(
                vertexBuffer: cubeVB,
                indexBuffer: cubeIB,
                pipeline: cubePipeline,
                indexCount: 36,
                uniforms: perFrame,
                textures: Array.Empty<ShaderTextureBinding>(),
                material: cubeMaterial,
                pushConstants: modelPushBytes);
        }, clearColor: new GraphicsColor(0.06f, 0.08f, 0.12f, 1.0f));

        // Pass 2 (graph): fullscreen invert → inverted. Reads hdr via the
        // graph Read edge declared at compile time.
        var hdrTexture = graph.GetColorTexture(hdrHandle);
        graph.Pass(invertPassHandle, scope =>
            fullscreen.Draw(scope, invertPipeline,
                new[] { new ShaderTextureBinding("uHdr", hdrTexture, Slot: 0) }));

        // Append graph passes to the command list.
        graph.Execute(commandList);

        // Pass 3 (imperative): present quad samples inverted → swapchain.
        var invertedTexture = graph.GetColorTexture(invertedHandle);
        commandList.Pass(
            "present",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0, 0, 0, 1) },
                ClearDepth: true),
            pass => fullscreen.Draw(pass, presentPipeline,
                new[] { new ShaderTextureBinding("uInverted", invertedTexture, Slot: 0) }));
    }

    public void Debug(DebugContext debug)
    {
        debug.Values.Value("frame", frameCount);
        using (debug.Scope("cube"))
        {
            debug.Values.Value("rotY-rad", currentRotY);
            debug.Values.Value("rotX-rad", currentRotX);
        }
        using (debug.Scope("graph"))
        {
            debug.Values.Value("passes", "scene → invert → present");
            debug.Values.Value("hdr-handle", hdrHandle.Id);
            debug.Values.Value("inverted-handle", invertedHandle.Id);
        }
        using (debug.Scope("camera"))
        {
            debug.Values.Value("position", cameraPosition);
            debug.Values.Value("target", cameraTarget);
            debug.Values.Value("fovY-rad", fovYRadians);
        }
        if (gpuInfo is { } info)
        {
            using (debug.Scope("gpu"))
            {
                debug.Values.Value("vendor", info.Vendor);
                debug.Values.Value("renderer", info.Renderer);
            }
        }

        debug.Draw.ViewProjection = viewProj;
        var obb = Matrix4x4.CreateScale(0.51f) * currentModel;
        debug.Draw.Obb("cube/obb", obb, new GraphicsColor(1f, 1f, 1f, 0.85f));
    }

    public void Dispose()
    {
        graph?.Dispose();
        fullscreen?.Dispose();
    }

    private static (VertexPosition3Texture[] Vertices, ushort[] Indices) BuildCube()
    {
        VertexPosition3Texture V(float x, float y, float z, float u, float v) =>
            new(new GraphicsVector3(x, y, z), new GraphicsVector2(u, v));
        var vertices = new VertexPosition3Texture[]
        {
            V(-0.5f, -0.5f, -0.5f, 0, 1), V( 0.5f, -0.5f, -0.5f, 1, 1),
            V( 0.5f,  0.5f, -0.5f, 1, 0), V(-0.5f,  0.5f, -0.5f, 0, 0),
            V(-0.5f, -0.5f,  0.5f, 0, 1), V( 0.5f, -0.5f,  0.5f, 1, 1),
            V( 0.5f,  0.5f,  0.5f, 1, 0), V(-0.5f,  0.5f,  0.5f, 0, 0),
            V(-0.5f, -0.5f, -0.5f, 0, 1), V(-0.5f, -0.5f,  0.5f, 1, 1),
            V(-0.5f,  0.5f,  0.5f, 1, 0), V(-0.5f,  0.5f, -0.5f, 0, 0),
            V( 0.5f, -0.5f, -0.5f, 0, 1), V( 0.5f,  0.5f, -0.5f, 0, 0),
            V( 0.5f,  0.5f,  0.5f, 1, 0), V( 0.5f, -0.5f,  0.5f, 1, 1),
            V(-0.5f, -0.5f, -0.5f, 0, 1), V( 0.5f, -0.5f, -0.5f, 1, 1),
            V( 0.5f, -0.5f,  0.5f, 1, 0), V(-0.5f, -0.5f,  0.5f, 0, 0),
            V(-0.5f,  0.5f, -0.5f, 0, 1), V(-0.5f,  0.5f,  0.5f, 0, 0),
            V( 0.5f,  0.5f,  0.5f, 1, 0), V( 0.5f,  0.5f, -0.5f, 1, 1),
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

    private static byte[] BuildUvAwareCheckerboard(int size, int cellCount)
    {
        var data = new byte[size * size * 4];
        var cellSize = size / cellCount;
        var markerSize = size / 16;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dark = ((x / cellSize) + (y / cellSize)) % 2 == 0;
            var idx = (y * size + x) * 4;
            var c = dark ? (byte)40 : (byte)220;
            data[idx] = c; data[idx + 1] = c; data[idx + 2] = c; data[idx + 3] = 255;
            if (x < markerSize && y < markerSize)
            {
                data[idx] = 220; data[idx + 1] = 50; data[idx + 2] = 50;
            }
            else if (x >= size - markerSize && y < markerSize)
            {
                data[idx] = 50; data[idx + 1] = 220; data[idx + 2] = 50;
            }
        }
        return data;
    }
}
