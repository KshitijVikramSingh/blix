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
    private const float Speed = 16.0f;                      // world units / second

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

        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        UpdateCamera();
    }

    public void OnResize(int width, int height)
    {
        if (height > 0) aspect = width / (float)height;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        // Behind + above the origin, looking down the track into -Z.
        var eye = new Vector3(0f, 6.5f, 11f);
        var view = Matrix4x4.CreateLookAt(eye, new Vector3(0f, 1.0f, -10f), Vector3.UnitY);
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3f, aspect, 0.1f, 400f);
        viewProj = view * proj;
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;
        var t = (float)time.Total;
        var scroll = t * Speed;

        world.Begin(viewProj);
        EmitTiles(scroll);
        EmitObstaclesAndCoins(scroll, t);

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
    // stable frame-to-frame as it scrolls). Coins spin via per-instance rotation.
    private void EmitObstaclesAndCoins(float scroll, float t)
    {
        for (var seg = 0; seg < SegmentCount; seg++)
        {
            var h = Hash((uint)seg);
            var segZ = seg * SegmentSpacing;

            // One obstacle in a hashed lane.
            var obstacleLane = (int)(h % 3);
            var oz = WrapZ(segZ + scroll);
            world.Add(
                Matrix4x4.CreateScale(1.3f, 1.3f, 1.3f) *
                Matrix4x4.CreateTranslation(LaneX[obstacleLane], 0.65f, oz),
                ObstacleTint);

            // A run of coins in a different lane, spaced along the segment.
            var coinLane = (obstacleLane + 1 + (int)((h >> 3) & 1)) % 3;
            for (var c = 0; c < CoinsPerRun; c++)
            {
                var cz = WrapZ(segZ + scroll - c * 1.4f - 2f);
                var model =
                    Matrix4x4.CreateScale(0.35f) *
                    Matrix4x4.CreateRotationY(t * 3f + c) *
                    Matrix4x4.CreateTranslation(LaneX[coinLane], 1.0f, cz);
                world.Add(model, CoinTint);
            }
        }
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
        if (key == Key.Escape) host.RequestClose();
    }
}
