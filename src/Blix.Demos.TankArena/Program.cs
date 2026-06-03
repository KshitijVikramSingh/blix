using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Core;
using Blix.Diagnostics;
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
        var debugOverlay = args.Contains("--debug");   // live tuning + diagnostics overlay (` to show)

        var loop = new TankArenaLoop(exitAfterFrames, debugOverlay);
        using var window = new Window(loop, new WindowOptions("Blix — Tank Arena", 1280, 720));
        window.Run();
    }
}

// Live-tunable feel knobs — reflected into the --debug overlay via [Tune] and read
// each frame, so movement / camera / turning / ballistics can be dialled in while
// playing. Defaults are the current tuned values.
internal sealed class TankFeel
{
    // Movement / camera / turning default to slider-min (slow, deliberate baseline);
    // ballistics (shell speed, gravity, pitch, reload) keep their tuned values.
    [Tune(3f, 18f)]    public float DriveSpeed = 3f;
    [Tune(2f, 10f)]    public float ReverseSpeed = 2f;
    [Tune(5f, 60f)]    public float DriveAccel = 5f;
    [Tune(5f, 90f)]    public float DriveDecel = 5f;
    [Tune(0.3f, 2.5f)] public float TurnSpeed = 0.3f;
    [Tune(0.5f, 4f)]   public float TurretSpeed = 0.5f;
    [Tune(0.3f, 2.5f)] public float PitchSpeed = 0.3f;
    [Tune(0.3f, 1.4f)] public float MaxPitch = 0.8f;          // kept — ballistic range
    [Tune(0.4f, 3.14f)] public float TurretLimit = 1.7f;      // clamp turret to a front arc (no 360 spin)
    [Tune(15f, 70f)]   public float MuzzleSpeed = 32f;        // kept
    [Tune(-40f, -4f)]  public float ShellGravity = -16f;      // kept
    [Tune(-60f, -8f)]  public float TankGravity = -32f;       // kept
    [Tune(0.2f, 2.5f)] public float Reload = 0.95f;           // kept
    [Tune(0f, 12f)]    public float Knockback = 3f;           // recoil shove on firing
    [Tune(0f, 0.7f)]   public float ShadowAlpha = 0.32f;      // planar sun-shadow darkness
    [Tune(6f, 30f)]    public float CamDistance = 6f;
    [Tune(3f, 22f)]    public float CamHeight = 3f;
    [Tune(1f, 14f)]    public float CamSmooth = 1f;

    // Enemy knobs — turn them down/off to tune in peace.
    [Tune(0f, 8f)]     public float EnemyMax = 1f;       // 0 clears the arena
    [Tune]             public bool EnemiesFire = true;   // off = present but harmless
    [Tune(1.5f, 9f)]   public float EnemySpeed = 4.5f;
    [Tune(0.5f, 6f)]   public float EnemyReload = 2.8f;
    [Tune(2f, 40f)]    public float EnemyDamage = 18f;
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

internal sealed class TankArenaLoop : IGameLoop, IInputHandler, IDebuggable
{
    private const float ArenaHalf = 42f;
    private const float PivotFactor = 0f;       // no in-place spin — must be moving to turn
    private const float ShellLife = 6f;
    private const float HitRadius = 2.0f;       // a touch forgiving for lobbed arcs
    private const float EnemyStandoff = 12f;
    private const float EnemyFireRange = 28f;
    private const float PlayerMaxHealth = 100f;
    private static readonly Vector3 GroundScale = new(2f * ArenaHalf, 0.2f, 2f * ArenaHalf);

    // Live-tunable feel knobs (movement / camera / turning / ballistics), exposed in
    // the --debug overlay. Read each frame so dragging a slider updates the game live.
    private readonly TankFeel feel = new();
    private ObjectTunables tunables = null!;
    private readonly bool debugOverlay;

    private readonly int exitAfterFrames;
    private VulkanGraphicsDevice vk = null!;
    private IRenderHost host = null!;
    private InstanceBuffer instanceBuffer = null!;
    private InstancedBatch batch = null!;
    private FullscreenPass sky = null!;
    private PipelineHandle skyPipeline;
    private InstanceBuffer shadowInstances = null!;
    private InstancedBatch shadowBatch = null!;
    private readonly byte[] pushBytes = new byte[96];   // viewProj + camPos + sunDir
    private readonly byte[] skyPush = new byte[96];     // invViewProj + camPos + sunDir
    private static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(0.35f, 0.82f, 0.45f));
    private const float ShadowLift = 0.03f;             // planar shadows sit just above the ground

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
    private Vector3 recoilVel;   // decaying recoil shove from firing (added to drive)
    private float camYaw;        // smoothed camera yaw, trails the turret facing
    private const float RecoilDamp = 4f;
    private Matrix4x4 viewProj;
    private float aspect = 16f / 9f;
    private int frameCount;
    private readonly Random rng = new(1234);

