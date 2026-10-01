using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Primitives;
using Blix.Render;
using Blix.Runtime.Silk;

namespace Blix.Demos.TankArena;

// Tank Arena — a survival shooter built on Transform3D parenting. Each tank is a
// hull -> turret -> barrel hierarchy (see Tank): driving the hull carries the
// turret + barrel, while the turret yaws to aim independently. The player drives
// and aims; enemy tanks roll in from the arena edges, steer around cover toward the
// player, track them with their turrets, and fire. Shells spawn as a child of the
// firing barrel and then SetParent(null, keepWorldPose) detaches them into a
// PhysicsHost3D arc — the parenting model's detach op.
//
// Rendering: tanks are the articulated Quaternius glTF model (per-part meshes on the
// rig, see LoadTankParts); barrels/crates are static props tinted by their material
// colours and act as cover; the ground, walls, shells and explosion sparks draw as
// cube instances. Everything goes through InstancedBatch into a RenderGraph sun-shadow
// + HDR pipeline. Enemy navigation is obstacle-avoidance steering (AvoidObstacles) —
// the first consumer pressuring a navigation primitive.
//
// ── Executable spec for (engine primitives this demo proves) ──
//   • Transform3D parenting: hull → turret → barrel compose + WorldPosition muzzle
//   • SetParent(null, keepWorldPose) — the shell detach-and-fly op
//   • glTF ImportNodes → measured-pivot rig fit (`blix inspect` workflow)
//   • RenderGraph sun-shadow + HDR, per-part InstancedBatch on one shared pipeline
// ── Intentionally owns (stays local; don't extract until a 2nd consumer needs it) ──
//   • enemy AI, obstacle-avoidance steering, turret tracking, combat-feel tuning
//   • arena rules, spawn waves, shell lifetime, props-as-cover
//   • AvoidObstacles is the FIRST pressure on a nav primitive — not yet a primitive
public static class Program
{
    public static int Main(string[] args) => BlixApps.Main(args, Run);

    private static int Run(AppArgs args)
    {
        // --debug opens with the live tuning + diagnostics overlay showing; ` toggles it either way.
        var options = WindowOptions.FromArgs(args, new WindowOptions("Blix — Tank Arena", 1280, 720));
        var loop = new TankArenaLoop(options.ExitAfterFrames);
        using var window = new Window(loop, options);
        window.Run();
        return 0;
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
    [Tune(6f, 30f)]    public float CamDistance = 6f;
    [Tune(3f, 22f)]    public float CamHeight = 3f;
    [Tune(1f, 14f)]    public float CamSmooth = 1f;

    // Enemy knobs — turn them down/off to tune in peace.
    [Tune(0f, 8f)]     public float EnemyMax = 1f;       // 0 clears the arena
    [Tune]             public bool EnemiesFire = true;   // off = present but harmless
    [Tune(1.5f, 9f)]   public float EnemySpeed = 4.5f;
    [Tune(0.8f, 5f)]   public float EnemyTurn = 2.2f;    // hull turn rate (rad/s) — smooths steering
    [Tune(0.5f, 6f)]   public float EnemyReload = 2.8f;
    [Tune(2f, 40f)]    public float EnemyDamage = 18f;
}

// Live-tunable fit for the glTF tank model -> game rig. Screenshots don't work, so
// these are dialled in the --debug overlay until the model sits right: GlobalScale
// maps the asset's model-world units (the Quaternius tank is ~14 units long) into
// game units; YawFix rotates the asset's -X forward onto the engine's -Z forward
// (-90°); ModelLift seats the chassis on the ground. The part pivots/offsets come
// from the asset's measured node transforms, so only these three knobs remain.
internal sealed class TankModelFit
{
    [Tune(0.15f, 1.2f)]   public float GlobalScale = 0.4f;
    [Tune(-3.15f, 3.15f)] public float YawFix = -1.5708f;   // -90°: model -X -> engine -Z
    [Tune(-3f, 3f)]       public float ModelLift = -0.15f;  // vertical seat onto the ground
}

// Live-tunable combat-feel "juice" — screen shake (trauma model), gun recoil kick,
// and burst sizes. Read each frame so the punch can be dialled while playing.
internal sealed class TankJuice
{
    // Screen shake: events add trauma [0,1]; the offset is trauma² so it eases out.
    [Tune(0f, 1.5f)] public float ShakeFire = 0.16f;       // firing your own gun
    [Tune(0f, 1.5f)] public float ShakeHit = 0.55f;        // taking a shell
    [Tune(0f, 1.5f)] public float ShakeExplosion = 0.8f;   // barrel blast (scaled by proximity)
    [Tune(0f, 3f)]   public float ShakeAmount = 0.9f;      // world-units of camera offset at full trauma
    [Tune(1f, 8f)]   public float TraumaDecay = 2.6f;      // trauma units shed per second

    // Gun recoil: the barrel slides back into the turret on firing, then recovers.
    [Tune(0f, 1f)]   public float Recoil = 0.5f;           // slide-back distance (game units)
    [Tune(4f, 30f)]  public float RecoilRecover = 9f;      // exponential recover rate

    // Particle burst sizes (debris counts).
    [Tune(8f, 60f)]  public float ExplosionDebris = 30f;
    [Tune(6f, 40f)]  public float DeathDebris = 18f;
}

// One tank: the hull -> turret -> barrel transform hierarchy plus its combat state.
// Local yaws drive the parts; WorldMatrix composes the chain for rendering + aim.
internal sealed class Tank
{
    // Collision/gameplay chassis footprint (width, height, length) — sized to the
    // tank model at the default GlobalScale. The visual model is taller (turret);
    // this is just the box that rests on the ground and slides along walls.
    public static readonly Vector3 HullScale = new(3.6f, 1.4f, 5.0f);
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
    public float GunRecoil;     // 0..1 visual recoil, kicked to 1 on firing, decays

