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

// Bulwark — tower-defense game #2 (see plan-bulwark.md). M0 proof gates, built on
// reused primitives so build effort flows to the new axes.
//
// Gate A — Pick & Place: an orbiting RTS camera over a flat grid; the cursor casts a
// ray (Camera3D.ScreenPointToRay) onto the ground plane (Intersection.Raycast); the
// hit maps to a grid cell; a ghost snaps there (green placeable / red occupied);
// left-click places a tower, right-click removes. The SAME Camera3D feeds picking
// and the render view-projection, so the pick stays locked to what's on screen as
// the camera orbits.
//
// Gate B — Path & March: enemies walk a shortest path from the spawn edge to the
// core (A* over the grid, towers impassable). The path recomputes on every build,
// and a placement that would fully wall the route is rejected. This is the engine's
// SECOND consumer of navigation (after TankArena's steering) — kept deliberately
// LOCAL: the extraction call (a shared NavGrid + A*?) waits until the duplication is
// real and visible, per docs/conventions.md §4.
//
// Everything goes through one InstancedBatch (tiles + towers + ghost + enemies),
// lifted from VulkanInstanced. The sun-shadow + HDR graph from TankArena lands at M3.
//
// ── Executable spec for (engine primitives this gate proves) ──
//   • Picking: Camera3D.ScreenPointToRay → Intersection.Raycast(ray, ground) → cell
//   • A* grid pathfinding: dynamic re-path + wall-off rejection (nav's 2nd consumer)
//   • Orbit/zoom RTS camera; build UI (hover ghost, place/remove)
// ── Intentionally owns (stays local — NOT extracted) ──
//   • the grid model, A* + path-following, placement rules, camera feel
public static class Program
{
    public static void Main(string[] args)
    {
        // --selftest: run the A* / wall-off assertions headless (no window, no Vulkan)
        // and exit with the failure count. The nav lives in the demo (not extracted),
        // so its proof lives here too rather than in Blix.Test.Graphics.
        if (args.Contains("--selftest"))
        {
            Environment.Exit(new BulwarkLoop(0).RunNavSelfTest());
        }

        var exitAfterFrames = 0;   // 0 = interactive; --frames N for the headless smoke
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--frames" && int.TryParse(args[i + 1], out var n)) exitAfterFrames = n;
        }

        var loop = new BulwarkLoop(exitAfterFrames);
        using var window = new Window(loop, new WindowOptions("Blix — Bulwark (Gate B: Path & March)", 1280, 720));
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

    // Placement state: occupancy grid (the model picking + nav share).
    private readonly bool[] occupied = new bool[GridW * GridH];
    private int placedCount;

    // Gate B — navigation (kept local; see header). Enemies march a shortest path
    // spawn→core; A* runs on placement (not per frame), towers are impassable, and a
    // build that would wall the route is rejected. Spawn + core sit on opposite edges.
    private const int SpawnCx = 0;
    private const int SpawnCz = GridH / 2;
    private const int CoreCx = GridW - 1;
    private const int CoreCz = GridH / 2;
    private const int EnemyCount = 6;
    private const float EnemySpeed = 4.5f;
    private List<(int cx, int cz)> path = new();
    private readonly HashSet<int> pathCells = new();
    private readonly Vector3[] enemyPos = new Vector3[EnemyCount];

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
        Recompute();
        InitEnemies();

        Console.WriteLine("Bulwark Gate B — Path & March");
        Console.WriteLine("  move mouse: hover   left-click: place   right-click: remove");
        Console.WriteLine("  arrows: orbit   wheel: zoom   Esc: quit");
        Console.WriteLine("  enemies march spawn→core; a build that walls the path is rejected");
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
        MoveEnemies(dt);
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

