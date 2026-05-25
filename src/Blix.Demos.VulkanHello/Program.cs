using System.Numerics;
using Blix;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanHello;

// Spinning lit cube on the Vulkan backend. First 3D content on this
// backend, exercising depth attachment + descriptor sets + per-frame UBO
// + name-keyed ShaderUniform writes routed through a UniformBlockLayout.
//
// Validation lens (see docs/vulkan-friction.md): keep the existing
// ShaderUniform("uModel", ...) API at the call site even though the
// backend has to do extra work to translate it. The point is to land
// observations about which abstractions creak before redesigning them.
public static class Program
{
    public static void Main()
    {
        var loop = new HelloLoop();
        using var window = new Window(loop, new WindowOptions("Blix — Vulkan Cube", 1280, 720));
        window.Run();
    }
}

internal sealed class HelloLoop : IGameLoop, IDebuggable
{
    public string DebugName => "vulkan-cube";

    private VertexBufferHandle vertexBuffer;
    private IndexBufferHandle indexBuffer;
    private ShaderProgramHandle shaderProgram;
    private PipelineHandle pipeline;
    private TextureHandle albedoTexture;
    private int frameCount;
    private Matrix4x4 viewProj;

    // State surfaced through IDebuggable — see Debug() below.
    private GraphicsDeviceInfo? gpuInfo;
    private Vector3 cameraPosition;
    private Vector3 cameraTarget;
    private float fovYRadians;
    private float currentRotY;
    private float currentRotX;
    private Matrix4x4 currentModel;
    private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(0.55f, 1.0f, 0.45f));

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        host.SetTitle("Blix — Vulkan Cube");
        var vk = (VulkanGraphicsDevice)graphicsDevice;
        gpuInfo = graphicsDevice.Info;

        var (vertices, indices) = BuildCube();
        var vertexData = VertexPosition3Texture.CreateBufferData(vertices);
        vertexBuffer = vk.CreateVertexBuffer(vertexData, "cube.vb");
        indexBuffer = vk.CreateIndexBuffer(indices, name: "cube.ib");

        // Procedural checkerboard albedo with red/green UV-corner markers so
        // texture orientation is visually verifiable: red marker = UV (0,0)
        // origin, green = UV (1,0). 256×256 RGBA8 sRGB.
        var checker = BuildUvAwareCheckerboard(size: 256, cellCount: 8);
        albedoTexture = vk.CreateTexture2D(
            new TextureDescription(256, 256, TextureFormat.Rgba8Srgb, SamplerDescription.LinearRepeat),
            checker,
            "cube.albedo");

        // Explicit UBO layout: the shader declares
        //   layout(set=0, binding=0) uniform Frame { mat4 uViewProjection; mat4 uModel; };
        // so members land at offset 0 and 64, total 128 bytes. The runtime
        // routes name-keyed ShaderUniform writes to these offsets.
        var uniformLayout = new UniformBlockLayout(
            TotalSize: 128,
            Members: new[]
            {
                new UniformBlockMember("uViewProjection", Offset: 0, Size: 64),
                new UniformBlockMember("uModel", Offset: 64, Size: 64),
            });

        // Declared binding contract: per-frame UBO at (set 0, binding 0) plus
        // an albedo sampler at (set 0, binding 1). 2c's single-set assumption:
        // ShaderTextureBinding.Slot in the draw call is matched against
        // DescriptorSetSlot.Binding to resolve the descriptor.
        var cubeInterface = new ShaderInterface(new[]
        {
            new DescriptorSetSlot(
                Set: 0, Binding: 0,
                Type: ShaderResourceType.UniformBuffer,
                Stages: ShaderStages.Vertex | ShaderStages.Fragment,
                BlockLayout: uniformLayout),
            new DescriptorSetSlot(
                Set: 0, Binding: 1,
                Type: ShaderResourceType.SampledImage,
                Stages: ShaderStages.Fragment),
        });

        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var vertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "cube.vert.spv"));
        var fragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "cube.frag.spv"));
        shaderProgram = vk.CreateShaderProgramFromSpv(vertSpv, fragSpv, cubeInterface, "cube");

        pipeline = vk.CreatePipeline(new PipelineDescription(
            shaderProgram,
            VertexPosition3Texture.Layout,
            PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite,
            RasterizerState.NoCulling,
            BlendState.Disabled), "cube");

        // View-projection is constant for now. Vulkan NDC: +Y down, depth [0,1].
        // System.Numerics' CreatePerspectiveFieldOfView returns a GL-style proj
        // matrix; we build a Vulkan-correct one directly. See F-010 in friction notes.
        var aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        cameraPosition = new Vector3(2.5f, 1.8f, 3.5f);
        cameraTarget = Vector3.Zero;
        fovYRadians = MathF.PI / 3f;
        var view = Matrix4x4.CreateLookAt(cameraPosition, cameraTarget, Vector3.UnitY);
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(fovYRadians, aspect, nearPlane: 0.1f, farPlane: 100f);
        viewProj = view * proj;
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;
        currentRotY = (float)time.Total * 0.8f;
        currentRotX = (float)time.Total * 0.3f;
        currentModel = Matrix4x4.CreateRotationY(currentRotY)
                     * Matrix4x4.CreateRotationX(currentRotX);
        var model = currentModel;

        commandList.Pass(
            "cube",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0.06f, 0.08f, 0.12f, 1.0f) },
                ClearDepth: true),
            pass =>
            {
                pass.DrawIndexed(
                    vertexBuffer: vertexBuffer,
                    indexBuffer: indexBuffer,
                    pipeline: pipeline,
                    indexCount: 36,
                    uniforms: new ShaderUniform[]
                    {
                        new("uViewProjection", new Matrix4x4Uniform(viewProj)),
                        new("uModel", new Matrix4x4Uniform(model)),
                    },
                    textures: new[]
                    {
                        new ShaderTextureBinding("uAlbedo", albedoTexture, Slot: 1),
                    });
            });
    }

    public void Debug(DebugContext debug)
    {
        debug.Values.Value("frame", frameCount);

        using (debug.Scope("light"))
        {
            debug.Values.Value("direction", LightDirection);
        }
        using (debug.Scope("cube"))
        {
            debug.Values.Value("rotY-rad", currentRotY);
            debug.Values.Value("rotX-rad", currentRotX);
            debug.Values.Value("vertices", 24);
            debug.Values.Value("triangles", 12);
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
                debug.Values.Value("version", info.Version);
            }
        }

        debug.Stats.Gauge("triangles", 12);
        debug.Stats.Gauge("vertices", 24);

        // === Visual debug overlays ===
        // ViewProjection is what the Vulkan-side line drawer uses to project
        // these world-space coordinates onto the swapchain. Must match the
        // camera's matrix or the overlay floats away from the cube.
        debug.Draw.ViewProjection = viewProj;

        // World axes at origin (X red, Y green, Z blue). One meter long.
        // Confirms which way each axis goes after the Vulkan +Y-down flip
        // (Y should point UP visually even though +Y is DOWN in clip space).
        const float axisLen = 1.0f;
        debug.Draw.Line("axis/x", Vector3.Zero, Vector3.UnitX * axisLen, new GraphicsColor(0.95f, 0.30f, 0.30f, 1f));
        debug.Draw.Line("axis/y", Vector3.Zero, Vector3.UnitY * axisLen, new GraphicsColor(0.30f, 0.95f, 0.40f, 1f));
        debug.Draw.Line("axis/z", Vector3.Zero, Vector3.UnitZ * axisLen, new GraphicsColor(0.40f, 0.55f, 0.95f, 1f));

        // Light direction arrow: tail at "where the sun is" (origin + lightDir
        // scaled out), head at cube origin. Visually represents "light travels
        // in this direction and lands on the cube." Soft yellow.
        var lightSource = LightDirection * 2.0f;
        debug.Draw.Arrow("light/direction", lightSource, Vector3.Zero, new GraphicsColor(1.0f, 0.95f, 0.45f, 1f));

        // Cube's world-space OBB tracking the actual rotation. Slightly inflated
        // (scale 0.51 instead of 0.50) so the wireframe doesn't z-fight or get
        // visually swallowed by the cube it outlines.
        var obb = Matrix4x4.CreateScale(0.51f) * currentModel;
        debug.Draw.Obb("cube/obb", obb, new GraphicsColor(1f, 1f, 1f, 0.85f));
    }

    private static (VertexPosition3Texture[] Vertices, ushort[] Indices) BuildCube()
    {
        // 24 vertices = 4 per face. Per-face UV mapping wraps the full
        // texture onto each face. UV (0,0) is top-left of the texture; the
        // vertex order matches the index buffer's triangulation:
        //   v0 = lower-left  → UV (0, 1)
        //   v1 = lower-right → UV (1, 1)
        //   v2 = upper-right → UV (1, 0)
        //   v3 = upper-left  → UV (0, 0)
        VertexPosition3Texture V(float x, float y, float z, float u, float v) =>
            new(new GraphicsVector3(x, y, z), new GraphicsVector2(u, v));

        var vertices = new VertexPosition3Texture[]
        {
            // -Z face
            V(-0.5f, -0.5f, -0.5f, 0, 1), V( 0.5f, -0.5f, -0.5f, 1, 1),
            V( 0.5f,  0.5f, -0.5f, 1, 0), V(-0.5f,  0.5f, -0.5f, 0, 0),
            // +Z face
            V(-0.5f, -0.5f,  0.5f, 0, 1), V( 0.5f, -0.5f,  0.5f, 1, 1),
            V( 0.5f,  0.5f,  0.5f, 1, 0), V(-0.5f,  0.5f,  0.5f, 0, 0),
            // -X face
            V(-0.5f, -0.5f, -0.5f, 0, 1), V(-0.5f, -0.5f,  0.5f, 1, 1),
            V(-0.5f,  0.5f,  0.5f, 1, 0), V(-0.5f,  0.5f, -0.5f, 0, 0),
            // +X face
            V( 0.5f, -0.5f, -0.5f, 0, 1), V( 0.5f,  0.5f, -0.5f, 0, 0),
            V( 0.5f,  0.5f,  0.5f, 1, 0), V( 0.5f, -0.5f,  0.5f, 1, 1),
            // -Y face
            V(-0.5f, -0.5f, -0.5f, 0, 1), V( 0.5f, -0.5f, -0.5f, 1, 1),
            V( 0.5f, -0.5f,  0.5f, 1, 0), V(-0.5f, -0.5f,  0.5f, 0, 0),
            // +Y face
            V(-0.5f,  0.5f, -0.5f, 0, 1), V(-0.5f,  0.5f,  0.5f, 0, 0),
            V( 0.5f,  0.5f,  0.5f, 1, 0), V( 0.5f,  0.5f, -0.5f, 1, 1),
        };
        var indices = new ushort[]
        {
            0,  1,  2,   0,  2,  3,    // -Z
            4,  5,  6,   4,  6,  7,    // +Z
            8,  9, 10,   8, 10, 11,    // -X
            12, 13, 14,  12, 14, 15,   // +X
            16, 17, 18,  16, 18, 19,   // -Y
            20, 21, 22,  20, 22, 23,   // +Y
        };
        return (vertices, indices);
    }

    // Procedural texture: greyscale checkerboard with two coloured corner
    // markers so UV orientation is visible. Red at the texture's (0,0)
    // pixel (UV origin), green at the (size-1, 0) pixel (UV (1,0) end).
    // Row order is top-down — pixel (x, y) where y=0 is the top row, so the
    // red marker lands in the first scanline written.
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
            data[idx]     = c;
            data[idx + 1] = c;
            data[idx + 2] = c;
            data[idx + 3] = 255;
            // UV (0,0) corner marker — red
            if (x < markerSize && y < markerSize)
            {
                data[idx] = 220; data[idx + 1] = 50; data[idx + 2] = 50;
            }
            // UV (1,0) corner marker — green
            else if (x >= size - markerSize && y < markerSize)
            {
                data[idx] = 50; data[idx + 1] = 220; data[idx + 2] = 50;
            }
        }
        return data;
    }

}
