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

namespace Blix.Demos.TankArena;

// Tank Arena — a survival shooter built on Transform3D parenting. Each tank is a
// hull -> turret -> barrel hierarchy (see Tank): driving the hull carries the
// turret + barrel, while the turret yaws to aim independently. The player drives
// and aims; enemy tanks roll in from the arena edges, track the player with their
// turrets, and fire. Shells are spawned as a child of the firing barrel and then
// SetParent(null, keepWorldPose) detaches them into a PhysicsHost3D arc — the
// parenting model's detach op. Stylized primitives only; everything draws as cube
// instances through one InstancedBatch.
//
// This is the "is it a game" test for the engine; the juicy bits (recoil, death
// FX, smoothed tracking) land in a follow-up pass that pressures the animation
// surface. M1 here is the playable loop: combat, health, waves, game-over.
public static class Program
{
    public static void Main(string[] args)
    {
        var exitAfterFrames = 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--frames" && int.TryParse(args[i + 1], out var n)) exitAfterFrames = n;
        }

        var loop = new TankArenaLoop(exitAfterFrames);
        using var window = new Window(loop, new WindowOptions("Blix — Tank Arena", 1280, 720));
        window.Run();
    }
}

// One tank: the hull -> turret -> barrel transform hierarchy plus its combat state.
// Local yaws drive the parts; WorldMatrix composes the chain for rendering + aim.
internal sealed class Tank
{
    public static readonly Vector3 HullScale = new(2.2f, 0.7f, 3.2f);
    public static readonly Vector3 TurretScale = new(1.3f, 0.6f, 1.3f);
    public static readonly Vector3 BarrelScale = new(0.24f, 0.24f, 1.8f);
    private const float GravityValue = -32f;   // snappy fall/settle

    public readonly Transform3D Hull = new();
    public readonly Transform3D Turret = new();
    public readonly Transform3D Barrel = new();
    // Gravity body driving the hull — tanks fall and rest on the ground via world
    // collision, same as shells (just with the drive setting horizontal velocity).
    public readonly PhysicsHost3D Physics;
    public float HullYaw;
    public float TurretYaw;     // LOCAL turret yaw, relative to the hull
    public float BarrelPitch;   // gun elevation — sets the ballistic arc / range
    public float Health;
    public float FireTimer;

    public Tank()
    {
        Physics = new PhysicsHost3D { Target = Hull, GravityScale = 1f, Gravity = new Vector3(0f, GravityValue, 0f) };
        Hull.Position = new Vector3(0f, HullScale.Y * 0.5f, 0f);
        Turret.Position = new Vector3(0f, HullScale.Y * 0.5f + TurretScale.Y * 0.5f, 0f);
        Turret.Parent = Hull;
        // Barrel pivots at the breach (turret front face); its box is pushed forward
        // at draw time so it elevates from the mount, not its midpoint.
        Barrel.Position = new Vector3(0f, TurretScale.Y * 0.15f, -TurretScale.Z * 0.5f);
        Barrel.Parent = Turret;
    }

    public Vector3 Position
    {
        get => Hull.Position;
        set => Hull.Position = value;
    }

    // Push the yaw state into the transforms (called once per update after AI/input).
    public void Apply()
    {
        Hull.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, HullYaw);
        Turret.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, TurretYaw);
        Barrel.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, BarrelPitch);
    }

    public Vector3 BarrelForward => Vector3.Transform(-Vector3.UnitZ, Barrel.WorldRotation);
}

