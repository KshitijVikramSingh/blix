using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Core;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Primitives;
using Blix.Graphics.Vulkan;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Demos.VulkanInstanced;

// Instancing proof gate: 5000 instanced cubes drawn in ONE
// vkCmdDrawIndexed(instanceCount=5000), each at a distinct transform + tint
// pulled from the per-instance SSBO (set 3) via gl_InstanceIndex. Auto-exits
// after a few frames (> frames-in-flight) so both replicated SSBO slots are
// written and bound at least once — that's what would surface an in-flight
// hazard. Correctness is asserted by running under BLIX_VK_VALIDATE=1 and
// grepping for [vk-ERR]/[vk-WARN] (screenshots don't work on this setup).
//
// ── Executable spec for (engine primitives this demo proves) ──
//   • The instancing foundation: one vkCmdDrawIndexed(instanceCount=N) reading a
//     per-instance transform/tint from a set-3 SSBO via gl_InstanceIndex
//   • In-flight SSBO replication correctness (both slots written + bound) — the gate
// ── Intentionally owns (stays local) ──
//   • the 5000-cube grid scene + the --frames auto-exit validation harness
public static class Program
{
    public static void Main(string[] args)
    {
        // Default: interactive — the window stays open until Esc/close so the
        // 5000-cube field is actually visible. `--frames N` auto-exits after N
        // frames (the headless validation gate uses --frames 8, which is > the
        // frames-in-flight count so both replicated SSBO slots get exercised).
        var exitAfterFrames = 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--frames" && int.TryParse(args[i + 1], out var n)) exitAfterFrames = n;
        }

        var loop = new InstancedLoop(exitAfterFrames);
        using var window = new Window(loop, new WindowOptions("Blix — Instancing Proof (5000 cubes)", 1280, 720));
        window.Run();
    }
}

internal sealed class InstancedLoop : IGameLoop, IInputHandler
{
    private const int InstanceCount = 5000;
    private const int GridX = 100;            // 100 columns × 50 rows = 5000
    private const int GridZ = InstanceCount / GridX;
    private const float Spacing = 1.5f;

    private readonly int exitAfterFrames; // 0 = stay open until Esc/close
    private VulkanGraphicsDevice vk = null!;
    private IRenderHost host = null!;
    private InstanceBuffer instanceBuffer = null!;
    private InstancedBatch batch = null!;
    private readonly byte[] pushBytes = new byte[64];   // mat4 view-projection
    private readonly InstanceData[] instances = new InstanceData[InstanceCount];

    private Matrix4x4 viewProj;
    private float aspect = 16f / 9f;
    private int frameCount;

    public InstancedLoop(int exitAfterFrames) => this.exitAfterFrames = exitAfterFrames;

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        vk = (VulkanGraphicsDevice)graphicsDevice;

        var vb = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(Cube.Vertices), "cube.vb");
        var ib = vk.CreateIndexBuffer(Cube.Indices, name: "cube.ib");
        var cube = new Mesh("cube", vb, ib, Cube.Indices.Length,
            new Bounds3(new Vector3(-0.5f), new Vector3(0.5f)));

        // Pipeline consumes only position + normal (stride matched to the cube's
        // VertexPosition3NormalTexture so it reads correctly without an
        // unconsumed-uv validation warning).
        var meshLayout = new VertexLayout(
            Stride: VertexPosition3NormalTexture.Layout.Stride,
            Attributes: new[]
            {
                new VertexAttribute(0, VertexAttributeFormat.Float3, 0),
                new VertexAttribute(1, VertexAttributeFormat.Float3, 3 * sizeof(float)),
            });
        var iface = new ShaderInterface(
            Slots: new[] { InstanceBuffer.Slot },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var shader = vk.CreateShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "cube.vert.spv")),
            File.ReadAllBytes(Path.Combine(shaderDir, "cube.frag.spv")),
            iface, "cube");
        var pipeline = vk.CreatePipeline(
            new PipelineDescription(shader, meshLayout, PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite, RasterizerState.BackFaceCulling, new[] { BlendState.Disabled }),
            "cube");
        instanceBuffer = new InstanceBuffer(vk, shader, "cubes");
        batch = new InstancedBatch(cube, pipeline, instanceBuffer);

        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        BuildFrame(0f); // seed so frame 0 is valid even if OnUpdate hasn't run
    }

    public void OnResize(int width, int height)
    {
        if (height > 0) aspect = width / (float)height;
    }

    public void OnUpdate(Time time) => BuildFrame((float)time.Total);

    // Camera orbiting above the field + distinct per-instance transforms (grid
    // placement, per-cube spin, Y bob) and rainbow tints — proves each instance
    // reads its own SSBO row, not a shared transform.
    private void BuildFrame(float t)
    {
        var camX = MathF.Sin(t * 0.15f) * 30f;
        var camZ = 70f + MathF.Cos(t * 0.15f) * 10f;
        var eye = new Vector3(camX, 45f, camZ);
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3f, aspect, 0.5f, 400f);
        viewProj = view * proj;

        for (var i = 0; i < InstanceCount; i++)
        {
            var gx = i % GridX;
            var gz = i / GridX;
            var px = (gx - GridX / 2) * Spacing;
            var pz = (gz - GridZ / 2) * Spacing;
            var phase = i * 0.137f;
            var py = MathF.Sin(t * 1.5f + phase) * 1.2f;
            var model =
                Matrix4x4.CreateScale(0.6f) *
                Matrix4x4.CreateRotationY(t * 0.7f + phase) *
                Matrix4x4.CreateRotationX(t * 0.4f + phase * 0.5f) *
                Matrix4x4.CreateTranslation(new Vector3(px, py, pz));
            var tint = new Vector4(
                0.5f + 0.5f * MathF.Sin(phase),
                0.5f + 0.5f * MathF.Sin(phase + 2.094f),
                0.5f + 0.5f * MathF.Sin(phase + 4.188f),
                1f);
            instances[i] = new InstanceData(model, tint);
        }
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;

        MemoryMarshal.Write(pushBytes.AsSpan(0, 64), in viewProj);
        batch.Begin(pushBytes);
        batch.SetInstances(instances);
        commandList.Pass(
            "instanced-cubes",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0.04f, 0.05f, 0.08f, 1f) },
                ClearDepth: true),
            pass => batch.End(pass));

        if (exitAfterFrames > 0 && frameCount >= exitAfterFrames)
        {
            host.RequestClose();
        }
    }

    // No OnUnload disposal: RequestClose() fires the window's Closing before the
    // current frame's already-recorded draw executes, so disposing the batch here
    // would free its pipeline out from under that final Execute. Sibling demos
    // (VulkanHello/VulkanLit) rely on device.Dispose() — which vkDeviceWaitIdle's
    // and frees every resource table — to clean up at process teardown.

    public void OnKeyDown(Key key)
    {
        if (key == Key.Escape) host.RequestClose();
    }
}
