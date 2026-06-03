using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
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
// M1 — Playable core: towers target the nearest enemy in range and fire homing
// shots; enemies have HP; a kill pays scrap, a leak costs a life; scrap builds more
// towers. Each tower aims with a Transform3D turret→barrel rig — the SAME pattern as
// TankArena's tank turret (LookAt to yaw, a parented barrel whose WorldPosition is
// the muzzle), now its SECOND consumer. Kept local; the TurretRig extraction call
// waits, like nav.
//
// M2a — The game: a discrete wave director (escalating size + HP, win on clearing
// the last wave, defeat at 0 lives, ENTER to restart), tower upgrades (left-click an
// existing tower to level it up — more damage + range), and a SpriteBatch/Font HUD
// (wave / lives / scrap + centre banners). [M2b adds impact particles + SFX.]
//
// Everything 3D goes through one InstancedBatch (tiles + towers + enemies + shots +
// ghost), lifted from VulkanInstanced; the HUD is a SpriteBatch over the same pass.
// The sun-shadow + HDR graph from TankArena lands at M3.
//
// ── Executable spec for (engine primitives this demo proves) ──
//   • Picking: Camera3D.ScreenPointToRay → Intersection.Raycast(ray, ground) → cell
//   • A* grid pathfinding: dynamic re-path + wall-off rejection (nav's 2nd consumer)
//   • Transform3D turret→barrel aim rig: LookAt + parented-barrel muzzle (rig's 2nd consumer)
//   • SpriteBatch/Font HUD composited over the 3D pass (Vulkan-NDC ortho)
//   • Orbit/zoom RTS camera; build/upgrade UI + scrap economy
// ── Intentionally owns (stays local — NOT extracted) ──
//   • grid, A* + path-following, turret aim, economy, wave director, HUD layout, camera feel
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
        using var window = new Window(loop, new WindowOptions("Blix — Bulwark (M2: Tower Defense)", 1280, 720));
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

    // Navigation (kept local; see header). Enemies march a shortest path spawn→core;
    // A* runs on placement (not per frame), towers are impassable, and a build that
    // would wall the route is rejected. Spawn + core sit on opposite edges.
    private const int SpawnCx = 0;
    private const int SpawnCz = GridH / 2;
    private const int CoreCx = GridW - 1;
    private const int CoreCz = GridH / 2;
    private const float EnemySpeed = 3.6f;
    private List<(int cx, int cz)> path = new();
    private readonly HashSet<int> pathCells = new();

    // M1 — combat loop. Towers (a Transform3D turret→barrel aim rig lifted from
    // TankArena, kept local) target the nearest enemy in range and fire homing shots;
    // enemies have HP; a kill pays scrap, a leak costs a life; scrap builds towers.
    private const int TowerCost = 50;
    private const int KillReward = 9;
    private const int StartScrap = 150;   // = 3 towers; you must build during Prep
    private const int StartLives = 20;
    private const float TowerRange = 6f;
    private const float FireInterval = 0.55f;
    private const float ShotDamage = 11f;
    private const float EnemyMaxHp = 55f;
    private const float SpawnInterval = 1.0f;
    private const int MaxAlive = 20;
    private const float ProjSpeed = 22f;

    private const int TotalWaves = 5;
    private const int MaxLevel = 3;

    private readonly List<Enemy> enemies = new();
    private readonly Dictionary<int, Tower> towers = new();
    private readonly List<Projectile> shots = new();
    private float spawnTimer;
    private float statusTimer;
    private int lives = StartLives;
    private int scrap = StartScrap;

    // Wave director. Prep = build freely + SPACE to launch; Wave = enemies inbound;
    // Won/Lost = end states (ENTER to restart). autoPlay launches waves with no key
    // input for the headless --frames smoke.
    private Phase phase = Phase.Prep;
    private int wave;        // 0 before the first wave starts
    private int toSpawn;     // enemies left to spawn this wave
    private readonly bool autoPlay;

    // HUD: a SpriteBatch over the swapchain + the Bowlby font (best-effort load).
    private SpriteBatch hud = null!;
    private Font? hudFont;

    // Held-key orbit state (OnKeyDown/Up is edge-triggered; apply in OnUpdate).
    private bool orbitLeft, orbitRight, orbitUp, orbitDown;

    private int frameCount;

    public BulwarkLoop(int exitAfterFrames)
    {
        this.exitAfterFrames = exitAfterFrames;
        autoPlay = exitAfterFrames > 0;   // headless smoke: auto-run waves (no key input)
    }

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

        CreateHud();
        UpdateCamera();
        UpdatePick();
        if (autoPlay) SeedStarterTowers();   // headless smoke only — the player builds their own
        Recompute();

        Console.WriteLine("Bulwark M2 — Tower Defense");
        Console.WriteLine("  SPACE: launch wave   left-click: build (50) / upgrade existing   right-click: sell");
        Console.WriteLine("  arrows: orbit   wheel: zoom   ENTER: restart (after win/lose)   Esc: quit");
        Console.WriteLine($"  survive {TotalWaves} waves — start: {StartLives} lives, {StartScrap} scrap");
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

        if (phase == Phase.Prep && autoPlay) StartWave();

        if (phase == Phase.Wave)
        {
            SpawnWave(dt);
            UpdateEnemies(dt);   // may flip phase → Lost on a fatal leak
            UpdateTowers(dt);
            UpdateShots(dt);

            // Wave cleared when nothing is left to spawn and the field is empty.
            if (phase == Phase.Wave && toSpawn == 0 && enemies.Count == 0)
            {
                phase = wave >= TotalWaves ? Phase.Won : Phase.Prep;
                Console.WriteLine(phase == Phase.Won
                    ? "  *** VICTORY — all waves cleared ***"
                    : $"  wave {wave} cleared — build, then SPACE for wave {wave + 1}");
            }
        }

        PrintStatus(dt);
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
            pass =>
            {
                batch.End(pass);
                DrawHud(pass, frame.Width, frame.Height);   // depth-disabled, alpha-blended, over the 3D
            });

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
            }
        }

        // Core pillar — the objective the enemies march toward.
        var core = CellCenter(CoreCx, CoreCz);
        instances.Add(new InstanceData(
            Matrix4x4.CreateScale(Cell * 0.4f, 2.4f, Cell * 0.4f) *
            Matrix4x4.CreateTranslation(core.X, 1.2f, core.Z),
            new Vector4(0.30f, 0.85f, 1.0f, 1f)));

        // Towers: a fixed pedestal + the aiming turret (oriented by its Transform3D,
        // so the elongated box visibly points its -Z barrel at the current target).
        foreach (var t in towers.Values)
        {
            instances.Add(new InstanceData(
                Matrix4x4.CreateScale(Cell * 0.5f, 1.0f, Cell * 0.5f) *
                Matrix4x4.CreateTranslation(t.Pos.X, 0.5f, t.Pos.Z),
                new Vector4(0.30f, 0.34f, 0.42f, 1f)));
            var turretTint = t.Level switch   // brighter → gold as it upgrades
            {
                >= 3 => new Vector4(0.95f, 0.82f, 0.35f, 1f),
                2 => new Vector4(0.55f, 0.75f, 1.00f, 1f),
                _ => new Vector4(0.45f, 0.60f, 0.90f, 1f),
            };
            instances.Add(new InstanceData(t.Turret.ToMatrix(), turretTint));
        }

        // Enemies, tinted green (full HP) → red (dying).
        var green = new Vector4(0.30f, 0.90f, 0.35f, 1f);
        var red = new Vector4(0.95f, 0.22f, 0.16f, 1f);
        foreach (var e in enemies)
        {
            var hp = Math.Clamp(e.Health / e.MaxHealth, 0f, 1f);
            var tint = Vector4.Lerp(red, green, hp);
            instances.Add(new InstanceData(
                Matrix4x4.CreateScale(0.85f) * Matrix4x4.CreateTranslation(e.Pos.X, 0.45f, e.Pos.Z), tint));
        }

        // In-flight homing shots.
        foreach (var s in shots)
        {
            instances.Add(new InstanceData(
                Matrix4x4.CreateScale(0.28f) * Matrix4x4.CreateTranslation(s.Pos.X, s.Pos.Y, s.Pos.Z),
                new Vector4(1.0f, 0.95f, 0.40f, 1f)));
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

    // ── Combat (M1) — all local to this demo ──

    // Launch the next wave: escalating count, HP scales in SpawnWave.
    private void StartWave()
    {
        wave++;
        toSpawn = 6 + wave * 4;   // w1=10 … w5=26
        spawnTimer = 0f;
        phase = Phase.Wave;
        Console.WriteLine($"  -- WAVE {wave}/{TotalWaves} -- {toSpawn} inbound");
    }

    // Emit this wave's enemies from the spawn cell on an interval, up to the alive cap.
    // HP scales ~18% per wave so later waves need upgraded towers.
    private void SpawnWave(float dt)
    {
        if (toSpawn <= 0) return;
        spawnTimer -= dt;
        if (spawnTimer <= 0f && enemies.Count < MaxAlive)
        {
            spawnTimer = SpawnInterval;
            var hp = EnemyMaxHp * (1f + 0.30f * (wave - 1));   // w5 ≈ 2.2× base
            enemies.Add(new Enemy { Pos = Center((SpawnCx, SpawnCz)), Health = hp, MaxHealth = hp });
            toSpawn--;
        }
    }

    // Reset to a fresh game (ENTER from Won/Lost).
    private void Restart()
    {
        enemies.Clear();
        shots.Clear();
        towers.Clear();
        Array.Clear(occupied);
        placedCount = 0;
        lives = StartLives;
        scrap = StartScrap;
        wave = 0;
        phase = Phase.Prep;
        if (autoPlay) SeedStarterTowers();
        Recompute();
        Console.WriteLine("  -- restarted --");
    }

    // March enemies along the current path (nearest-node lookahead, same as Gate B),
    // and resolve deaths (→ scrap) and leaks (→ a life). Backwards iteration so
    // removal during the loop is safe.
    private void UpdateEnemies(float dt)
    {
        if (path.Count < 2) return;
        var core = Center(path[^1]);
        for (var i = enemies.Count - 1; i >= 0; i--)
        {
            var e = enemies[i];
            if (e.Health <= 0f)
            {
                enemies.RemoveAt(i);
                scrap += KillReward;
                continue;
            }

            var nearest = 0;
            var best = float.MaxValue;
            for (var k = 0; k < path.Count; k++)
            {
                var d = Vector3.DistanceSquared(e.Pos, Center(path[k]));
                if (d < best) { best = d; nearest = k; }
            }
            var target = Center(path[Math.Min(nearest + 1, path.Count - 1)]);
            var to = target - e.Pos;
            var dist = to.Length();
            if (dist > 1e-4f) e.Pos += to / dist * Math.Min(EnemySpeed * dt, dist);

            if (Vector3.Distance(e.Pos, core) < 0.5f)
            {
                enemies.RemoveAt(i);
                lives--;
                Console.WriteLine($"  LEAK — lives {lives}");
                if (lives <= 0) { phase = Phase.Lost; Console.WriteLine("  *** DEFEAT ***"); }
            }
        }
    }

    // Each tower aims its Transform3D turret (yaw-only LookAt) at the nearest enemy in
    // range and fires a homing shot from the barrel's muzzle when its cooldown is up.
    private void UpdateTowers(float dt)
    {
        foreach (var t in towers.Values)
        {
            t.Cooldown -= dt;
            var range = TowerRange + (t.Level - 1) * 1.5f;     // upgrades extend reach
            var target = NearestEnemyInRange(t.Pos, range);
            if (target is null) continue;

            // Aim flat at the target (yaw only — barrel stays level).
            t.Turret.LookAt(new Vector3(target.Pos.X, t.Turret.Position.Y, target.Pos.Z), Vector3.UnitY);
            if (t.Cooldown <= 0f)
            {
                t.Cooldown = FireInterval;
                var dmg = ShotDamage * (1f + 0.6f * (t.Level - 1));   // and damage
                shots.Add(new Projectile { Pos = t.Barrel.WorldPosition, Target = target, Damage = dmg });
            }
        }
    }

    // Homing shots: chase the assigned target, apply damage on contact, fizzle if the
    // target died mid-flight.
    private void UpdateShots(float dt)
    {
        for (var i = shots.Count - 1; i >= 0; i--)
        {
            var s = shots[i];
            if (s.Target is null || s.Target.Health <= 0f) { shots.RemoveAt(i); continue; }
            var to = s.Target.Pos - s.Pos;
            var dist = to.Length();
            if (dist < 0.5f) { s.Target.Health -= s.Damage; shots.RemoveAt(i); continue; }
            s.Pos += to / dist * Math.Min(ProjSpeed * dt, dist);
        }
    }

    private Enemy? NearestEnemyInRange(Vector3 from, float range)
    {
        Enemy? best = null;
        var bestD = range * range;
        foreach (var e in enemies)
        {
            if (e.Health <= 0f) continue;
            var d = Vector3.DistanceSquared(from, e.Pos);
            if (d <= bestD) { bestD = d; best = e; }
        }
        return best;
    }

    // A tower's aim rig: a turret Transform3D (root, at the tower top) that LookAts the
    // target, and a barrel parented to it whose WorldPosition is the muzzle. This is
    // TankArena's turret pattern, kept local — see header (TurretRig extraction TBD).
    private Tower MakeTower(int cx, int cz)
    {
        var c = CellCenter(cx, cz);
        var turret = new Transform3D
        {
            Position = new Vector3(c.X, 1.4f, c.Z),
            Scale = new Vector3(0.7f, 0.5f, 1.1f),
        };
        var barrel = new Transform3D { Position = new Vector3(0f, 0f, -0.9f), Parent = turret };
        return new Tower { Cx = cx, Cz = cz, Pos = c, Turret = turret, Barrel = barrel };
    }

    // Headless-only: the --frames smoke has no mouse, so seed a few towers off the
    // lane to exercise aim + fire under validation. Interactive play starts empty —
    // the player must build during Prep (do nothing → leaks → defeat).
    private void SeedStarterTowers()
    {
        foreach (var (cx, cz) in new[] { (4, 7), (8, 9), (11, 7) })
        {
            var idx = Idx(cx, cz);
            occupied[idx] = true;
            towers[idx] = MakeTower(cx, cz);
            placedCount++;
        }
    }

    private void PrintStatus(float dt)
    {
        statusTimer -= dt;
        if (statusTimer > 0f) return;
        statusTimer = 2f;
        Console.WriteLine($"  [{phase} w{wave}/{TotalWaves} | lives {lives} | scrap {scrap} | enemies {enemies.Count} | towers {towers.Count}]");
    }

    // ── HUD (SpriteBatch + Font) — composited over the 3D pass ──

    private void CreateHud()
    {
        hud = new SpriteBatch(vk);   // null render target = swapchain
        try
        {
            var assets = new AssetDatabase()
                .RegisterImporter(new FontImporter())
                .LoadManifest(Path.Combine(AppContext.BaseDirectory, "Assets", "manifest.json"));
            hudFont = Font.Upload(vk, assets.Load<FontData>(AssetId.Parse("fonts/bowlby")));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  font unavailable, HUD text won't render: {ex.Message}");
        }
    }

    // Wave / lives / scrap top-left, a centred banner for the current phase. Screen-
    // space ortho in framebuffer pixels (pixelSize is physical px, dpiScale = 1) —
    // SpriteBatch is depth-disabled + alpha-blended, so it draws over the 3D scene.
    private void DrawHud(RenderPassBuilder pass, int width, int height)
    {
        if (hudFont is null) return;
        var ortho = GraphicsMatrices.CreateOrthographicOffCenterVulkan(0f, width, height, 0f, -1f, 1f);
        hud.Begin(ortho);

        var pad = height * 0.03f;
        var size = height * 0.042f;
        hud.DrawText(hudFont, size, $"WAVE {Math.Max(wave, 1)}/{TotalWaves}", new Vector2(pad, pad), new GraphicsColor(0.85f, 0.92f, 1f, 1f));
        hud.DrawText(hudFont, size, $"LIVES {lives}", new Vector2(pad, pad + size * 1.2f), new GraphicsColor(0.55f, 0.95f, 0.55f, 1f));
        hud.DrawText(hudFont, size, $"SCRAP {scrap}", new Vector2(pad, pad + size * 2.4f), new GraphicsColor(0.96f, 0.82f, 0.30f, 1f));

        var banner = phase switch
        {
            Phase.Prep => wave == 0 ? "BUILD — THEN SPACE" : $"SPACE — WAVE {wave + 1}",
            Phase.Won => "VICTORY",
            Phase.Lost => "DEFEAT",
            _ => null,
        };
        if (banner is not null)
        {
            var big = height * 0.10f;
            var col = phase == Phase.Lost ? new GraphicsColor(0.96f, 0.36f, 0.30f, 1f)
                                          : new GraphicsColor(0.95f, 0.90f, 0.50f, 1f);
            var bs = SpriteBatchUiExtensions.MeasureText(hudFont, big, banner);
            hud.DrawText(hudFont, big, banner, new Vector2((width - bs.X) * 0.5f, height * 0.30f), col);

            if (phase is Phase.Won or Phase.Lost)
            {
                const string prompt = "ENTER TO RESTART";
                var sub = height * 0.04f;
                var ps = SpriteBatchUiExtensions.MeasureText(hudFont, sub, prompt);
                hud.DrawText(hudFont, sub, prompt, new Vector2((width - ps.X) * 0.5f, height * 0.30f + big), new GraphicsColor(0.90f, 0.92f, 0.96f, 1f));
            }
        }

        hud.End(pass);
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
        if (!hoverValid || phase is Phase.Won or Phase.Lost) return;
        var idx = hoverCz * GridW + hoverCx;
        var onEndpoint = (hoverCx == SpawnCx && hoverCz == SpawnCz) || (hoverCx == CoreCx && hoverCz == CoreCz);

        if (button == MouseButton.Left)
        {
            if (onEndpoint) { Console.WriteLine("  can't build on the spawn or the core"); return; }

            if (towers.TryGetValue(idx, out var existing))
            {
                // Upgrade an existing tower.
                if (existing.Level >= MaxLevel) { Console.WriteLine("  tower already at max level"); return; }
                var upCost = 40 * existing.Level;
                if (scrap < upCost) { Console.WriteLine($"  upgrade needs {upCost} scrap (have {scrap})"); return; }
                scrap -= upCost;
                existing.Level++;
                Console.WriteLine($"  upgraded tower ({hoverCx}, {hoverCz}) → L{existing.Level}, scrap {scrap}");
                return;
            }

            // Build a new tower.
            if (scrap < TowerCost) { Console.WriteLine($"  need {TowerCost} scrap (have {scrap})"); return; }
            occupied[idx] = true;
            // Reject a placement that would fully wall the route: tentatively occupy,
            // re-run A*, and revert if no path survives.
            if (FindPath() is null)
            {
                occupied[idx] = false;
                Console.WriteLine($"  blocked: a tower at ({hoverCx}, {hoverCz}) would wall off the path");
                return;
            }
            scrap -= TowerCost;
            placedCount++;
            towers[idx] = MakeTower(hoverCx, hoverCz);
            Recompute();
            Console.WriteLine($"  built tower ({hoverCx}, {hoverCz}) — scrap {scrap}, {placedCount} towers, path {path.Count}");
        }
        else if (button == MouseButton.Right && occupied[idx])
        {
            var refund = 25 + 20 * (towers.TryGetValue(idx, out var t) ? t.Level - 1 : 0);
            occupied[idx] = false;
            towers.Remove(idx);
            placedCount--;
            scrap += refund;
            Recompute();
            Console.WriteLine($"  sold tower ({hoverCx}, {hoverCz}) (+{refund}) — scrap {scrap}, {placedCount} towers, path {path.Count}");
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
            case Key.Space: if (phase == Phase.Prep) StartWave(); break;
            case Key.Enter: if (phase is Phase.Won or Phase.Lost) Restart(); break;
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

// ── Game state + combat actors — plain mutable data, local to the demo ──

internal enum Phase { Prep, Wave, Won, Lost }

internal sealed class Enemy
{
    public Vector3 Pos;
    public float Health;
    public float MaxHealth;   // for the HP tint (scales per wave)
}

internal sealed class Tower
{
    public int Cx;
    public int Cz;
    public Vector3 Pos;                  // pedestal world position (cell centre)
    public Transform3D Turret = null!;   // root aim transform; LookAts the target
    public Transform3D Barrel = null!;   // child of Turret; WorldPosition is the muzzle
    public float Cooldown;
    public int Level = 1;                // 1..MaxLevel; boosts damage + range
}

internal sealed class Projectile
{
    public Vector3 Pos;
    public Enemy? Target;
    public float Damage;
}