internal sealed class TankArenaLoop : IGameLoop, IInputHandler
{
    private const float ArenaHalf = 42f;
    // Deliberate, tactical pace — positioning + ballistic aim, not twitch.
    private const float DriveSpeed = 8f;
    private const float ReverseSpeed = 4.5f;
    private const float DriveAccel = 22f;       // ramp up
    private const float DriveDecel = 36f;       // ramp down (crisper stop, less glide)
    private const float TurnSpeed = 0.85f;      // max yaw rate (rad/s)
    private const float PivotFactor = 0f;       // no in-place spin — must be moving to turn
    private const float TurretSpeed = 1.5f;     // turret yaw aim speed
    private const float PitchSpeed = 0.85f;     // barrel elevation speed
    private const float MaxPitch = 0.8f;        // ~46deg up; flat (0) is min
    private const float ShellGravity = -16f;    // proper arc — pitch sets the range
    private const float CamDistance = 16f;      // pulled back for a tactical view
    private const float CamHeight = 10f;
    private const float CamSmooth = 5f;          // camera-yaw follow rate
    private const float MuzzleSpeed = 32f;      // tuned with ShellGravity so pitch spans the arena
    private const float PlayerFireCooldown = 0.95f;   // slower reload — make each shot count
    private const float EnemyFireCooldown = 2.8f;
    private const float ShellLife = 6f;
    private const float HitRadius = 2.0f;       // a touch forgiving for lobbed arcs
    private const float EnemySpeed = 4.5f;
    private const float EnemyStandoff = 12f;
    private const float EnemyFireRange = 28f;
    private const float PlayerMaxHealth = 100f;
    private const float EnemyShellDamage = 18f;
    private const int MaxEnemies = 12;
    private static readonly Vector3 GroundScale = new(2f * ArenaHalf, 0.2f, 2f * ArenaHalf);

    private readonly int exitAfterFrames;
    private VulkanGraphicsDevice vk = null!;
    private IRenderHost host = null!;
    private InstanceBuffer instanceBuffer = null!;
    private InstancedBatch batch = null!;
    private readonly byte[] pushBytes = new byte[64];

    private sealed class Shell
    {
        public required Transform3D Transform { get; init; }
        public required PhysicsHost3D Physics { get; init; }
        public required bool FromPlayer { get; init; }
        public float Age;
    }

    private Tank player = null!;
    private readonly List<Tank> enemies = new();
    private readonly List<Shell> shells = new();
    private readonly HashSet<Key> held = new();

    // Static world: ground slab + four arena walls, as Bounds3 colliders. Tanks
    // resolve their AABB against it each step (gravity rests them on the ground;
    // walls keep them in the arena).
    private readonly CollisionWorld3D<int> world = new();
    private readonly List<CollisionContact3D<int>> contacts = new();

    private float health;
    private int score;
    private int wave = 1;
    private float waveTimer;
    private float spawnTimer;
    private bool gameOver;
    private bool seededShot;

    private float playerSpeed;   // current forward speed (ramped toward target)
    private float camYaw;        // smoothed camera yaw, trails the hull facing
    private Matrix4x4 viewProj;
    private float aspect = 16f / 9f;
    private int frameCount;
    private readonly Random rng = new(1234);

    public TankArenaLoop(int exitAfterFrames) => this.exitAfterFrames = exitAfterFrames;

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
        instanceBuffer = new InstanceBuffer(vk, shader, "tanks");
        batch = new InstancedBatch(cube, pipeline, instanceBuffer);

