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
using Plane = Blix.Geometry.Plane;

namespace Blix.Demos.Bulwark;

// Bulwark — tower-defense game #2 (see plan-bulwark.md). This is M0 Gate A:
// Pick & Place. It proves the engine's first-ever game use of pointer-driven world
// interaction — the wholly-new axis Tank Arena / Runner / Pong never touched.
//
// The proof: an orbiting RTS camera over a flat grid; the mouse cursor casts a ray
// (Camera3D.ScreenPointToRay) onto the ground plane (Intersection.Raycast); the hit
// point maps to a grid cell; a ghost cube snaps to that cell (green = placeable,
// red = occupied); left-click places a tower, right-click removes it. Because the
// SAME Camera3D feeds both ScreenPointToRay and the render view-projection, picking
// stays locked to what's on screen as the camera orbits/zooms — that's the
// invariant the gate exists to demonstrate.
//
// Everything rendered goes through one InstancedBatch (grid tiles + towers + ghost),
// lifted from VulkanInstanced. The sun-shadow + HDR graph from Tank Arena lands later
// (M3 polish) — a proof gate proves the NEW thing minimally.
//
// ── Executable spec for (engine primitives this gate proves) ──
//   • Picking: Camera3D.ScreenPointToRay → Intersection.Raycast(ray, ground plane)
//     → grid cell, consistent with the render view-projection across camera motion
//   • Orbit/zoom RTS camera driving a Camera3D
//   • Build UI: hover ghost + place/remove on a square grid
// ── Intentionally owns (stays local) ──
//   • the grid model, placement rules, camera control feel
public static class Program
{
    public static void Main(string[] args)
    {
        var exitAfterFrames = 0;   // 0 = interactive; --frames N for the headless smoke
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--frames" && int.TryParse(args[i + 1], out var n)) exitAfterFrames = n;
        }

        var loop = new BulwarkLoop(exitAfterFrames);
        using var window = new Window(loop, new WindowOptions("Blix — Bulwark (Gate A: Pick & Place)", 1280, 720));
        window.Run();
    }
}

internal sealed class BulwarkLoop : IGameLoop, IInputHandler
{
    // Grid: GridW × GridH square cells of Cell units, centred on the world origin.
    private const int GridW = 16;
    private const int GridH = 16;
    private const float Cell = 2.0f;

    private readonly int exitAfterFrames;
    private VulkanGraphicsDevice vk = null!;
    private IRenderHost host = null!;
    private InstanceBuffer instanceBuffer = null!;
    private InstancedBatch batch = null!;
    private readonly byte[] pushBytes = new byte[64];          // mat4 view-projection
    private readonly List<InstanceData> instances = new(GridW * GridH + 64);

    // Camera (orbit model): a target on the ground, a yaw/pitch, and a distance.
    private readonly Camera3D camera = new() { NearPlane = 0.5f, FarPlane = 400f };
    private readonly Vector3 camTarget = Vector3.Zero;
    private float camYaw;                 // radians, around +Y
    private float camPitch = 0.95f;       // radians above the ground plane (~54°)
    private float camDistance = 36f;
    private float aspect = 16f / 9f;

    // The ground plane the cursor ray hits (y = 0, normal +Y).
    private readonly Plane ground = Plane.FromPointNormal(Vector3.Zero, Vector3.UnitY);

    // Mouse + picking state.
    private float mouseX, mouseY;         // logical pixels (top-left origin)
    private int hoverCx = -1, hoverCz = -1;
    private bool hoverValid;

    // Placement state: occupancy grid (the model picking + a future nav share).
    private readonly bool[] occupied = new bool[GridW * GridH];
    private int placedCount;

    // Held-key orbit state (OnKeyDown/Up is edge-triggered; apply in OnUpdate).
    private bool orbitLeft, orbitRight, orbitUp, orbitDown;

    private int frameCount;

    public BulwarkLoop(int exitAfterFrames) => this.exitAfterFrames = exitAfterFrames;

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        vk = (VulkanGraphicsDevice)graphicsDevice;

