using System.Numerics;
using Blix;
using Blix.Core;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Primitives;
using Blix.Graphics.Vulkan;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Demos.Runner;

// Blix 3D endless runner. Milestone 2: the treadmill world — a scrolling,
// recycling track of ground tiles, obstacles, and coins, all drawn through a
// single instanced draw (InstancedBatch over one shared cube mesh). The world
// flows toward the camera (+Z) and wraps procedurally; coins spin via their
// per-instance transform. Player controller, collision, and presentation come
// in later milestones.
public static class Program
{
    public static void Main(string[] args)
    {
        var exitAfterFrames = 0; // 0 = interactive; --frames N for the headless gate
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--frames" && int.TryParse(args[i + 1], out var n)) exitAfterFrames = n;
        }

        var loop = new RunnerLoop(exitAfterFrames);
        using var window = new Window(loop, new WindowOptions("Blix — Endless Runner", 1280, 720));
        window.Run();
    }
}

internal sealed class RunnerLoop : IGameLoop, IInputHandler
{
    // --- Track geometry -----------------------------------------------------
    private static readonly float[] LaneX = { -2.2f, 0f, 2.2f };
    private const float TrackWidth = 7.0f;
    private const float TileLength = 4.0f;
    private const int TileCount = 40;                       // 40 * 4 = 160 units of track
    private const float TrackLength = TileCount * TileLength;
    private const float SegmentSpacing = 8.0f;              // obstacle/coin cadence
    private const int SegmentCount = (int)(TrackLength / SegmentSpacing);
    private const int CoinsPerRun = 5;

    // Recycle window: objects flow toward +Z and wrap once they pass behind the
    // camera. zMax sits just behind the camera; zMin is one track-length ahead.
    private const float RecycleZ = 14.0f;
    private const float BaseSpeed = 14.0f;                  // world units / second
    private const float MaxSpeed = 34.0f;
    private const float SpeedRampPerMetre = 0.02f;

    // Tints
    private static readonly Vector4 TileA = new(0.28f, 0.30f, 0.36f, 1f);
    private static readonly Vector4 TileB = new(0.34f, 0.37f, 0.44f, 1f);
    private static readonly Vector4 ObstacleTint = new(0.85f, 0.25f, 0.22f, 1f);
    private static readonly Vector4 CoinTint = new(0.95f, 0.78f, 0.20f, 1f);

    private readonly int exitAfterFrames;
    private VulkanGraphicsDevice vk = null!;
    private IRenderHost host = null!;
    private InstancedBatch world = null!;

    private Matrix4x4 viewProj;
    private float aspect = 16f / 9f;
    private int frameCount;

    // --- Player (kinematic): lane-lerp on X, PhysicsHost3D gravity on Y --------
    private readonly Transform3D player = new();
    private PhysicsHost3D physics = null!;
    private int laneIndex = 1;        // start centre lane
    private float currentX;
    private bool grounded = true;
    private const float JumpSpeed = 17f;
    private const float PlayerHalfHeight = 0.8f;
    private static readonly Vector4 PlayerTint = new(0.30f, 0.85f, 0.95f, 1f);
    private static readonly Vector4 PlayerDeadTint = new(0.45f, 0.45f, 0.50f, 1f);

    // --- Game state -----------------------------------------------------------
    private float scrollDistance;     // stateful so it freezes on game-over
    private float currentSpeed = BaseSpeed;
    private int coins;
    private bool gameOver;

    // Coins are procedural (recomputed each frame from scrollDistance); collected
    // ones are remembered by a lap-stable key so they stay gone for that lap but
    // return fresh on the next lap. Obstacles need no identity (hitting one ends
    // the run).
    private readonly HashSet<long> collectedKeys = new();
    private readonly record struct Collider(bool Obstacle, long CoinKey);
    private readonly CollisionWorld3D<Collider> collision = new();
    private readonly List<CollisionContact3D<Collider>> hits = new();

    // Spawn lists rebuilt each frame and shared by render + collision so the two
    // can never disagree about where an obstacle/coin is.
    private readonly List<Vector3> obstacles = new();
    private readonly List<(Vector3 Pos, long Key)> coinSpawns = new();

