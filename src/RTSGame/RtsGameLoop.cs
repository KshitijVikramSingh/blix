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
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;
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
    /// 1.5, which is what §3 proposed and what judging it on the slider settled on: 1x is
    /// unbearably slow to sit through, 2x reads as agitated, and 1.5 is the one that felt like
    /// the world running at its own pace rather than at the player's. It is a tick-rate
    /// multiplier and not a speed multiplier, so nothing about the body or the geometry moves
    /// with it — only how long the waiting takes in the chair.
    /// </remarks>
    public const float DefaultCompression = 1.5f;

    /// <summary>Seconds a hand-assigned unit spends at each place before moving on.</summary>
    /// <remarks>
    /// Six seconds, which is long enough to read as work being done rather than as a unit
    /// bouncing, and short enough that a shuttle's rhythm is visible inside a minute of
    /// watching. Nothing depends on it — when the economy arrives, how long the work takes is
    /// the work's own number.
    /// </remarks>
    private const float PostDwellSeconds = 6f;

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
    // A store, a field and a woodlot, told apart at a glance because the whole point of the
    // low-attention mode is that a settlement is legible without reading a number.
    private static readonly Vector4 GranaryColor = new(0.82f, 0.66f, 0.30f, 1f);
    private static readonly Vector4 FarmColor = new(0.55f, 0.68f, 0.28f, 1f);
    private static readonly Vector4 WoodcutterColor = new(0.42f, 0.31f, 0.22f, 1f);
    private static readonly Vector4 DepotColor = new(0.62f, 0.55f, 0.42f, 1f);
    // Goods, wherever they are: on a cart's back or lying in the road. The same two colours for both,
    // so a heap and a load read as the same substance in two places.
    private static readonly Vector4 HouseColor = new(0.72f, 0.46f, 0.36f, 1f);
    private static readonly Vector4 GrainColor = new(0.90f, 0.78f, 0.34f, 1f);
    private static readonly Vector4 WoodColor = new(0.48f, 0.34f, 0.21f, 1f);
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
    // Per-cell detail gets its own batch: the coarse ground alone is fourteen thousand
    // instances and the ceiling is sixteen, so sharing one leaves no room for the thing the
    // detail exists to draw.
    private InstancedBatch detailBatch = null!;
    private InstanceBuffer detailBuffer = null!;
    // The ground does not change between frames and was being rebuilt from scratch on every
    // one of them: fourteen thousand blocks, each with a bilinear surface and height sample.
    // That is 18 ms of a 16 ms frame spent redrawing a field that had not moved.
    private readonly List<InstanceData> groundInstances = new();
    private readonly List<InstanceData> detailInstances = new();
    private int groundTerrainRevision = -1;
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
        float compression = DefaultCompression,
        bool startVillage = false)
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
        // What is left on the panel is what is still a question. Everything the locomotion
        // work settled — solver relaxation, congestion decay, contact yielding, the recovery
        // timings — is a constant in the code with its measurements beside it, and everything
        // that is a fixed proportion of another number is now written as that proportion.
        // What is left on the panel is what is still a question. Everything the locomotion
        // work settled — solver relaxation, congestion decay, contact yielding, the recovery
        // timings — is a constant in the code with its measurements beside it, and everything
        // that is a fixed proportion of another number is now written as that proportion.
        // Forty-five sliders to eight, and none of the eight is derivable from another.
        tunables = new ObjectTunables(
            bodyFeel,
            clock,
            new WallSettings(),
            new RoutingSettings(),
            new GroupSettings());
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
        if (startVillage)
        {
            LoadSettlementScenario();
        }
        else if (startTerrainLab)
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

    /// <summary>
    /// Drops a working settlement into the live world, in the middle of a harvest.
    /// </summary>
    /// <remarks>
    /// The same arrangement <c>--settlement</c> asserts about for a year, laid out tighter so it fits a
    /// screen: half the ring radius, and started at day 155 rather than at day zero because spring
    /// brings in no grain by design and a demo that opened there would be a demo of waiting. What there
    /// is to watch is hands standing in fields, carts deciding for themselves which yard to empty next,
    /// and the granary filling — and then the season turning and the wood going the other way.
    /// </remarks>
    private void LoadSettlementScenario()
    {
        simulation = new SimulationWorld(worldExtentMeters);
        simulation.StartAtSeconds(3100f);
        selection.Clear();
        SettlementScenarios.Populate(
            simulation, farms: 8, woodcutters: 4, carts: 4, wagons: 1, ringRadius: 19f);
        cameraFocus = Vector2.Zero;
        cameraDistance = MathF.Min(58f, cameraMaximumDistance);
        Console.WriteLine(
            $"  settlement: {simulation.Nodes.LiveCount} nodes, {simulation.Agents.LiveCount} people, " +
            $"{simulation.Date}");
        Console.WriteLine(
            "  watch the panel's settlement scope: what is stored, and how many seasons it lasts");
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

    /// <summary>
    /// Terrain to move over: the laboratory on the tuned world, a real map on a larger one.
    /// </summary>
    /// <remarks>
    /// The laboratory is a fixture — thirty metres of ramp, hills and pond that two self-tests
    /// assert against — and it stays exactly that. It is not a map, and stretching it over six
    /// hundred metres produced a few chunky rectangles adrift in a plain. Anything bigger than
    /// the world it was drawn for gets terrain drawn to its own scale instead.
    /// </remarks>
    private void LoadTerrainScenario()
    {
        simulation = new SimulationWorld(worldExtentMeters);
        cameraFocus = Vector2.Zero;
        simulationAccumulator = 0;
        stressScenarioIndex = -1;
        var laboratory = worldExtentMeters <= SimulationWorld.DefaultExtentMeters * 1.5f;
        var ids = laboratory
            ? TerrainStressScenarios.Populate(simulation)
            : WorldTerrainScenarios.Populate(simulation);
        RebuildTerrainSurfaceLayersIfReady();
        selection.ReplaceWith(ids);
        Console.WriteLine(laboratory
            ? "  terrain laboratory: road, mud, rough ground, cliff, ramp, and impassable pond"
            : "  world terrain: a ridge with one pass, a lake in a basin, a road through it");
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
        detailBuffer = new InstanceBuffer(vk, worldShader, "rts-ground-detail");
        detailBatch = new InstancedBatch(cubeMesh, worldPipeline, detailBuffer);
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
        // as player input; it does not mutate agent movement state directly. It does not do it to a
        // settlement, though: an order is an interrupt, so this correctly pulled all twelve hands out
        // of their fields and the smoke run then reported a village where nobody was working.
        if (exitAfterFrames > 0 && simulation.Nodes.LiveCount == 0)
        {
            simulation.QueueMove(AllAgentIds(), new Vector2(7f, 5f));
        }

        Console.WriteLine("RTSGame — Agent Movement Layer");
        Console.WriteLine("  left-click/drag: select   Ctrl: additive selection   right-click: group move");
        Console.WriteLine("  B: toggle block-edit mode   left-click in block mode: add/remove block");
        Console.WriteLine("  S: stop   F: follow   P: patrol to pointer   H: chase   X: flee   Backspace: despawn selected");
        Console.WriteLine("  U: post selected at pointer   O: shuttle (press twice for both ends)   Y: off work");
        Console.WriteLine("  D: granary at pointer   A: farm   Ctrl+A: house   W: woodcutter   U: post hands");
        Console.WriteLine("  houses are the only things that eat: one outside every catchment goes hungry");
        Console.WriteLine("  --village starts a working settlement mid-harvest; --compression <x> sets the clock");
        Console.WriteLine("  a standing job survives an order: give one, let go, and they go back to it");
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

    /// <summary>
    /// First end of a shuttle being laid out, waiting for the second. Interface state only —
    /// nothing in the simulation knows about it.
    /// </summary>
    private Vector2? shuttleAnchor;

    /// <summary>Builds a node at the pointer, and turns a hauler loose if the settlement has none.</summary>
    /// <remarks>
    /// A granary with nobody to carry to it is a granary that never fills, and the board only ever
    /// notices a cart that already exists — so the first store built brings its own carts. That is a
    /// convenience of this testbed and not a mechanic: when construction is real, carts are built.
    /// </remarks>
    private void Build(NodeKind kind, int capacity, Resource produces, int occupancy = 0)
    {
        if (!pointerOnTerrain) return;
        var id = simulation.AddNode(kind, pointerWorld, capacity, produces, occupancy: occupancy);
        Console.WriteLine(
            $"  {kind} at ({pointerWorld.X:F0}, {pointerWorld.Y:F0}) — " +
            $"{simulation.Nodes.LiveCount} node(s). Post hands with U; carts find their own work");
        if (kind != NodeKind.Granary) return;

        var carts = 0;
        foreach (ref readonly var agent in simulation.Agents.All)
        {
            if (agent.IsAlive && agent.CarryCapacity >= UnitType.HaulerCart.CarryCapacity) carts++;
        }

        for (var i = carts; i < 4; i++)
        {
            simulation.SpawnAgent(
                pointerWorld + new Vector2(3f + i * 1.4f, -3f), UnitType.HaulerCart);
        }

        if (carts < 4) Console.WriteLine($"  {4 - carts} hauler cart(s) turned loose");
    }

    /// <summary>Puts the selection to work standing at the pointer.</summary>
    private void AssignPost()
    {
        if (!pointerOnTerrain || selection.Selected.Count == 0) return;
        shuttleAnchor = null;
        // Posting on a building posts at the building: the place is its centre and its extent is its
        // footprint, so hands gather round the yard instead of trying to occupy the middle of it.
        var node = EconomySystem.NodeAt(simulation.Nodes, pointerWorld, radius: 3.5f);
        var at = simulation.Nodes.Contains(node) ? simulation.Nodes.Get(node).Position : pointerWorld;
        var extent = simulation.Nodes.Contains(node)
            ? simulation.Nodes.Get(node).FootprintRadius
            : 0f;
        simulation.QueueAssign(selection.Snapshot(), Assignment.Hold(at, PostDwellSeconds, extent));
        Console.WriteLine(
            $"  {selection.Selected.Count} unit(s) posted at ({at.X:F1}, {at.Y:F1})" +
            (extent > 0f ? $" — working the {simulation.Nodes.Get(node).Kind}" : string.Empty));
    }

    /// <summary>
    /// Lays out a shuttle in two presses: the first marks one end, the second the other.
    /// </summary>
    /// <remarks>
    /// A shuttle is hauling with the cargo left out, which makes this the most useful thing to
    /// be able to set by hand right now — watching twenty units run a lane both ways is how
    /// the congestion terms get judged before there is any cargo to judge them with.
    /// </remarks>
    private void AssignShuttle()
    {
        if (!pointerOnTerrain || selection.Selected.Count == 0) return;
        if (shuttleAnchor is not { } anchor)
        {
            shuttleAnchor = pointerWorld;
            Console.WriteLine(
                $"  shuttle: first end at ({pointerWorld.X:F1}, {pointerWorld.Y:F1}) — " +
                "press O again for the other end");
            return;
        }

        shuttleAnchor = null;
        simulation.QueueAssign(
            selection.Snapshot(), Assignment.Shuttle(anchor, pointerWorld, PostDwellSeconds));
        Console.WriteLine(
            $"  {selection.Selected.Count} unit(s) shuttling ({anchor.X:F1}, {anchor.Y:F1}) <-> " +
            $"({pointerWorld.X:F1}, {pointerWorld.Y:F1})");
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

        ReportJobs(debug);
        ReportEconomy(debug);
    }

    /// <summary>
    /// The one thing §8 says the interface is for: how long this lasts without you.
    /// </summary>
    /// <remarks>
    /// Autonomy time is the score, the win condition and the soak assertion all at once — "granary: 2.4
    /// seasons at current draw" — so it is displayed as seasons rather than as a stock level. A stock
    /// level tells a player a number; seasons-until-empty tells them whether to do something about it,
    /// which is the only question the low-attention mode can afford to ask.
    /// </remarks>
    private void ReportEconomy(DebugContext debug)
    {
        if (simulation.Nodes.LiveCount == 0) return;
        var date = simulation.Date;
        using var scope = debug.Scope("settlement");
        debug.Values.Value("year", date.Year + 1);
        debug.Values.Value("season", date.Season.ToString());
        debug.Values.Value("day", date.Day);

        foreach (var resource in Resources.All)
        {
            var outlook = simulation.Economy.Outlook(
                resource, simulation.Nodes, simulation.Agents, date.Season);
            debug.Values.Value($"{resource} stored", outlook.Stored);
            debug.Values.Value(
                $"{resource} lasts",
                float.IsPositiveInfinity(outlook.Seasons) ? "growing" : $"{outlook.Seasons:F1} seasons");
            debug.Stats.Gauge($"{resource} stored", outlook.Stored);
        }

        var hands = 0;
        foreach (ref readonly var node in simulation.Nodes.All) hands += node.Hands;
        debug.Values.Value("hands at work", hands);
        debug.Values.Value("unhoused", simulation.UnhousedCount);
        debug.Values.Value("nodes", simulation.Nodes.LiveCount);
        debug.Values.Value("hauls", simulation.Economy.HaulsAssigned);
        debug.Values.Value("went short", simulation.Economy.Unmet.Grain + simulation.Economy.Unmet.Wood);
    }

    /// <summary>
    /// What the standing arrangement is currently doing, which is the quantity the whole design
    /// is about: how much is happening without the player.
    /// </summary>
    /// <remarks>
    /// Four numbers rather than a list, because the useful reading is a proportion — how many
    /// of the units that have a job are actually working it, against how many are interrupted
    /// or cannot reach their place. When autonomy time becomes measurable in Session 6, this is
    /// the panel it grows out of.
    /// </remarks>
    private void ReportJobs(DebugContext debug)
    {
        var assigned = 0;
        var working = 0;
        var interrupted = 0;
        var stranded = 0;
        var legs = 0;
        foreach (ref readonly var agent in simulation.Agents.All)
        {
            if (!agent.IsAlive || !agent.Jobs.HasAssignment) continue;
            assigned++;
            legs += agent.Jobs.LegsCompleted;
            if (agent.Jobs.IsInterrupted) interrupted++;
            else if (agent.Jobs.CannotReachWork) stranded++;
            else working++;
        }

        using var scope = debug.Scope("jobs");
        debug.Values.Value("assigned", assigned);
        debug.Values.Value("working", working);
        debug.Values.Value("interrupted", interrupted);
        debug.Values.Value("cannot reach", stranded);
        debug.Values.Value("legs done", legs);
        debug.Stats.Gauge("working", working);
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
        bodyFeel.Observe(simulation);
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
                detailBatch.End(pass);
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
        RebuildGroundInstancesIfStale();
        groundBatch.Begin(viewProjectionBytes);
        groundBatch.SetInstances(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(groundInstances));
        detailBatch.Begin(viewProjectionBytes);
        detailBatch.SetInstances(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(detailInstances));
        terrainBatch.Begin(viewProjectionBytes);
        BuildObstacleInstances();
        BuildNodeInstances();
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
            // The coarse pass always draws. An earlier version skipped blocks whose cells did
            // not all agree, on the theory that the detail pass would cover them — true only
            // where the detail pass reaches. On terrain that rolls, no block agrees with itself
            // to the centimetre, so nothing was drawn beyond the detail radius and the map
            // became a few islands floating in the sky.
            var color = TerrainColor(terrain.SampleSurface(center), (x + z) % 2 == 0);
            // Sized exactly to its spacing. An earlier version grew each block by a hair to
            // close sub-pixel cracks and bought a far worse artefact: neighbours then overlap
            // in a five-centimetre band of coplanar surface, which z-fights into speckle and
            // long bright seams running the width of the map.
            groundInstances.Add(new InstanceData(
                GroundColumn(center, terrain.SampleHeight(center), block),
                color));
        }

        BuildTerrainFeatures();
    }

    /// <summary>
    /// Rebuilds the ground instance lists, but only when something they depend on has moved.
    /// </summary>
    /// <remarks>
    /// Nothing here depends on the camera any more, so a rebuild happens on a terrain edit and
    /// at no other time.
    /// </remarks>
    private void RebuildGroundInstancesIfStale()
    {
        // Terrain only. The detail used to be a window around the camera, so this also had to
        // watch where the camera was and rebuild fourteen thousand blocks whenever it crossed
        // one; patches do not move when the viewer does.
        if (groundTerrainRevision == simulation.Terrain.Revision) return;
        groundTerrainRevision = simulation.Terrain.Revision;
        groundInstances.Clear();
        detailInstances.Clear();
        BuildCoarseGround();
    }

    /// <summary>How far a cell may sit from its block's height before it is drawn itself.</summary>
    /// <remarks>
    /// Generous on purpose. Ground that rolls gently is described perfectly well by a flat
    /// plate every five metres, and demanding agreement to the centimetre means every block on
    /// a natural landscape counts as disputed — which is how the map became a few islands
    /// floating in the sky. What this is for is catching a step: a bank, a ridge, the lip of a
    /// basin, where a flat plate stops being an answer at all.
    /// </remarks>
    private const float BlockHeightTolerance = 0.35f;
    /// <summary>
    /// The parts of the map that are not plain grass at ground level, drawn as whole patches.
    /// </summary>
    /// <remarks>
    /// This used to be per-cell within forty metres of the camera, which put a visible seam
    /// across the map — a ridge stepped finely near the crowd and blocky beyond it. Any fixed
    /// radius does that and moving it only moves the seam. A patch of ground that is one surface
    /// at one height is exactly describable by a single box however large and however distant, so
    /// the terrain is merged into those patches once per edit and every one of them is drawn.
    /// Detail now exists where the ground has detail rather than where the camera happens to be.
    /// </remarks>
    private void BuildTerrainFeatures()
    {
        if (UsesFineGround) return;
        var grid = simulation.Navigation.Transform;
        var cell = grid.CellSize;
        foreach (var patch in TerrainPatches().All)
        {
            if (patch.Surface == TerrainSurface.Grass && MathF.Abs(patch.Height) < 0.02f) continue;
            if (detailInstances.Count == MaximumDetailPatches) return;
            var width = (patch.MaximumX - patch.MinimumX + 1) * cell;
            var depth = (patch.MaximumZ - patch.MinimumZ + 1) * cell;
            var centre = grid.Origin + new Vector2(
                (patch.MinimumX + (patch.MaximumX - patch.MinimumX + 1) * 0.5f) * cell,
                (patch.MinimumZ + (patch.MaximumZ - patch.MinimumZ + 1) * 0.5f) * cell);
            // Checkered off the patch's own position so a large feature still reads as ground
            // rather than as one flat slab of colour.
            var light = (patch.MinimumX / 8 + patch.MinimumZ / 8) % 2 == 0;
            var model = GroundColumn(centre, patch.Height + 0.012f, 1f);
            model = Matrix4x4.CreateScale(width, 1f, depth) * model;
            detailInstances.Add(new InstanceData(model, TerrainColor(patch.Surface, light)));
        }
    }

    private TerrainRectangles TerrainPatches()
    {
        if (terrainPatches is null || terrainPatches.Revision != simulation.Terrain.Revision)
        {
            terrainPatches = TerrainRectangles.Build(simulation.Terrain);
        }

        return terrainPatches;
    }

    private TerrainRectangles? terrainPatches;

    /// <summary>Patches one batch can carry, minus room to spare.</summary>
    private const int MaximumDetailPatches = 16_000;

    /// <summary>
    /// A patch of ground as a column standing on a floor, rather than a plate floating at its
    /// own height.
    /// </summary>
    /// <remarks>
    /// Flat plates cannot describe a slope. On level ground nobody notices, and every version of
    /// this before was only ever looked at on a plain; put a twenty-metre ridge in front of it
    /// and the ground becomes a staircase of disconnected tiles with the sky visible between
    /// them. A column from a floor below the map up to the ground's own height has no gaps
    /// between neighbours at all, whatever the step between them.
    /// </remarks>
    private static Matrix4x4 GroundColumn(Vector2 centre, float height, float width)
    {
        const float floor = -1.5f;
        var thickness = MathF.Max(0.02f, height - floor);
        return Matrix4x4.CreateScale(width, thickness, width) *
               Matrix4x4.CreateTranslation(centre.X, height - thickness * 0.5f, centre.Y);
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

    /// <summary>
    /// Draws the nodes, so a settlement is something you can see rather than infer.
    /// </summary>
    /// <remarks>
    /// A block per node, coloured by kind and raised by how full it is: a granary at capacity stands a
    /// metre taller than an empty one. It is a placeholder that happens to say the useful thing — the
    /// stock level is readable across the map without selecting anything, which is what §2's
    /// low-attention mode needs from a building before it needs a model.
    /// </remarks>
    private void BuildNodeInstances()
    {
        foreach (ref readonly var node in simulation.Nodes.All)
        {
            if (!node.IsAlive) continue;
            if (node.IsPile)
            {
                DrawPile(in node);
                continue;
            }

            var color = node.Kind switch
            {
                NodeKind.Granary => GranaryColor,
                NodeKind.Farm => FarmColor,
                NodeKind.Woodcutter => WoodcutterColor,
                NodeKind.House => HouseColor,
                _ => DepotColor,
            };
            // A store's height reads how full it is; a house's reads how full <em>it</em> is, which is
            // people. Both are the same idea: the useful number legible from across the map.
            var fullness = node.IsSink
                ? node.Occupancy <= 0 ? 0f : MathF.Min(1f, node.Occupants / (float)node.Occupancy)
                : node.Capacity <= 0 ? 0f : MathF.Min(1f, node.Stock.Total / (float)node.Capacity);
            // The same footprint the collider uses and the jobs layer arrives at, so what is drawn is
            // what a body actually walks up to.
            var width = node.FootprintRadius * 2f;
            var height = 0.9f + fullness * 1.6f;
            var ground = simulation.Terrain.SampleHeight(node.Position);
            terrainBatch.Add(
                Matrix4x4.CreateScale(width, height, width) *
                Matrix4x4.CreateTranslation(node.Position.X, ground + height * 0.5f, node.Position.Y),
                color);
        }
    }

    /// <summary>
    /// A heap of goods on the ground, low and wide, the colour of what it is.
    /// </summary>
    /// <remarks>
    /// Drawn small and flat so it reads as spillage rather than as a building — a thing to go and fetch.
    /// Which is the point: a cart lost on the road leaves this behind, and whoever gets to it first has
    /// it, so it needs to be visible from across the map without being mistaken for a granary.
    /// </remarks>
    private void DrawPile(in EconomyNode pile)
    {
        var ground = simulation.Terrain.SampleHeight(pile.Position);
        foreach (var resource in Resources.All)
        {
            var units = pile.Stock[resource];
            if (units <= 0) continue;
            // A heap of forty grain is a cart's worth and about a metre across; bigger heaps spread
            // rather than tower, because a pile of grain does.
            var spread = 0.7f + MathF.Sqrt(units / 40f) * 0.9f;
            var height = 0.22f + MathF.Min(0.5f, units / 200f);
            terrainBatch.Add(
                Matrix4x4.CreateScale(spread, height, spread) *
                Matrix4x4.CreateTranslation(pile.Position.X, ground + height * 0.5f, pile.Position.Y),
                resource == Resource.Grain ? GrainColor : WoodColor);
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

            // Width is the footprint; height is not. Height used to track radius one for one, which was
            // fine when there was a single body class and became silly the moment there were three: a
            // 0.90 m wagon is 2.4 times a villager across, and drawing it 2.4 times as tall made it a
            // five-metre crate. A cube root or so keeps the original intent — a smaller body still
            // reads as a smaller body — while letting a wide thing be wide: the wagon comes out 2.4
            // times across and a third taller, which is what a cart looks like.
            var bodyHeight = AgentDefaults.BodyHeight * MathF.Pow(bodyScale, 0.3f);
            var unitModel = Matrix4x4.CreateScale(bodyScale, bodyHeight, bodyScale) *
                            Matrix4x4.CreateTranslation(position.X, height + bodyHeight * 0.5f, position.Y);
            // A load is physically on the body, so it is drawn on the body: a cart you can see is loaded
            // is a cart you can see is worth intercepting, which is the whole of §7's return trip.
            if (agent.Jobs.CarriedUnits > 0)
            {
                var loadWidth = bodyScale * 0.72f;
                var loadHeight = 0.18f + 0.34f * MathF.Min(1f, agent.Jobs.CarriedUnits /
                    MathF.Max(1f, agent.CarryCapacity));
                unitBatch.Add(
                    Matrix4x4.CreateScale(loadWidth, loadHeight, loadWidth) *
                    Matrix4x4.CreateTranslation(
                        position.X, height + bodyHeight + loadHeight * 0.5f, position.Y),
                    agent.Jobs.Carrying == Resource.Grain ? GrainColor : WoodColor);
            }

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
            case Key.U:
                AssignPost();
                break;
            case Key.O:
                AssignShuttle();
                break;
            case Key.Y:
                simulation.QueueAssign(selection.Snapshot(), Assignment.None);
                shuttleAnchor = null;
                Console.WriteLine($"  {selection.Selected.Count} unit(s) taken off work");
                break;
            case Key.D:
                Build(NodeKind.Granary, capacity: 2000, Resource.Grain);
                break;
            case Key.A:
                // Ctrl to place the sink rather than the source: houses are the only thing in the
                // economy that consumes, so they want a key, and every letter was already taken.
                if (additiveSelection) Build(NodeKind.House, capacity: 0, Resource.Grain, occupancy: 4);
                else Build(NodeKind.Farm, capacity: 150, Resource.Grain);
                break;
            case Key.W:
                Build(NodeKind.Woodcutter, capacity: 150, Resource.Wood);
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
        detailBuffer?.Dispose();
        unitBuffer?.Dispose();
        if (vk is not null) DisposeTerrainSurfaceLayers();
    }
}
