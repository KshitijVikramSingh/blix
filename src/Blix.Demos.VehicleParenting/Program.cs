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

namespace Blix.Demos.VehicleParenting;

// Transform-parenting showcase: a drivable tank whose hull -> turret -> barrel
// form a Transform3D hierarchy. The hull is the root (driven by input); the turret
// is its child (yaws independently); the barrel is the turret's child (pitches).
// Each part's WorldMatrix composes the chain, so turning the hull carries the
// turret + barrel with it while they keep their own local aim.
//
// Firing is the hero of the parenting model: a shell is spawned AS A CHILD of the
// barrel (at the muzzle, in barrel-local space) and then SetParent(null,
// keepWorldPose: true) detaches it into world space with its world pose preserved
// — so it leaves the muzzle exactly where the (moving, aimed) barrel points and
// flies a free gravity arc, instead of snapping to the barrel's local frame.
//
// Everything draws through one InstancedBatch: each part/target/shell is a unit
// cube instance whose model = scale(visual) * WorldMatrix and whose tint colours
// it. (The render-matrix convention — WorldMatrix fed straight to a `model * v`
// shader, no transpose — is pinned by Blix.Test.Graphics Section AH.)
public static class Program
{
    public static void Main(string[] args)
    {
        var exitAfterFrames = 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--frames" && int.TryParse(args[i + 1], out var n)) exitAfterFrames = n;
        }

        var loop = new VehicleLoop(exitAfterFrames);
        using var window = new Window(loop, new WindowOptions("Blix — Transform Parenting (vehicle + turret)", 1280, 720));
        window.Run();
    }
}

internal sealed class VehicleLoop : IGameLoop, IInputHandler
{
    // Visual box dimensions per part (the pose hierarchy itself stays unit-scaled so
    // a part's non-uniform box doesn't shear its children — the scale is applied only
    // at draw time, in the part's own local frame, via scale(visual) * WorldMatrix).
    private static readonly Vector3 HullScale = new(2.2f, 0.7f, 3.2f);
    private static readonly Vector3 TurretScale = new(1.3f, 0.6f, 1.3f);
    private static readonly Vector3 BarrelScale = new(0.24f, 0.24f, 1.8f);
    private const float ProjScale = 0.28f;
    private const float TargetScale = 1.0f;
    private static readonly Vector3 GroundScale = new(160f, 0.2f, 160f);

    private const float DriveSpeed = 9f;
    private const float TurnSpeed = 1.6f;
    private const float TurretSpeed = 1.8f;
    private const float PitchSpeed = 1.1f;
    private const float MinPitch = -0.25f, MaxPitch = 0.7f;
    private const float MuzzleSpeed = 34f;
    private const float FireCooldown = 0.28f;
    private const float HitRadius = 1.3f;
    private const float ProjLife = 6f;

    private readonly int exitAfterFrames;
    private VulkanGraphicsDevice vk = null!;
    private IRenderHost host = null!;
    private InstanceBuffer instanceBuffer = null!;
    private InstancedBatch batch = null!;
    private readonly byte[] pushBytes = new byte[64];

    // The transform hierarchy: hull (root) -> turret -> barrel.
    private readonly Transform3D hull = new();
    private readonly Transform3D turret = new();
    private readonly Transform3D barrel = new();
    private float hullYaw, turretYaw, barrelPitch;

    private sealed class Projectile
    {
        public required Transform3D Transform { get; init; }
        public required PhysicsHost3D Physics { get; init; }
        public float Age;
    }

    private readonly List<Projectile> projectiles = new();
    private readonly List<Transform3D> targets = new();
    private readonly bool[] targetAlive = new bool[8];
    private readonly HashSet<Key> held = new();
    private float fireTimer;
    private int score;
    private bool autoFired;

    private Matrix4x4 viewProj;
    private float aspect = 16f / 9f;
    private int frameCount;