    public RunnerLoop(int exitAfterFrames) => this.exitAfterFrames = exitAfterFrames;

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        vk = (VulkanGraphicsDevice)graphicsDevice;

        var vb = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(Cube.Vertices), "cube.vb");
        var ib = vk.CreateIndexBuffer(Cube.Indices, name: "cube.ib");
        var cube = new Mesh("cube", vb, ib, Cube.Indices.Length,
            new Bounds3(new Vector3(-0.5f), new Vector3(0.5f)));
        world = new InstancedBatch(vk, cube);

        physics = new PhysicsHost3D { Target = player, Gravity = new Vector3(0f, -55f, 0f), GravityScale = 1f };
        player.Position = new Vector3(LaneX[laneIndex], 0f, 0f);
        currentX = LaneX[laneIndex];

        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        UpdateCamera();
    }

    public void OnUpdate(Time time)
    {
        var dt = (float)time.Delta;

        if (!gameOver)
        {
            // Forward motion + difficulty ramp (frozen once the run ends).
            currentSpeed = MathF.Min(MaxSpeed, BaseSpeed + scrollDistance * SpeedRampPerMetre);
            scrollDistance += currentSpeed * dt;

            // Vertical: gravity + jump impulse via PhysicsHost3D, clamped to the
            // ground plane (y = 0). Velocity.X/Z stay 0 so only Y is physics-driven.
            physics.FixedUpdate(time);
            var y = player.Position.Y;
            if (y <= 0f)
            {
                y = 0f;
                physics.Velocity = Vector3.Zero;
                grounded = true;
            }

            // Lateral: frame-rate-independent lerp toward the active lane.
            currentX += (LaneX[laneIndex] - currentX) * (1f - MathF.Exp(-12f * dt));
            player.Position = new Vector3(currentX, y, 0f);
        }

        BuildSpawns(scrollDistance);
        if (!gameOver) ResolveCollisions();
        UpdateCamera();
        UpdateTitle();
    }

    // Player sphere vs every obstacle/coin via CollisionWorld3D.Overlap. Obstacle
    // contact ends the run; coin contact scores once (the lap-stable key dedupes
    // repeat hits while the coin sits in the overlap zone across frames).
    private void ResolveCollisions()
    {
        collision.Clear();
        foreach (var o in obstacles) collision.Add(new Collider(true, 0), new BoundingSphere(o, 0.9f));
        foreach (var (pos, key) in coinSpawns) collision.Add(new Collider(false, key), new BoundingSphere(pos, 0.45f));

        var playerSphere = new BoundingSphere(new Vector3(currentX, player.Position.Y + PlayerHalfHeight, 0f), 0.6f);
        hits.Clear();
        collision.Overlap(playerSphere, hits);

        foreach (var hit in hits)
        {
            if (hit.Owner.Obstacle)
            {
                gameOver = true;
                return;
            }
            if (collectedKeys.Add(hit.Owner.CoinKey)) coins++;
        }
    }

    private void UpdateTitle() => host.SetTitle(gameOver
        ? $"Blix Runner — {coins} coins — {(int)scrollDistance} m — GAME OVER (Enter to restart)"
        : $"Blix Runner — {coins} coins — {(int)scrollDistance} m");

    private void Restart()
    {
        scrollDistance = 0f;
        currentSpeed = BaseSpeed;
        coins = 0;
        gameOver = false;
        laneIndex = 1;
        currentX = LaneX[laneIndex];
        grounded = true;
        physics.Velocity = Vector3.Zero;
        player.Position = new Vector3(currentX, 0f, 0f);
        collectedKeys.Clear();
    }

    public void OnResize(int width, int height)
    {
        if (height > 0) aspect = width / (float)height;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        // Behind + above the player, panning partway with its lane so switches
        // read clearly without locking the camera rigidly to the player's X.
        var eye = new Vector3(currentX * 0.5f, 6.5f, 11f);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(currentX, 1.0f, -10f), Vector3.UnitY);
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3f, aspect, 0.1f, 400f);
        viewProj = view * proj;
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;
        var t = (float)time.Total;

        world.Begin(viewProj);
        EmitTiles(scrollDistance);
        EmitSpawns(t);
        EmitPlayer();

        commandList.Pass(
            "runner-world",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0.07f, 0.09f, 0.13f, 1f) },
                ClearDepth: true),
            pass => world.End(pass));

        if (exitAfterFrames > 0 && frameCount >= exitAfterFrames) host.RequestClose();
    }

    // Contiguous scrolling ground: one wide, thin tile per track segment.
    private void EmitTiles(float scroll)
    {
        for (var i = 0; i < TileCount; i++)
        {
            var z = WrapZ(i * TileLength + scroll);
            var model =
                Matrix4x4.CreateScale(TrackWidth, 0.5f, TileLength) *
                Matrix4x4.CreateTranslation(0f, -0.25f, z);
            world.Add(model, (i & 1) == 0 ? TileA : TileB);
        }
    }

    // Deterministic obstacle + coin layout per segment (no RNG so the field is
    // stable as it scrolls). Fills the shared spawn lists used by both render and
    // collision. Collected coins (lap-stable key) are skipped so they vanish.
    private void BuildSpawns(float scroll)
    {
        obstacles.Clear();
        coinSpawns.Clear();
        for (var seg = 0; seg < SegmentCount; seg++)
        {
            var h = Hash((uint)seg);
            var segZ = seg * SegmentSpacing;

            var obstacleLane = (int)(h % 3);
            obstacles.Add(new Vector3(LaneX[obstacleLane], 0.65f, WrapZ(segZ + scroll)));

            var coinLane = (obstacleLane + 1 + (int)((h >> 3) & 1)) % 3;
            for (var c = 0; c < CoinsPerRun; c++)
            {
                var raw = segZ + scroll - c * 1.4f - 2f;
                var lap = (int)MathF.Floor((raw - RecycleZ) / TrackLength);
                var key = ((long)(lap + 1024)) * 1_000_000 + seg * 100 + c;
                if (collectedKeys.Contains(key)) continue;
                coinSpawns.Add((new Vector3(LaneX[coinLane], 1.0f, WrapZ(raw)), key));
            }
        }
    }

    // Render obstacles + coins from the spawn lists. Coins spin via their
    // per-instance transform (a live read of the per-instance SSBO).
    private void EmitSpawns(float t)
    {
        foreach (var o in obstacles)
            world.Add(Matrix4x4.CreateScale(1.3f) * Matrix4x4.CreateTranslation(o), ObstacleTint);
        foreach (var (pos, _) in coinSpawns)
            world.Add(
                Matrix4x4.CreateScale(0.35f) * Matrix4x4.CreateRotationY(t * 3f) * Matrix4x4.CreateTranslation(pos),
                CoinTint);
    }

    // Player box (placeholder until the animated glTF character lands in M5).
    // Drawn through the same instanced batch as the world — one draw for everything.
    private void EmitPlayer()
    {
        world.Add(
            Matrix4x4.CreateScale(0.8f, PlayerHalfHeight * 2f, 0.8f) *
            Matrix4x4.CreateTranslation(player.Position.X, player.Position.Y + PlayerHalfHeight, 0f),
            gameOver ? PlayerDeadTint : PlayerTint);
    }

    // Map a raw advancing z into the recycle window [RecycleZ - TrackLength, RecycleZ).
    private static float WrapZ(float z)
    {
        var m = (z - RecycleZ) % TrackLength;
        if (m > 0) m -= TrackLength; // ((z-RecycleZ) mod L) in (-L, 0]
        return RecycleZ + m;
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return x;
    }

    public void OnKeyDown(Key key)
    {
        if (key == Key.Escape) { host.RequestClose(); return; }

        if (gameOver)
        {
            if (key is Key.Enter or Key.R) Restart();
            return;
        }

        switch (key)
        {
            case Key.Left or Key.A:
                laneIndex = Math.Max(0, laneIndex - 1);
                break;
            case Key.Right or Key.D:
                laneIndex = Math.Min(LaneX.Length - 1, laneIndex + 1);
                break;
            case Key.Up or Key.W or Key.Space:
                if (grounded) { physics.Velocity = new Vector3(0f, JumpSpeed, 0f); grounded = false; }
                break;
        }
    }
}
