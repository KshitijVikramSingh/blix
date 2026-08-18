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
using RTSGame.Control;
using RTSGame.Debug;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame;

internal sealed class RtsGameLoop : IGameLoop, IInputHandler, IDebuggable, IDisposable
{
    // Body feel is judged by eye, not by the benchmark: a crowd can score well on route
    // length and stall time and still look wrong. Sliders, with the crowd metrics beside
    // them so a change can be judged against something.
    /// <summary>Side of the world the game runs on, in metres.</summary>
    /// <remarks>
    /// Not <c>SimulationWorld.DefaultExtentMeters</c>, which is the 30 m square every
    /// locomotion threshold was calibrated against and must not move. This is the world the
    /// <em>game</em> is played on, and it is 600 m for reasons argued in
    /// <c>plan-rts-game.md</c> §3: at walking pace that is a map crossed in about six
    /// minutes, a sixteen per cent contested fraction, and roughly one and a half times the
    /// area of an AoE2 Large map per unit — where 1200 m was seven times emptier than one.
    /// </remarks>
    public const float DefaultWorldExtentMeters = 600f;

    /// <summary>Sim seconds per wall-clock second the game starts at.</summary>
    /// <remarks>
    /// One. Compression is the dial for wall-clock impatience and it is left alone until
    /// there is something to be impatient about — starting it above one would mean judging
    /// how the body moves through a clock that is not the one the design reasons in.
    /// </remarks>
    public const float DefaultCompression = 1f;

    private readonly BodyFeelSettings bodyFeel = new();
    private readonly ClockSettings clock = new();
    // The world this session is judging the body on. Session 2 exists because a body cannot
    // be judged on a 30 m square and then assumed to feel the same crossing a kilometre.
    private readonly float worldExtentMeters;
    private readonly CrowdMetrics crowdMetrics = new();
    private readonly ObjectTunables tunables;
    private static readonly Vector4 GrassLight = new(0.42f, 0.52f, 0.35f, 1f);
    private static readonly Vector4 GrassDark = new(0.36f, 0.45f, 0.30f, 1f);
    private static readonly Vector4 UnitColor = new(0.86f, 0.47f, 0.20f, 1f);
    private static readonly Vector4 SelectedUnitColor = new(1.00f, 0.68f, 0.22f, 1f);
    private static readonly Vector4 SelectionColor = new(0.26f, 0.86f, 0.94f, 1f);
    private static readonly Vector4 DestinationColor = new(0.98f, 0.82f, 0.32f, 1f);
    private static readonly Vector4 ObstacleColor = new(0.33f, 0.35f, 0.37f, 1f);
    private static readonly Vector4 BuildValidColor = new(0.30f, 0.84f, 0.72f, 1f);
    private static readonly Vector4 BuildRemoveColor = new(0.96f, 0.53f, 0.28f, 1f);
    private static readonly Vector4 BuildInvalidColor = new(0.82f, 0.24f, 0.22f, 1f);
    private static readonly Vector4 NavWalkableColor = new(0.30f, 0.64f, 0.62f, 1f);
    private static readonly Vector4 NavClearanceColor = new(0.82f, 0.56f, 0.23f, 1f);
    private static readonly Vector4 NavBlockedColor = new(0.70f, 0.22f, 0.20f, 1f);
    private static readonly Vector4 CongestionClearColor = new(0.18f, 0.34f, 0.30f, 1f);
    private static readonly Vector4 CongestionHotColor = new(0.95f, 0.28f, 0.18f, 1f);
    private static readonly Vector4 RoadColor = new(0.52f, 0.48f, 0.38f, 1f);
    private static readonly Vector4 RoughColor = new(0.48f, 0.40f, 0.26f, 1f);
    private static readonly Vector4 MudColor = new(0.30f, 0.23f, 0.17f, 1f);
    private static readonly Vector4 ImpassableColor = new(0.20f, 0.34f, 0.42f, 1f);
    private static readonly Vector4 AvoidanceColliderColor = new(0.20f, 0.78f, 0.92f, 1f);
    private static readonly Vector4 PlacementColliderColor = new(0.34f, 0.86f, 0.44f, 1f);
    private static readonly Vector4 InteractionColliderColor = new(0.78f, 0.38f, 0.92f, 1f);
    private static readonly Vector4 MovementColliderColor = new(0.20f, 0.42f, 0.96f, 1f);
    private static readonly Vector4 PreferredVelocityColor = new(0.22f, 0.92f, 0.66f, 1f);
    private static readonly Vector4 ResolvedVelocityColor = new(0.96f, 0.30f, 0.72f, 1f);
    private static readonly Vector4 StuckUnitColor = new(0.86f, 0.18f, 0.22f, 1f);
    private static readonly Vector4 QueuedUnitColor = new(0.24f, 0.58f, 0.90f, 1f);
    private static readonly Vector4 PathColor = new(0.96f, 0.90f, 0.30f, 1f);
    private static readonly Vector4 IdleStateColor = new(0.62f, 0.64f, 0.66f, 1f);
    private static readonly Vector4 MoveStateColor = new(0.94f, 0.54f, 0.20f, 1f);
    private static readonly Vector4 FollowStateColor = new(0.30f, 0.78f, 0.42f, 1f);
    private static readonly Vector4 PatrolStateColor = new(0.68f, 0.38f, 0.88f, 1f);
    private static readonly Vector4 ChaseStateColor = new(0.92f, 0.28f, 0.24f, 1f);
    private static readonly Vector4 FleeStateColor = new(0.24f, 0.66f, 0.92f, 1f);
    private static readonly int[] StressScenarioCounts = { 50, 200, 500, 30 };

    private readonly int exitAfterFrames;
    private LiveMovementTrace? movementTrace;
    private SimulationWorld simulation;
    private readonly SelectionController selection = new();
    private readonly Camera3D camera = new()
    {
        VerticalFieldOfView = MathF.PI * 42f / 180f,
        NearPlane = 0.25f,
        FarPlane = 150f,
    };
    // What the camera is looking at. It used to be the origin, full stop, which is fine on
    // a thirty-metre square and useless on a kilometre one: the body has to be watched
    // covering distance, and that means going with it.
    private Vector2 cameraFocus;
    private bool cameraFollowsSelection = true;
    private readonly byte[] viewProjectionBytes = new byte[64];