        var vb = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(Cube.Vertices), "cube.vb");
        var ib = vk.CreateIndexBuffer(Cube.Indices, name: "cube.ib");
        var cube = new Mesh("cube", vb, ib, Cube.Indices.Length,
            new Bounds3(new Vector3(-0.5f), new Vector3(0.5f)));

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
        instanceBuffer = new InstanceBuffer(vk, shader, "bulwark");
        batch = new InstancedBatch(cube, pipeline, instanceBuffer);

        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        mouseX = host.LogicalSize.Width * 0.5f;
        mouseY = host.LogicalSize.Height * 0.5f;

        UpdateCamera();
        UpdatePick();

        Console.WriteLine("Bulwark Gate A — Pick & Place");
        Console.WriteLine("  move mouse: hover a cell   left-click: place   right-click: remove");
        Console.WriteLine("  arrows: orbit camera   mouse wheel: zoom   Esc: quit");
    }

    public void OnResize(int width, int height)
    {
        if (height > 0) aspect = width / (float)height;
    }

    public void OnUpdate(Time time)
    {
        var dt = (float)time.Delta;
        var yawRate = 1.4f;
        var pitchRate = 1.0f;
        if (orbitLeft) camYaw -= yawRate * dt;
        if (orbitRight) camYaw += yawRate * dt;
        if (orbitUp) camPitch += pitchRate * dt;
        if (orbitDown) camPitch -= pitchRate * dt;
        // Keep the camera above the ground and short of straight-down (degenerate pick).
        camPitch = Math.Clamp(camPitch, 0.2f, 1.45f);

        UpdateCamera();
        UpdatePick();
    }

    // Rebuild the Camera3D pose from the orbit params. eye = target + dir*distance,
    // where dir comes from yaw (around +Y) and pitch (elevation above the ground).
    private void UpdateCamera()
    {
        var cp = MathF.Cos(camPitch);
        var dir = new Vector3(cp * MathF.Sin(camYaw), MathF.Sin(camPitch), cp * MathF.Cos(camYaw));
        camera.Transform.Position = camTarget + dir * camDistance;
        camera.Transform.LookAt(camTarget, Vector3.UnitY);
    }

    // The picking step: cursor → world ray (through the SAME camera that renders) →
    // ground-plane hit → grid cell. This is the gate's whole point.
    private void UpdatePick()
    {
        var (w, h) = host.LogicalSize;
        var ray = camera.ScreenPointToRay(mouseX, mouseY, w, h);
        var hit = Intersection.Raycast(ray, ground);
        if (hit is { } h2 && TryWorldToCell(h2.Point, out var cx, out var cz))
        {
            hoverCx = cx;
            hoverCz = cz;
            hoverValid = true;
        }
        else
        {
            hoverValid = false;
        }
    }

    private static bool TryWorldToCell(Vector3 world, out int cx, out int cz)
    {
        cx = (int)MathF.Floor(world.X / Cell + GridW / 2f);
        cz = (int)MathF.Floor(world.Z / Cell + GridH / 2f);
        return cx >= 0 && cx < GridW && cz >= 0 && cz < GridH;
    }

    // Cell (cx, cz) centre in world space (ground plane).
    private static Vector3 CellCenter(int cx, int cz) =>
        new((cx - GridW / 2f + 0.5f) * Cell, 0f, (cz - GridH / 2f + 0.5f) * Cell);

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;
        BuildInstances();

        var viewProj = camera.GetViewProjection(aspect);
        MemoryMarshal.Write(pushBytes.AsSpan(0, 64), in viewProj);

        batch.Begin(pushBytes);
        batch.SetInstances(CollectionsMarshal.AsSpan(instances));
        commandList.Pass(
            "bulwark-grid",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0.05f, 0.07f, 0.10f, 1f) },
                ClearDepth: true),
            pass => batch.End(pass));

        if (exitAfterFrames > 0 && frameCount >= exitAfterFrames) host.RequestClose();
    }

    // One instanced draw holds the whole scene: a flat checkerboard tile per cell,
    // a raised cube per placed tower, and the hover ghost.
    private void BuildInstances()
    {
        instances.Clear();

        for (var cz = 0; cz < GridH; cz++)
        {
            for (var cx = 0; cx < GridW; cx++)
            {
                var center = CellCenter(cx, cz);
                var tile = Matrix4x4.CreateScale(Cell * 0.95f, 0.2f, Cell * 0.95f) *
                           Matrix4x4.CreateTranslation(center.X, -0.1f, center.Z);
                var dark = ((cx + cz) & 1) == 0;
                var tint = dark ? new Vector4(0.16f, 0.22f, 0.18f, 1f)
                                : new Vector4(0.22f, 0.30f, 0.24f, 1f);
                // Brighten the hovered tile so the pick reads even under the ghost.
                if (hoverValid && cx == hoverCx && cz == hoverCz)
                    tint = new Vector4(tint.X + 0.10f, tint.Y + 0.14f, tint.Z + 0.10f, 1f);
                instances.Add(new InstanceData(tile, tint));

                if (occupied[cz * GridW + cx])
                {
                    var tower = Matrix4x4.CreateScale(Cell * 0.55f, 1.6f, Cell * 0.55f) *
                                Matrix4x4.CreateTranslation(center.X, 0.8f, center.Z);
                    instances.Add(new InstanceData(tower, new Vector4(0.35f, 0.55f, 0.85f, 1f)));
                }
            }
        }

        if (hoverValid)
        {
            var center = CellCenter(hoverCx, hoverCz);
            var ghost = Matrix4x4.CreateScale(Cell * 0.55f, 1.6f, Cell * 0.55f) *
                        Matrix4x4.CreateTranslation(center.X, 0.8f, center.Z);
            var placeable = !occupied[hoverCz * GridW + hoverCx];
            var tint = placeable ? new Vector4(0.30f, 0.90f, 0.40f, 1f)
                                  : new Vector4(0.90f, 0.25f, 0.25f, 1f);
            instances.Add(new InstanceData(ghost, tint));
        }
    }

    public void OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        mouseX = x;
        mouseY = y;
    }

    public void OnMouseDown(MouseButton button)
    {
        if (!hoverValid) return;
        var idx = hoverCz * GridW + hoverCx;
        if (button == MouseButton.Left && !occupied[idx])
        {
            occupied[idx] = true;
            placedCount++;
            Console.WriteLine($"  placed tower at ({hoverCx}, {hoverCz}) — {placedCount} total");
        }
        else if (button == MouseButton.Right && occupied[idx])
        {
            occupied[idx] = false;
            placedCount--;
            Console.WriteLine($"  removed tower at ({hoverCx}, {hoverCz}) — {placedCount} total");
        }
    }

    public void OnMouseWheel(float offsetX, float offsetY)
    {
        camDistance = Math.Clamp(camDistance - offsetY * 2f, 12f, 80f);
    }

    public void OnKeyDown(Key key)
    {
        switch (key)
        {
            case Key.Escape: host.RequestClose(); break;
            case Key.Left: orbitLeft = true; break;
            case Key.Right: orbitRight = true; break;
            case Key.Up: orbitUp = true; break;
            case Key.Down: orbitDown = true; break;
        }
    }

    public void OnKeyUp(Key key)
    {
        switch (key)
        {
            case Key.Left: orbitLeft = false; break;
            case Key.Right: orbitRight = false; break;
            case Key.Up: orbitUp = false; break;
            case Key.Down: orbitDown = false; break;
        }
    }
}
