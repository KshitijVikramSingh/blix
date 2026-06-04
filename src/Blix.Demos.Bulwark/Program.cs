using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Assets;
using Blix.Audio;
using Blix.Core;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.Primitives;
using Blix.Graphics.Vulkan;
using Blix.Render;
using Blix.Runtime.Silk;
using Plane = Blix.Geometry.Plane;

namespace Blix.Demos.Bulwark;

// Bulwark — tower-defense, Blix game #2 (see plan-bulwark.md). Defend a central core
// from waves of enemies converging on four fronts: build + upgrade towers with the
// scrap you earn from kills, survive 5 waves (a leak costs a life). Built to pressure
// the engine where TankArena/Runner/Pong didn't — pointer-driven picking, navigation,
// and a skinned animated crowd.
//
// Frame shape: a RenderGraph (lifted from TankArena) — sun shadow depth pass → HDR
// scene pass (procedural sky + single-tap sun shadow + distance fog) → present; the
// SpriteBatch/Font HUD composites on the present pass. The scene is all instanced
// draws: grid tiles / shots / hover ghost (cubes), CC0 props (Quaternius Turret
// Cannon on the aim rig, iPoly3D Crystal core), and the animated enemy crowd via
// skinned-mesh instancing. Particles are additive bursts into the HDR; SFX are
// synthesized through OpenAL.
//
// Engine-learning notes (the point of building game #2): navigation (grid A*) and the
// turret aim rig are each a 2nd consumer vs TankArena, but after building both the
// verdict was to extract NEITHER — the turret rig is incidental sharing of primitives
// that already exist, and grid-A* vs continuous steering are too different to unify
// (docs/conventions.md §4). Skinned-mesh instancing is the one genuinely new capability
// and is built LOCAL here (the SkinnedInstancedBatch candidate) — to be promoted to
// Blix.Render only when a 2nd consumer appears.
//
// ── Executable spec for (engine capabilities this demo proves) ──
//   • Picking: Camera3D.ScreenPointToRay → Intersection.Raycast(ray, ground) → grid cell
//   • A* grid pathfinding: multi-front, dynamic re-path, wall-off rejection
//   • Transform3D turret→barrel aim rig (LookAt yaw + parented-barrel muzzle)
//   • glTF static import (ImportNodes + BakeMerge) AND skinned import (GltfImporter + clips)
//   • Skinned-mesh INSTANCING: one [N×bones] world-baked palette indexed by
//     gl_InstanceIndex → the whole animated crowd in one instanced draw per primitive,
//     in both the lit scene pass and the shadow caster
//   • RenderGraph sun-shadow + HDR; SpriteBatch/Font HUD; ParticleBatch bursts; OpenAL SFX
// ── Intentionally owns (stays local — NOT extracted) ──
//   • grid + A* + path-following, turret aim, economy, wave director, HUD layout,
//     the skinned-instancing crowd code, camera feel
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
        using var window = new Window(loop, new WindowOptions("Blix — Bulwark (Tower Defense)", 1280, 720));
        window.Run();
    }
}