    private IRenderHost host = null!;
    private IGraphicsDevice graphicsDevice = null!;
    private InstanceBuffer terrainBuffer = null!;
    private InstanceBuffer unitBuffer = null!;
    private InstancedBatch terrainBatch = null!;
    // The coarse ground gets its own batch rather than sharing the debug one. They have
    // different sizes and different lifetimes, and a shared batch means the ground can run a
    // per-cell overlay past the batch's instance ceiling — which it did, immediately.
    private InstancedBatch groundBatch = null!;
    private InstanceBuffer groundBuffer = null!;
    private InstancedBatch unitBatch = null!;
    private readonly List<TerrainSurfaceLayer> terrainSurfaceLayers = new();
    private VulkanGraphicsDevice vk = null!;
    private ShaderProgramHandle worldShader;
    private PipelineHandle worldPipeline;
    private TerrainMap? renderedTerrain;
    private int renderedTerrainRevision = -1;
    private SpriteBatch selectionUi = null!;
    private TextureHandle selectionPixel = new(-1);

    private double simulationAccumulator;
    private float aspect = 16f / 9f;
    private float cameraYaw = MathF.PI * 0.25f;
    private float cameraDistance = 31f;
    private float cameraMinimumDistance = 19f;
    private float cameraMaximumDistance = 46f;
    private float mouseX;
    private float mouseY;
    private Vector2 pointerWorld;
    private bool pointerOnTerrain;
    private bool additiveSelection;
    private bool obstacleEditMode;
    private int navigationDebugMode;
    private bool colliderDebug;
    private bool velocityDebug;
    private bool pathDebug;
    private bool stateDebug;
    private bool timingDebug;
    private int stressScenarioIndex = -1;
    private int penScenarioVariant;
    private int frameCount;
    private double nextTimingReport;

    private sealed record TerrainSurfaceLayer(
        Mesh Mesh,
        InstanceBuffer Buffer,
        InstancedBatch Batch,
        Vector4 Color);

    public RtsGameLoop(
        int exitAfterFrames,
        bool traceMovement = false,
        bool startTerrainLab = false,
        bool debugAll = false,
        float extentMeters = DefaultWorldExtentMeters,
        float compression = DefaultCompression)
    {
        worldExtentMeters = extentMeters;
        clock.Compression = compression;
        // A camera sized for a thirty-metre square shows a kilometre map as a patch of
        // ground, which is the one thing this session must not do — the body has to be
        // watched crossing real distances as well as stepping round a doorway.
        cameraMinimumDistance = 19f;
        cameraMaximumDistance = MathF.Max(46f, extentMeters * 1.35f);
        cameraDistance = MathF.Min(46f, cameraMaximumDistance);
        camera.FarPlane = MathF.Max(150f, extentMeters * 3f);
        tunables = new ObjectTunables(
            bodyFeel,
            clock,
            new AvoidanceSettings(),
            new ContactSettings(),
            new CongestionSettings(),
            new RoutingSettings(),
            new GroupSettings(),
            new RecoverySettings());
        this.exitAfterFrames = exitAfterFrames;
        movementTrace = traceMovement || debugAll ? new LiveMovementTrace() : null;
        if (debugAll)
        {
            timingDebug = true;
            colliderDebug = true;
            velocityDebug = true;
            pathDebug = true;
            stateDebug = true;
            navigationDebugMode = 4;
        }
        simulation = new SimulationWorld(worldExtentMeters);
        cameraFocus = Vector2.Zero;
        if (startTerrainLab)
        {
            LoadTerrainScenario();
        }
        else
        {
            SpawnScenarioAgents(30);
        }
        if (movementTrace is not null) Console.WriteLine("  live movement trace: ON");
        if (debugAll) Console.WriteLine("  all overlays ON: congestion, colliders, velocity, paths, states, timings");
    }

    private void SpawnScenarioAgents(int count)
    {
        MovementStressScenarios.Populate(simulation, count, issueGroupMove: false);
    }

    private void LoadStressScenario(int count)
    {
        simulation = new SimulationWorld(worldExtentMeters);
        cameraFocus = Vector2.Zero;
        selection.Clear();
        simulationAccumulator = 0;
        MovementStressScenarios.Populate(simulation, count, issueGroupMove: count != 30);
        RebuildTerrainSurfaceLayersIfReady();
        Console.WriteLine($"  stress scenario: {count} agents{(count == 30 ? " (playground)" : " (group move running)")}");
    }

    private void LoadPenEscapeScenario()
    {
        simulation = new SimulationWorld(worldExtentMeters);
        cameraFocus = Vector2.Zero;
        simulationAccumulator = 0;
        stressScenarioIndex = -1;
        var seeds = new[] { 17, 73, 211, 419 };
        var seed = seeds[penScenarioVariant++ % seeds.Length];
        var blockPrimary = penScenarioVariant % 2 == 0;
        var scenario = MovementStressScenarios.PopulatePenEscape(
            simulation,
            seed: seed,
            blockPrimary: blockPrimary);
        RebuildTerrainSurfaceLayersIfReady();
        selection.ReplaceWith(scenario.Agents);
        Console.WriteLine($"  stress scenario: 30-agent pen escape, seed {seed}, " +
                          $"main exit {(blockPrimary ? "body-blocked" : "open")}, " +
                          $"{scenario.ExitCenters.Length - 1} alternate exits");
    }

    private void LoadTerrainScenario()
    {
        simulation = new SimulationWorld(worldExtentMeters);
        cameraFocus = Vector2.Zero;
        simulationAccumulator = 0;
        stressScenarioIndex = -1;
        var ids = TerrainStressScenarios.Populate(simulation);
        RebuildTerrainSurfaceLayersIfReady();
        selection.ReplaceWith(ids);
        Console.WriteLine("  terrain laboratory: road, mud, rough ground, cliff, ramp, and impassable pond");
    }