    // One instanced draw holds the whole scene: a flat tile per cell (checkerboard,
    // with the spawn / core / current path tinted distinctly), a raised cube per
    // placed tower, the core pillar, the marching enemies, and the hover ghost.
    private void BuildInstances()
    {
        instances.Clear();

        for (var cz = 0; cz < GridH; cz++)
        {
            for (var cx = 0; cx < GridW; cx++)
            {
                var idx = cz * GridW + cx;
                var center = CellCenter(cx, cz);
                var tile = Matrix4x4.CreateScale(Cell * 0.95f, 0.2f, Cell * 0.95f) *
                           Matrix4x4.CreateTranslation(center.X, -0.1f, center.Z);
                var dark = ((cx + cz) & 1) == 0;
                var tint = dark ? new Vector4(0.16f, 0.22f, 0.18f, 1f)
                                : new Vector4(0.22f, 0.30f, 0.24f, 1f);
                if (pathCells.Contains(idx)) tint = new Vector4(0.45f, 0.40f, 0.28f, 1f);   // the route
                if (cx == SpawnCx && cz == SpawnCz) tint = new Vector4(0.85f, 0.62f, 0.18f, 1f); // spawn
                if (cx == CoreCx && cz == CoreCz) tint = new Vector4(0.18f, 0.62f, 0.78f, 1f);   // core
                // Brighten the hovered tile so the pick reads even under the ghost.
                if (hoverValid && cx == hoverCx && cz == hoverCz)
                    tint = new Vector4(tint.X + 0.12f, tint.Y + 0.14f, tint.Z + 0.12f, 1f);
                instances.Add(new InstanceData(tile, tint));

                if (occupied[idx])
                {
                    var tower = Matrix4x4.CreateScale(Cell * 0.55f, 1.6f, Cell * 0.55f) *
                                Matrix4x4.CreateTranslation(center.X, 0.8f, center.Z);
                    instances.Add(new InstanceData(tower, new Vector4(0.35f, 0.55f, 0.85f, 1f)));
                }
            }
        }

        // Core pillar — the objective the enemies march toward.
        var core = CellCenter(CoreCx, CoreCz);
        instances.Add(new InstanceData(
            Matrix4x4.CreateScale(Cell * 0.4f, 2.4f, Cell * 0.4f) *
            Matrix4x4.CreateTranslation(core.X, 1.2f, core.Z),
            new Vector4(0.30f, 0.85f, 1.0f, 1f)));

        // Enemies marching the current path.
        foreach (var p in enemyPos)
        {
            instances.Add(new InstanceData(
                Matrix4x4.CreateScale(0.8f) * Matrix4x4.CreateTranslation(p.X, 0.4f, p.Z),
                new Vector4(0.95f, 0.45f, 0.20f, 1f)));
        }

        if (hoverValid)
        {
            var center = CellCenter(hoverCx, hoverCz);
            var ghost = Matrix4x4.CreateScale(Cell * 0.55f, 1.6f, Cell * 0.55f) *
                        Matrix4x4.CreateTranslation(center.X, 0.8f, center.Z);
            var buildable = !occupied[hoverCz * GridW + hoverCx]
                && !(hoverCx == SpawnCx && hoverCz == SpawnCz)
                && !(hoverCx == CoreCx && hoverCz == CoreCz);
            var tint = buildable ? new Vector4(0.30f, 0.90f, 0.40f, 1f)
                                 : new Vector4(0.90f, 0.25f, 0.25f, 1f);
            instances.Add(new InstanceData(ghost, tint));
        }
    }

    // ── Navigation (Gate B) — local to this demo, NOT an engine primitive yet ──

    private static int Idx(int cx, int cz) => cz * GridW + cx;

    private static Vector3 Center((int cx, int cz) c) => CellCenter(c.cx, c.cz);

    // Recompute the spawn→core path and the path-cell set. Called on load and after
    // every successful place/remove — never per frame.
    private void Recompute()
    {
        path = FindPath() ?? path;   // FindPath only returns null on a walled grid, which placement rejects
        pathCells.Clear();
        foreach (var c in path) pathCells.Add(Idx(c.cx, c.cz));
    }

    // A* over the grid: 4-connected, uniform step cost, Manhattan heuristic. Occupied
    // cells (towers) are impassable. Returns the cell path spawn→core, or null if the
    // grid is fully walled.
    private List<(int cx, int cz)>? FindPath()
    {
        int start = Idx(SpawnCx, SpawnCz), goal = Idx(CoreCx, CoreCz);
        var g = new float[GridW * GridH];
        var from = new int[GridW * GridH];
        var closed = new bool[GridW * GridH];
        Array.Fill(g, float.PositiveInfinity);
        Array.Fill(from, -1);
        g[start] = 0f;

        var open = new PriorityQueue<int, float>();
        open.Enqueue(start, Heuristic(start, goal));
        while (open.Count > 0)
        {
            var cur = open.Dequeue();
            if (cur == goal) return Reconstruct(from, cur);
            if (closed[cur]) continue;     // stale heap entry (lazy decrease-key)
            closed[cur] = true;

            int cx = cur % GridW, cz = cur / GridW;
            foreach (var (nx, nz) in new[] { (cx + 1, cz), (cx - 1, cz), (cx, cz + 1), (cx, cz - 1) })
            {
                if (nx < 0 || nx >= GridW || nz < 0 || nz >= GridH) continue;
                var n = Idx(nx, nz);
                if (closed[n] || occupied[n]) continue;
                var tentative = g[cur] + 1f;
                if (tentative < g[n])
                {
                    g[n] = tentative;
                    from[n] = cur;
                    open.Enqueue(n, tentative + Heuristic(n, goal));
                }
            }
        }
        return null;
    }

    private static float Heuristic(int a, int b) =>
        MathF.Abs(a % GridW - b % GridW) + MathF.Abs(a / GridW - b / GridW);

    private static List<(int cx, int cz)> Reconstruct(int[] from, int cur)
    {
        var result = new List<(int, int)>();
        for (var c = cur; c != -1; c = from[c]) result.Add((c % GridW, c / GridW));
        result.Reverse();
        return result;
    }

    // Stagger the enemies evenly along the current path so they read as a column.
    private void InitEnemies()
    {
        for (var i = 0; i < EnemyCount; i++)
            enemyPos[i] = PathPointAtFraction((float)i / EnemyCount);
    }