        BuildWorld();
        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        Reset();
    }

    // Ground slab (solid below y=0) + four wall slabs just outside the arena. All
    // Bounds3 so tank-AABB resolution is plain box-vs-box.
    private void BuildWorld()
    {
        const float e = ArenaHalf;
        world.Add(0, new Bounds3(new Vector3(-e - 10f, -20f, -e - 10f), new Vector3(e + 10f, 0f, e + 10f)));     // ground
        world.Add(1, new Bounds3(new Vector3(-e - 2f, -1f, -e - 2f), new Vector3(-e, 10f, e + 2f)));             // -X wall
        world.Add(2, new Bounds3(new Vector3(e, -1f, -e - 2f), new Vector3(e + 2f, 10f, e + 2f)));               // +X wall
        world.Add(3, new Bounds3(new Vector3(-e - 2f, -1f, -e - 2f), new Vector3(e + 2f, 10f, -e)));             // -Z wall
        world.Add(4, new Bounds3(new Vector3(-e - 2f, -1f, e), new Vector3(e + 2f, 10f, e + 2f)));               // +Z wall
    }

    // Resolve a tank's world AABB against the static world: depenetrate out of every
    // contact and remove the into-surface velocity (so it rests on the ground and
    // slides along walls). Single pass — fine for a flat arena.
    private void ResolveTank(Tank tank)
    {
        var half = Tank.HullScale * 0.5f;
        var c = tank.Hull.Position;
        contacts.Clear();
        world.Overlap(new Bounds3(c - half, c + half), contacts);
        foreach (var contact in contacts)
        {
            tank.Hull.Position += contact.Hit.Normal * contact.Hit.Depth;
            tank.Physics.Velocity = CollisionResponse.RemoveNormalComponent(tank.Physics.Velocity, contact.Hit.Normal);
        }
    }

    private void Reset()
    {
        player = new Tank();
        player.Position = new Vector3(0f, 4f, 0f);   // drop in under gravity
        playerSpeed = 0f;
        camYaw = 0f;
        enemies.Clear();
        shells.Clear();
        health = PlayerMaxHealth;
        score = 0;
        wave = 1;
        waveTimer = 0f;
        spawnTimer = 0f;
        gameOver = false;
        player.Apply();
        UpdateTitle();
    }

    public void OnResize(int width, int height)
    {
        if (height > 0) aspect = width / (float)height;
    }

    public void OnUpdate(Time time)
    {
        var dt = (float)time.Delta;
        if (gameOver)
        {
            if (held.Contains(Key.Enter) || held.Contains(Key.R)) Reset();
            return;
        }

        UpdatePlayer(time, dt);
        UpdateSpawning(dt);
        for (var i = enemies.Count - 1; i >= 0; i--) UpdateEnemy(enemies[i], time, dt);
        UpdateShells(time, dt);

        // Headless gate: a deterministic player shot so the run exercises the
        // fire/detach/collision path within the short --frames window.
        if (exitAfterFrames > 0 && !seededShot && time.Total > 0.0)
        {
            Fire(player, fromPlayer: true);
            seededShot = true;
        }
    }

    private void UpdatePlayer(Time time, float dt)
    {
        // Ramp forward speed toward the throttle target (W forward / S reverse) so the
        // hull has weight instead of snapping to full speed.
        var targetSpeed = (held.Contains(Key.W) ? DriveSpeed : 0f) - (held.Contains(Key.S) ? ReverseSpeed : 0f);
        var rate = MathF.Abs(targetSpeed) > MathF.Abs(playerSpeed) ? DriveAccel : DriveDecel;
        playerSpeed = MoveToward(playerSpeed, targetSpeed, rate * dt);

        // Steering is coupled to motion: full turn rate while driving, only a slow
        // pivot when parked — so the tank carves a turn radius rather than spinning
        // on a dime. (speedFrac scales the available yaw rate with current speed.)
        var steer = (held.Contains(Key.A) ? 1f : 0f) - (held.Contains(Key.D) ? 1f : 0f);
        var speedFrac = MathF.Min(1f, MathF.Abs(playerSpeed) / DriveSpeed);
        player.HullYaw += steer * TurnSpeed * (PivotFactor + (1f - PivotFactor) * speedFrac) * dt;

        if (held.Contains(Key.Left)) player.TurretYaw += TurretSpeed * dt;
        if (held.Contains(Key.Right)) player.TurretYaw -= TurretSpeed * dt;
        // Up/Down elevate the gun — higher pitch lobs the shell further (range control).
        if (held.Contains(Key.Up)) player.BarrelPitch += PitchSpeed * dt;
        if (held.Contains(Key.Down)) player.BarrelPitch -= PitchSpeed * dt;
        player.BarrelPitch = Math.Clamp(player.BarrelPitch, 0f, MaxPitch);
        player.Apply();

        // Drive sets horizontal velocity along the hull facing; gravity owns vertical.
        var forward = Vector3.Transform(-Vector3.UnitZ, player.Hull.Rotation);
        SetHorizontalVelocity(player, forward * playerSpeed);
        player.Physics.FixedUpdate(new Time(time.Total, dt));
        ResolveTank(player);

        player.FireTimer -= dt;
        if (held.Contains(Key.Space) && player.FireTimer <= 0f) Fire(player, fromPlayer: true);
    }

    private static float MoveToward(float current, float target, float maxDelta)
    {
        var delta = target - current;
        return MathF.Abs(delta) <= maxDelta ? target : current + MathF.Sign(delta) * maxDelta;
    }

    private static void SetHorizontalVelocity(Tank tank, Vector3 horizontal) =>
        tank.Physics.Velocity = new Vector3(horizontal.X, tank.Physics.Velocity.Y, horizontal.Z);

    private void UpdateSpawning(float dt)
    {
        waveTimer += dt;
        if (waveTimer > 18f) { waveTimer = 0f; wave++; UpdateTitle(); }

        // One enemy at a time for now — respawns shortly after the current one dies.
        const int target = 1;
        spawnTimer -= dt;
        if (enemies.Count < target && (spawnTimer <= 0f || enemies.Count == 0))
        {
            SpawnEnemy();
            spawnTimer = 1.5f;
        }
    }

    private void SpawnEnemy()
    {
        var angle = (float)(rng.NextDouble() * Math.Tau);
        var e = new Tank { Health = 1f, FireTimer = EnemyFireCooldown * (0.4f + (float)rng.NextDouble()) };
        e.Position = new Vector3(
            MathF.Sin(angle) * (ArenaHalf - 4f), 4f, MathF.Cos(angle) * (ArenaHalf - 4f));   // drop in
        enemies.Add(e);
    }

    private void UpdateEnemy(Tank e, Time time, float dt)
    {
        var toPlayer = player.Position - e.Position;
        toPlayer.Y = 0f;
        var dist = toPlayer.Length();
        var dir = dist > 0.0001f ? toPlayer / dist : -Vector3.UnitZ;

        e.HullYaw = MathF.Atan2(-dir.X, -dir.Z);
        // Turret tracks the player in world space; convert to a local yaw under the
        // hull. (M2 will smooth this rather than snap — a likely rotate-toward helper.)
        var worldAim = MathF.Atan2(-toPlayer.X, -toPlayer.Z);
        e.TurretYaw = worldAim - e.HullYaw;
        e.Apply();

        // Drive toward the player until standoff range; gravity + walls via Resolve.
        SetHorizontalVelocity(e, dist > EnemyStandoff ? dir * EnemySpeed : Vector3.Zero);
        e.Physics.FixedUpdate(new Time(time.Total, dt));
        ResolveTank(e);

        e.FireTimer -= dt;
        if (dist < EnemyFireRange && e.FireTimer <= 0f)
        {
            Fire(e, fromPlayer: false);
        }
    }

    private void Fire(Tank tank, bool fromPlayer)
    {
        tank.FireTimer = fromPlayer ? PlayerFireCooldown : EnemyFireCooldown;
        // Spawn the shell as a child of the barrel at the muzzle, then detach it into
        // world space keeping that pose — it leaves exactly where the barrel points.
        var shell = new Transform3D { Position = new Vector3(0f, 0f, -Tank.BarrelScale.Z), Parent = tank.Barrel };
        shell.SetParent(null, keepWorldPose: true);
        var physics = new PhysicsHost3D { Target = shell, Velocity = tank.BarrelForward * MuzzleSpeed, GravityScale = 1f, Gravity = new Vector3(0f, ShellGravity, 0f) };
        shells.Add(new Shell { Transform = shell, Physics = physics, FromPlayer = fromPlayer });
    }

    private void UpdateShells(Time time, float dt)
    {
        for (var i = shells.Count - 1; i >= 0; i--)
        {
            var s = shells[i];
            s.Physics.FixedUpdate(new Time(time.Total, dt));
            s.Age += dt;
            var pos = s.Transform.WorldPosition;
            if (s.Age > ShellLife || pos.Y < -1f || OutOfArena(pos)) { shells.RemoveAt(i); continue; }

            if (s.FromPlayer)
            {
                for (var ei = enemies.Count - 1; ei >= 0; ei--)
                {
                    if (Vector3.Distance(pos, enemies[ei].Position) < HitRadius)
                    {
                        enemies.RemoveAt(ei);
                        shells.RemoveAt(i);
                        score++;
                        UpdateTitle();
                        break;
                    }
                }
            }
            else if (Vector3.Distance(pos, player.Position) < HitRadius)
            {
                shells.RemoveAt(i);
                health -= EnemyShellDamage;
                if (health <= 0f) { health = 0f; gameOver = true; }
                UpdateTitle();
            }
        }
    }

    private static bool OutOfArena(Vector3 p) =>
        MathF.Abs(p.X) > ArenaHalf + 4f || MathF.Abs(p.Z) > ArenaHalf + 4f;

    private void UpdateTitle() => host.SetTitle(gameOver
        ? $"Blix — Tank Arena | GAME OVER — score {score}, wave {wave} — Enter to restart"
        : $"Blix — Tank Arena | HP {health:0} | score {score} | wave {wave}");

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;

        // Chase cam that trails the TURRET's world facing (hull + turret yaw),
        // smoothed — the camera looks where the gun points, so aiming the turret
        // scans the arena and you can see what you're shooting at.
        var aimYaw = player.HullYaw + player.TurretYaw;
        camYaw += (aimYaw - camYaw) * MathF.Min(1f, CamSmooth * (float)time.Delta);
        var camForward = Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, camYaw));
        var p = player.Position;
        var eye = p - camForward * CamDistance + Vector3.UnitY * CamHeight;
        var view = Matrix4x4.CreateLookAt(eye, p + Vector3.UnitY * 1.2f, Vector3.UnitY);
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3f, aspect, 0.3f, 400f);
        viewProj = view * proj;

        MemoryMarshal.Write(pushBytes.AsSpan(0, 64), in viewProj);
        batch.Begin(pushBytes);

        batch.Add(Matrix4x4.CreateScale(GroundScale) * Matrix4x4.CreateTranslation(0f, -0.1f, 0f),
            new Vector4(0.11f, 0.13f, 0.17f, 1f));

        // Low perimeter walls for spatial reference (the tall colliders are invisible).
        const float w = ArenaHalf;
        var wallTint = new Vector4(0.22f, 0.25f, 0.32f, 1f);
        var span = 2f * w + 1f;
        AddBox(new Vector3(-w, 0.7f, 0f), new Vector3(1f, 1.4f, span), wallTint);
        AddBox(new Vector3(w, 0.7f, 0f), new Vector3(1f, 1.4f, span), wallTint);
        AddBox(new Vector3(0f, 0.7f, -w), new Vector3(span, 1.4f, 1f), wallTint);
        AddBox(new Vector3(0f, 0.7f, w), new Vector3(span, 1.4f, 1f), wallTint);

        AddTank(player, new Vector4(0.30f, 0.55f, 0.85f, 1f), new Vector4(0.38f, 0.62f, 0.9f, 1f));
        foreach (var e in enemies)
            AddTank(e, new Vector4(0.78f, 0.28f, 0.24f, 1f), new Vector4(0.86f, 0.36f, 0.3f, 1f));

        foreach (var s in shells)
        {
            var tint = s.FromPlayer ? new Vector4(0.6f, 0.85f, 1f, 1f) : new Vector4(1f, 0.7f, 0.3f, 1f);
            batch.Add(Matrix4x4.CreateScale(0.28f) * s.Transform.WorldMatrix, tint);
        }

        commandList.Pass(
            "arena",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0.05f, 0.06f, 0.09f, 1f) },
                ClearDepth: true),
            pass => batch.End(pass));

        if (exitAfterFrames > 0 && frameCount >= exitAfterFrames) host.RequestClose();
    }

    private void AddBox(Vector3 center, Vector3 scale, Vector4 tint) =>
        batch.Add(Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(center), tint);

    private void AddTank(Tank t, Vector4 hullTint, Vector4 turretTint)
    {
        batch.Add(Matrix4x4.CreateScale(Tank.HullScale) * t.Hull.WorldMatrix, hullTint);
        batch.Add(Matrix4x4.CreateScale(Tank.TurretScale) * t.Turret.WorldMatrix, turretTint);
        // Barrel box pushed forward half a length so it runs from the breach to the muzzle.
        batch.Add(
            Matrix4x4.CreateScale(Tank.BarrelScale) * Matrix4x4.CreateTranslation(0f, 0f, -Tank.BarrelScale.Z * 0.5f) * t.Barrel.WorldMatrix,
            new Vector4(0.18f, 0.2f, 0.22f, 1f));
    }

    public void OnKeyDown(Key key)
    {
        held.Add(key);
        if (key == Key.Escape) host.RequestClose();
    }

    public void OnKeyUp(Key key) => held.Remove(key);
}