    public TankArenaLoop(int exitAfterFrames, bool debugOverlay)
    {
        this.exitAfterFrames = exitAfterFrames;
        this.debugOverlay = debugOverlay;
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
        // Push: viewProj (vertex) + camPos + sunDir (fragment lighting/fog) = 96 bytes.
        var iface = new ShaderInterface(
            Slots: new[] { InstanceBuffer.Slot },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex | ShaderStages.Fragment, 0, 96) });
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

        // Procedural daylight sky: a fullscreen-triangle pass drawn first (depth off)
        // so the world draws over it. Push = inverse-VP + camPos + sunDir.
        var skyIface = new ShaderInterface(
            Slots: Array.Empty<DescriptorSetSlot>(),
            PushConstants: new[] { new PushConstantRange(ShaderStages.Fragment, 0, 96) });
        var skyShader = vk.CreateShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "sky.vert.spv")),
            File.ReadAllBytes(Path.Combine(shaderDir, "sky.frag.spv")),
            skyIface, "sky");
        skyPipeline = vk.CreatePipeline(
            new PipelineDescription(skyShader, VertexPosition3NormalTexture.Layout, PrimitiveTopology.Triangles,
                DepthState.Disabled, RasterizerState.NoCulling, new[] { BlendState.Disabled }),
            "sky");
        sky = new FullscreenPass(vk, "sky");

        // Planar sun shadows: the world geometry re-drawn flattened onto the ground
        // along the sun direction, dark + alpha-blended, depth-tested but not written.
        // Reuses cube.vert (instanced) with a flat shadow.frag.
        var shadowShader = vk.CreateShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "cube.vert.spv")),
            File.ReadAllBytes(Path.Combine(shaderDir, "shadow.frag.spv")),
            iface, "shadow");
        var shadowPipeline = vk.CreatePipeline(
            new PipelineDescription(shadowShader, meshLayout, PrimitiveTopology.Triangles,
                DepthState.LessEqualNoWrite, RasterizerState.NoCulling, new[] { BlendState.AlphaBlend }),
            "shadow");
        shadowInstances = new InstanceBuffer(vk, shadowShader, "shadows");
        shadowBatch = new InstancedBatch(cube, shadowPipeline, shadowInstances);
        batch = new InstancedBatch(cube, pipeline, instanceBuffer);
        tunables = new ObjectTunables(feel);

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
        recoilVel = Vector3.Zero;
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
        var targetSpeed = (held.Contains(Key.W) ? feel.DriveSpeed : 0f) - (held.Contains(Key.S) ? feel.ReverseSpeed : 0f);
        var rate = MathF.Abs(targetSpeed) > MathF.Abs(playerSpeed) ? feel.DriveAccel : feel.DriveDecel;
        playerSpeed = MoveToward(playerSpeed, targetSpeed, rate * dt);

        // Steering is coupled to motion: full turn rate while driving, only a slow
        // pivot when parked — so the tank carves a turn radius rather than spinning
        // on a dime. (speedFrac scales the available yaw rate with current speed.)
        var steer = (held.Contains(Key.A) ? 1f : 0f) - (held.Contains(Key.D) ? 1f : 0f);
        var speedFrac = MathF.Min(1f, MathF.Abs(playerSpeed) / feel.DriveSpeed);
        player.HullYaw += steer * feel.TurnSpeed * (PivotFactor + (1f - PivotFactor) * speedFrac) * dt;

        if (held.Contains(Key.Left)) player.TurretYaw += feel.TurretSpeed * dt;
        if (held.Contains(Key.Right)) player.TurretYaw -= feel.TurretSpeed * dt;
        // Clamp the turret to a forward arc (no 360 spin) so the gun — and the
        // turret-follow camera — stay anchored to where the hull faces.
        player.TurretYaw = Math.Clamp(player.TurretYaw, -feel.TurretLimit, feel.TurretLimit);
        // Up/Down elevate the gun — higher pitch lobs the shell further (range control).
        if (held.Contains(Key.Up)) player.BarrelPitch += feel.PitchSpeed * dt;
        if (held.Contains(Key.Down)) player.BarrelPitch -= feel.PitchSpeed * dt;
        player.BarrelPitch = Math.Clamp(player.BarrelPitch, 0f, feel.MaxPitch);
        player.Apply();

        // Drive sets horizontal velocity along the hull facing; gravity owns vertical.
        var forward = Vector3.Transform(-Vector3.UnitZ, player.Hull.Rotation);
        recoilVel *= MathF.Max(0f, 1f - RecoilDamp * dt);   // recoil shove fades out
        SetHorizontalVelocity(player, forward * playerSpeed + recoilVel);
        player.Physics.Gravity = new Vector3(0f, feel.TankGravity, 0f);   // live-tunable
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

        // Concurrent-enemy target is a live knob; 0 clears the arena. Despawn extras
        // immediately when it's lowered, spawn up to it otherwise.
        var target = (int)MathF.Round(feel.EnemyMax);
        while (enemies.Count > target) enemies.RemoveAt(enemies.Count - 1);
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
        var e = new Tank { Health = 1f, FireTimer = feel.EnemyReload * (0.4f + (float)rng.NextDouble()) };
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
        SetHorizontalVelocity(e, dist > EnemyStandoff ? dir * feel.EnemySpeed : Vector3.Zero);
        e.Physics.Gravity = new Vector3(0f, feel.TankGravity, 0f);
        e.Physics.FixedUpdate(new Time(time.Total, dt));
        ResolveTank(e);

        e.FireTimer -= dt;
        if (feel.EnemiesFire && dist < EnemyFireRange && e.FireTimer <= 0f)
        {
            Fire(e, fromPlayer: false);
        }
    }

    private void Fire(Tank tank, bool fromPlayer)
    {
        tank.FireTimer = fromPlayer ? feel.Reload : feel.EnemyReload;
        // Spawn the shell as a child of the barrel at the muzzle, then detach it into
        // world space keeping that pose — it leaves exactly where the barrel points.
        var shell = new Transform3D { Position = new Vector3(0f, 0f, -Tank.BarrelScale.Z), Parent = tank.Barrel };
        shell.SetParent(null, keepWorldPose: true);
        var physics = new PhysicsHost3D { Target = shell, Velocity = tank.BarrelForward * feel.MuzzleSpeed, GravityScale = 1f, Gravity = new Vector3(0f, feel.ShellGravity, 0f) };
        shells.Add(new Shell { Transform = shell, Physics = physics, FromPlayer = fromPlayer });

        // Knockback: shove the firing tank back along the gun's horizontal facing,
        // so shooting nudges the player (and can be used to reposition). Player only
        // — rides recoilVel, which the drive blends in + decays.
        if (fromPlayer)
        {
            var back = tank.BarrelForward;
            back.Y = 0f;
            if (back.LengthSquared() > 1e-4f) recoilVel -= Vector3.Normalize(back) * feel.Knockback;
        }
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
                health -= feel.EnemyDamage;
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
        camYaw += (aimYaw - camYaw) * MathF.Min(1f, feel.CamSmooth * (float)time.Delta);
        var camForward = Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, camYaw));
        var p = player.Position;
        var eye = p - camForward * feel.CamDistance + Vector3.UnitY * feel.CamHeight;
        var view = Matrix4x4.CreateLookAt(eye, p + Vector3.UnitY * 1.2f, Vector3.UnitY);
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3f, aspect, 0.3f, 400f);
        viewProj = view * proj;

        // Push payloads: viewProj/invViewProj + camera pos + sun dir, shared by the
        // world (lighting + fog) and the sky (view-ray reconstruction).
        var camPos = new Vector4(eye, 1f);
        var sun = new Vector4(SunDir, 0f);
        Matrix4x4.Invert(viewProj, out var invViewProj);
        MemoryMarshal.Write(pushBytes.AsSpan(0, 64), in viewProj);
        MemoryMarshal.Write(pushBytes.AsSpan(64, 16), in camPos);
        MemoryMarshal.Write(pushBytes.AsSpan(80, 16), in sun);
        MemoryMarshal.Write(skyPush.AsSpan(0, 64), in invViewProj);
        MemoryMarshal.Write(skyPush.AsSpan(64, 16), in camPos);
        MemoryMarshal.Write(skyPush.AsSpan(80, 16), in sun);

        batch.Begin(pushBytes);

        batch.Add(Matrix4x4.CreateScale(GroundScale) * Matrix4x4.CreateTranslation(0f, -0.1f, 0f),
            new Vector4(0.38f, 0.50f, 0.33f, 1f));   // grassy ground

        // Low perimeter walls for spatial reference (the tall colliders are invisible).
        const float w = ArenaHalf;
        var wallTint = new Vector4(0.62f, 0.50f, 0.36f, 1f);   // warm tan
        var span = 2f * w + 1f;
        AddBox(new Vector3(-w, 0.7f, 0f), new Vector3(1f, 1.4f, span), wallTint);
        AddBox(new Vector3(w, 0.7f, 0f), new Vector3(1f, 1.4f, span), wallTint);
        AddBox(new Vector3(0f, 0.7f, -w), new Vector3(span, 1.4f, 1f), wallTint);
        AddBox(new Vector3(0f, 0.7f, w), new Vector3(span, 1.4f, 1f), wallTint);

        AddTank(player, new Vector4(0.22f, 0.52f, 0.92f, 1f), new Vector4(0.30f, 0.60f, 0.98f, 1f));
        foreach (var e in enemies)
            AddTank(e, new Vector4(0.86f, 0.30f, 0.24f, 1f), new Vector4(0.94f, 0.40f, 0.30f, 1f));

        foreach (var s in shells)
        {
            var tint = s.FromPlayer ? new Vector4(0.80f, 0.92f, 1f, 1f) : new Vector4(1f, 0.62f, 0.25f, 1f);
            batch.Add(Matrix4x4.CreateScale(0.28f) * s.Transform.WorldMatrix, tint);
        }

        // Planar sun shadows: tank geometry flattened onto the ground along the sun
        // direction, drawn (blended) after the opaque world so they darken the ground.
        var shadowMatrix = ShadowMatrix(SunDir);
        var shadowTint = new Vector4(0f, 0f, 0f, feel.ShadowAlpha);
        shadowBatch.Begin(pushBytes);
        AddTankShadow(player, shadowMatrix, shadowTint);
        foreach (var e in enemies) AddTankShadow(e, shadowMatrix, shadowTint);

        commandList.Pass(
            "arena",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0.72f, 0.84f, 0.94f, 1f) },
                ClearDepth: true),
            pass =>
            {
                sky.Draw(pass, skyPipeline, Array.Empty<ShaderTextureBinding>(), skyPush);   // background
                batch.End(pass);                                                              // opaque world
                shadowBatch.End(pass);                                                        // shadows on top
            });

        if (exitAfterFrames > 0 && frameCount >= exitAfterFrames) host.RequestClose();
    }

    private void AddBox(Vector3 center, Vector3 scale, Vector4 tint) =>
        batch.Add(Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(center), tint);

    // The three drawable cube models for a tank (hull / turret / barrel-from-breach),
    // shared by the lit draw and the planar-shadow projection.
    private static (Matrix4x4 Hull, Matrix4x4 Turret, Matrix4x4 Barrel) TankCubeModels(Tank t) => (
        Matrix4x4.CreateScale(Tank.HullScale) * t.Hull.WorldMatrix,
        Matrix4x4.CreateScale(Tank.TurretScale) * t.Turret.WorldMatrix,
        Matrix4x4.CreateScale(Tank.BarrelScale) * Matrix4x4.CreateTranslation(0f, 0f, -Tank.BarrelScale.Z * 0.5f) * t.Barrel.WorldMatrix);

    private void AddTank(Tank t, Vector4 hullTint, Vector4 turretTint)
    {
        var (hull, turret, barrel) = TankCubeModels(t);
        batch.Add(hull, hullTint);
        batch.Add(turret, turretTint);
        batch.Add(barrel, new Vector4(0.30f, 0.31f, 0.34f, 1f));   // gunmetal
    }

    private void AddTankShadow(Tank t, Matrix4x4 shadow, Vector4 tint)
    {
        var (hull, turret, barrel) = TankCubeModels(t);
        shadowBatch.Add(hull * shadow, tint);
        shadowBatch.Add(turret * shadow, tint);
        shadowBatch.Add(barrel * shadow, tint);
    }

    // Planar projection that flattens world geometry onto the ground (y = ShadowLift)
    // along the sun's travel direction — row-vector form (v * realModel * shadow).
    private static Matrix4x4 ShadowMatrix(Vector3 sunDir)
    {
        var l = -sunDir;   // light travel direction (downward; l.Y < 0)
        return new Matrix4x4(
            1f, 0f, 0f, 0f,
            -l.X / l.Y, 0f, -l.Z / l.Y, 0f,
            0f, 0f, 1f, 0f,
            0f, ShadowLift, 0f, 1f);
    }

    public string DebugName => "tank-arena";

    public void Debug(DebugContext debug)
    {
        // The overlay renders only when Enabled (--debug); the backtick key toggles
        // ShowOverlay. When off, emit nothing.
        debug.State.Enabled = debugOverlay;
        if (!debugOverlay) return;

        tunables.BuildControls(debug);   // live [Tune] sliders for the feel knobs

        using (debug.Scope("arena"))
        {
            debug.Values.Value("hp", health);
            debug.Values.Value("score", score);
            debug.Values.Value("wave", wave);
            debug.Values.Value("enemies", enemies.Count);
            debug.Values.Value("shells", shells.Count);
            debug.Values.Value("speed", playerSpeed);
            debug.Values.Value("pitch", player.BarrelPitch);
        }
    }

    public void OnKeyDown(Key key)
    {
        held.Add(key);
        if (key == Key.Escape) host.RequestClose();
    }

    public void OnKeyUp(Key key) => held.Remove(key);
}