    public VehicleLoop(int exitAfterFrames) => this.exitAfterFrames = exitAfterFrames;

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        vk = (VulkanGraphicsDevice)graphicsDevice;

        var vb = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(Cube.Vertices), "cube.vb");
        var ib = vk.CreateIndexBuffer(Cube.Indices, name: "cube.ib");
        var cube = new Mesh("cube", vb, ib, Cube.Indices.Length,
            new Bounds3(new Vector3(-0.5f), new Vector3(0.5f)));

        // Pipeline consumes position + normal (stride matches the cube's full vertex
        // so the unconsumed uv doesn't trip a validation warning).
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
        instanceBuffer = new InstanceBuffer(vk, shader, "vehicle");
        batch = new InstancedBatch(cube, pipeline, instanceBuffer);

        // Wire the hierarchy. Local positions stack the parts: turret on the hull
        // top, barrel forward of the turret (forward = -Z, the engine convention).
        hull.Position = new Vector3(0f, HullScale.Y * 0.5f, 0f);

        turret.Position = new Vector3(0f, HullScale.Y * 0.5f + TurretScale.Y * 0.5f, 0f);
        turret.Parent = hull;

        barrel.Position = new Vector3(0f, TurretScale.Y * 0.15f, -(TurretScale.Z * 0.5f + BarrelScale.Z * 0.5f));
        barrel.Parent = turret;

        // A ring of targets to shoot.
        for (var i = 0; i < targetAlive.Length; i++)
        {
            var a = i / (float)targetAlive.Length * MathF.Tau;
            targets.Add(new Transform3D { Position = new Vector3(MathF.Sin(a) * 22f, 0.5f, MathF.Cos(a) * 22f - 8f) });
            targetAlive[i] = true;
        }

        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
    }

    public void OnResize(int width, int height)
    {
        if (height > 0) aspect = width / (float)height;
    }

    public void OnUpdate(Time time)
    {
        var dt = (float)time.Delta;

        // Drive the hull (root). Its world pose carries turret + barrel along.
        var hullForward = Vector3.Transform(-Vector3.UnitZ, hull.Rotation);
        if (held.Contains(Key.W)) hull.Position += hullForward * DriveSpeed * dt;
        if (held.Contains(Key.S)) hull.Position -= hullForward * DriveSpeed * dt;
        if (held.Contains(Key.A)) hullYaw += TurnSpeed * dt;
        if (held.Contains(Key.D)) hullYaw -= TurnSpeed * dt;
        hull.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, hullYaw);

        // Aim the turret (local yaw) + pitch the barrel (local X), independent of the hull.
        if (held.Contains(Key.Left)) turretYaw += TurretSpeed * dt;
        if (held.Contains(Key.Right)) turretYaw -= TurretSpeed * dt;
        turret.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, turretYaw);

        if (held.Contains(Key.Up)) barrelPitch += PitchSpeed * dt;
        if (held.Contains(Key.Down)) barrelPitch -= PitchSpeed * dt;
        barrelPitch = Math.Clamp(barrelPitch, MinPitch, MaxPitch);
        barrel.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, barrelPitch);

        // Fire (held Space, rate-limited). Headless runs auto-fire once so the gate
        // exercises the spawn + detach + draw path.
        fireTimer -= dt;
        if (held.Contains(Key.Space) && fireTimer <= 0f) Fire();
        if (exitAfterFrames > 0 && !autoFired && time.Total > 0.0) { Fire(); autoFired = true; }

        // Integrate shells (gravity arc) and retire spent ones.
        for (var i = projectiles.Count - 1; i >= 0; i--)
        {
            var p = projectiles[i];
            p.Physics.FixedUpdate(new Time(time.Total, dt));
            p.Age += dt;
            if (p.Age > ProjLife || p.Transform.Position.Y < -1f) { projectiles.RemoveAt(i); continue; }

            for (var ti = 0; ti < targets.Count; ti++)
            {
                if (!targetAlive[ti]) continue;
                if (Vector3.Distance(p.Transform.WorldPosition, targets[ti].Position) < HitRadius)
                {
                    targetAlive[ti] = false;
                    projectiles.RemoveAt(i);
                    score++;
                    host.SetTitle($"Blix — Transform Parenting | hits: {score}");
                    break;
                }
            }
        }
    }

    // Spawn a shell at the muzzle as a CHILD of the barrel, then detach it into world
    // space keeping that world pose — the parenting-model centrepiece. It then flies
    // along the barrel's world-forward under gravity.
    private void Fire()
    {
        fireTimer = FireCooldown;
        var shell = new Transform3D { Position = new Vector3(0f, 0f, -(BarrelScale.Z * 0.5f)), Parent = barrel };
        shell.SetParent(null, keepWorldPose: true);
        var worldForward = Vector3.Transform(-Vector3.UnitZ, barrel.WorldRotation);
        var physics = new PhysicsHost3D { Target = shell, Velocity = worldForward * MuzzleSpeed, GravityScale = 1f };
        projectiles.Add(new Projectile { Transform = shell, Physics = physics });
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;

        // Chase camera trailing the hull's world pose.
        var hullPos = hull.WorldPosition;
        var fwd = Vector3.Transform(-Vector3.UnitZ, hull.Rotation);
        var eye = hullPos - fwd * 13f + Vector3.UnitY * 7f;
        var view = Matrix4x4.CreateLookAt(eye, hullPos + Vector3.UnitY * 1.5f, Vector3.UnitY);
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3f, aspect, 0.3f, 400f);
        viewProj = view * proj;

        MemoryMarshal.Write(pushBytes.AsSpan(0, 64), in viewProj);
        batch.Begin(pushBytes);

        // Ground.
        batch.Add(Matrix4x4.CreateScale(GroundScale) * Matrix4x4.CreateTranslation(0f, -0.1f, 0f),
            new Vector4(0.12f, 0.14f, 0.18f, 1f));

        // The vehicle: each part's box = scale(visual) * its WorldMatrix.
        batch.Add(Matrix4x4.CreateScale(HullScale) * hull.WorldMatrix, new Vector4(0.30f, 0.45f, 0.30f, 1f));
        batch.Add(Matrix4x4.CreateScale(TurretScale) * turret.WorldMatrix, new Vector4(0.38f, 0.52f, 0.36f, 1f));
        batch.Add(Matrix4x4.CreateScale(BarrelScale) * barrel.WorldMatrix, new Vector4(0.22f, 0.24f, 0.22f, 1f));

        // Targets (dimmed once hit) + shells.
        for (var i = 0; i < targets.Count; i++)
        {
            var tint = targetAlive[i] ? new Vector4(0.8f, 0.3f, 0.25f, 1f) : new Vector4(0.2f, 0.2f, 0.2f, 1f);
            batch.Add(Matrix4x4.CreateScale(TargetScale) * targets[i].WorldMatrix, tint);
        }
        foreach (var p in projectiles)
            batch.Add(Matrix4x4.CreateScale(ProjScale) * p.Transform.WorldMatrix, new Vector4(1f, 0.85f, 0.3f, 1f));

        commandList.Pass(
            "vehicle",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0.05f, 0.06f, 0.09f, 1f) },
                ClearDepth: true),
            pass => batch.End(pass));

        if (exitAfterFrames > 0 && frameCount >= exitAfterFrames) host.RequestClose();
    }

    // No OnUnload disposal: RequestClose fires Closing before the recorded final
    // draw executes, so freeing the batch/pipeline here would pull it out from under
    // that Execute (the VulkanInstanced/runner lesson). device.Dispose() waits idle
    // and frees every resource table at teardown.

    public void OnKeyDown(Key key)
    {
        held.Add(key);
        if (key == Key.Escape) host.RequestClose();
    }

    public void OnKeyUp(Key key) => held.Remove(key);
}