    // Sample the polyline through the current path's cell centres at fraction f∈[0,1].
    private Vector3 PathPointAtFraction(float f)
    {
        if (path.Count == 0) return CellCenter(SpawnCx, SpawnCz);
        if (path.Count == 1) return Center(path[0]);
        var total = 0f;
        for (var i = 1; i < path.Count; i++) total += Vector3.Distance(Center(path[i - 1]), Center(path[i]));
        var target = f * total;
        var acc = 0f;
        for (var i = 1; i < path.Count; i++)
        {
            var a = Center(path[i - 1]);
            var b = Center(path[i]);
            var seg = Vector3.Distance(a, b);
            if (acc + seg >= target) return Vector3.Lerp(a, b, seg > 1e-4f ? (target - acc) / seg : 0f);
            acc += seg;
        }
        return Center(path[^1]);
    }

    // Steer each enemy toward the path node just ahead of it (found by nearest node),
    // so a dynamic re-path reroutes the marchers with no per-enemy path state. Reaching
    // the core loops the enemy back to the spawn (Gate B has no HP/economy yet — M1).
    private void MoveEnemies(float dt)
    {
        if (path.Count < 2) return;
        var coreCenter = Center(path[^1]);
        for (var i = 0; i < EnemyCount; i++)
        {
            var nearest = 0;
            var best = float.MaxValue;
            for (var k = 0; k < path.Count; k++)
            {
                var d = Vector3.DistanceSquared(enemyPos[i], Center(path[k]));
                if (d < best) { best = d; nearest = k; }
            }
            var target = Center(path[Math.Min(nearest + 1, path.Count - 1)]);
            var to = target - enemyPos[i];
            var dist = to.Length();
            if (dist > 1e-4f) enemyPos[i] += to / dist * Math.Min(EnemySpeed * dt, dist);
            if (Vector3.Distance(enemyPos[i], coreCenter) < 0.4f) enemyPos[i] = Center(path[0]);
        }
    }

    // Deterministic proof of the Gate B nav invariants — no window, no device. Run
    // via `--selftest` (returns the failure count). Straight = Manhattan + 1: spawn
    // (0,8) → core (15,8) is one row, so 16 cells.
    public int RunNavSelfTest()
    {
        var failed = 0;
        void Check(string label, bool ok)
        {
            Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {label}");
            if (!ok) failed++;
        }

        // 1. Empty grid → a straight 16-cell path with the right endpoints.
        Array.Clear(occupied);
        var p = FindPath();
        Check("empty grid has a path", p is not null);
        Check("path starts at spawn", p is { } && p[0] == (SpawnCx, SpawnCz));
        Check("path ends at core", p is { } && p[^1] == (CoreCx, CoreCz));
        Check("straight path is 16 cells", p is { Count: 16 });

        // 2. A wall down column 8 with the top row open → still a path, but longer.
        Array.Clear(occupied);
        for (var cz = 1; cz < GridH; cz++) occupied[Idx(8, cz)] = true;
        var detour = FindPath();
        Check("partial wall still has a path", detour is not null);
        Check("detour is longer than the straight route", detour is { } && detour.Count > 16);

        // 3. A full column 8 wall → no path at all (wall-off detection: the rejection rule).
        Array.Clear(occupied);
        for (var cz = 0; cz < GridH; cz++) occupied[Idx(8, cz)] = true;
        Check("full wall has NO path (wall-off detected)", FindPath() is null);

        // 4. Open one gap in the wall → the path returns, threaded through the gap.
        occupied[Idx(8, 3)] = false;
        var threaded = FindPath();
        Check("opening a gap restores the path", threaded is not null);
        Check("threaded path detours longer than straight", threaded is { } && threaded.Count > 16);

        Array.Clear(occupied);
        Console.WriteLine($"  nav self-test: {(failed == 0 ? "all passed" : failed + " FAILED")}");
        return failed;
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
        // The spawn and the core are never buildable.
        if ((hoverCx == SpawnCx && hoverCz == SpawnCz) || (hoverCx == CoreCx && hoverCz == CoreCz))
        {
            if (button == MouseButton.Left) Console.WriteLine("  can't build on the spawn or the core");
            return;
        }
        if (button == MouseButton.Left && !occupied[idx])
        {
            occupied[idx] = true;
            // Reject a placement that would fully wall the route: tentatively occupy,
            // re-run A*, and revert if no path survives.
            if (FindPath() is null)
            {
                occupied[idx] = false;
                Console.WriteLine($"  blocked: a tower at ({hoverCx}, {hoverCz}) would wall off the path");
                return;
            }
            placedCount++;
            Recompute();
            Console.WriteLine($"  placed tower at ({hoverCx}, {hoverCz}) — {placedCount} total, path {path.Count} cells");
        }
        else if (button == MouseButton.Right && occupied[idx])
        {
            occupied[idx] = false;
            placedCount--;
            Recompute();
            Console.WriteLine($"  removed tower at ({hoverCx}, {hoverCz}) — {placedCount} total, path {path.Count} cells");
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