internal sealed class BulwarkLoop : IGameLoop, IInputHandler, IDisposable
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

    // Navigation (kept local; see header). The core sits at the CENTRE; enemies
    // converge from four spawn fronts (N/S/E/W edge midpoints), each marching its own
    // A* path to the core. A* runs on placement (not per frame), towers are
    // impassable, and a build that would wall off ANY front is rejected.
    private const int CoreCx = GridW / 2;
    private const int CoreCz = GridH / 2;
    private static readonly (int cx, int cz)[] Spawns =
    {
        (0, GridH / 2),           // west
        (GridW - 1, GridH / 2),   // east
        (GridW / 2, 0),           // north
        (GridW / 2, GridH - 1),   // south
    };
    private const float EnemySpeed = 3.0f;   // paths are ~half as long now (edge→centre)
    private readonly List<(int cx, int cz)>[] paths = new List<(int cx, int cz)>[Spawns.Length];
    private readonly HashSet<int> pathCells = new();

    // M1 — combat loop. Towers (a Transform3D turret→barrel aim rig lifted from
    // TankArena, kept local) target the nearest enemy in range and fire homing shots;
    // enemies have HP; a kill pays scrap, a leak costs a life; scrap builds towers.
    private const int TowerCost = 50;
    private const int KillReward = 9;
    private const int StartScrap = 200;   // ~4 towers — one per front; you build during Prep
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

    // M2b juice. ParticleBatch (the showcase primitive, now a gameplay mechanic) for
    // impact/death bursts — a caller-owned additive pipeline drawn over the cubes.
    // SFX synthesized through OpenAL (best-effort; null if no audio backend).
    private ParticleBatch particles = null!;
    private PipelineHandle particlePipeline;
    private readonly Random rng = new(20260604);
    private IAudioDevice? audio;
    private AudioSource? fireSfx, deathSfx, leakSfx;
    private float fireSfxCooldown;

    // M3 art: real CC0 meshes (Quaternius Turret Cannon + Robot Enemy, iPoly3D Crystal).
    // Each is its own InstancedBatch on the SHARED cube pipeline (same pos+normal layout);
    // tiles / shots / ghost stay cubes. Best-effort — falls back to primitives on load
    // failure (artLoaded=false). Fit knobs below are exposed for visual tuning (no live
    // overlay yet — edit + rerun, or tell me the values).
    private bool artLoaded;
    private InstancedBatch turretBaseBatch = null!, turretTopBatch = null!, enemyBatch = null!, coreBatch = null!;
    private readonly List<InstanceData> turretBaseInst = new();
    private readonly List<InstanceData> turretTopInst = new();
    private readonly List<InstanceData> enemyInst = new();
    private readonly List<InstanceData> coreInst = new(1);
    private float gameTime;   // drives the crystal-core spin

    // ── visual fit knobs (tweak to taste) ──
    private const float TowerScale = 1.3f;
    private const float TurretYawFix = MathF.PI;   // model barrel +Z → engine -Z forward
    private const float EnemyScale = 1.15f;
    private const float CoreScale = 0.22f;
    private const float CoreLift = 0.2f;

    // M3b lighting: a RenderGraph — sun shadow depth pass → HDR scene pass (samples
    // the shadow map, procedural sky) → present. Lifted from TankArena. Casters
    // (towers/enemy/core) draw a second time into the shadow map; tiles/shots/ghost
    // are scene-only receivers. Particles draw additively into the HDR scene.
    private RenderGraph graph = null!;
    private GraphResourceHandle hdrHandle, sceneDepthHandle, sunShadowHandle;
    private PassHandle shadowPassHandle, scenePassHandle;
    private PipelineHandle worldPipeline, casterPipeline, skyPipeline, presentPipeline;
    private FullscreenPass fullscreen = null!;
    private InstancedBatch turretBaseCaster = null!, turretTopCaster = null!, enemyCaster = null!, coreCaster = null!;
    private readonly byte[] worldPush = new byte[160];   // viewProj + camPos + sunDir + sunShadowVP
    private readonly byte[] skyPush = new byte[96];      // invViewProj + camPos + sunDir
    private readonly byte[] shadowPush = new byte[64];   // sun shadow VP (caster pass)
    private const int ShadowMapSize = 2048;
    private const float SunOrthoExtent = 44f;            // covers the 32-unit grid + margin
    private const float SunDistance = 120f;
    private static readonly Vector3 SunDir = Vector3.Normalize(new Vector3(0.35f, 0.82f, 0.45f));

    // M4 Gate A: one skinned, animated enemy (de-risks the skinned path × RenderGraph
    // before Gate B generalises it to an instanced crowd). Reuses Runner's bone-palette
    // approach; the palette is baked into world space per frame so skinned.vert needs
    // no model push (matches the Gate B instanced plan). Best-effort.
    private bool skinnedLoaded;
    private PipelineHandle skinnedPipeline;         // scene (lit, instanced)
    private PipelineHandle skinnedShadowPipeline;   // shadow caster (depth-only, instanced)
    private VertexBufferHandle[] enemyVBs = Array.Empty<VertexBufferHandle>();
    private IndexBufferHandle[] enemyIBs = Array.Empty<IndexBufferHandle>();
    private int[] enemyIndexCounts = Array.Empty<int>();
    private Skeleton enemySkeleton = null!;
    private Pose enemyRestPose = null!, enemyPose = null!;
    private BonePalette enemyBonePalette = null!;
    private byte[] enemyPalettePayload = Array.Empty<byte>();   // [MaxAlive × EnemyBones] world-space mat4s
    private AnimationClip enemyWalk = null!;
    private AnimationClip enemyDeath = null!;
    private float enemyDeathHold = 0.8f;   // corpse lingers playing the Death clip, then is removed
    private Matrix4x4 enemyMeshNodeTransform = Matrix4x4.Identity;
    private MaterialBindings enemyBones = null!;   // set 3 palette SSBO, frames-in-flight; shared by both skinned pipelines
    private const int EnemyBones = 15;             // the robot skeleton; the loader asserts it (matches the shaders' BONE_COUNT)
    private const float SkinnedEnemyScale = 1.15f;   // visual dial
    private const float SkinnedEnemyYawFix = 0f;     // model forward → engine; dial if facing is off

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
        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        byte[] Spv(string n) => File.ReadAllBytes(Path.Combine(shaderDir, n));

        // RenderGraph: sun shadow depth → HDR scene (samples shadow + procedural sky) → present.
        graph = new RenderGraph(vk);
        var fullSize = new MatchSwapchainGraphSize(1.0f);
        sunShadowHandle = graph.DepthTarget("sun-shadow", new FixedGraphSize(ShadowMapSize, ShadowMapSize));
        hdrHandle = graph.ColorTarget("hdr", TextureFormat.Rgba16F, fullSize);
        sceneDepthHandle = graph.DepthTarget("scene-depth", fullSize);

        var casterIface = new ShaderInterface(
            Slots: new[] { InstanceBuffer.Slot },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });
        var worldIface = new ShaderInterface(
            Slots: new[] { new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment), InstanceBuffer.Slot },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex | ShaderStages.Fragment, 0, 160) });
        var skyIface = new ShaderInterface(
            Slots: Array.Empty<DescriptorSetSlot>(),
            PushConstants: new[] { new PushConstantRange(ShaderStages.Fragment, 0, 96) });
        var presentIface = new ShaderInterface(
            Slots: new[] { new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment) },
            PushConstants: Array.Empty<PushConstantRange>());

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

        // Pipelines target their pass surfaces (caster→shadow, world/sky/particle→scene).
        var casterShader = vk.CreateShaderProgramFromSpv(Spv("shadow_caster.vert.spv"), Spv("shadow_caster.frag.spv"), casterIface, "caster");
        casterPipeline = vk.CreatePipeline(new PipelineDescription(casterShader, meshLayout, PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite, RasterizerState.NoCulling, Array.Empty<BlendState>(),
            RenderTarget: graph.GetPassSurface(shadowPassHandle)), "caster");

        var worldShader = vk.CreateShaderProgramFromSpv(Spv("cube.vert.spv"), Spv("cube.frag.spv"), worldIface, "world");
        worldPipeline = vk.CreatePipeline(new PipelineDescription(worldShader, meshLayout, PrimitiveTopology.Triangles,
            DepthState.LessEqualWrite, RasterizerState.BackFaceCulling, new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(scenePassHandle)), "world");
        instanceBuffer = new InstanceBuffer(vk, worldShader, "bulwark");
        batch = new InstancedBatch(cube, worldPipeline, instanceBuffer);

        var skyShader = vk.CreateShaderProgramFromSpv(Spv("sky.vert.spv"), Spv("sky.frag.spv"), skyIface, "sky");
        skyPipeline = vk.CreatePipeline(new PipelineDescription(skyShader, VertexPosition3NormalTexture.Layout, PrimitiveTopology.Triangles,
            DepthState.Disabled, RasterizerState.NoCulling, new[] { BlendState.Disabled },
            RenderTarget: graph.GetPassSurface(scenePassHandle)), "sky");

        var presentShader = vk.CreateShaderProgramFromSpv(Spv("present.vert.spv"), Spv("present.frag.spv"), presentIface, "present");
        presentPipeline = vk.CreatePipeline(new PipelineDescription(presentShader, VertexPosition3NormalTexture.Layout, PrimitiveTopology.Triangles,
            DepthState.Disabled, RasterizerState.NoCulling, new[] { BlendState.Disabled }), "present");
        fullscreen = new FullscreenPass(vk, "fullscreen");

        LoadArt(worldShader, worldPipeline, casterShader, casterPipeline);   // world + shadow-caster batches
        LoadSkinnedEnemy(shaderDir);   // M4 Gate A: one animated enemy (best-effort)

        // Particle pipeline: geometry-only ParticleBatch + a minimal additive pipeline
        // (push = view-projection) into the HDR scene pass. No soft-depth / bloom.
        var particleIface = new ShaderInterface(
            Slots: Array.Empty<DescriptorSetSlot>(),
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });
        var particleShader = vk.CreateShaderProgramFromSpv(Spv("particle.vert.spv"), Spv("particle.frag.spv"), particleIface, "particle");
        particlePipeline = vk.CreatePipeline(new PipelineDescription(particleShader, ParticleBatch.VertexLayoutDescription,
            PrimitiveTopology.Triangles, DepthState.Disabled, RasterizerState.NoCulling, new[] { BlendState.Additive },
            RenderTarget: graph.GetPassSurface(scenePassHandle)), "particle");
        particles = new ParticleBatch(vk, maxParticles: 2048, "bulwark.particles");

        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        mouseX = host.LogicalSize.Width * 0.5f;
        mouseY = host.LogicalSize.Height * 0.5f;

        CreateHud();
        CreateAudio();
        UpdateCamera();
        UpdatePick();
        if (autoPlay) SeedStarterTowers();   // headless smoke only — the player builds their own
        Recompute();

        Console.WriteLine("Bulwark — Tower Defense");
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
        gameTime += dt;
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

        // Particles arc + fade every frame (so a wave's last bursts finish in Prep).
        particles.Update(dt, new Vector3(0f, -9f, 0f), drag: 1.2f);
        if (fireSfxCooldown > 0f) fireSfxCooldown -= dt;

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

    // Sun shadow view-projection — the shared engine helper (extent/distance are this
    // demo's scene tuning; pairs with Blix.Shaders/shadow.glsl's blix_sun_shadow).
    private static Matrix4x4 SunShadowVP() =>
        GraphicsMatrices.SunShadowViewProjection(SunDir, SunDistance, SunOrthoExtent, 20f, SunDistance + 90f);

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;
        BuildInstances();

        // Camera + sun push payloads (world 160B / sky 96B / shadow 64B; particles reuse
        // the 64B view-projection in pushBytes).
        var viewProj = camera.GetViewProjection(aspect);
        var sunVP = SunShadowVP();
        var camPos = new Vector4(camera.Transform.Position, 1f);
        var sun = new Vector4(SunDir, 0f);
        Matrix4x4.Invert(viewProj, out var invViewProj);
        MemoryMarshal.Write(worldPush.AsSpan(0, 64), in viewProj);
        MemoryMarshal.Write(worldPush.AsSpan(64, 16), in camPos);
        MemoryMarshal.Write(worldPush.AsSpan(80, 16), in sun);
        MemoryMarshal.Write(worldPush.AsSpan(96, 64), in sunVP);
        MemoryMarshal.Write(skyPush.AsSpan(0, 64), in invViewProj);
        MemoryMarshal.Write(skyPush.AsSpan(64, 16), in camPos);
        MemoryMarshal.Write(skyPush.AsSpan(80, 16), in sun);
        MemoryMarshal.Write(shadowPush.AsSpan(0, 64), in sunVP);
        MemoryMarshal.Write(pushBytes.AsSpan(0, 64), in viewProj);

        // Stage: world batch (scene) + per-mesh world & shadow-caster batches.
        batch.Begin(worldPush);
        batch.SetInstances(CollectionsMarshal.AsSpan(instances));
        if (artLoaded)
        {
            turretBaseBatch.Begin(worldPush); turretBaseBatch.SetInstances(CollectionsMarshal.AsSpan(turretBaseInst));
            turretTopBatch.Begin(worldPush); turretTopBatch.SetInstances(CollectionsMarshal.AsSpan(turretTopInst));
            enemyBatch.Begin(worldPush); enemyBatch.SetInstances(CollectionsMarshal.AsSpan(enemyInst));
            coreBatch.Begin(worldPush); coreBatch.SetInstances(CollectionsMarshal.AsSpan(coreInst));
            turretBaseCaster.Begin(shadowPush); turretBaseCaster.SetInstances(CollectionsMarshal.AsSpan(turretBaseInst));
            turretTopCaster.Begin(shadowPush); turretTopCaster.SetInstances(CollectionsMarshal.AsSpan(turretTopInst));
            enemyCaster.Begin(shadowPush); enemyCaster.SetInstances(CollectionsMarshal.AsSpan(enemyInst));
            coreCaster.Begin(shadowPush); coreCaster.SetInstances(CollectionsMarshal.AsSpan(coreInst));
        }

        // Pose + upload the whole skinned crowd once (both passes read this palette).
        if (skinnedLoaded && enemies.Count > 0) WriteCrowdPalette();

        // Shadow depth pass: casters block the sun — static props + the skinned crowd.
        graph.Pass(shadowPassHandle, scope =>
        {
            if (artLoaded)
            {
                turretBaseCaster.End(scope);
                turretTopCaster.End(scope);
                enemyCaster.End(scope);
                coreCaster.End(scope);
            }
            if (skinnedLoaded && enemies.Count > 0)
            {
                var instN = Math.Min(enemies.Count, MaxAlive);
                for (var i = 0; i < enemyVBs.Length; i++)
                    scope.DrawIndexedInstanced(enemyVBs[i], enemyIBs[i], skinnedShadowPipeline, enemyIndexCounts[i],
                        instN, Array.Empty<ShaderUniform>(), Array.Empty<ShaderTextureBinding>(), enemyBones.Handle, shadowPush);
            }
        });

        // HDR scene pass: procedural sky → lit world (samples the shadow map) → particles.
        var shadowTex = graph.GetDepthTexture(sunShadowHandle);
        var shadowBind = new[] { new ShaderTextureBinding("uSunShadowMap", shadowTex, Slot: 0) };
        graph.Pass(scenePassHandle, scope =>
        {
            fullscreen.Draw(scope, skyPipeline, Array.Empty<ShaderTextureBinding>(), skyPush);
            batch.End(scope, shadowBind);   // tiles + shots + ghost (receivers)
            if (artLoaded)
            {
                turretBaseBatch.End(scope, shadowBind);
                turretTopBatch.End(scope, shadowBind);
                enemyBatch.End(scope, shadowBind);
                coreBatch.End(scope, shadowBind);
            }
            // The skinned crowd: ONE instanced draw per primitive for ALL enemies —
            // each picks its world-space palette by gl_InstanceIndex, lit shadow-aware.
            if (skinnedLoaded && enemies.Count > 0)
            {
                var instN = Math.Min(enemies.Count, MaxAlive);
                for (var i = 0; i < enemyVBs.Length; i++)
                    scope.DrawIndexedInstanced(enemyVBs[i], enemyIBs[i], skinnedPipeline, enemyIndexCounts[i],
                        instN, Array.Empty<ShaderUniform>(), shadowBind, enemyBones.Handle, worldPush);
            }
            particles.Draw(scope, particlePipeline,
                camera.Transform.Right, camera.Transform.Up, camera.Transform.Position,
                sortByDepth: false, pushBytes, Array.Empty<ShaderTextureBinding>());
        });
        graph.Execute(commandList);

        // Present the HDR scene to the swapchain, then the HUD on top.
        var hdrTex = graph.GetColorTexture(hdrHandle);
        commandList.Pass(
            "present",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0f, 0f, 0f, 1f) },
                ClearDepth: true),
            pass =>
            {
                fullscreen.Draw(pass, presentPipeline, new[] { new ShaderTextureBinding("uHdr", hdrTex, Slot: 0) });
                DrawHud(pass, frame.Width, frame.Height);   // depth-disabled, alpha-blended, on top
            });

        if (exitAfterFrames > 0 && frameCount >= exitAfterFrames) host.RequestClose();
    }

    // The Window calls Dispose AFTER WaitIdle (OnUnload fires at Closing, before the
    // GPU is idle — disposing there trips destroy-in-use). The graph owns render passes
    // + offscreen images that aren't in the device's auto-freed tables, so free them
    // here; same pattern as TankArena.
    public void Dispose()
    {
        graph?.Dispose();
        fullscreen?.Dispose();
        particles?.Dispose();
        instanceBuffer?.Dispose();
    }

    // One instanced draw holds the whole scene: a flat tile per cell (checkerboard,
    // with the spawn / core / current path tinted distinctly), a raised cube per
    // placed tower, the core pillar, the marching enemies, and the hover ghost.
    private void BuildInstances()
    {
        instances.Clear();
        turretBaseInst.Clear();
        turretTopInst.Clear();
        enemyInst.Clear();
        coreInst.Clear();

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
                if (IsSpawn(cx, cz)) tint = new Vector4(0.85f, 0.62f, 0.18f, 1f);                 // spawn fronts
                if (cx == CoreCx && cz == CoreCz) tint = new Vector4(0.18f, 0.62f, 0.78f, 1f);   // core
                // Brighten the hovered tile so the pick reads even under the ghost.
                if (hoverValid && cx == hoverCx && cz == hoverCz)
                    tint = new Vector4(tint.X + 0.12f, tint.Y + 0.14f, tint.Z + 0.12f, 1f);
                instances.Add(new InstanceData(tile, tint));
            }
        }

        var core = CellCenter(CoreCx, CoreCz);
        var green = new Vector4(0.30f, 0.90f, 0.35f, 1f);
        var red = new Vector4(0.95f, 0.22f, 0.16f, 1f);
        Vector4 TurretTint(int level) => level switch   // brighter → gold as it upgrades
        {
            >= 3 => new Vector4(0.95f, 0.82f, 0.35f, 1f),
            2 => new Vector4(0.55f, 0.75f, 1.00f, 1f),
            _ => new Vector4(0.55f, 0.62f, 0.72f, 1f),
        };

        if (artLoaded)
        {
            // Core: the slowly-spinning crystal.
            coreInst.Add(new InstanceData(
                Matrix4x4.CreateScale(CoreScale) *
                Matrix4x4.CreateRotationY(gameTime * 0.5f) *
                Matrix4x4.CreateTranslation(core.X, CoreLift, core.Z),
                new Vector4(0.45f, 0.95f, 1.0f, 1f)));

            foreach (var t in towers.Values)
            {
                // Base sits flat at the cell; top yaws to aim (its model +Z barrel is
                // flipped by TurretYawFix so it points along the turret's -Z aim).
                var place = Matrix4x4.CreateTranslation(t.Pos.X, 0f, t.Pos.Z);
                turretBaseInst.Add(new InstanceData(
                    Matrix4x4.CreateScale(TowerScale) * place,
                    new Vector4(0.40f, 0.43f, 0.48f, 1f)));
                turretTopInst.Add(new InstanceData(
                    Matrix4x4.CreateScale(TowerScale) *
                    Matrix4x4.CreateRotationY(TurretYawFix) *
                    Matrix4x4.CreateFromQuaternion(t.Turret.Rotation) * place,
                    TurretTint(t.Level)));
            }

            // Enemies are the instanced skinned crowd when available; otherwise static meshes.
            for (var ei = 0; ei < enemies.Count && !skinnedLoaded; ei++)
            {
                var e = enemies[ei];
                var hp = Math.Clamp(e.Health / e.MaxHealth, 0f, 1f);
                enemyInst.Add(new InstanceData(
                    Matrix4x4.CreateScale(EnemyScale) * Matrix4x4.CreateTranslation(e.Pos.X, 0f, e.Pos.Z),
                    Vector4.Lerp(red, green, hp)));
            }
        }
        else
        {
            // Fallback to primitives (art failed to load).
            instances.Add(new InstanceData(
                Matrix4x4.CreateScale(Cell * 0.4f, 2.4f, Cell * 0.4f) * Matrix4x4.CreateTranslation(core.X, 1.2f, core.Z),
                new Vector4(0.30f, 0.85f, 1.0f, 1f)));
            foreach (var t in towers.Values)
            {
                instances.Add(new InstanceData(
                    Matrix4x4.CreateScale(Cell * 0.5f, 1.0f, Cell * 0.5f) * Matrix4x4.CreateTranslation(t.Pos.X, 0.5f, t.Pos.Z),
                    new Vector4(0.30f, 0.34f, 0.42f, 1f)));
                instances.Add(new InstanceData(t.Turret.ToMatrix(), TurretTint(t.Level)));
            }
            for (var ei = 0; ei < enemies.Count && !skinnedLoaded; ei++)
            {
                var e = enemies[ei];
                var hp = Math.Clamp(e.Health / e.MaxHealth, 0f, 1f);
                instances.Add(new InstanceData(
                    Matrix4x4.CreateScale(0.85f) * Matrix4x4.CreateTranslation(e.Pos.X, 0.45f, e.Pos.Z),
                    Vector4.Lerp(red, green, hp)));
            }
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
                && !IsSpawn(hoverCx, hoverCz)
                && !(hoverCx == CoreCx && hoverCz == CoreCz);
            var tint = buildable ? new Vector4(0.30f, 0.90f, 0.40f, 1f)
                                 : new Vector4(0.90f, 0.25f, 0.25f, 1f);
            instances.Add(new InstanceData(ghost, tint));
        }
    }

    // ── Navigation (Gate B) — local to this demo, NOT an engine primitive yet ──

    private static int Idx(int cx, int cz) => cz * GridW + cx;

    private static Vector3 Center((int cx, int cz) c) => CellCenter(c.cx, c.cz);

    private static bool IsSpawn(int cx, int cz)
    {
        foreach (var s in Spawns) if (s.cx == cx && s.cz == cz) return true;
        return false;
    }

    // True if any front has lost its route to the core — used to reject a placement.
    private bool AnyLaneBlocked()
    {
        foreach (var s in Spawns) if (FindPath(s) is null) return true;
        return false;
    }

    // Recompute every front's path to the core + the union of their cells. Called on
    // load and after every successful place/remove — never per frame.
    private void Recompute()
    {
        pathCells.Clear();
        for (var i = 0; i < Spawns.Length; i++)
        {
            // FindPath only returns null on a walled grid, which placement rejects —
            // keep the prior path as a fallback so a lane is never momentarily empty.
            paths[i] = FindPath(Spawns[i]) ?? paths[i] ?? new List<(int cx, int cz)>();
            foreach (var c in paths[i]) pathCells.Add(Idx(c.cx, c.cz));
        }
    }

    // A* over the grid from a spawn to the core: 4-connected, uniform step cost,
    // Manhattan heuristic. Occupied cells (towers) are impassable. Returns the cell
    // path spawn→core, or null if that front is fully walled off.
    private List<(int cx, int cz)>? FindPath((int cx, int cz) spawn)
    {
        int start = Idx(spawn.cx, spawn.cz), goal = Idx(CoreCx, CoreCz);
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

    // Coordinated multi-front spawn: each interval, emit one enemy from EVERY front
    // at once (up to the alive cap), so the player defends all sides simultaneously.
    // HP scales 30%/wave so later waves need upgraded / more towers.
    private void SpawnWave(float dt)
    {
        if (toSpawn <= 0) return;
        spawnTimer -= dt;
        if (spawnTimer > 0f) return;
        spawnTimer = SpawnInterval;
        var hp = EnemyMaxHp * (1f + 0.30f * (wave - 1));   // w5 ≈ 2.2× base
        for (var lane = 0; lane < Spawns.Length && toSpawn > 0 && enemies.Count < MaxAlive; lane++)
        {
            enemies.Add(new Enemy { Pos = Center(Spawns[lane]), Health = hp, MaxHealth = hp, Lane = lane, AnimPhase = (float)rng.NextDouble() * 2f });
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

    // March each enemy toward the core along ITS front's path (nearest-node lookahead,
    // same as Gate B — automatically reroutes on a re-path). Resolve deaths (→ scrap)
    // and leaks (→ a life). Backwards iteration so removal during the loop is safe.
    private void UpdateEnemies(float dt)
    {
        var core = CellCenter(CoreCx, CoreCz);
        for (var i = enemies.Count - 1; i >= 0; i--)
        {
            var e = enemies[i];

            // A killed enemy lingers as a corpse playing the Death clip, then is removed
            // (corpses don't move, leak, or get targeted).
            if (e.Dying)
            {
                e.DyingTime += dt;
                if (e.DyingTime >= enemyDeathHold) enemies.RemoveAt(i);
                continue;
            }
            if (e.Health <= 0f)
            {
                e.Dying = true;
                e.DyingTime = 0f;
                scrap += KillReward;
                EmitBurst(e.Pos + new Vector3(0f, 0.5f, 0f), new Vector4(1.0f, 0.55f, 0.18f, 1f), 16, 5.5f, 0.55f, 0.6f);
                PlaySfx(deathSfx, 0.9f + (float)rng.NextDouble() * 0.2f);
                continue;
            }

            var lane = paths[e.Lane];
            if (lane.Count >= 2)
            {
                var nearest = 0;
                var best = float.MaxValue;
                for (var k = 0; k < lane.Count; k++)
                {
                    var d = Vector3.DistanceSquared(e.Pos, Center(lane[k]));
                    if (d < best) { best = d; nearest = k; }
                }
                var target = Center(lane[Math.Min(nearest + 1, lane.Count - 1)]);
                var to = target - e.Pos;
                var dist = to.Length();
                if (dist > 1e-4f) e.Pos += to / dist * Math.Min(EnemySpeed * dt, dist);
            }

            if (Vector3.Distance(e.Pos, core) < 0.5f)
            {
                enemies.RemoveAt(i);
                lives--;
                PlaySfx(leakSfx, 1f);
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
                if (fireSfxCooldown <= 0f) { PlaySfx(fireSfx, 1f + (float)rng.NextDouble() * 0.15f); fireSfxCooldown = 0.09f; }
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
            if (dist < 0.5f)
            {
                s.Target.Health -= s.Damage;
                EmitBurst(s.Pos, new Vector4(1.0f, 0.92f, 0.45f, 1f), 5, 3f, 0.28f, 0.28f);   // impact spark
                shots.RemoveAt(i);
                continue;
            }
            s.Pos += to / dist * Math.Min(ProjSpeed * dt, dist);
        }
    }

    private Enemy? NearestEnemyInRange(Vector3 from, float range)
    {
        Enemy? best = null;
        var bestD = range * range;
        foreach (var e in enemies)
        {
            if (e.Dying || e.Health <= 0f) continue;   // don't shoot corpses
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

    // Headless-only: the --frames smoke has no mouse, so seed a defensive ring around
    // the core (one per front) to exercise aim + fire under validation. Interactive
    // play starts empty — the player must build during Prep (do nothing → leaks → defeat).
    private void SeedStarterTowers()
    {
        foreach (var (cx, cz) in new[] { (CoreCx - 2, CoreCz), (CoreCx + 2, CoreCz), (CoreCx, CoreCz - 2), (CoreCx, CoreCz + 2) })
        {
            var idx = Idx(cx, cz);
            occupied[idx] = true;
            towers[idx] = MakeTower(cx, cz);
            placedCount++;
        }
    }

    // ── Art (M3): load the CC0 meshes, best-effort ──
    //
    // Each mesh-bearing node is baked from its composed-world transform into one
    // VertexPosition3NormalTexture mesh (lifting TankArena's BakeMerge/UploadMesh) and
    // wrapped in an InstancedBatch on the SHARED cube pipeline. Any failure leaves
    // artLoaded=false and the demo falls back to primitives.
    private void LoadArt(ShaderProgramHandle worldShader, PipelineHandle worldPipe,
                         ShaderProgramHandle casterShader, PipelineHandle casterPipe)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Assets", "models");

            Mesh NodeMesh(string file, string nodeName, string meshName)
            {
                var model = new GltfStaticImporter().ImportNodes(
                    new AssetImportContext(AssetId.Parse(meshName), Path.Combine(dir, file)));
                var nodes = model.Nodes;
                var idx = Array.FindIndex(nodes, n => n.Name == nodeName);
                if (idx < 0) throw new InvalidOperationException($"node '{nodeName}' not found in {file}");
                Matrix4x4 World(int i)
                {
                    var m = nodes[i].LocalTransform;
                    for (var p = nodes[i].ParentIndex; p >= 0; p = nodes[p].ParentIndex) m *= nodes[p].LocalTransform;
                    return m;
                }
                var w = World(idx);
                return UploadMesh(BakeMerge(meshName, nodes[idx].Primitives.Select(prim => (prim.Mesh, w))));
            }

            // Each mesh gets a world batch (lit scene pass) + a caster batch (shadow pass).
            (InstancedBatch World, InstancedBatch Caster) Pair(string file, string node, string name)
            {
                var m = NodeMesh(file, node, name);
                return (new InstancedBatch(m, worldPipe, new InstanceBuffer(vk, worldShader, name + ".w")),
                        new InstancedBatch(m, casterPipe, new InstanceBuffer(vk, casterShader, name + ".c")));
            }

            (turretBaseBatch, turretBaseCaster) = Pair("turret.glb", "Turret_Cannon_Base", "art.turretBase");
            (turretTopBatch, turretTopCaster) = Pair("turret.glb", "Turret_Cannon_Top", "art.turretTop");
            (enemyBatch, enemyCaster) = Pair("enemy.glb", "Enemy_Robot_2Legs", "art.enemy");
            (coreBatch, coreCaster) = Pair("core.glb", "crystal_4", "art.core");
            artLoaded = true;
            Console.WriteLine("  art: loaded turret + enemy + core meshes (CC0)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  art models unavailable, using primitives: {ex.Message}");
            artLoaded = false;
        }
    }

    // Transform each part's primitives by its bake matrix (positions + normals) and
    // concatenate into one VertexPosition3NormalTexture mesh, reindexing as we go.
    // (Lifted from TankArena.) These meshes are < 65k verts so u16 indices suffice.
    private static MeshData BakeMerge(string name, IEnumerable<(MeshData Mesh, Matrix4x4 Xform)> parts)
    {
        var floats = new List<float>();
        var indices = new List<ushort>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var vbase = 0;
        var stride = VertexPosition3NormalTexture.Layout.Stride;
        foreach (var (md, xform) in parts)
        {
            Matrix4x4.Invert(xform, out var inv);
            var normalMatrix = Matrix4x4.Transpose(inv);
            for (var v = 0; v < md.VertexCount; v++)
            {
                var o = v * stride;
                var p = new Vector3(
                    BitConverter.ToSingle(md.VertexBytes, o),
                    BitConverter.ToSingle(md.VertexBytes, o + 4),
                    BitConverter.ToSingle(md.VertexBytes, o + 8));
                var n = new Vector3(
                    BitConverter.ToSingle(md.VertexBytes, o + 12),
                    BitConverter.ToSingle(md.VertexBytes, o + 16),
                    BitConverter.ToSingle(md.VertexBytes, o + 20));
                var pw = Vector3.Transform(p, xform);
                var nw = Vector3.Normalize(Vector3.TransformNormal(n, normalMatrix));
                min = Vector3.Min(min, pw); max = Vector3.Max(max, pw);
                floats.Add(pw.X); floats.Add(pw.Y); floats.Add(pw.Z);
                floats.Add(nw.X); floats.Add(nw.Y); floats.Add(nw.Z);
                floats.Add(BitConverter.ToSingle(md.VertexBytes, o + 24));   // u
                floats.Add(BitConverter.ToSingle(md.VertexBytes, o + 28));   // v
            }
            foreach (var idx in md.Indices) checked { indices.Add((ushort)(idx + vbase)); }
            vbase += md.VertexCount;
            if (vbase > ushort.MaxValue)
                throw new InvalidOperationException($"art mesh '{name}' exceeds u16 index range ({vbase} verts).");
        }

        var bytes = new byte[floats.Count * sizeof(float)];
        Buffer.BlockCopy(floats.ToArray(), 0, bytes, 0, bytes.Length);
        return new MeshData(name, bytes, indices.ToArray(),
            VertexPosition3NormalTexture.Layout, new Bounds3(min, max));
    }

    private Mesh UploadMesh(MeshData md)
    {
        var vb = vk.CreateVertexBuffer(
            new VertexBufferData(new VertexBufferDescription(md.Layout, md.VertexCount, GraphicsBufferUsage.Static), md.VertexBytes),
            $"{md.Name}.vb");
        var (ib, count) = md.Indices32 is { } u32
            ? (vk.CreateIndexBuffer(u32, name: $"{md.Name}.ib"), u32.Length)
            : (vk.CreateIndexBuffer(md.Indices, name: $"{md.Name}.ib"), md.Indices.Length);
        return new Mesh(md.Name, vb, ib, count, md.Bounds);
    }

    // ── Skinned enemy (M4 Gate A) — full GltfImporter (Skeleton + clips + JOINTS/
    // WEIGHTS); one animated enemy, shadow-aware via the shared cube.frag. ──
    private void LoadSkinnedEnemy(string shaderDir)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "models", "enemy.glb");
            var model = new GltfImporter().Import(new AssetImportContext(AssetId.Parse("enemy.skinned"), path));
            if (model.Animations.Length == 0) throw new InvalidOperationException("no animations");

            enemySkeleton = model.Skeleton;
            if (enemySkeleton.BoneCount != EnemyBones)
                throw new InvalidOperationException($"expected {EnemyBones} bones, got {enemySkeleton.BoneCount} (update the shaders' BONE_COUNT)");
            enemyRestPose = enemySkeleton.CreateRestPose();
            enemyPose = enemySkeleton.CreateRestPose();
            enemyBonePalette = new BonePalette(EnemyBones);
            enemyPalettePayload = new byte[MaxAlive * EnemyBones * 64];   // one world-space palette per instance
            enemyMeshNodeTransform = model.MeshNodeTransform;
            enemyWalk = FindEnemyClip(model, "Walk") ?? FindEnemyClip(model, "Run") ?? model.Animations[0];
            enemyDeath = FindEnemyClip(model, "Death") ?? enemyWalk;
            enemyDeathHold = (float)(enemyDeath.Duration > 0 ? enemyDeath.Duration : 0.8);

            // Set 3 b0: a [MaxAlive × EnemyBones] palette SSBO indexed by gl_InstanceIndex.
            var boneLayout = new UniformBlockLayout(
                TotalSize: MaxAlive * EnemyBones * 64,
                Members: new[] { new UniformBlockMember("bones", 0, MaxAlive * EnemyBones * 64, ElementStride: 64) });
            // Scene: shadow sampler (set 0, frag) + palette (set 3, vertex), 160B push → cube.frag (shadow-aware).
            var sceneIface = new ShaderInterface(
                Slots: new[]
                {
                    new DescriptorSetSlot(0, 0, ShaderResourceType.SampledImage, ShaderStages.Fragment),
                    new DescriptorSetSlot(3, 0, ShaderResourceType.StorageBuffer, ShaderStages.Vertex, BlockLayout: boneLayout),
                },
                PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex | ShaderStages.Fragment, 0, 160) });
            var sceneShader = vk.CreateShaderProgramFromSpv(
                File.ReadAllBytes(Path.Combine(shaderDir, "skinned_instanced.vert.spv")),
                File.ReadAllBytes(Path.Combine(shaderDir, "cube.frag.spv")), sceneIface, "skinned.scene");
            skinnedPipeline = vk.CreatePipeline(new PipelineDescription(sceneShader,
                VertexPosition3NormalTextureSkin4Tangent.Layout, PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite, RasterizerState.BackFaceCulling, new[] { BlendState.Disabled },
                RenderTarget: graph.GetPassSurface(scenePassHandle)), "skinned.scene");

            // Shadow: same set-3 palette (so they share one material), 64B sun-VP push, depth-only.
            var shadowIface = new ShaderInterface(
                Slots: new[] { new DescriptorSetSlot(3, 0, ShaderResourceType.StorageBuffer, ShaderStages.Vertex, BlockLayout: boneLayout) },
                PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });
            var shadowShader = vk.CreateShaderProgramFromSpv(
                File.ReadAllBytes(Path.Combine(shaderDir, "skinned_shadow_instanced.vert.spv")),
                File.ReadAllBytes(Path.Combine(shaderDir, "shadow_caster.frag.spv")), shadowIface, "skinned.shadow");
            skinnedShadowPipeline = vk.CreatePipeline(new PipelineDescription(shadowShader,
                VertexPosition3NormalTextureSkin4Tangent.Layout, PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite, RasterizerState.NoCulling, Array.Empty<BlendState>(),
                RenderTarget: graph.GetPassSurface(shadowPassHandle)), "skinned.shadow");

            var n = model.Primitives.Length;
            enemyVBs = new VertexBufferHandle[n];
            enemyIBs = new IndexBufferHandle[n];
            enemyIndexCounts = new int[n];
            for (var i = 0; i < n; i++)
            {
                var mesh = model.Primitives[i].Mesh;
                enemyVBs[i] = vk.CreateVertexBuffer(
                    new VertexBufferData(new VertexBufferDescription(mesh.Layout, mesh.VertexCount, GraphicsBufferUsage.Static), mesh.VertexBytes),
                    $"enemy.vb{i}");
                enemyIBs[i] = mesh.Indices32 is { } u32
                    ? vk.CreateIndexBuffer(u32, name: $"enemy.ib{i}")
                    : vk.CreateIndexBuffer(mesh.Indices, name: $"enemy.ib{i}");
                enemyIndexCounts[i] = mesh.IndexCount;
            }
            enemyBones = vk.CreateMaterial(sceneShader, setIndex: 3, framesInFlight: vk.MaxFramesInFlightCount, name: "enemy.bones");
            skinnedLoaded = true;
            Console.WriteLine($"  skinned crowd: {n} prim(s), {enemySkeleton.BoneCount} bones, clip '{enemyWalk.Name}', up to {MaxAlive} instanced");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  skinned enemy unavailable, enemies stay static: {ex.Message}");
            skinnedLoaded = false;
        }
    }

    private static AnimationClip? FindEnemyClip(GltfModel model, string contains)
    {
        foreach (var c in model.Animations)
            if (c.Name.Contains(contains, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }

    // Pose every enemy (phase-staggered Walk), bake each palette into WORLD space at its
    // instance slot in the shared [MaxAlive×EnemyBones] buffer, and upload the block.
    // N skeleton evals/frame (cheap at this crowd size); gl_InstanceIndex reads the slot.
    private void WriteCrowdPalette()
    {
        var walkDur = enemyWalk.Duration > 0 ? enemyWalk.Duration : 1.0;
        var deathDur = enemyDeath.Duration > 0 ? enemyDeath.Duration : 1.0;
        var f = MemoryMarshal.Cast<byte, float>(enemyPalettePayload.AsSpan());
        var count = Math.Min(enemies.Count, MaxAlive);
        for (var i = 0; i < count; i++)
        {
            var e = enemies[i];
            // Dying → play the Death clip once (clamp = settle on the final frame);
            // alive → looping Walk, phase-staggered so the crowd doesn't lockstep.
            var (clip, t) = e.Dying
                ? (enemyDeath, Math.Min(e.DyingTime, deathDur))
                : (enemyWalk, (gameTime + e.AnimPhase) % walkDur);
            enemyPose.CopyFrom(enemyRestPose);
            clip.Sample(t, enemyPose);
            enemySkeleton.ComputeBonePalette(enemyPose, enemyBonePalette);
            var model = SkinnedEnemyModel(e.Pos);
            var slot = i * EnemyBones * 16;
            for (var b = 0; b < EnemyBones; b++)
            {
                var m = enemyBonePalette.Matrices[b] * model;   // world-space skin (row-vector: skin × model)
                var o = slot + b * 16;
                f[o + 0] = m.M11; f[o + 1] = m.M12; f[o + 2] = m.M13; f[o + 3] = m.M14;
                f[o + 4] = m.M21; f[o + 5] = m.M22; f[o + 6] = m.M23; f[o + 7] = m.M24;
                f[o + 8] = m.M31; f[o + 9] = m.M32; f[o + 10] = m.M33; f[o + 11] = m.M34;
                f[o + 12] = m.M41; f[o + 13] = m.M42; f[o + 14] = m.M43; f[o + 15] = m.M44;
            }
        }
        enemyBones.WriteBuffer(vk.CurrentFrameSlot, 0, enemyPalettePayload);
    }

    // Place/scale/face one enemy at a world position (faces the core it marches to).
    private Matrix4x4 SkinnedEnemyModel(Vector3 pos)
    {
        var core = CellCenter(CoreCx, CoreCz);
        var to = new Vector3(core.X - pos.X, 0f, core.Z - pos.Z);
        var yaw = (to.LengthSquared() > 1e-4f ? MathF.Atan2(to.X, to.Z) : 0f) + SkinnedEnemyYawFix;
        var user = Matrix4x4.CreateScale(SkinnedEnemyScale)
                 * Matrix4x4.CreateRotationY(yaw)
                 * Matrix4x4.CreateTranslation(pos.X, 0f, pos.Z);
        return enemyMeshNodeTransform * user;
    }

    private void PrintStatus(float dt)
    {
        statusTimer -= dt;
        if (statusTimer > 0f) return;
        statusTimer = 2f;
        Console.WriteLine($"  [{phase} w{wave}/{TotalWaves} | lives {lives} | scrap {scrap} | enemies {enemies.Count} | towers {towers.Count}]");
    }

    // ── Juice (M2b) ──

    // A short outward burst of additive billboards that arc + fade (gravity applied in
    // particles.Update). Spawned on impacts and deaths.
    private void EmitBurst(Vector3 pos, Vector4 color, int count, float speed, float size, float life)
    {
        var fade = new Vector4(color.X, color.Y, color.Z, 0f);   // dissolve, don't pop
        for (var i = 0; i < count; i++)
        {
            var dir = new Vector3(
                (float)rng.NextDouble() * 2f - 1f,
                (float)rng.NextDouble() * 1.2f + 0.2f,   // upward bias
                (float)rng.NextDouble() * 2f - 1f);
            if (dir.LengthSquared() > 1e-4f) dir = Vector3.Normalize(dir);
            var v = dir * (speed * (0.5f + (float)rng.NextDouble()));
            particles.Emit(pos, v, color, fade, size, size * 0.3f, life * (0.6f + 0.6f * (float)rng.NextDouble()));
        }
    }

    // Synthesized one-shot SFX through the runtime's OpenAL device (best-effort —
    // null if no backend). Lifted from Runner.
    private void CreateAudio()
    {
        try { audio = (host as IAudioHost)?.AudioDevice; }
        catch { audio = null; }
        if (audio is not { } a) return;
        fireSfx = AudioSource.Create(a, a.CreateClip(SynthBlip(440f, 0.06f, 0.30f, rising: false), "bulwark.fire"), "bulwark.fire");
        deathSfx = AudioSource.Create(a, a.CreateClip(SynthBlip(180f, 0.18f, 0.50f, rising: false), "bulwark.death"), "bulwark.death");
        leakSfx = AudioSource.Create(a, a.CreateClip(SynthBlip(90f, 0.32f, 0.60f, rising: false), "bulwark.leak"), "bulwark.leak");
    }

    private void PlaySfx(AudioSource? source, float pitch)
    {
        if (source is null || audio is not { } a) return;
        source.Pitch = pitch;
        source.Gain = 0.5f;
        source.Sync(a);
        source.Stop(a);   // rewind so rapid re-triggers restart cleanly
        source.Play(a);
    }

    // Square-wave blip with a decay envelope + pitch sweep. 16-bit mono PCM, no asset.
    private static AudioClipData SynthBlip(float baseFreq, float seconds, float amplitude, bool rising)
    {
        const int rate = 44100;
        var n = (int)(rate * seconds);
        var pcm = new byte[n * 2];
        var phase = 0f;
        for (var i = 0; i < n; i++)
        {
            var u = (float)i / n;
            var env = 1f - u;
            var freq = rising ? baseFreq * (1f + 0.8f * u) : baseFreq * (1f - 0.5f * u);
            phase += freq / rate;
            if (phase >= 1f) phase -= 1f;
            var square = phase < 0.5f ? 1f : -1f;
            var s = (short)(square * env * amplitude * short.MaxValue);
            pcm[i * 2 + 0] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return new AudioClipData(SampleRate: rate, Channels: 1, BitsPerSample: 16, PcmData: pcm);
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

    // Deterministic proof of the nav invariants — no window, no device. Run via
    // `--selftest` (returns the failure count). Topology: core at the centre
    // (GridW/2, GridH/2), four edge-midpoint spawns; straight = Manhattan + 1, so the
    // west front (0,8)→(8,8) is 9 cells.
    public int RunNavSelfTest()
    {
        var failed = 0;
        void Check(string label, bool ok)
        {
            Console.WriteLine($"  {(ok ? "OK  " : "FAIL")} {label}");
            if (!ok) failed++;
        }

        // 1. Empty grid → every front reaches the core, with the right endpoints/length.
        Array.Clear(occupied);
        foreach (var s in Spawns)
            Check($"front ({s.cx},{s.cz}) reaches the core", FindPath(s) is not null);
        var west = FindPath((0, GridH / 2));
        Check("west front starts at its spawn", west is { } && west[0] == (0, GridH / 2));
        Check("west front ends at the core", west is { } && west[^1] == (CoreCx, CoreCz));
        Check("west front is 9 cells (Manhattan 8 + 1)", west is { Count: 9 });

        // 2. Box in the core (occupy its four neighbours) → NO front can reach it.
        Array.Clear(occupied);
        foreach (var (nx, nz) in new[] { (CoreCx + 1, CoreCz), (CoreCx - 1, CoreCz), (CoreCx, CoreCz + 1), (CoreCx, CoreCz - 1) })
            occupied[Idx(nx, nz)] = true;
        Check("core boxed in → all fronts blocked (wall-off detected)", Spawns.All(s => FindPath(s) is null));

        // 3. Open one side of the box → at least one front's route returns.
        occupied[Idx(CoreCx - 1, CoreCz)] = false;
        Check("opening a side restores a front", Spawns.Any(s => FindPath(s) is not null));

        // 4. A partial wall across one front still leaves a (longer) route.
        Array.Clear(occupied);
        for (var cz = 1; cz < GridH; cz++) occupied[Idx(CoreCx - 2, cz)] = true;   // gap at cz=0
        var detour = FindPath((0, GridH / 2));
        Check("partial wall still has a route", detour is not null);
        Check("detour is longer than the straight 9", detour is { } && detour.Count > 9);

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
        var onEndpoint = IsSpawn(hoverCx, hoverCz) || (hoverCx == CoreCx && hoverCz == CoreCz);

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
            // Reject a placement that would wall off ANY front: tentatively occupy,
            // re-run A* per spawn, and revert if a lane loses its route.
            if (AnyLaneBlocked())
            {
                occupied[idx] = false;
                Console.WriteLine($"  blocked: a tower at ({hoverCx}, {hoverCz}) would wall off a front");
                return;
            }
            scrap -= TowerCost;
            placedCount++;
            towers[idx] = MakeTower(hoverCx, hoverCz);
            Recompute();
            Console.WriteLine($"  built tower ({hoverCx}, {hoverCz}) — scrap {scrap}, {placedCount} towers");
        }
        else if (button == MouseButton.Right && occupied[idx])
        {
            var refund = 25 + 20 * (towers.TryGetValue(idx, out var t) ? t.Level - 1 : 0);
            occupied[idx] = false;
            towers.Remove(idx);
            placedCount--;
            scrap += refund;
            Recompute();
            Console.WriteLine($"  sold tower ({hoverCx}, {hoverCz}) (+{refund}) — scrap {scrap}, {placedCount} towers");
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
    public int Lane;          // which spawn front → which path it follows
    public float AnimPhase;   // per-enemy Walk-clip offset so the crowd doesn't lockstep
    public bool Dying;        // killed → playing the Death clip before removal
    public float DyingTime;
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