    public Tank()
    {
        Physics = new PhysicsHost3D { Target = Hull, GravityScale = 1f, Gravity = new Vector3(0f, GravityValue, 0f) };
        Hull.Position = new Vector3(0f, HullScale.Y * 0.5f, 0f);
        // Turret/Barrel local positions are the model's yaw/pitch pivots, seated each
        // frame from the live fit (TankArenaLoop.SeatRig) — the asset measures them.
        Turret.Parent = Hull;
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

internal sealed class TankArenaLoop : IGameLoop, IDebuggable, IDisposable
{
    private const float ArenaHalf = 42f;
    private const float PivotFactor = 0f;       // no in-place spin — must be moving to turn
    private const float ShellLife = 6f;
    private const float HitRadius = 2.8f;       // covers the (larger) model hull; forgiving for lobs
    private const float EnemyStandoff = 12f;
    private const float EnemyFireRange = 28f;
    private const float PlayerMaxHealth = 100f;
    private static readonly Vector3 GroundScale = new(2f * ArenaHalf, 0.2f, 2f * ArenaHalf);

    // Live-tunable feel knobs (movement / camera / turning / ballistics), exposed in
    // the --debug overlay. Read each frame so dragging a slider updates the game live.
    private readonly TankFeel feel = new();
    private readonly TankModelFit fit = new();
    private readonly TankJuice juice = new();
    private ObjectTunables tunables = null!;
    private float trauma;   // screen-shake accumulator [0,1], decays each frame

    // Model-space pivots measured from tank.glb's node transforms (model-world units;
    // the asset's ×100 node scale is baked into the part meshes at load). The rig's
    // turret/barrel local positions are these offsets mapped through the fit each
    // frame, so the rendered parts and the gameplay rig stay locked together.
    private static readonly Vector3 HullPivotModel = new(0f, 1.3f, -0.07f);
    private static readonly Vector3 TurretPivotModel = new(1.55f, 3.72f, -0.04f);
    private static readonly Vector3 GunPivotModel = new(-0.54f, 3.9f, -0.07f);
    private static readonly Vector3 TurretOffsetModel = TurretPivotModel - HullPivotModel;
    private static readonly Vector3 GunOffsetModel = GunPivotModel - TurretPivotModel;
    private const float GunLengthModel = 9.3f;   // breach -> muzzle, for shell spawn

    // Which rig transform a tank part rides. Hull carries body + tracks; Turret yaws
    // the turret mesh; Barrel pitches the gun.
    private enum Attach { Hull, Turret, Barrel }

    // One drawable tank part: a model-space mesh plus its world + shadow-caster
    // batches. FixedTint null => the part takes the tank's team colour (body/turret);
    // otherwise a constant (tracks, gun).
    private sealed class TankPart
    {
        public required Attach Attach;
        public required Vector4? FixedTint;
        public required Mesh Mesh;               // the part's merged geometry, freed in OnUnload
        public required InstancedBatch World;
        public required InstancedBatch Caster;
        public required InstanceBuffer WorldInstances;
        public required InstanceBuffer CasterInstances;
    }

    private readonly List<TankPart> tankParts = new();

    // Static environment props (barrels / crates) — cover that blocks driving and
    // flat shots, and casts shadows. A PropType is one loaded model (one mesh + its
    // material colour per primitive); placements share it. Unlike the tank's gameplay
    // team tints, props keep their authored material colours (the world shader is
    // tint×lighting, so per-primitive BaseColorFactor reproduces the model's look).
    private sealed class PropType
    {
        public required string Name;
        public required float Scale;          // model units -> game
        public required float Radius;         // game-space horizontal half-extent (cover footprint)
        public required float Height;         // game-space height (shells lob over the top)
        public required bool Explosive;       // detonates on hit: AoE damage + chain
        // <b>The per-primitive batches, instance buffers and tints used to live here by hand.</b>
        // A prop is one model split into a primitive per material, so every part has to be handed
        // the SAME instance list -- and keeping four lists in step by hand is how a lid ends up on
        // a different barrel from its body. PropModel owns exactly that, was written for this
        // shape, and this game is one of the two whose private version motivated it.
        public required PropModel Model;
    }
    // A placed prop. Destructible (Alive) so an exploding barrel can be removed from
    // render + collision when it detonates; OwnerId is its collision-world handle.
    private sealed class Prop
    {
        public required PropType Type;
        public required Vector3 Position;
        public required float Yaw;
        public required Bounds3 Bounds;
        public required int OwnerId;
        public bool Alive = true;
    }

    private readonly List<PropType> propTypes = new();
    private readonly List<Prop> props = new();

    // VFX debris — short-lived cubes drawn through the world cube batch (no separate
    // pipeline). Pure visuals: no collision, no damage. Used for muzzle flash, impact
    // dust, enemy death and barrel explosions; each carries its own colour/size/life.
    private sealed class Spark { public Vector3 Pos; public Vector3 Vel; public float Age; public float Life; public Vector4 Tint; public float Size; }
    private readonly List<Spark> sparks = new();

    private const float ExplosionRadius = 6.5f;       // AoE kill/damage radius
    private const float ExplosionChainRadius = 5.5f;  // detonates nearby explosive barrels
    private const float ExplosionDamage = 38f;        // to the player at the epicentre (linear falloff)
    private const float ExplosionKnockback = 10f;     // blast shove on the player

    private readonly int exitAfterFrames;
    private IGraphicsDevice device = null!;
    private IRenderHost host = null!;

    // What the devices did this tick. Safe to read from any helper the update calls, because it
    // does not move until the next one — which is the whole reason the runtime holds it still.
    private IInputState Input => host.Input;
    private InstanceBuffer instanceBuffer = null!;
    private Mesh cube = null!;
    private readonly List<ShaderProgramHandle> ownedPrograms = new();
    private InstancedBatch batch = null!;               // lit world (ground/walls/tanks/shells)
    private InstanceBuffer casterInstances = null!;
    private InstancedBatch casterBatch = null!;         // depth-only shadow casters
    private FullscreenPass fullscreen = null!;          // sky background + present blit
    private RenderGraph graph = null!;
    private GraphResourceHandle hdrHandle, sceneDepthHandle, sunShadowHandle;
    private PassHandle shadowPassHandle, scenePassHandle;
    private PipelineHandle skyPipeline, worldPipeline, casterPipeline, presentPipeline;
    // Reused every frame. The layouts are the shaders', written by the generator below.
    private readonly byte[] worldPush = new byte[WorldPush.SizeInBytes];
    private readonly byte[] skyPush = new byte[SkyPush.SizeInBytes];
    private readonly byte[] shadowPush = new byte[ShadowPush.SizeInBytes];
    private Matrix4x4 sunShadowVP;
    private static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(0.35f, 0.82f, 0.45f));
    private const int ShadowMapSize = 2048;
    private const float SunDistance = 120f;             // light eye height up the sun direction
    private const float SunOrthoExtent = 150f;          // ortho covering the arena footprint

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

    public TankArenaLoop(int exitAfterFrames)
    {
        this.exitAfterFrames = exitAfterFrames;
    }

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        device = graphicsDevice;

        var vb = device.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(Cube.Vertices), "cube.vb");
        var ib = device.CreateIndexBuffer(Cube.Indices, name: "cube.ib");
        cube = new Mesh("cube", vb, ib, Cube.Indices.Length,
            new Bounds3(new Vector3(-0.5f), new Vector3(0.5f)), VertexPosition3NormalTexture.Layout);

        var meshLayout = new VertexLayout(
            Stride: VertexPosition3NormalTexture.Layout.Stride,
            Attributes: new[]
            {
                new VertexAttribute(0, VertexAttributeFormat.Float3, 0),
                new VertexAttribute(1, VertexAttributeFormat.Float3, 3 * sizeof(float)),
            });
        var shaderDir = AppFiles.Shaders;
        Func<string, byte[]> spv = name => File.ReadAllBytes(Path.Combine(shaderDir, name));