    public void OnLoad(IRenderHost host, IGraphicsDevice graphicsDevice)
    {
        this.host = host;
        this.graphicsDevice = graphicsDevice;
        vk = (VulkanGraphicsDevice)graphicsDevice;

        var meshLayout = new VertexLayout(
            Stride: VertexPosition3NormalTexture.Layout.Stride,
            Attributes: new[]
            {
                new VertexAttribute(0, VertexAttributeFormat.Float3, 0),
                new VertexAttribute(1, VertexAttributeFormat.Float3, 3 * sizeof(float)),
            });
        var shaderInterface = new ShaderInterface(
            Slots: new[] { InstanceBuffer.Slot },
            PushConstants: new[] { new PushConstantRange(ShaderStages.Vertex, 0, 64) });

        var shaderDirectory = Path.Combine(AppContext.BaseDirectory, "Shaders");
        worldShader = vk.CreateShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDirectory, "world.vert.spv")),
            File.ReadAllBytes(Path.Combine(shaderDirectory, "world.frag.spv")),
            shaderInterface,
            "rts-world");
        worldPipeline = vk.CreatePipeline(
            new PipelineDescription(
                worldShader,
                meshLayout,
                PrimitiveTopology.Triangles,
                DepthState.LessEqualWrite,
                RasterizerState.BackFaceCulling,
                new[] { BlendState.Disabled }),
            "rts-world");

        var cubeMesh = CreateMesh(vk, "terrain-cube", Cube.Vertices, Cube.Indices);
        var cylinderMesh = CreateMesh(vk, "agent-cylinder", Cylinder.Vertices, Cylinder.Indices);

        terrainBuffer = new InstanceBuffer(vk, worldShader, "rts-terrain-debug");
        unitBuffer = new InstanceBuffer(vk, worldShader, "rts-agents");
        terrainBatch = new InstancedBatch(cubeMesh, worldPipeline, terrainBuffer);
        groundBuffer = new InstanceBuffer(vk, worldShader, "rts-coarse-ground");
        groundBatch = new InstancedBatch(cubeMesh, worldPipeline, groundBuffer);
        unitBatch = new InstancedBatch(cylinderMesh, worldPipeline, unitBuffer);
        RebuildTerrainSurfaceLayers();
        selectionUi = new SpriteBatch(vk);
        selectionPixel = graphicsDevice.CreateTexture2D(
            new TextureDescription(1, 1, TextureFormat.Rgba8, SamplerDescription.PixelatedRepeat),
            new byte[] { 255, 255, 255, 255 },
            "rts-selection-pixel");

        aspect = host.LogicalSize.Width / (float)host.LogicalSize.Height;
        mouseX = host.LogicalSize.Width * 0.5f;
        mouseY = host.LogicalSize.Height * 0.5f;
        UpdateCamera();
        UpdatePointerWorld();

        // The automated Vulkan smoke exercises the same queued group-command seam
        // as player input; it does not mutate agent movement state directly.
        if (exitAfterFrames > 0)
        {
            simulation.QueueMove(AllAgentIds(), new Vector2(7f, 5f));
        }

        Console.WriteLine("RTSGame — Agent Movement Layer");
        Console.WriteLine("  left-click/drag: select   Ctrl: additive selection   right-click: group move");
        Console.WriteLine("  B: toggle block-edit mode   left-click in block mode: add/remove block");
        Console.WriteLine("  S: stop   F: follow   P: patrol to pointer   H: chase   X: flee   Backspace: despawn selected");
        Console.WriteLine("  N: nav / surface / slope / congestion overlays   C: colliders   V: velocity   K: paths   I: states");
        Console.WriteLine("  T: per-phase timings   M: live movement trace (cohort/slot, contacts, worst overlap)");
        Console.WriteLine("  G: cycle 50 / 200 / 500-agent stress scenarios");
        Console.WriteLine("  J: cycle pen-escape variants (main exit + random alternates)");
        Console.WriteLine("  L: load terrain laboratory (ramp, cliff, road, mud, rough, pond)");
        Console.WriteLine("  blue unit: yielding under crowd pressure   red unit: navigation progress failure");
        Console.WriteLine("  Q/E or arrows: rotate 90°   wheel: zoom   Esc: quit");
        Console.WriteLine($"  {simulation.Agents.Count} agents   simulation: 30 Hz fixed step");
        Console.WriteLine("  placement grid: 1.5 m   navigation grid: 0.5 m   collider hash: 2.0 m");
    }

    private AgentId[] AllAgentIds()
    {
        var ids = new AgentId[simulation.Agents.Count];
        for (var i = 0; i < ids.Length; i++) ids[i] = new AgentId(i);
        return ids;
    }

    private static Mesh CreateMesh(
        VulkanGraphicsDevice vk,
        string name,
        VertexPosition3NormalTexture[] vertices,
        ushort[] indices)
    {
        var vertexBuffer = vk.CreateVertexBuffer(VertexPosition3NormalTexture.CreateBufferData(vertices), $"{name}.vb");
        var indexBuffer = vk.CreateIndexBuffer(indices, name: $"{name}.ib");
        return new Mesh(name, vertexBuffer, indexBuffer, indices.Length,
            new Bounds3(new Vector3(-0.5f), new Vector3(0.5f)));
    }

    private void RebuildTerrainSurfaceLayersIfReady()
    {
        if (vk is not null) RebuildTerrainSurfaceLayers();
    }

    /// <summary>
    /// Largest cell count the one-mesh-per-surface ground can carry.
    /// </summary>
    /// <remarks>
    /// The surface meshes index with <c>ushort</c>, and the worst case is a map that is all
    /// one surface: half its cells land in each parity layer, six vertices each, so the
    /// ceiling is 65,535/3 cells. Beyond that the ground is drawn coarsely instead — which is
    /// also the right greybox affordance at that size, since a half-metre checkerboard over a
    /// kilometre is not a scale reference, it is noise.
    /// </remarks>
    private const int FineGroundCellLimit = 21_000;

    private bool UsesFineGround =>
        simulation.Navigation.Width * simulation.Navigation.Height <= FineGroundCellLimit;

    private void RebuildTerrainSurfaceLayers()
    {
        if (vk is null) return;
        if (terrainSurfaceLayers.Count > 0)
        {
            vk.WaitIdle();
            DisposeTerrainSurfaceLayers();
        }

        renderedTerrain = simulation.Terrain;
        renderedTerrainRevision = simulation.Terrain.Revision;
        if (!UsesFineGround) return;

        foreach (var surface in Enum.GetValues<TerrainSurface>())
        for (var parity = 0; parity < 2; parity++)
        {
            var (vertices, indices) = BuildTerrainSurfaceMesh(simulation.Terrain, surface, parity);
            if (indices.Length == 0) continue;
            var name = $"terrain-{surface.ToString().ToLowerInvariant()}-{parity}";
            var mesh = CreateMesh(vk, name, vertices, indices);
            var buffer = new InstanceBuffer(vk, worldShader, $"{name}-instance");
            terrainSurfaceLayers.Add(new TerrainSurfaceLayer(
                mesh,
                buffer,
                new InstancedBatch(mesh, worldPipeline, buffer),
                TerrainColor(surface, parity == 0)));
        }
        renderedTerrain = simulation.Terrain;
        renderedTerrainRevision = simulation.Terrain.Revision;
    }

    private static (VertexPosition3NormalTexture[] Vertices, ushort[] Indices) BuildTerrainSurfaceMesh(
        TerrainMap terrain,
        TerrainSurface surface,
        int parity)
    {
        var vertices = new List<VertexPosition3NormalTexture>();
        var indices = new List<ushort>();
        var grid = terrain.Transform;
        var size = grid.CellSize;

        VertexPosition3NormalTexture Vertex(Vector3 position, Vector3 normal, float u, float v) =>
            new(
                new GraphicsVector3(position.X, position.Y, position.Z),
                new GraphicsVector3(normal.X, normal.Y, normal.Z),
                new GraphicsVector2(u, v));

        void AddTriangle(Vector3 first, Vector3 second, Vector3 third)
        {
            var normal = Vector3.Normalize(Vector3.Cross(second - first, third - first));
            var start = checked((ushort)vertices.Count);
            vertices.Add(Vertex(first, normal, 0f, 0f));
            vertices.Add(Vertex(second, normal, 0f, 1f));
            vertices.Add(Vertex(third, normal, 1f, 0f));
            indices.Add(start);
            indices.Add((ushort)(start + 1));
            indices.Add((ushort)(start + 2));
        }

        for (var z = 0; z < grid.Height; z++)
        for (var x = 0; x < grid.Width; x++)
        {
            var cell = new GridCell(x, z);
            if (terrain.Surface(cell) != surface || (x + z) % 2 != parity) continue;
            var x0 = grid.Origin.X + x * size;
            var z0 = grid.Origin.Y + z * size;
            var x1 = x0 + size;
            var z1 = z0 + size;
            var v00 = new Vector3(x0, terrain.VertexHeight(x, z), z0);
            var v10 = new Vector3(x1, terrain.VertexHeight(x + 1, z), z0);
            var v01 = new Vector3(x0, terrain.VertexHeight(x, z + 1), z1);
            var v11 = new Vector3(x1, terrain.VertexHeight(x + 1, z + 1), z1);
            AddTriangle(v00, v01, v10);
            AddTriangle(v11, v10, v01);
        }
        return (vertices.ToArray(), indices.ToArray());
    }

    private void DisposeTerrainSurfaceLayers()
    {
        foreach (var layer in terrainSurfaceLayers)
        {
            layer.Buffer.Dispose();
            vk.DestroyVertexBuffer(layer.Mesh.VertexBuffer);
            vk.DestroyIndexBuffer(layer.Mesh.IndexBuffer);
        }
        terrainSurfaceLayers.Clear();
        renderedTerrain = null;
        renderedTerrainRevision = -1;
    }

    public void OnResize(int width, int height)
    {
        if (height > 0) aspect = width / (float)height;
    }

    public string DebugName => "rts-movement";

    public void Debug(DebugContext debug)
    {
        // Always on for this testbed — it exists to be watched. The backtick key still
        // hides the panels, and freezing them does not stop a slider taking effect.
        debug.State.Enabled = true;
        tunables.BuildControls(debug);
        crowdMetrics.Report(debug);
        ReportBodyScale(debug);
        using (debug.Scope("sim"))
        {
            debug.Values.Value("agents", simulation.Agents.Count);
            debug.Values.Value("tick", simulation.TickNumber);
            debug.Values.Value("contacts", simulation.LastContactCount);
            debug.Values.Value("flow-fields", simulation.FlowFieldBuilds);
            debug.Values.Value("astar", simulation.PathQueries);
            debug.Values.Value("detours", simulation.CongestionRepathCount);
            debug.Values.Value("congestion-peak", MathF.Round(simulation.Congestion.Peak, 1));
            var solves = Math.Max(1L, simulation.AvoidanceSolves);
            debug.Values.Value(
                "infeasible-%",
                MathF.Round(simulation.AvoidanceInfeasible * 100f / solves, 2));
            debug.Values.Value("dead-stops", simulation.AvoidanceTerrainDeadStops);
            debug.Stats.Gauge("contacts", simulation.LastContactCount);
            debug.Stats.Gauge("congestion-peak", simulation.Congestion.Peak);
        }
    }

    /// <summary>
    /// What the body sliders mean in the units the design argues in: distance, minutes, and
    /// how much map a player can answer for.
    /// </summary>
    /// <remarks>
    /// The point of this session is a decision, not a preference, and the decision is
    /// coupled: top speed sets reach, reach sets the contested fraction of the map, and the
    /// contested fraction is what the map size was chosen for. A slider that only shows
    /// "1.5" invites tuning it until the crowd looks nice and discovering afterwards that the
    /// map is now the wrong size. These are the same quantities §3 of the plan derives the
    /// map from, computed live from whatever the sliders currently say.
    /// </remarks>
    private void ReportBodyScale(DebugContext debug)
    {
        using var scope = debug.Scope("body scale");
        var speed = MathF.Max(0.01f, bodyFeel.MaximumSpeed);
        var compression = MathF.Max(0.01f, clock.Compression);
        var crossSeconds = simulation.ExtentMeters / speed;
        debug.Values.Value("map (m)", MathF.Round(simulation.ExtentMeters));
        debug.Values.Value("cross map (sim min)", MathF.Round(crossSeconds / 60f, 1));
        debug.Values.Value("cross map (wall min)", MathF.Round(crossSeconds / 60f / compression, 1));
        // How far a body gets in the time a player is willing to be away from one place.
        // This is "reach", and it is what decides how much of a map is contested.
        debug.Values.Value("reach in 40s (m)", MathF.Round(speed * 40f));
        debug.Values.Value(
            "spin-up (s)",
            MathF.Round(speed / MathF.Max(0.01f, bodyFeel.Acceleration), 2));
        debug.Values.Value(
            "stopping (m)",
            MathF.Round(speed * speed / (2f * MathF.Max(0.01f, bodyFeel.Deceleration)), 2));
        // A body that cannot complete a right-angle turn inside its own body length is
        // steering a vehicle, whatever the model looks like.
        debug.Values.Value(
            "quarter-turn (m)",
            MathF.Round(speed * (MathF.PI * 0.5f / MathF.Max(0.01f, bodyFeel.MaximumTurnSpeed)), 2));
    }

    public void OnUpdate(Time time)
    {
        // Before stepping, so a slider moved this frame is felt this frame.
        bodyFeel.Apply(simulation);
        // Compression scales wall-clock time on the way in, never the bodies. The spiral
        // guard scales with it too: a quarter-second of catch-up at 1x is a quarter-second
        // of catch-up at 3x only if the ceiling moves as well.
        var compression = Math.Clamp(clock.Compression, 0.25f, 6f);
        simulationAccumulator += Math.Clamp(time.Delta * compression, 0.0, 0.25 * compression);
        while (simulationAccumulator >= SimulationWorld.FixedDeltaSeconds)
        {
            simulation.Tick((float)SimulationWorld.FixedDeltaSeconds);
            movementTrace?.Observe(simulation, (float)SimulationWorld.FixedDeltaSeconds);
            simulationAccumulator -= SimulationWorld.FixedDeltaSeconds;
        }
        crowdMetrics.Sample(simulation);

        if (timingDebug && time.Total >= nextTimingReport)
        {
            Console.WriteLine($"  {simulation.Timings.Format(simulation.Agents.Count, simulation.TickNumber)}");
            nextTimingReport = time.Total + 1.0;
        }

        UpdateCameraFocus((float)Math.Clamp(time.Delta, 0.0, 0.25));
        UpdateCamera();
        UpdatePointerWorld();
    }

    private void UpdateCamera()
    {
        const float elevation = 0.82f;
        var horizontal = MathF.Cos(elevation) * cameraDistance;
        var focus = new Vector3(cameraFocus.X, 0f, cameraFocus.Y);
        var eye = focus + new Vector3(
            MathF.Sin(cameraYaw) * horizontal,
            MathF.Sin(elevation) * cameraDistance,
            MathF.Cos(cameraYaw) * horizontal);
        camera.Transform.Position = eye;
        camera.Transform.LookAt(focus, Vector3.UnitY);
    }

    /// <summary>
    /// Keeps the camera over whatever is selected, so a long walk can be watched rather than
    /// inferred from the moment it leaves the screen.
    /// </summary>
    private void UpdateCameraFocus(float deltaSeconds)
    {
        if (!cameraFollowsSelection) return;
        var centroid = Vector2.Zero;
        var counted = 0;
        foreach (var id in selection.Snapshot())
        {
            ref readonly var agent = ref simulation.Agents.Get(id);
            if (!agent.IsAlive) continue;
            centroid += agent.Position;
            counted++;
        }

        if (counted == 0) return;
        centroid /= counted;
        // Eased rather than snapped: a camera that tracks a crowd exactly makes the crowd
        // look stationary, which is the opposite of what a session about how movement feels
        // wants to show.
        var follow = 1f - MathF.Exp(-deltaSeconds * 2.2f);
        cameraFocus += (centroid - cameraFocus) * follow;
    }

    private void UpdatePointerWorld()
    {
        pointerOnTerrain = TryScreenToGround(mouseX, mouseY, out var world) && simulation.Terrain.Contains(world);
        if (pointerOnTerrain) pointerWorld = simulation.Terrain.ClampPosition(world);
    }

    private bool TryScreenToGround(float screenX, float screenY, out Vector2 world)
    {
        var (width, height) = host.LogicalSize;
        var ray = camera.ScreenPointToRay(screenX, screenY, width, height);
        return simulation.Terrain.TryRaycast(ray.Origin, ray.Direction, out world);
    }

    public void OnRender(Time time, RenderFrameContext frame, RenderCommandList commandList)
    {
        frameCount++;
        var viewProjection = camera.GetViewProjection(aspect);
        MemoryMarshal.Write(viewProjectionBytes.AsSpan(), in viewProjection);

        BuildTerrainInstances();
        BuildAgentInstances((float)time.Total);

        commandList.Pass(
            "greybox-kingdom",
            new RenderPassDescription(
                Target: RenderSurfaceHandle.Default,
                ClearColors: new GraphicsColor?[] { new GraphicsColor(0.63f, 0.72f, 0.78f, 1f) },
                ClearDepth: true),
            pass =>
            {
                foreach (var layer in terrainSurfaceLayers) layer.Batch.End(pass);
                groundBatch.End(pass);
                terrainBatch.End(pass);
                unitBatch.End(pass);
                DrawSelectionMarquee(pass);
            });

        if (exitAfterFrames > 0 && frameCount >= exitAfterFrames)
        {
            host.RequestClose();
        }
    }

    private void BuildTerrainInstances()
    {
        if (renderedTerrain != simulation.Terrain || renderedTerrainRevision != simulation.Terrain.Revision)
        {
            RebuildTerrainSurfaceLayers();
        }
        foreach (var layer in terrainSurfaceLayers)
        {
            layer.Batch.Begin(viewProjectionBytes);
            layer.Batch.Add(Matrix4x4.Identity, layer.Color);
        }
        groundBatch.Begin(viewProjectionBytes);
        BuildCoarseGround();
        terrainBatch.Begin(viewProjectionBytes);
        BuildObstacleInstances();
        BuildNavigationOverlay();
        BuildPathDebug();
        BuildVelocityDebug();
    }

    /// <summary>
    /// Ground for maps too large to mesh cell by cell: one flat block per checker square,
    /// coloured by the surface under its centre.
    /// </summary>
    /// <remarks>
    /// Block size scales with the map so the pattern stays a scale reference rather than
    /// becoming either a blur or a single flat colour — about a hundred and twenty squares
    /// across at any extent, which is 10 m squares on a 1200 m map. That matters more than it
    /// sounds for a session about how movement feels: perceived speed on an untextured plane
    /// comes almost entirely from crossing edges, so the ground is what makes a 1.5 m/s body
    /// look like it is walking rather than sliding.
    /// </remarks>
    private void BuildCoarseGround()
    {
        if (UsesFineGround) return;
        var terrain = simulation.Terrain;
        var grid = simulation.Navigation.Transform;
        var block = CoarseGroundBlockSize;
        var blocks = (int)MathF.Ceiling(simulation.ExtentMeters / block);
        for (var z = 0; z < blocks; z++)
        for (var x = 0; x < blocks; x++)
        {
            var center = grid.Origin + new Vector2((x + 0.5f) * block, (z + 0.5f) * block);
            if (!terrain.Contains(center)) continue;
            var color = TerrainColor(terrain.SampleSurface(center), (x + z) % 2 == 0);
            var model = Matrix4x4.CreateScale(block, 0.02f, block) *
                        Matrix4x4.CreateTranslation(center.X, terrain.SampleHeight(center) - 0.01f, center.Y);
            groundBatch.Add(model, color);
        }
    }

    /// <summary>Checker squares across the map, bounded by what one batch can hold.</summary>
    /// <remarks>
    /// A hundred and twenty is what makes the pattern read as ground rather than as a blur or
    /// a flat colour. The clamp is the instanced batch's ceiling — 16,384 instances in one
    /// Begin/End — which a square grid hits at 128 a side, so the constant is not free to grow.
    /// </remarks>
    private const int CoarseGroundBlocksPerSide = 120;

    private float CoarseGroundBlockSize
    {
        get
        {
            var cell = simulation.Navigation.Transform.CellSize;
            var target = simulation.ExtentMeters / CoarseGroundBlocksPerSide;
            return MathF.Max(cell, MathF.Round(target / cell) * cell);
        }
    }

    /// <summary>How far from the camera the per-cell debug overlay is drawn, in metres.</summary>
    /// <remarks>
    /// The overlay is one instance per navigation cell, which is 3,600 on the tuned world and
    /// 5.76M on a 1200 m one. A window is not an optimisation here, it is the difference
    /// between a debug view and a hang — and since the overlay exists to be read, drawing it
    /// where nobody is looking was never worth anything.
    /// </remarks>
    private const float NavigationOverlayRadius = 26f;

    private void BuildNavigationOverlay()
    {
        if (navigationDebugMode == 0) return;
        var grid = simulation.Navigation;
        var cellScale = grid.Transform.CellSize * 0.82f;
        var windowed = !UsesFineGround;
        var minimumX = 0;
        var minimumZ = 0;
        var maximumX = grid.Width - 1;
        var maximumZ = grid.Height - 1;
        if (windowed)
        {
            var cellSize = grid.Transform.CellSize;
            var low = (cameraFocus - new Vector2(NavigationOverlayRadius) - grid.Transform.Origin) / cellSize;
            var high = (cameraFocus + new Vector2(NavigationOverlayRadius) - grid.Transform.Origin) / cellSize;
            minimumX = Math.Clamp((int)MathF.Floor(low.X), 0, grid.Width - 1);
            minimumZ = Math.Clamp((int)MathF.Floor(low.Y), 0, grid.Height - 1);
            maximumX = Math.Clamp((int)MathF.Ceiling(high.X), 0, grid.Width - 1);
            maximumZ = Math.Clamp((int)MathF.Ceiling(high.Y), 0, grid.Height - 1);
        }

        for (var z = minimumZ; z <= maximumZ; z++)
        for (var x = minimumX; x <= maximumX; x++)
        {
            var cell = new GridCell(x, z);
            var center = grid.CellCenter(cell);
            var color = navigationDebugMode switch
            {
                2 => TerrainCostColor(simulation.Terrain.SampleSurface(center)),
                3 => SlopeColor(simulation.Terrain.SampleGrade(center)),
                4 => CongestionColor(simulation.Congestion.At(cell)),
                _ => grid.IsBlocked(cell)
                    ? NavBlockedColor
                    : grid.IsWalkable(cell, AgentDefaults.Radius)
                        ? NavWalkableColor
                        : NavClearanceColor,
            };
            var height = simulation.Terrain.SampleHeight(center);
            var model = Matrix4x4.CreateScale(cellScale, 0.018f, cellScale) *
                        Matrix4x4.CreateTranslation(center.X, height + 0.022f, center.Y);
            terrainBatch.Add(model, color);
        }
    }

    private void BuildObstacleInstances()
    {
        var grid = simulation.Placement;
        var blockWidth = grid.Transform.CellSize * 0.78f;
        const float blockHeight = 1.45f;
        foreach (var cell in grid.OccupiedCells)
        {
            var center = grid.Transform.CellCenter(cell);
            var terrainHeight = simulation.Terrain.SampleHeight(center);
            var model = Matrix4x4.CreateScale(blockWidth, blockHeight, blockWidth) *
                        Matrix4x4.CreateTranslation(center.X, terrainHeight + blockHeight * 0.5f, center.Y);
            terrainBatch.Add(model, ObstacleColor);
        }

        if (!obstacleEditMode || !pointerOnTerrain || !simulation.TryGetPlacementCell(pointerWorld, out var hoverCell)) return;
        var hoverCenter = grid.Transform.CellCenter(hoverCell);
        var isRemoval = grid.IsOccupied(hoverCell);
        var valid = simulation.CanToggleObstacle(hoverCell);
        var hoverTerrainHeight = simulation.Terrain.SampleHeight(hoverCenter);
        var hoverHeight = hoverTerrainHeight + (isRemoval ? blockHeight + 0.04f : 0.04f);
        var hoverModel = Matrix4x4.CreateScale(grid.Transform.CellSize * 0.88f, 0.065f, grid.Transform.CellSize * 0.88f) *
                         Matrix4x4.CreateTranslation(hoverCenter.X, hoverHeight, hoverCenter.Y);
        terrainBatch.Add(hoverModel, !valid ? BuildInvalidColor : isRemoval ? BuildRemoveColor : BuildValidColor);
    }

    private void DrawSelectionMarquee(RenderPassBuilder pass)
    {
        if (!selection.IsMarqueeVisible || obstacleEditMode) return;
        var minimum = Vector2.Min(selection.DragStart, selection.DragCurrent);
        var maximum = Vector2.Max(selection.DragStart, selection.DragCurrent);
        var size = maximum - minimum;
        var (width, height) = host.LogicalSize;
        var ortho = GraphicsMatrices.CreateOrthographicOffCenterVulkan(
            0f, width, height, 0f, -1f, 1f);
        selectionUi.Begin(ortho);

        var fill = new GraphicsColor(0.26f, 0.86f, 0.94f, 0.10f);
        var border = new GraphicsColor(0.26f, 0.86f, 0.94f, 0.95f);
        const float thickness = 2f;
        selectionUi.DrawSolidRect(selectionPixel,
            new Rect(minimum.X, minimum.Y, size.X, size.Y), fill);
        selectionUi.DrawSolidRect(selectionPixel,
            new Rect(minimum.X, minimum.Y, size.X, thickness), border);
        selectionUi.DrawSolidRect(selectionPixel,
            new Rect(minimum.X, maximum.Y - thickness, size.X, thickness), border);
        selectionUi.DrawSolidRect(selectionPixel,
            new Rect(minimum.X, minimum.Y, thickness, size.Y), border);
        selectionUi.DrawSolidRect(selectionPixel,
            new Rect(maximum.X - thickness, minimum.Y, thickness, size.Y), border);
        selectionUi.End(pass);
    }

    private void BuildVelocityDebug()
    {
        if (!velocityDebug) return;
        foreach (ref readonly var agent in simulation.Agents.All)
        {
            if (!agent.IsAlive || !selection.Contains(agent.Id)) continue;
            AddGroundLine(agent.Position, agent.Position + agent.PreferredVelocity * 0.32f,
                PreferredVelocityColor, 0.035f);
            AddGroundLine(agent.Position, agent.Position + agent.Velocity * 0.32f,
                ResolvedVelocityColor, 0.055f);
        }
    }

    private void BuildPathDebug()
    {
        if (!pathDebug) return;
        foreach (ref readonly var agent in simulation.Agents.All)
        {
            if (!agent.IsAlive || !selection.Contains(agent.Id)) continue;
            var start = agent.Position;
            foreach (var waypoint in simulation.GetRemainingPath(agent.Id))
            {
                AddGroundLine(start, waypoint, PathColor, 0.075f);
                start = waypoint;
            }
        }
    }

    private void AddGroundLine(Vector2 start, Vector2 end, Vector4 color, float height)
    {
        var delta = end - start;
        var length = delta.Length();
        if (length < 0.001f) return;
        var midpoint = (start + end) * 0.5f;
        var yaw = MathF.Atan2(-delta.Y, delta.X);
        var model = Matrix4x4.CreateScale(length, 0.045f, 0.075f) *
                    Matrix4x4.CreateRotationY(yaw) *
                    Matrix4x4.CreateTranslation(midpoint.X, simulation.Terrain.SampleHeight(midpoint) + height, midpoint.Y);
        terrainBatch.Add(model, color);
    }

    private static Vector4 TerrainColor(TerrainSurface surface, bool light) => surface switch
    {
        TerrainSurface.Road => light ? RoadColor * new Vector4(1.05f, 1.05f, 1.05f, 1f) : RoadColor,
        TerrainSurface.Rough => light ? RoughColor * new Vector4(1.08f, 1.08f, 1.08f, 1f) : RoughColor,
        TerrainSurface.Mud => light ? MudColor * new Vector4(1.10f, 1.10f, 1.10f, 1f) : MudColor,
        TerrainSurface.Impassable => light ? ImpassableColor * new Vector4(1.08f, 1.08f, 1.08f, 1f) : ImpassableColor,
        _ => light ? GrassLight : GrassDark,
    };

    private static Vector4 TerrainCostColor(TerrainSurface surface) => surface switch
    {
        TerrainSurface.Road => new Vector4(0.25f, 0.72f, 0.88f, 1f),
        TerrainSurface.Grass => NavWalkableColor,
        TerrainSurface.Rough => new Vector4(0.92f, 0.67f, 0.20f, 1f),
        TerrainSurface.Mud => new Vector4(0.82f, 0.32f, 0.16f, 1f),
        _ => NavBlockedColor,
    };

    private static Vector4 CongestionColor(float pressure)
    {
        // Pressure rarely exceeds a couple of units outside a hard jam, so scale
        // to that rather than to the field's ceiling or the overlay reads flat.
        var amount = Math.Clamp(pressure / 2.5f, 0f, 1f);
        return Vector4.Lerp(CongestionClearColor, CongestionHotColor, amount);
    }

    private static Vector4 SlopeColor(float grade)
    {
        var amount = Math.Clamp(grade / TerrainMap.MaximumTraversableGrade, 0f, 1f);
        return Vector4.Lerp(NavWalkableColor, NavBlockedColor, amount);
    }

    private void BuildAgentInstances(float totalSeconds)
    {
        unitBatch.Begin(viewProjectionBytes);
        var interpolation = (float)(simulationAccumulator / SimulationWorld.FixedDeltaSeconds);

        foreach (ref readonly var agent in simulation.Agents.All)
        {
            if (!agent.IsAlive) continue;
            var position = Vector2.Lerp(agent.PreviousPosition, agent.Position, interpolation);
            var height = simulation.Terrain.SampleHeight(position);
            var selected = selection.Contains(agent.Id);
            var bodyScale = agent.Radius / AgentDefaults.Radius;

            if (selected && colliderDebug)
            {
                AddColliderDisc(agent.Colliders.Avoidance, position, height + 0.012f, AvoidanceColliderColor);
                AddColliderDisc(agent.Colliders.Placement, position, height + 0.022f, PlacementColliderColor);
                AddColliderDisc(agent.Colliders.Interaction, position, height + 0.032f, InteractionColliderColor);
                AddColliderDisc(agent.Colliders.Movement, position, height + 0.042f, MovementColliderColor);
            }
            else if (selected)
            {
                var ringModel = Matrix4x4.CreateScale(bodyScale * 1.42f, 0.055f, bodyScale * 1.42f) *
                                Matrix4x4.CreateTranslation(position.X, height + 0.035f, position.Y);
                unitBatch.Add(ringModel, SelectionColor);
            }

            // Height tracks radius so a smaller unit reads as a smaller body rather
            // than a thinner column of the same stature.
            var bodyHeight = AgentDefaults.BodyHeight * bodyScale;
            var unitModel = Matrix4x4.CreateScale(bodyScale, bodyHeight, bodyScale) *
                            Matrix4x4.CreateTranslation(position.X, height + bodyHeight * 0.5f, position.Y);
            var crowdYielding = agent.IsVisiblyYielding;
            var unitColor = crowdYielding
                ? QueuedUnitColor
                : agent.StuckSeconds > 0.35f ? StuckUnitColor
                : stateDebug ? StateColor(agent.LocomotionState)
                : selected ? SelectedUnitColor : UnitColor;
            unitBatch.Add(unitModel, unitColor);

            if (selected && agent.HasDestination)
            {
                var pulse = 1f + 0.10f * MathF.Sin(totalSeconds * 7f + agent.Id.Value * 0.17f);
                var destinationHeight = simulation.Terrain.SampleHeight(agent.Destination);
                var destinationModel = Matrix4x4.CreateScale(bodyScale * 0.82f * pulse, 0.045f, bodyScale * 0.82f * pulse) *
                                       Matrix4x4.CreateTranslation(agent.Destination.X, destinationHeight + 0.03f, agent.Destination.Y);
                unitBatch.Add(destinationModel, DestinationColor);
            }
        }
    }

    private static Vector4 StateColor(AgentLocomotionState state) => state switch
    {
        AgentLocomotionState.Idle => IdleStateColor,
        AgentLocomotionState.Move => MoveStateColor,
        AgentLocomotionState.Follow => FollowStateColor,
        AgentLocomotionState.Patrol => PatrolStateColor,
        AgentLocomotionState.Chase => ChaseStateColor,
        AgentLocomotionState.Flee => FleeStateColor,
        _ => UnitColor,
    };

    private void AddColliderDisc(ColliderId id, Vector2 position, float height, Vector4 color)
    {
        var collider = simulation.Colliders.Get(id);
        if (collider.Shape.Kind != ColliderShapeKind.Circle) return;
        var bodyScale = collider.Shape.Radius / AgentDefaults.Radius;
        var model = Matrix4x4.CreateScale(bodyScale, 0.035f, bodyScale) *
                    Matrix4x4.CreateTranslation(position.X, height, position.Y);
        unitBatch.Add(model, color);
    }

    public void OnMouseMove(float x, float y, float deltaX, float deltaY)
    {
        mouseX = x;
        mouseY = y;
        selection.Update(x, y);
        UpdatePointerWorld();
    }

    public void OnMouseDown(MouseButton button)
    {
        if (obstacleEditMode)
        {
            if (button == MouseButton.Left && pointerOnTerrain) simulation.QueueToggleObstacle(pointerWorld);
            return;
        }

        if (button == MouseButton.Left)
        {
            selection.Begin(mouseX, mouseY);
        }
        else if (button == MouseButton.Right)
        {
            UpdatePointerWorld();
            if (pointerOnTerrain) simulation.QueueMove(selection.Snapshot(), pointerWorld);
        }
    }

    public void OnMouseUp(MouseButton button)
    {
        if (obstacleEditMode) return;
        if (button != MouseButton.Left) return;
        selection.Update(mouseX, mouseY);
        var (width, height) = host.LogicalSize;
        selection.End(
            simulation.Agents,
            camera.GetViewProjection(aspect),
            width,
            height,
            additiveSelection);
    }

    public void OnMouseWheel(float offsetX, float offsetY)
    {
        // Zoom steps proportionally, so pulling back over a kilometre does not take a
        // hundred notches of wheel that were sized for a thirty-metre square.
        var step = MathF.Max(2f, cameraDistance * 0.12f);
        cameraDistance = Math.Clamp(
            cameraDistance - offsetY * step,
            cameraMinimumDistance,
            cameraMaximumDistance);
    }

    private AgentId? FindNearestUnselectedTarget()
    {
        var selected = selection.Snapshot();
        if (selected.Length == 0) return null;
        var center = Vector2.Zero;
        foreach (var id in selected) center += simulation.Agents.Get(id).Position;
        center /= selected.Length;

        AgentId? nearest = null;
        var nearestDistance = float.PositiveInfinity;
        foreach (ref readonly var agent in simulation.Agents.All)
        {
            if (!agent.IsAlive || selection.Contains(agent.Id)) continue;
            var distance = Vector2.DistanceSquared(center, agent.Position);
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearest = agent.Id;
        }
        return nearest;
    }

    private void IssueTargetBehavior(Action<AgentId[], AgentId> issue, string label)
    {
        var selected = selection.Snapshot();
        var target = FindNearestUnselectedTarget();
        if (selected.Length == 0 || target is not { } targetId)
        {
            Console.WriteLine($"  {label}: select units and leave at least one unselected target");
            return;
        }
        issue(selected, targetId);
        Console.WriteLine($"  {label}: {selected.Length} unit(s) -> {targetId}");
    }

    public void OnKeyDown(Key key)
    {
        switch (key)
        {
            case Key.Escape:
                if (obstacleEditMode)
                {
                    obstacleEditMode = false;
                    Console.WriteLine("  block-edit mode: OFF");
                }
                else host.RequestClose();
                break;
            case Key.B:
                obstacleEditMode = !obstacleEditMode;
                Console.WriteLine($"  block-edit mode: {(obstacleEditMode ? "ON" : "OFF")}");
                break;
            case Key.N:
                navigationDebugMode = (navigationDebugMode + 1) % 5;
                Console.WriteLine($"  terrain overlay: {navigationDebugMode switch { 1 => "navigation", 2 => "surface cost", 3 => "slope", 4 => "congestion pressure", _ => "off" }}");
                break;
            case Key.C:
                colliderDebug = !colliderDebug;
                Console.WriteLine($"  collider debug: {(colliderDebug ? "ON" : "OFF")}");
                break;
            case Key.V:
                velocityDebug = !velocityDebug;
                Console.WriteLine($"  velocity debug: {(velocityDebug ? "ON" : "OFF")} (green preferred, pink resolved)");
                break;
            case Key.K:
                pathDebug = !pathDebug;
                Console.WriteLine($"  path debug: {(pathDebug ? "ON" : "OFF")} (selected units)");
                break;
            case Key.I:
                stateDebug = !stateDebug;
                Console.WriteLine($"  state debug: {(stateDebug ? "ON" : "OFF")} (idle grey, move orange, follow green, patrol purple, chase red, flee blue)");
                break;
            case Key.M:
                movementTrace = movementTrace is null ? new LiveMovementTrace() : null;
                Console.WriteLine($"  movement trace: {(movementTrace is null ? "OFF" : "ON")}");
                break;
            case Key.T:
                timingDebug = !timingDebug;
                nextTimingReport = 0;
                Console.WriteLine($"  timing counters: {(timingDebug ? "ON" : "OFF")}");
                break;
            case Key.G:
                stressScenarioIndex = (stressScenarioIndex + 1) % StressScenarioCounts.Length;
                LoadStressScenario(StressScenarioCounts[stressScenarioIndex]);
                break;
            case Key.J:
                LoadPenEscapeScenario();
                break;
            case Key.L:
                LoadTerrainScenario();
                break;
            case Key.Z:
                cameraFollowsSelection = !cameraFollowsSelection;
                Console.WriteLine(
                    $"  camera follows selection: {(cameraFollowsSelection ? "ON" : "OFF")}");
                break;
            case Key.R:
                cameraFocus = Vector2.Zero;
                Console.WriteLine("  camera recentred");
                break;
            case Key.S:
                simulation.QueueStop(selection.Snapshot());
                break;
            case Key.Backspace:
                var removed = simulation.DespawnAgents(selection.Snapshot());
                selection.Clear();
                Console.WriteLine($"  despawned {removed} unit(s); {simulation.Agents.LiveCount} remain");
                break;
            case Key.F:
                IssueTargetBehavior(simulation.QueueFollow, "follow");
                break;
            case Key.P:
                if (pointerOnTerrain) simulation.QueuePatrol(selection.Snapshot(), pointerWorld);
                break;
            case Key.H:
                IssueTargetBehavior(simulation.QueueChase, "chase");
                break;
            case Key.X:
                IssueTargetBehavior(simulation.QueueFlee, "flee");
                break;
            case Key.LeftControl:
            case Key.RightControl:
                additiveSelection = true;
                break;
            case Key.Q:
            case Key.Left:
                cameraYaw -= MathF.PI * 0.5f;
                break;
            case Key.E:
            case Key.Right:
                cameraYaw += MathF.PI * 0.5f;
                break;
        }
    }

    public void OnKeyUp(Key key)
    {
        if (key is Key.LeftControl or Key.RightControl) additiveSelection = false;
    }

    public void Dispose()
    {
        selectionUi?.Dispose();
        if (selectionPixel.Id >= 0) graphicsDevice?.DestroyTexture(selectionPixel);
        terrainBuffer?.Dispose();
        groundBuffer?.Dispose();
        unitBuffer?.Dispose();
        if (vk is not null) DisposeTerrainSurfaceLayers();
    }
}
