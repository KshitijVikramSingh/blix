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
        var vertexData = VertexPosition3Color.CreateBufferData(vertices);
        vertexBuffer = vk.CreateVertexBuffer(vertexData, "cube.vb");
        indexBuffer = vk.CreateIndexBuffer(indices, name: "cube.ib");

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

        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var vertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "cube.vert.spv"));
        var fragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "cube.frag.spv"));
        shaderProgram = vk.CreateShaderProgramFromSpv(vertSpv, fragSpv, uniformLayout, "cube");

        pipeline = vk.CreatePipeline(new PipelineDescription(
            shaderProgram,
            VertexPosition3Color.Layout,
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
        var proj = VulkanPerspective(fovY: fovYRadians, aspect: aspect, near: 0.1f, far: 100f);
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
                    textures: Array.Empty<ShaderTextureBinding>());
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

    private static (VertexPosition3Color[] Vertices, ushort[] Indices) BuildCube()
    {
        // 24 vertices = 4 per face. Per-face colours interpolate trivially
        // (all four corners share the colour), avoiding the diagonal-seam
        // artifact you get with an 8-vertex cube where each vertex carries
        // a different colour and the two triangles per face share only
        // 3 of the 4 corner colours each.
        var red     = new GraphicsColor(0.95f, 0.30f, 0.30f, 1f); // -Z
        var cyan    = new GraphicsColor(0.30f, 0.85f, 0.95f, 1f); // +Z
        var yellow  = new GraphicsColor(0.95f, 0.85f, 0.25f, 1f); // -X
        var magenta = new GraphicsColor(0.95f, 0.30f, 0.85f, 1f); // +X
        var orange  = new GraphicsColor(0.95f, 0.55f, 0.20f, 1f); // -Y
        var green   = new GraphicsColor(0.30f, 0.80f, 0.40f, 1f); // +Y

        VertexPosition3Color V(float x, float y, float z, GraphicsColor c) =>
            new(new GraphicsVector3(x, y, z), c);

        var vertices = new VertexPosition3Color[]
        {
            // -Z face (red)
            V(-0.5f, -0.5f, -0.5f, red), V( 0.5f, -0.5f, -0.5f, red),
            V( 0.5f,  0.5f, -0.5f, red), V(-0.5f,  0.5f, -0.5f, red),
            // +Z face (cyan)
            V(-0.5f, -0.5f,  0.5f, cyan), V( 0.5f, -0.5f,  0.5f, cyan),
            V( 0.5f,  0.5f,  0.5f, cyan), V(-0.5f,  0.5f,  0.5f, cyan),
            // -X face (yellow)
            V(-0.5f, -0.5f, -0.5f, yellow), V(-0.5f, -0.5f,  0.5f, yellow),
            V(-0.5f,  0.5f,  0.5f, yellow), V(-0.5f,  0.5f, -0.5f, yellow),
            // +X face (magenta)
            V( 0.5f, -0.5f, -0.5f, magenta), V( 0.5f,  0.5f, -0.5f, magenta),
            V( 0.5f,  0.5f,  0.5f, magenta), V( 0.5f, -0.5f,  0.5f, magenta),
            // -Y face (orange)
            V(-0.5f, -0.5f, -0.5f, orange), V( 0.5f, -0.5f, -0.5f, orange),
            V( 0.5f, -0.5f,  0.5f, orange), V(-0.5f, -0.5f,  0.5f, orange),
            // +Y face (green)
            V(-0.5f,  0.5f, -0.5f, green), V(-0.5f,  0.5f,  0.5f, green),
            V( 0.5f,  0.5f,  0.5f, green), V( 0.5f,  0.5f, -0.5f, green),
        };
        // Each face's 4 vertices are laid out so (a,b,c,d) triangulates as
        // (a,b,c) + (a,c,d) without diagonal-color discontinuity.
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

    // Right-handed view space → Vulkan clip space: +Y down, depth in [0, 1].
    // Built directly in row-vector (System.Numerics) form, so when the
    // backend transposes on UBO write the GLSL side sees the standard
    // column-vector Vulkan perspective.
    private static Matrix4x4 VulkanPerspective(float fovY, float aspect, float near, float far)
    {
        var f = 1.0f / MathF.Tan(fovY * 0.5f);
        var m = new Matrix4x4
        {
            M11 = f / aspect,
            M22 = -f,
            M33 = far / (near - far),
            M34 = -1,
            M43 = (near * far) / (near - far),
            M44 = 0,
        };
        return m;
    }
}