        // --- Render graph: sun shadow depth pass -> HDR scene pass -> present -------
        graph = new RenderGraph(device);
        var fullSize = new MatchSwapchainGraphSize(1.0f);
        sunShadowHandle = graph.DepthTarget("sun-shadow", new FixedGraphSize(ShadowMapSize, ShadowMapSize));
        hdrHandle = graph.ColorTarget("hdr", TextureFormat.Rgba16F, fullSize);
        sceneDepthHandle = graph.DepthTarget("scene-depth", fullSize);

        // Each interface is read from its shaders. InstanceBuffer supplies the instance count,
        // which is the one part of the set-3 block the shader leaves unsized.
        var casterIface = InstanceBuffer.Size(ShaderReflection.ForProgram(shaderDir, "shadow_caster.vert", "shadow_caster.frag"));
        var worldIface = InstanceBuffer.Size(ShaderReflection.ForProgram(shaderDir, "cube.vert", "cube.frag"));
        var skyIface = ShaderReflection.ForProgram(shaderDir, "sky.vert", "sky.frag");
        var presentIface = ShaderReflection.ForProgram(shaderDir, "present.vert", "present.frag");

        shadowPassHandle = graph.GraphicsPass("sun-shadow")
            .Depth(sunShadowHandle, LoadOp.Clear, StoreOp.Store)
            .Shader(casterIface)
            .Handle;
        scenePassHandle = graph.GraphicsPass("scene")
            .Target(hdrHandle, LoadOp.Clear, StoreOp.Store)
            .Depth(sceneDepthHandle, LoadOp.Clear, StoreOp.Store)
            .Read(sunShadowHandle)
            .Shader(skyIface, worldIface)
            .Handle;
        graph.Compile();

        // Pipelines (after Compile; world/sky/caster target their pass surfaces).
        var casterShader = device.CreateShaderProgramFromSpv(spv("shadow_caster.vert.spv"), spv("shadow_caster.frag.spv"), casterIface, "caster");
        ownedPrograms.Add(casterShader);
        casterPipeline = device.CreatePipeline(new PipelineDescription(casterShader, meshLayout, PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite, RasterizerState.NoCulling, Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(shadowPassHandle)), "caster");
        casterInstances = new InstanceBuffer(device, casterShader, "casters");
        casterBatch = new InstancedBatch(cube, casterPipeline, casterInstances);

        var worldShader = device.CreateShaderProgramFromSpv(spv("cube.vert.spv"), spv("cube.frag.spv"), worldIface, "world");
        ownedPrograms.Add(worldShader);
        worldPipeline = device.CreatePipeline(new PipelineDescription(worldShader, meshLayout, PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite, RasterizerState.BackFaceCulling, new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(scenePassHandle)), "world");
        instanceBuffer = new InstanceBuffer(device, worldShader, "tanks");
        batch = new InstancedBatch(cube, worldPipeline, instanceBuffer);

        var skyShader = device.CreateShaderProgramFromSpv(spv("sky.vert.spv"), spv("sky.frag.spv"), skyIface, "sky");
        ownedPrograms.Add(skyShader);
        skyPipeline = device.CreatePipeline(new PipelineDescription(skyShader, VertexPosition3NormalTexture.Layout, PrimitiveTopology.Triangles,
            DepthState.Disabled, RasterizerState.NoCulling, new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(scenePassHandle)), "sky");

        // Present targets the swapchain (default), copying the HDR scene.
        var presentShader = device.CreateShaderProgramFromSpv(spv("present.vert.spv"), spv("present.frag.spv"), presentIface, "present");
        ownedPrograms.Add(presentShader);
        presentPipeline = device.CreatePipeline(new PipelineDescription(presentShader, VertexPosition3NormalTexture.Layout, PrimitiveTopology.Triangles,
            DepthState.Disabled, RasterizerState.NoCulling, new[] { BlendState.Disabled }), "present");
        fullscreen = new FullscreenPass(device, "fullscreen");

        LoadTankParts(worldShader, casterShader);
        propTypes.Add(LoadProp("barrel.glb", targetHeight: 1.4f, explosive: false, worldShader, casterShader));
        propTypes.Add(LoadProp("crate.glb", targetHeight: 1.5f, explosive: false, worldShader, casterShader));
        propTypes.Add(LoadProp("barrel_explosive.glb", targetHeight: 1.4f, explosive: true, worldShader, casterShader));

        tunables = new ObjectTunables(feel, fit, juice);   // grouped by type: feel + fit + juice
        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        Reset();   // builds the collision world + places props (so a restart restores them)
    }

    // Load the articulated tank model and split it into the four drawable parts
    // (body / tracks / turret / gun), each baked to its rig pivot. ImportNodes keeps
    // every node in LOCAL space; we compose each node's world transform, bake
    // (assemble + recentre-on-pivot) into the part mesh, and hand the rest of the
    // fit (scale / yaw / lift) to the per-frame instance matrix so the live knobs
    // stay cheap. Body + turret take the team tint; tracks + gun are constant.
    private void LoadTankParts(ShaderProgramHandle worldShader, ShaderProgramHandle casterShader)
    {
        // The cooked scene graph read static: the tank's skinned meshes arrive at their bind pose.
        var model = ModelData.Load(AppFiles.Asset("models", "tank.blixmesh"), new ModelNeeds(Skinned: false));

        int Find(string name) => model.FindNode(name);
        // Assemble a node's primitives, recentred so `pivot` sits at the origin.
        IEnumerable<(MeshData, Matrix4x4)> Baked(int node, Vector3 pivot)
        {
            var xform = model.World[node] * Matrix4x4.CreateTranslation(-pivot);
            return model.Meshes[model.Nodes[node].MeshIndex].Primitives.Select(prim => (prim.Mesh, xform));
        }

        var body = Baked(Find("Tank_body"), HullPivotModel).Merge("tank.body");
        var tracks = Baked(Find("TrackMesh.L"), HullPivotModel).Concat(Baked(Find("TrackMesh.R"), HullPivotModel)).Merge("tank.tracks");
        var turret = Baked(Find("Tank_Turret"), TurretPivotModel).Merge("tank.turret");
        var gun = Baked(Find("Tank_Gun"), GunPivotModel).Merge("tank.gun");

        TankPart Part(MeshData md, Attach attach, Vector4? tint)
        {
            var mesh = device.CreateMesh(md);
            var wInst = new InstanceBuffer(device, worldShader, $"{md.Name}.w");
            var cInst = new InstanceBuffer(device, casterShader, $"{md.Name}.c");
            return new TankPart
            {
                Attach = attach,
                FixedTint = tint,
                WorldInstances = wInst,
                CasterInstances = cInst,
                Mesh = mesh,
                World = new InstancedBatch(mesh, worldPipeline, wInst),
                Caster = new InstancedBatch(mesh, casterPipeline, cInst),
            };
        }

        var trackTint = new Vector4(0.12f, 0.12f, 0.13f, 1f);   // dark rubber/steel
        var gunTint = new Vector4(0.30f, 0.31f, 0.34f, 1f);     // gunmetal
        tankParts.Add(Part(body, Attach.Hull, null));
        tankParts.Add(Part(tracks, Attach.Hull, trackTint));
        tankParts.Add(Part(turret, Attach.Turret, null));
        tankParts.Add(Part(gun, Attach.Barrel, gunTint));
    }

    // Load a static prop model: flat Import (bakes node transforms into one space),
    // uniformly scaled so the model is `targetHeight` game units tall. Each primitive
    // becomes an instanced batch tinted by its material's base colour. Returns the
    // shared PropType; PlaceProps scatters instances.
    // Called by the host with the GPU idle: everything this loop made. BLIX_TEARDOWN_TRACE=1 lists
    // whatever is still live after it.
    public void OnUnload()
    {
        foreach (var prop in propTypes) prop.Model.Dispose();
        foreach (var part in tankParts)
        {
            part.WorldInstances.Dispose();
            part.CasterInstances.Dispose();
            device.DestroyVertexBuffer(part.Mesh.VertexBuffer);
            device.DestroyIndexBuffer(part.Mesh.IndexBuffer);
        }

        fullscreen?.Dispose();
        foreach (var pipeline in new[] { skyPipeline, worldPipeline, casterPipeline, presentPipeline }) device.DestroyPipeline(pipeline);
        foreach (var program in ownedPrograms) device.DestroyShaderProgram(program);
        instanceBuffer?.Dispose();
        casterInstances?.Dispose();
        if (cube is not null)
        {
            device.DestroyVertexBuffer(cube.VertexBuffer);
            device.DestroyIndexBuffer(cube.IndexBuffer);
        }

        graph?.Dispose();
    }

    private PropType LoadProp(string file, float targetHeight, bool explosive, ShaderProgramHandle worldShader, ShaderProgramHandle casterShader)
    {
        var primitives = ModelData.Load(AppFiles.Asset("models", Path.ChangeExtension(file, ".blixmesh")))
            .Flattened().Select(p => p.Primitive).ToArray();

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var prim in primitives)
        {
            min = Vector3.Min(min, prim.Mesh.Bounds.Min);
            max = Vector3.Max(max, prim.Mesh.Bounds.Max);
        }
        var size = max - min;
        var scale = size.Y > 1e-3f ? targetHeight / size.Y : 1f;
        var radius = 0.5f * MathF.Max(size.X, size.Z) * scale;

        // Tint per primitive is the material's own base colour: the world shader is
        // tint x lighting, so this reproduces the authored look without a texture.
        var parts = primitives.Select(prim =>
        {
            var c = prim.Material?.BaseColorFactor ?? new Vector4(0.7f, 0.7f, 0.7f, 1f);
            return (prim.Mesh, new Vector4(c.X, c.Y, c.Z, 1f));
        });
        var prop = PropModel.Create(
            device, file, parts, worldShader, worldPipeline, casterShader, casterPipeline);
        return new PropType { Name = file, Scale = scale, Radius = radius, Height = targetHeight, Explosive = explosive, Model = prop };
    }

    // Scatter cover props in a mid-arena ring (deterministic), keeping clear of the
    // player's centre spawn and the enemy edge ring, and not overlapping each other.
    // Each placement seeds a collision box (tanks can't drive through) + a bounds
    // entry for shell hits.
    private void PlaceProps()
    {
        if (propTypes.Count == 0) return;
        var prng = new Random(20260604);
        const int target = 16;
        var placed = new List<Vector3>();
        var attempts = 0;
        while (props.Count < target && attempts++ < 400)
        {
            var angle = (float)(prng.NextDouble() * Math.Tau);
            var dist = 9f + (float)prng.NextDouble() * 25f;          // ring [9, 34]
            var pos = new Vector3(MathF.Sin(angle) * dist, 0f, MathF.Cos(angle) * dist);
            var type = propTypes[prng.Next(propTypes.Count)];
            // Reject overlaps (prop-vs-prop) and the immediate spawn circle.
            if (pos.Length() < 8f) continue;
            if (placed.Any(q => Vector3.Distance(q, pos) < type.Radius + 2.5f)) continue;

            var yaw = (float)(prng.NextDouble() * Math.Tau);
            placed.Add(pos);

            var r = type.Radius;
            var box = new Bounds3(pos + new Vector3(-r, 0f, -r), pos + new Vector3(r, type.Height, r));
            var ownerId = 100 + props.Count;
            props.Add(new Prop { Type = type, Position = pos, Yaw = yaw, Bounds = box, OwnerId = ownerId });
            world.Add(ownerId, box);   // tanks resolve against it (cover blocks driving)
        }
    }

    // Seat a tank's turret/barrel rig pivots from the live fit. The model's measured
    // pivot offsets, mapped through YawFix + GlobalScale, become the local positions
    // of the turret (relative to hull) and barrel (relative to turret) — so changing
    // the scale/yaw knobs keeps the articulation pivots aligned with the meshes.
    private void SeatRig(Tank t)
    {
        var rotY = Matrix4x4.CreateRotationY(fit.YawFix);
        t.Turret.Position = Vector3.Transform(TurretOffsetModel * fit.GlobalScale, rotY);
        t.Barrel.Position = Vector3.Transform(GunOffsetModel * fit.GlobalScale, rotY);
    }

    // The instance matrix for a tank part: bake-space mesh -> scale + yaw-fix ->
    // articulated rig transform -> global vertical seat.
    private Matrix4x4 PartModel(Tank t, Attach attach)
    {
        var bind = Matrix4x4.CreateScale(fit.GlobalScale) * Matrix4x4.CreateRotationY(fit.YawFix);
        var lift = Matrix4x4.CreateTranslation(0f, fit.ModelLift, 0f);
        switch (attach)
        {
            case Attach.Hull: return bind * t.Hull.WorldMatrix * lift;
            case Attach.Turret: return bind * t.Turret.WorldMatrix * lift;
            default:
                // Gun: slide the barrel back along its axis (+Z is rearward in the
                // post-bind barrel frame) by the recoil kick, between bind and the rig.
                var recoil = Matrix4x4.CreateTranslation(0f, 0f, t.GunRecoil * juice.Recoil);
                return bind * recoil * t.Barrel.WorldMatrix * lift;
        }
    }

    // Sun shadow view-projection: light eye up the sun direction, looking at the
    // arena centre, with an ortho big enough to cover the arena footprint. Matches
    // VulkanLit's construction (our SunDir points TOWARD the sun, so eye = +SunDir).
    private static Matrix4x4 SunShadowVP() =>
        GraphicsMatrices.SunShadowViewProjection(SunDir, SunDistance, SunOrthoExtent, 20f, SunDistance + 90f);

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
        // Rebuild the static world so a restart restores any detonated barrels
        // (PlaceProps is deterministic, so the layout is identical each game).
        world.Clear();
        BuildWorld();
        props.Clear();
        PlaceProps();
        sparks.Clear();

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
        SeatRig(player);
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
        // Escape is a transition, not a hold: closing once is the point, and Down would ask again
        // every tick until the window went away.
        if (Input[Key.Escape].Pressed) host.RequestClose();

        trauma = MathF.Max(0f, trauma - juice.TraumaDecay * dt);   // screen shake settles
        UpdateSparks(dt);                                          // VFX live on through game-over
        if (gameOver)
        {
            if (Input[Key.Enter].Down || Input[Key.R].Down) Reset();
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
        var targetSpeed = (Input[Key.W].Down ? feel.DriveSpeed : 0f) - (Input[Key.S].Down ? feel.ReverseSpeed : 0f);
        var rate = MathF.Abs(targetSpeed) > MathF.Abs(playerSpeed) ? feel.DriveAccel : feel.DriveDecel;
        playerSpeed = MoveToward(playerSpeed, targetSpeed, rate * dt);

        // Steering is coupled to motion: full turn rate while driving, only a slow
        // pivot when parked — so the tank carves a turn radius rather than spinning
        // on a dime. (speedFrac scales the available yaw rate with current speed.)
        var steer = (Input[Key.A].Down ? 1f : 0f) - (Input[Key.D].Down ? 1f : 0f);
        var speedFrac = MathF.Min(1f, MathF.Abs(playerSpeed) / feel.DriveSpeed);
        player.HullYaw += steer * feel.TurnSpeed * (PivotFactor + (1f - PivotFactor) * speedFrac) * dt;

        if (Input[Key.Left].Down) player.TurretYaw += feel.TurretSpeed * dt;
        if (Input[Key.Right].Down) player.TurretYaw -= feel.TurretSpeed * dt;
        // Clamp the turret to a forward arc (no 360 spin) so the gun — and the
        // turret-follow camera — stay anchored to where the hull faces.
        player.TurretYaw = Math.Clamp(player.TurretYaw, -feel.TurretLimit, feel.TurretLimit);
        // Up/Down elevate the gun — higher pitch lobs the shell further (range control).
        if (Input[Key.Up].Down) player.BarrelPitch += feel.PitchSpeed * dt;
        if (Input[Key.Down].Down) player.BarrelPitch -= feel.PitchSpeed * dt;
        player.BarrelPitch = Math.Clamp(player.BarrelPitch, 0f, feel.MaxPitch);
        SeatRig(player);
        player.Apply();

        // Drive sets horizontal velocity along the hull facing; gravity owns vertical.
        var forward = Vector3.Transform(-Vector3.UnitZ, player.Hull.Rotation);
        recoilVel *= MathF.Max(0f, 1f - RecoilDamp * dt);   // recoil shove fades out
        player.GunRecoil *= MathF.Max(0f, 1f - juice.RecoilRecover * dt);   // barrel kick recovers
        SetHorizontalVelocity(player, forward * playerSpeed + recoilVel);
        player.Physics.Gravity = new Vector3(0f, feel.TankGravity, 0f);   // live-tunable
        player.Physics.FixedUpdate(new Time(time.Total, dt));
        ResolveTank(player);

        player.FireTimer -= dt;
        if (Input[Key.Space].Down && player.FireTimer <= 0f) Fire(player, fromPlayer: true);
    }

    private static float MoveToward(float current, float target, float maxDelta)
    {
        var delta = target - current;
        return MathF.Abs(delta) <= maxDelta ? target : current + MathF.Sign(delta) * maxDelta;
    }

    // Rotate an angle toward a target by at most maxDelta, taking the shortest way
    // around the circle (so turning past ±π doesn't spin the long way).
    private static float TurnToward(float current, float target, float maxDelta)
    {
        var delta = MathF.IEEERemainder(target - current, MathF.Tau);   // wrap to [-π, π]
        if (MathF.Abs(delta) <= maxDelta) return target;
        return current + MathF.Sign(delta) * maxDelta;
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

    private const float SpawnMinRange = 26f;   // never closer than this to the player
    private const float SpawnMaxRange = 36f;

    // Spawn at a fair distance band AROUND THE PLAYER (not a random arena-edge point),
    // so enemies always roll in from a reasonable range with reaction time — never on
    // top of you, inside a wall, or on a prop. Re-rolls a few times, then falls back.
    private void SpawnEnemy()
    {
        var pos = new Vector3(0f, 4f, 0f);
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var angle = (float)(rng.NextDouble() * Math.Tau);
            var range = SpawnMinRange + (float)rng.NextDouble() * (SpawnMaxRange - SpawnMinRange);
            var c = player.Position + new Vector3(MathF.Sin(angle) * range, 0f, MathF.Cos(angle) * range);
            if (MathF.Abs(c.X) > ArenaHalf - 3f || MathF.Abs(c.Z) > ArenaHalf - 3f) continue;   // inside walls
            if (OnProp(c)) continue;                                                             // clear of cover
            pos = new Vector3(c.X, 4f, c.Z);   // drop in
            break;
        }
        var e = new Tank { Health = 1f, FireTimer = feel.EnemyReload * (0.4f + (float)rng.NextDouble()) };
        e.Position = pos;
        enemies.Add(e);
    }

    // True if a horizontal point sits on (or hard against) an alive prop's footprint.
    private bool OnProp(Vector3 p)
    {
        foreach (var prop in props)
        {
            if (prop.Alive && Flatten(p - prop.Position).Length() < prop.Type.Radius + 2.5f) return true;
        }
        return false;
    }

    private void UpdateEnemy(Tank e, Time time, float dt)
    {
        var toPlayer = player.Position - e.Position;
        toPlayer.Y = 0f;
        var dist = toPlayer.Length();
        var seek = dist > 0.0001f ? toPlayer / dist : -Vector3.UnitZ;

        // Steer the HULL around cover, but TURN toward that heading at a limited rate
        // (real tanks don't pivot instantly) — this is what stops the snapping when the
        // steer target shifts. The TURRET still tracks the player exactly.
        var drive = AvoidObstacles(e.Position, seek);
        var desiredYaw = MathF.Atan2(-drive.X, -drive.Z);
        e.HullYaw = TurnToward(e.HullYaw, desiredYaw, feel.EnemyTurn * dt);
        var worldAim = MathF.Atan2(-toPlayer.X, -toPlayer.Z);
        e.TurretYaw = worldAim - e.HullYaw;
        SeatRig(e);
        e.Apply();

        // Drive along the hull's ACTUAL facing (not the raw steer target) so motion
        // curves smoothly with the turn instead of sliding sideways.
        var forward = Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, e.HullYaw));
        SetHorizontalVelocity(e, dist > EnemyStandoff ? forward * feel.EnemySpeed : Vector3.Zero);
        e.GunRecoil *= MathF.Max(0f, 1f - juice.RecoilRecover * dt);   // barrel kick recovers
        e.Physics.Gravity = new Vector3(0f, feel.TankGravity, 0f);
        e.Physics.FixedUpdate(new Time(time.Total, dt));
        ResolveTank(e);

        e.FireTimer -= dt;
        // Don't fire when a prop sits between us and the player (we'd just hit cover).
        if (feel.EnemiesFire && dist < EnemyFireRange && e.FireTimer <= 0f && !PropBlocksShot(e.Position, toPlayer, dist))
        {
            Fire(e, fromPlayer: false);
        }
    }

    // Obstacle-avoidance steering: if a cover prop sits ahead within a lookahead and
    // close to the seek path, bend the heading perpendicular — around the side the
    // obstacle isn't on. Steers around convex cover without true path planning (good
    // enough for a sparse arena; a concave trap could still stall). The first real
    // consumer of a navigation angle — a `SteeringBehaviors`/nav primitive could
    // graduate out of this once a second consumer wants it.
    private Vector3 AvoidObstacles(Vector3 from, Vector3 seek)
    {
        const float lookahead = 9f;
        const float margin = 2.4f;       // tank half-width + clearance around the prop
        const float strength = 1.7f;

        Prop? threat = null;
        var nearest = float.MaxValue;
        foreach (var p in props)
        {
            if (!p.Alive) continue;
            var off = Flatten(p.Position - from);
            var ahead = Vector3.Dot(off, seek);                 // distance along the heading
            if (ahead <= 0.01f || ahead > lookahead) continue;  // behind us or beyond lookahead
            var lateral = (off - seek * ahead).Length();        // perpendicular distance to the path
            if (lateral < p.Type.Radius + margin && ahead < nearest) { nearest = ahead; threat = p; }
        }
        if (threat is null) return seek;

        var toObs = Flatten(threat.Position - from);
        var left = new Vector3(-seek.Z, 0f, seek.X);            // +90° from seek (left)
        var side = Vector3.Dot(toObs, left) > 0f ? -1f : 1f;    // obstacle on the left -> steer right
        var urgency = 1f - nearest / lookahead;                 // stronger the closer it is
        var steered = seek + left * (side * strength * urgency);
        return Vector3.Normalize(steered);
    }

    // True if an alive prop sits on the segment from `from` toward the player (within
    // its cover radius), so an enemy shouldn't waste a shot into it.
    private bool PropBlocksShot(Vector3 from, Vector3 toPlayer, float dist)
    {
        var dir = dist > 1e-3f ? toPlayer / dist : Vector3.Zero;
        foreach (var p in props)
        {
            if (!p.Alive) continue;
            var off = Flatten(p.Position - from);
            var along = Vector3.Dot(off, dir);
            if (along <= 0.5f || along >= dist) continue;       // behind the shooter or past the target
            var lateral = (off - dir * along).Length();
            if (lateral < p.Type.Radius + 0.6f) return true;
        }
        return false;
    }

    private void Fire(Tank tank, bool fromPlayer)
    {
        tank.FireTimer = fromPlayer ? feel.Reload : feel.EnemyReload;
        // Spawn the shell as a child of the barrel at the muzzle, then detach it into
        // world space keeping that pose — it leaves exactly where the barrel points.
        // Muzzle is the gun length (breach->tip) scaled by the fit, so shells leave
        // the real gun tip rather than a fixed offset.
        var muzzle = GunLengthModel * fit.GlobalScale;
        var shell = new Transform3D { Position = new Vector3(0f, 0f, -muzzle), Parent = tank.Barrel };
        shell.SetParent(null, keepWorldPose: true);
        var physics = new PhysicsHost3D { Target = shell, Velocity = tank.BarrelForward * feel.MuzzleSpeed, GravityScale = 1f, Gravity = new Vector3(0f, feel.ShellGravity, 0f) };
        shells.Add(new Shell { Transform = shell, Physics = physics, FromPlayer = fromPlayer });

        // Punch: muzzle flash at the gun tip, a visual recoil kick on the barrel, and
        // (for the player) a touch of screen shake.
        SpawnMuzzleFlash(shell.WorldPosition, tank.BarrelForward);
        tank.GunRecoil = 1f;
        if (fromPlayer) AddTrauma(juice.ShakeFire);

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
            if (s.Age > ShellLife || OutOfArena(pos)) { shells.RemoveAt(i); continue; }
            // Ground impact: a dirt kick-up where the arc lands.
            if (pos.Y <= 0f)
            {
                SpawnBurst(new Vector3(pos.X, 0.05f, pos.Z), 10, new Vector4(0.42f, 0.34f, 0.24f, 1f), speed: 5f, upBias: 0.7f, life: 0.4f, size: 0.22f);
                shells.RemoveAt(i);
                continue;
            }

            // Cover: a flat shot stops at a prop (lobs clear it). Explosive barrels detonate.
            if (HitProp(pos) is { } prop)
            {
                shells.RemoveAt(i);
                if (prop.Type.Explosive) Explode(prop);
                else SpawnBurst(pos, 8, new Vector4(0.55f, 0.5f, 0.42f, 1f), speed: 4f, upBias: 0.4f, life: 0.3f, size: 0.18f);   // dust puff
                continue;
            }

            if (s.FromPlayer)
            {
                for (var ei = enemies.Count - 1; ei >= 0; ei--)
                {
                    if (Vector3.Distance(pos, enemies[ei].Position) < HitRadius)
                    {
                        KillEnemy(ei);
                        shells.RemoveAt(i);
                        break;
                    }
                }
            }
            else if (Vector3.Distance(pos, player.Position) < HitRadius)
            {
                shells.RemoveAt(i);
                health -= feel.EnemyDamage;
                AddTrauma(juice.ShakeHit);
                SpawnBurst(pos, 12, new Vector4(1f, 0.5f, 0.3f, 1f), speed: 7f, upBias: 0.5f, life: 0.35f, size: 0.24f);
                if (health <= 0f) { health = 0f; gameOver = true; }
                UpdateTitle();
            }
        }
    }

    // Remove an enemy with a debris burst + a little shake. Shared by direct shell
    // hits and explosion AoE so kills always read with the same punch.
    private void KillEnemy(int index)
    {
        var at = enemies[index].Position + new Vector3(0f, 1.2f, 0f);
        enemies.RemoveAt(index);
        score++;
        SpawnBurst(at, (int)juice.DeathDebris, EnemyTeam, speed: 9f, upBias: 0.5f, life: 0.6f, size: 0.3f);
        AddTrauma(juice.ShakeFire * 0.7f);
        UpdateTitle();
    }

    private static bool OutOfArena(Vector3 p) =>
        MathF.Abs(p.X) > ArenaHalf + 4f || MathF.Abs(p.Z) > ArenaHalf + 4f;

    // The alive prop a shell point is inside (its cover box), or null. Boxes are only
    // as tall as the prop, so a high lob clears the cover — flat shots don't.
    private Prop? HitProp(Vector3 p)
    {
        foreach (var prop in props)
        {
            if (!prop.Alive) continue;
            var b = prop.Bounds;
            if (p.X >= b.Min.X && p.X <= b.Max.X &&
                p.Y >= b.Min.Y && p.Y <= b.Max.Y &&
                p.Z >= b.Min.Z && p.Z <= b.Max.Z)
            {
                return prop;
            }
        }
        return null;
    }

    // Detonate an explosive barrel: clear it from render + collision, throw a spark
    // burst, kill enemies and damage/shove the player within the blast (linear
    // falloff), then chain-detonate nearby explosive barrels. Recursive — each barrel
    // marks itself dead before chaining, so a cluster goes up once with no re-entry.
    private void Explode(Prop barrel)
    {
        if (!barrel.Alive) return;
        barrel.Alive = false;
        world.Remove(barrel.OwnerId);

        var center = Flatten(barrel.Position);
        var burstAt = barrel.Position + new Vector3(0f, barrel.Type.Height * 0.5f, 0f);
        SpawnBurst(burstAt, (int)juice.ExplosionDebris, new Vector4(1f, 0.5f, 0.12f, 1f), speed: 13f, upBias: 0.5f, life: 0.65f, size: 0.3f);
        SpawnBurst(burstAt, 6, new Vector4(1f, 0.95f, 0.7f, 1f), speed: 6f, upBias: 0.8f, life: 0.18f, size: 0.6f);   // bright flash core

        for (var ei = enemies.Count - 1; ei >= 0; ei--)
        {
            if (Flatten(enemies[ei].Position - center).Length() < ExplosionRadius) KillEnemy(ei);
        }

        if (!gameOver)
        {
            var toPlayer = Flatten(player.Position - center);
            var pd = toPlayer.Length();
            if (pd < ExplosionRadius)
            {
                var falloff = 1f - pd / ExplosionRadius;
                health -= ExplosionDamage * falloff;
                if (pd > 1e-3f) recoilVel += toPlayer / pd * (ExplosionKnockback * falloff);
                if (health <= 0f) { health = 0f; gameOver = true; }
            }
            // Shake scales with proximity, out to roughly twice the kill radius.
            AddTrauma(juice.ShakeExplosion * MathF.Max(0f, 1f - pd / (ExplosionRadius * 2f)));
        }
        UpdateTitle();

        // Chain: nearby explosive barrels go up too.
        foreach (var p in props)
        {
            if (p.Alive && p.Type.Explosive && Flatten(p.Position - center).Length() < ExplosionChainRadius)
            {
                Explode(p);
            }
        }
    }

    private static Vector3 Flatten(Vector3 v) => new(v.X, 0f, v.Z);

    private float Rand11() => (float)(rng.NextDouble() * 2.0 - 1.0);

    // Add screen-shake trauma (clamped). Shake offset is trauma², so small hits barely
    // register and big ones kick hard, both easing out as trauma decays.
    private void AddTrauma(float amount) => trauma = MathF.Min(1f, trauma + amount);

    // A radial debris burst at `center`, biased upward. Used for explosions, deaths,
    // and impact dust — colour/spread/life/size per call.
    private void SpawnBurst(Vector3 center, int count, Vector4 tint, float speed, float upBias, float life, float size)
    {
        for (var i = 0; i < count; i++)
        {
            var a = (float)(rng.NextDouble() * Math.Tau);
            var up = upBias + (float)rng.NextDouble() * 0.9f;
            var sp = speed * (0.5f + (float)rng.NextDouble());
            var dir = Vector3.Normalize(new Vector3(MathF.Sin(a), up, MathF.Cos(a)));
            sparks.Add(new Spark { Pos = center, Vel = dir * sp, Age = 0f, Life = life, Tint = tint, Size = size });
        }
    }

    // A tight cone of bright sparks along `dir` — the muzzle flash on firing.
    private void SpawnMuzzleFlash(Vector3 center, Vector3 dir)
    {
        var flash = new Vector4(1f, 0.86f, 0.42f, 1f);
        for (var i = 0; i < 8; i++)
        {
            var jitter = new Vector3(Rand11(), Rand11(), Rand11()) * 0.5f;
            var vel = dir * (10f + (float)rng.NextDouble() * 8f) + jitter * 6f;
            sparks.Add(new Spark { Pos = center, Vel = vel, Age = 0f, Life = 0.16f, Tint = flash, Size = 0.34f });
        }
    }

    private void UpdateSparks(float dt)
    {
        for (var i = sparks.Count - 1; i >= 0; i--)
        {
            var s = sparks[i];
            s.Vel.Y += SparkGravity * dt;
            s.Pos += s.Vel * dt;
            s.Age += dt;
            if (s.Age > s.Life || s.Pos.Y < 0f) sparks.RemoveAt(i);
        }
    }

    private const float SparkGravity = -26f;

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
        var target = p + Vector3.UnitY * 1.2f;
        // Screen shake: offset both eye and look-at by trauma² so the whole view kicks
        // (random direction each frame) and eases out as trauma decays.
        if (trauma > 0f)
        {
            var kick = trauma * trauma * juice.ShakeAmount;
            var jolt = new Vector3(Rand11(), Rand11() * 0.6f, Rand11()) * kick;
            eye += jolt;
            target += jolt * 0.5f;
        }
        var view = Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
        var proj = GraphicsMatrices.CreatePerspectiveVulkan(MathF.PI / 3f, aspect, 0.3f, 400f);
        viewProj = view * proj;

        // Push payloads, one struct per program's block.
        sunShadowVP = SunShadowVP();
        var camPos = new Vector4(eye, 1f);
        var sun = new Vector4(SunDir, 0f);
        Matrix4x4.Invert(viewProj, out var invViewProj);
        new WorldPush { ViewProjection = viewProj, CamPos = camPos, SunDir = sun, SunShadowVP = sunShadowVP }.WriteTo(worldPush);
        new SkyPush { InvViewProj = invViewProj, CamPos = camPos, SunDir = sun }.WriteTo(skyPush);
        new ShadowPush { ShadowViewProj = sunShadowVP }.WriteTo(shadowPush);

        // Lit world cubes: ground + walls + shells. Tanks draw as their model parts.
        batch.Begin(worldPush);
        batch.Add(Matrix4x4.CreateScale(GroundScale) * Matrix4x4.CreateTranslation(0f, -0.1f, 0f),
            new Vector4(0.38f, 0.50f, 0.33f, 1f));   // grassy ground
        var wallTint = new Vector4(0.62f, 0.50f, 0.36f, 1f);   // warm tan
        foreach (var (c, s) in Walls)
            batch.Add(Matrix4x4.CreateScale(s) * Matrix4x4.CreateTranslation(c), wallTint);
        foreach (var s in shells)
        {
            var tint = s.FromPlayer ? new Vector4(0.80f, 0.92f, 1f, 1f) : new Vector4(1f, 0.62f, 0.25f, 1f);
            batch.Add(Matrix4x4.CreateScale(0.28f) * s.Transform.WorldMatrix, tint);
        }
        var sparkTint = new Vector4(1f, 0.55f, 0.12f, 1f);   // explosion debris
        foreach (var s in sparks)
            batch.Add(Matrix4x4.CreateScale(0.2f) * Matrix4x4.CreateTranslation(s.Pos), sparkTint);

        // Tank model parts (world + shadow caster), team-tinted hull/turret.
        foreach (var part in tankParts) { part.World.Begin(worldPush); part.Caster.Begin(shadowPush); }
        AddTankParts(player, PlayerTeam);
        foreach (var e in enemies) AddTankParts(e, EnemyTeam);

        // Environment props (cover): material-coloured, instanced across placements. One Add per
        // placement reaches every part and both passes, which is the property worth having -- a
        // caster cannot drift from what it casts for.
        foreach (var pt in propTypes) pt.Model.Begin();
        foreach (var inst in props)
        {
            if (!inst.Alive) continue;   // detonated barrels are gone
            inst.Type.Model.Add(
                Matrix4x4.CreateScale(inst.Type.Scale)
                * Matrix4x4.CreateRotationY(inst.Yaw)
                * Matrix4x4.CreateTranslation(inst.Position));
        }
        foreach (var pt in propTypes) pt.Model.Stage(worldPush, shadowPush);

        // Shadow casters: walls (not the ground receiver or tiny shells); tanks via parts.
        casterBatch.Begin(shadowPush);
        foreach (var (c, s) in Walls)
            casterBatch.Add(Matrix4x4.CreateScale(s) * Matrix4x4.CreateTranslation(c), Vector4.Zero);

        // Record: shadow depth pass -> HDR scene pass (samples the shadow map) -> present.
        graph.Pass(shadowPassHandle, scope =>
        {
            casterBatch.End(scope);
            foreach (var part in tankParts) part.Caster.End(scope);
            foreach (var pt in propTypes) pt.Model.DrawShadow(scope);
        });
        var shadowTex = graph.GetDepthTexture(sunShadowHandle);
        graph.Pass(scenePassHandle, scope =>
        {
            fullscreen.Draw(scope, skyPipeline, Array.Empty<ShaderTextureBinding>(), skyPush);
            var shadowBind = new[] { new ShaderTextureBinding("uSunShadowMap", shadowTex) };
            batch.End(scope, shadowBind);
            foreach (var part in tankParts) part.World.End(scope, shadowBind);
            foreach (var pt in propTypes) pt.Model.DrawScene(scope, shadowBind);
        });
        graph.Execute(commandList);

        var hdrTex = graph.GetColorTexture(hdrHandle);
        commandList.Pass(
            "present",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0f, 0f, 0f, 1f) },
                ClearDepth: true),
            pass => fullscreen.Draw(pass, presentPipeline, new[] { new ShaderTextureBinding("uHdr", hdrTex) }));

        if (exitAfterFrames > 0 && frameCount >= exitAfterFrames) host.RequestClose();
    }

    // Arena perimeter walls (visual reference + shadow casters); shared by both batches.
    private static readonly (Vector3 Center, Vector3 Scale)[] Walls = BuildWalls();

    private static (Vector3, Vector3)[] BuildWalls()
    {
        const float w = ArenaHalf;
        var span = 2f * w + 1f;
        return new[]
        {
            (new Vector3(-w, 0.7f, 0f), new Vector3(1f, 1.4f, span)),
            (new Vector3(w, 0.7f, 0f), new Vector3(1f, 1.4f, span)),
            (new Vector3(0f, 0.7f, -w), new Vector3(span, 1.4f, 1f)),
            (new Vector3(0f, 0.7f, w), new Vector3(span, 1.4f, 1f)),
        };
    }

    private static readonly Vector4 PlayerTeam = new(0.22f, 0.52f, 0.92f, 1f);   // blue
    private static readonly Vector4 EnemyTeam = new(0.86f, 0.30f, 0.24f, 1f);    // red

    // Stage one tank into every part batch (world + caster). Body/turret take the
    // team tint; tracks/gun their fixed colour. PartModel composes the live fit with
    // the part's rig transform.
    private void AddTankParts(Tank t, Vector4 team)
    {
        foreach (var part in tankParts)
        {
            var model = PartModel(t, part.Attach);
            part.World.Add(model, part.FixedTint ?? team);
            part.Caster.Add(model, Vector4.Zero);
        }
    }

    public string DebugName => "tank-arena";

    public void Debug(DebugContext debug)
    {
        // The keys this game handles itself, listed with the overlay hidden too.
        debug.Keys.Describe("W / S", "drive forward / reverse");
        debug.Keys.Describe("A / D", "steer");
        debug.Keys.Describe("Left / Right", "turn the turret");
        debug.Keys.Describe("Up / Down", "raise / lower the barrel");
        debug.Keys.Describe(Key.Space, "fire");
        debug.Keys.Describe("Enter / R", "restart, once the round is over");
        debug.Keys.Describe(Key.Escape, "quit");

        // Controls and gizmos only while the overlay is up (--debug, or ` at any time).
        if (!debug.State.ShowOverlay) return;

        tunables.BuildControls(debug);   // live [Tune] sliders: feel + tank-model fit + juice

        using (debug.Scope("arena"))
        {
            debug.Values.Value("hp", health);
            debug.Values.Value("score", score);
            debug.Values.Value("wave", wave);
            debug.Values.Value("enemies", enemies.Count);
            debug.Values.Value("shells", shells.Count);
            debug.Values.Value("sparks", sparks.Count);
            debug.Values.Value("props", props.Count(p => p.Alive));
            debug.Values.Value("speed", playerSpeed);
            debug.Values.Value("pitch", player.BarrelPitch);
            debug.Values.Value("trauma", trauma);
        }
    }

    // Disposed by Window after WaitIdle (the graph owns render passes + offscreen
    // images that aren't in the device's auto-freed resource tables).
    public void Dispose()
    {
        graph?.Dispose();
        fullscreen?.Dispose();
        instanceBuffer?.Dispose();
        casterInstances?.Dispose();
        foreach (var part in tankParts)
        {
            part.WorldInstances.Dispose();
            part.CasterInstances.Dispose();
        }
        foreach (var pt in propTypes) pt.Model.Dispose();
    }
}

// Each program's push block, written from its shaders' reflection: fields, offsets and sizes come
// from the GLSL, and "u" is dropped from each name because these declarations say so.
[PushConstants("cube.vert", "cube.frag", Prefix = "u")]
internal partial struct WorldPush;

[PushConstants("sky.vert", "sky.frag", Prefix = "u")]
internal partial struct SkyPush;

[PushConstants("shadow_caster.vert", "shadow_caster.frag", Prefix = "u")]
internal partial struct ShadowPush;
