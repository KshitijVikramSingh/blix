using System.Numerics;
using System.Diagnostics;
using RTSGame.Debug;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Commands;
using RTSGame.Simulation.Jobs;
using RTSGame.Simulation.Movement;
using RTSGame.Simulation.Navigation;
using RTSGame.Simulation.Placement;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Simulation;

internal sealed class SimulationWorld
{
    public const double FixedDeltaSeconds = 1.0 / 30.0;

    private const float ArrivalDistance = 0.035f;
    private const float WaypointArrivalDistance = 0.075f;
    /// <summary>
    /// How near a contested destination a body must be before it may settle for standing there,
    /// as a share of its own radius.
    /// </summary>
    /// <remarks>
    /// Was a flat 0.24 m, which is 0.65 of a standard body and a fifth of a wagon — so the wider a
    /// unit got, the more exactly it was required to stand on a point it physically cannot occupy
    /// to that precision. A 0.9 m body approaching an occupied destination off-axis cannot correct
    /// onto it either: at its turning circle it orbits at a couple of metres, and 0.24 m of
    /// tolerance is a target it can never hit. Written as a share, it is 0.24 m for the body every
    /// arrival threshold here was tuned against and grows with anything larger, which is the only
    /// reading under which it says the same thing about both.
    /// </remarks>
    private const float CrowdedArrivalRadiusShare = 0.6486f;
    private const float CrowdedArrivalNeighborhood = 1.25f;
    internal const int CrowdedArrivalFailedAttemptLimit = 6;
    private const int CrowdedArrivalContactFramesPerAttempt = 4;
    private const float HoldPositionTolerance = 0.02f;
    /// <summary>Range over which a body starts turning into the next route leg.</summary>
    internal static float CornerBlendDistance = 1.10f;
    /// <summary>How much of the next leg's heading is adopted at the corner.</summary>
    internal static float CornerBlendStrength = 0.70f;
    /// <summary>How hard a travelling member pulls back toward its station.</summary>
    internal static float FormationKeepingGain = 1.08f;
    /// <summary>Cap on station-keeping speed, as a fraction of travel speed.</summary>
    internal static float FormationLateralSpeedFraction = 0.45f;
    /// <summary>Per-tick blend of the newly sampled flow direction into the smoothed one.</summary>
    internal static float FlowSmoothing = 0.16f;
    /// <summary>Longest a member may lag behind newly published routing.</summary>
    internal static float MaximumRouteAdoptionDelay = 0.90f * AgentDefaults.PaceScale;
    /// <summary>How long a member holds a route before it will reconsider.</summary>
    internal static float RouteCommitmentSeconds = 3.0f * AgentDefaults.PaceScale;
    /// <summary>Stall that releases a commitment early.</summary>
    internal static float RouteReconsiderStallSeconds = 1.2f * AgentDefaults.PaceScale;
    /// <summary>Congestion re-plans allowed per tick, map-wide.</summary>
    private const int MaxRoutePlansPerTick = 2;
    /// <summary>Rejected shared-field steps before a body demands a real route.</summary>
    private const int FlowTransitRejectionLimit = 5;
    /// <summary>Preferred speed below which a body counts as expressing no intent.</summary>
    private const float NoIntentSpeed = 0.05f;
    /// <summary>Intentless seconds before a route is demanded outright.</summary>
    private const float NoIntentRepathSeconds = 0.40f;
    /// <summary>Intentless seconds after which the order is abandoned.</summary>
    /// <remarks>
    /// Short. Some destinations genuinely have no route — across an impassable
    /// pond, or the far side of a cliff with no way round — and for those this is
    /// not a recovery window, it is just how long the unit stands there looking
    /// broken before admitting it. Three retries is enough to rule out a
    /// transient, and anything longer is visible to the player as a bug.
    /// </remarks>
    private const float NoIntentSettleSeconds = 1.30f;
    /// <summary>How often a unit under orders with no route retries pathfinding.</summary>
    private const float RoutelessRepathInterval = 0.35f;
    /// <summary>Stall after which a body stops believing in the gap it is leaning on.</summary>
    /// <remarks>
    /// Longer than the detour-grant threshold, because this is a stronger claim: not "try
    /// another way" but "that way is not going to work". A person wedged in a doorway for
    /// nearly two seconds with nothing moving has learnt something.
    /// </remarks>
    internal static float ApertureAbandonSeconds = 1.8f * AgentDefaults.PaceScale;
    /// <summary>How long that refusal is held before the gap is reconsidered.</summary>
    /// <remarks>
    /// Held rather than re-derived, which is the difference between a decision and a
    /// twitch. Long enough to walk out of the queue it was standing in and commit to going
    /// round; short enough that a gap which genuinely clears is not written off for good.
    /// </remarks>
    internal static float ApertureAbandonHoldSeconds = 4f * AgentDefaults.PaceScale;
    /// <summary>Stall a unit must accumulate before it is offered a detour.</summary>
    /// <remarks>
    /// A jammed body creeps rather than stopping, so a high bar here almost never
    /// trips: at 1.5s the pen granted one detour in ten seconds, which is not a
    /// deadlock breaker, it is a coin flip.
    /// </remarks>
    internal static float CongestionRecoveryStallSeconds = 0.9f * AgentDefaults.PaceScale;
    /// <summary>Minimum gap between detour grants, map-wide.</summary>
    internal static float CongestionRecoveryInterval = 0.5f * AgentDefaults.PaceScale;
    /// <summary>How many bodies may be offered a detour at once.</summary>
    private const int CongestionRecoveryBatch = 4;
    /// <summary>
    /// Expected delay, in seconds, from routing a granted re-plan through another
    /// body — settled, stopped, or moving.
    /// </summary>
    /// <remarks>
    /// Fed to A* alongside terrain and congestion, which are also seconds, and these
    /// mean what they say: walking round a settled body costs about a second, round a
    /// stopped one rather less, past a moving one almost nothing because it will not be
    /// there. The ceiling is what a bad pile-up costs, not a stand-in for impassable.
    /// <para>
    /// They were 9 / 6 / 2 with a ceiling of 24 — between eighty and two hundred cells
    /// of detour to avoid walking near a unit, which does not express a delay, it forbids
    /// a route. See PathService.DetourAvoidanceSeconds for why they could be honest here
    /// only once the congestion field stopped remembering jams for 4.5 s.
    /// </para>
    /// </remarks>
    private static readonly float SettledBodyDelaySeconds = 1.2f * AgentDefaults.PaceScale;
    private static readonly float StalledBodyDelaySeconds = 0.8f * AgentDefaults.PaceScale;
    private static readonly float MovingBodyDelaySeconds = 0.27f * AgentDefaults.PaceScale;
    private static readonly float MaximumBodyDelaySeconds = 3.19f * AgentDefaults.PaceScale;

    private readonly Queue<AgentCommand> commands = new();
    private readonly PathPool paths = new();
    private readonly PathService pathService;
    private readonly LocalSteeringSystem steeringSystem = new();
    private readonly CollisionSystem collisionSystem = new();
    private readonly AgentSpatialIndex agentIndex;
    private readonly Dictionary<int, MoveGroup> moveGroups = new();
    private int nextMoveGroupId;
    private readonly Dictionary<GridCell, ColliderId> blockColliders = new();
    private readonly List<ColliderId> placementHits = new();
    private readonly List<ColliderId> holdPositionHits = new();
    private float congestionRecoveryCooldown;
    private int rasterizedTerrainRevision = -1;
    private int routePlansThisTick;
    private long pathfindingTicksThisTick;

    public AgentStore Agents { get; } = new();
    public TerrainMap Terrain { get; }
    public PlacementGrid Placement { get; }
    public NavigationGrid Navigation { get; }
    public CongestionField Congestion { get; }
    public ColliderWorld Colliders { get; } = new();
    public long TickNumber { get; private set; }
    public int LastContactCount { get; private set; }
    public int CongestionRepathCount { get; private set; }
    public int ImmediateRouteRepairCount { get; private set; }
    /// <summary>Stored routes re-planned because the ground they cross became congested.</summary>
    public int CongestionRerouteCount { get; private set; }
    public int FlowFieldBuilds => pathService.FlowFieldBuilds;
    /// <summary>Regions the routing hierarchy divides this map into.</summary>
    public int RegionCount => pathService.RegionCount;
    /// <summary>Region-bounded searches run so far, the unit of hierarchical work.</summary>
    public long RegionSearches => pathService.RegionSearches;
    /// <summary>Region tiles refined so far.</summary>
    public long TileRefinements => pathService.TileRefinements;
    /// <summary>Walkable ground decomposed into uniform rectangles, for the adaptive partition.</summary>
    internal WalkableRectangles DecomposeWalkable(float agentRadius) =>
        WalkableRectangles.Build(Navigation, agentRadius);

    /// <summary>Bytes of navigation raster currently held.</summary>
    public long NavigationBytes => Navigation.ResidentBytes;
    /// <summary>Regions holding a full-resolution raster rather than five numbers.</summary>
    public int ChunkedRegions => Navigation.ChunkedRegions;



    /// <summary>How far the rectangle decomposition sits above the flat optimum.</summary>
    internal RoutingFidelity MeasureRectangleFidelity(Vector2 goalPosition, float agentRadius)
    {
        if (!Navigation.TryWorldToCell(Terrain.ClampPosition(goalPosition), out var goal))
        {
            throw new ArgumentOutOfRangeException(nameof(goalPosition));
        }

        return pathService.MeasureRectangleFidelity(goal, agentRadius);
    }
    public long PathQueries => pathService.PathQueries;
    public long AvoidanceSolves => steeringSystem.Solver.Solves;
    public long AvoidanceInfeasible => steeringSystem.Solver.InfeasibleSolves;
    public long AvoidanceTerrainFallbacks => steeringSystem.Solver.TerrainFallbacks;
    public long AvoidanceTerrainDeadStops => steeringSystem.Solver.TerrainFallbackFailures;
    /// <summary>Ticks in which some agent judged its destination locally contested.</summary>
    public int CrowdedArrivalBlockCount { get; private set; }
    public float CongestionRecoveryCooldown => congestionRecoveryCooldown;
    public SimulationTimings Timings { get; } = new();
    /// <summary>Buckets in the agent broad phase — swept in full by every rebuild.</summary>
    public int AgentIndexBuckets => agentIndex.BucketCount;
    /// <summary>Index rebuilds performed during the last tick.</summary>
    public int AgentIndexRebuildsPerTick => agentIndex.Rebuilds;
    public AgentId LastCongestionRoot { get; private set; } = new(-1);
    public AgentId LastCongestionRepathAgent { get; private set; } = new(-1);

    // State the determinism fingerprint has to read and nothing else needs. Each of these
    // is something the world carries from one tick into the next without it being visible
    // on any body, which is exactly the state a check that only looked at agents was blind
    // to. See DeterminismCheck for the ledger that classifies every field of this class.
    /// <summary>Orders accepted but not yet applied. They apply on the next tick, so they are state.</summary>
    internal IReadOnlyCollection<AgentCommand> PendingCommands => commands;
    /// <summary>Live group orders, keyed by id. Iterated in id order by anything that compares worlds.</summary>
    internal IReadOnlyDictionary<int, MoveGroup> MoveGroups => moveGroups;
    /// <summary>Next id a group order will take, which two runs have to agree on.</summary>
    internal int NextMoveGroupId => nextMoveGroupId;
    /// <summary>Terrain revision the navigation raster was last built from.</summary>
    internal int RasterizedTerrainRevision => rasterizedTerrainRevision;
    /// <summary>Route plans spent against this tick's budget.</summary>
    internal int RoutePlansThisTick => routePlansThisTick;
    /// <summary>Placement cells holding a built obstacle, and the collider standing in for each.</summary>
    internal IReadOnlyDictionary<GridCell, ColliderId> BlockColliders => blockColliders;

    /// <summary>Side length in metres of the world every tuned constant was measured on.</summary>
    /// <remarks>
    /// Not a suggestion. Every threshold in <c>--selftest</c> and every constant in
    /// <c>plan-rts.md</c> is calibrated against a 30 m square at 0.5 m cells, so the
    /// default has to stay exactly here even as the extent becomes a parameter. Larger
    /// worlds are something a caller asks for explicitly, and they are measured
    /// separately.
    /// </remarks>
    internal const float DefaultExtentMeters = 30f;
    /// <summary>Fine navigation/congestion cell size, in metres.</summary>
    internal const float NavigationCellSize = 0.5f;
    /// <summary>Building-placement cell size, in metres.</summary>
    internal const float PlacementCellSize = 1.5f;
    /// <summary>Bucket size of the agent broad phase, in metres.</summary>
    internal const float AgentIndexCellSize = 1.5f;

    /// <summary>Side length of this world in metres, after snapping.</summary>
    public float ExtentMeters { get; }

    public SimulationWorld(float extentMeters = DefaultExtentMeters)
    {
        if (!(extentMeters >= PlacementCellSize))
        {
            throw new ArgumentOutOfRangeException(nameof(extentMeters));
        }
        // The two grids have to describe the same square, so the extent is snapped up
        // to a whole placement cell — which is also a whole navigation cell, 1.5 being
        // three of them. The default is already exact, so snapping is a no-op there and
        // the calibrated world is bit-for-bit the one it always was.
        var placementCells = (int)MathF.Ceiling(extentMeters / PlacementCellSize);
        var extent = placementCells * PlacementCellSize;
        var navigationCells = (int)MathF.Round(extent / NavigationCellSize);
        ExtentMeters = extent;

        var origin = new Vector2(-extent * 0.5f);
        var navigationTransform =
            new GridTransform(navigationCells, navigationCells, NavigationCellSize, origin);
        Terrain = new TerrainMap(navigationTransform);
        Placement = new PlacementGrid(
            new GridTransform(placementCells, placementCells, PlacementCellSize, origin));
        Navigation = new NavigationGrid(navigationTransform);
        Congestion = new CongestionField(navigationTransform);
        RebuildTerrainNavigation();
        pathService = new PathService(Terrain, Placement, Navigation, Congestion);
        agentIndex = new AgentSpatialIndex(
            AgentIndexCellSize, Terrain.Minimum, Terrain.Maximum);
    }

    /// <summary>Spawns a unit of the given type.</summary>
    public AgentId SpawnAgent(Vector2 position, UnitType type, FactionId? faction = null) =>
        SpawnAgent(
            position,
            faction,
            type.Radius,
            type.MaximumSpeed,
            type.NavigationRadius,
            type.TurningRadius,
            type.CarryCapacity);

    public AgentId SpawnAgent(
        Vector2 position,
        FactionId? faction = null,
        float radius = AgentDefaults.Radius,
        float maximumSpeed = AgentDefaults.MaximumSpeed,
        float navigationRadius = 0f,
        float turningRadius = 0f,
        int carryCapacity = 0)
    {
        position = Terrain.ClampPosition(position, radius + BodyFootprint.NavigationMargin);
        var resolvedFaction = faction ?? new FactionId(0);
        var id = Agents.Spawn(
            position, resolvedFaction, radius, maximumSpeed, navigationRadius, turningRadius,
            carryCapacity);
        var owner = ColliderOwner.Agent(id);
        ref var agent = ref Agents.Get(id);
        agent.Colliders = new AgentColliderSet(
            Movement: Colliders.Add(owner, resolvedFaction, ColliderLayer.Agent,
                ColliderRole.MovementSolid, ColliderShape.Circle(radius), position),
            Avoidance: Colliders.Add(owner, resolvedFaction, ColliderLayer.Agent,
                ColliderRole.Avoidance, ColliderShape.Circle(radius + 0.30f), position),
            Placement: Colliders.Add(owner, resolvedFaction, ColliderLayer.Agent,
                ColliderRole.PlacementBlocker, ColliderShape.Circle(radius + 0.10f), position),
            Interaction: Colliders.Add(owner, resolvedFaction, ColliderLayer.Agent,
                ColliderRole.Interactable | ColliderRole.Damageable,
                ColliderShape.Circle(radius + 0.06f), position));
        return id;
    }

    public void QueueMove(IEnumerable<AgentId> agents, Vector2 target)
    {
        var snapshot = agents.Distinct().OrderBy(id => id.Value).ToArray();
        if (snapshot.Length > 0)
        {
            commands.Enqueue(new MoveGroupCommand(snapshot, Terrain.ClampPosition(target)));
        }
    }

    /// <summary>
    /// Commits units to a standing assignment, or takes their current one away with
    /// <see cref="Assignment.None"/>.
    /// </summary>
    /// <remarks>
    /// Note what this is not: an order. Every other queue method here interrupts what a unit
    /// is doing; this changes what it is for. The distinction is the jobs model's whole point,
    /// and it is why there is no "manual mode" anywhere in this file to be stranded in.
    /// </remarks>
    public void QueueAssign(IEnumerable<AgentId> agents, Assignment assignment)
    {
        var snapshot = agents.Distinct().OrderBy(id => id.Value).ToArray();
        if (snapshot.Length > 0) commands.Enqueue(new AssignGroupCommand(snapshot, assignment));
    }

    /// <summary>Removes units from the world and releases everything they own.</summary>
    public int DespawnAgents(IEnumerable<AgentId> ids)
    {
        var removed = 0;
        foreach (var id in ids.Distinct().OrderBy(id => id.Value))
        {
            if (!Agents.Contains(id)) continue;
            ref var agent = ref Agents.Get(id);
            CompletePath(ref agent);
            Colliders.Remove(agent.Colliders.Movement);
            Colliders.Remove(agent.Colliders.Avoidance);
            Colliders.Remove(agent.Colliders.Placement);
            Colliders.Remove(agent.Colliders.Interaction);
            Agents.Despawn(id);
            removed++;
        }
        return removed;
    }

    public void QueueStop(IEnumerable<AgentId> agents) =>
        EnqueueAgentCommand(agents, ids => new StopGroupCommand(ids));

    public void QueueFollow(IEnumerable<AgentId> agents, AgentId target) =>
        EnqueueAgentCommand(agents, ids => new FollowGroupCommand(ids, target));

    public void QueuePatrol(IEnumerable<AgentId> agents, Vector2 end) =>
        EnqueueAgentCommand(agents, ids => new PatrolGroupCommand(ids, Terrain.ClampPosition(end)));

    public void QueueChase(IEnumerable<AgentId> agents, AgentId target) =>
        EnqueueAgentCommand(agents, ids => new ChaseGroupCommand(ids, target));

    public void QueueFlee(IEnumerable<AgentId> agents, AgentId target) =>
        EnqueueAgentCommand(agents, ids => new FleeGroupCommand(ids, target));

    private void EnqueueAgentCommand(IEnumerable<AgentId> agents, Func<AgentId[], AgentCommand> commandFactory)
    {
        var snapshot = agents.Distinct().OrderBy(id => id.Value).ToArray();
        if (snapshot.Length > 0) commands.Enqueue(commandFactory(snapshot));
    }

    public bool TryGetPlacementCell(Vector2 world, out GridCell cell) => Placement.Transform.TryWorldToCell(world, out cell);

    /// <summary>Best possible travel time for an agent to reach a point, in seconds.</summary>
    public bool TryOptimalTravelTime(AgentId id, Vector2 goal, out float seconds)
    {
        seconds = 0f;
        if (!Agents.Contains(id)) return false;
        ref readonly var agent = ref Agents.Get(id);
        return pathService.TryOptimalTravelTime(goal, agent.Position, agent.NavigationRadius, out seconds);
    }

    /// <summary>
    /// Angle in degrees between a body's travel and the axis of the constriction it is in,
    /// or -1 where the ground is open. Diagnostics: see PathService.TryFindPassageAxis.
    /// </summary>
    public float ApertureApproachDegrees(AgentId id)
    {
        ref readonly var agent = ref Agents.Get(id);
        var travelling = AgentDefaults.TravellingSpeed;
        if (agent.Velocity.LengthSquared() < travelling * travelling) return -1f;
        if (!pathService.TryFindPassageAxis(agent.Position, agent.NavigationRadius, out var axis)) return -1f;
        var heading = Vector2.Normalize(agent.Velocity);
        // The axis is undirected, so measure to whichever end the body is heading for.
        var alignment = MathF.Abs(Vector2.Dot(heading, axis));
        return MathF.Acos(Math.Clamp(alignment, 0f, 1f)) * 180f / MathF.PI;
    }

    public bool IsAgentGeometryValid(AgentId id)
    {
        if (!Agents.Contains(id)) return false;
        ref var agent = ref Agents.Get(id);
        return pathService.IsPositionNavigable(agent.Position, agent.NavigationRadius);
    }

    public bool IsAgentStepGeometryValid(AgentId id, Vector2 position)
    {
        if (!Agents.Contains(id)) return false;
        ref var agent = ref Agents.Get(id);
        return pathService.IsStepClear(agent.Position, position, agent.NavigationRadius);
    }

    public bool IsAgentContinuousStepGeometryValid(AgentId id, Vector2 position)
    {
        if (!Agents.Contains(id)) return false;
        ref var agent = ref Agents.Get(id);
        return pathService.IsContinuousStepClear(agent.Position, position, agent.NavigationRadius);
    }

    /// <summary>
    /// Seconds a body of this size takes to get from one point to another, through terrain, roads
    /// and whatever is currently jammed.
    /// </summary>
    /// <remarks>
    /// The query a catchment is made of and the one hauling will be priced in. <b>Route seconds are
    /// at the router's reference pace, not at the asking body's</b> — one field serves every unit
    /// precisely because a body's own speed scales every leg equally. So a budget expressed at some
    /// other pace has to be converted before it is compared against this, and getting that backwards
    /// sizes a catchment by the ratio of the two speeds: 63% too large for a hauler.
    /// </remarks>
    internal bool TryTravelSeconds(Vector2 from, Vector2 to, float navigationRadius, out float seconds) =>
        pathService.TryOptimalTravelTime(to, from, navigationRadius, out seconds);

    /// <summary>The pace route seconds are denominated at, for converting a budget into them.</summary>
    internal static float RouteReferenceSpeed => AgentDefaults.WorldPace;

    public ReadOnlySpan<Vector2> GetRemainingPath(AgentId id)
    {
        if (!Agents.Contains(id)) return ReadOnlySpan<Vector2>.Empty;
        ref var agent = ref Agents.Get(id);
        if (!agent.Path.IsValid) return ReadOnlySpan<Vector2>.Empty;
        var waypoints = paths.Get(agent.Path);
        return agent.WaypointIndex < waypoints.Length
            ? waypoints[agent.WaypointIndex..]
            : ReadOnlySpan<Vector2>.Empty;
    }

    public void QueueToggleObstacle(Vector2 world)
    {
        if (TryGetPlacementCell(world, out var cell)) commands.Enqueue(new ToggleObstacleCommand(cell));
    }

    public bool CanToggleObstacle(GridCell cell)
    {
        if (!Placement.Transform.Contains(cell)) return false;
        if (Placement.IsOccupied(cell)) return true;

        var center = Placement.Transform.CellCenter(cell);
        var halfExtents = new Vector2(Placement.Transform.CellSize * 0.5f - 0.025f);
        if (!Terrain.CanPlace(center, halfExtents)) return false;
        Colliders.QueryAabb(
            center,
            halfExtents,
            new ColliderQueryFilter(
                ColliderRole.PlacementBlocker,
                ColliderLayer.Agent | ColliderLayer.Structure,
                RelationMask.All),
            placementHits);
        return placementHits.Count == 0;
    }

    public void RebuildTerrainNavigation()
    {
        NavigationRasterizer.Rebuild(Placement, Navigation, Terrain);
        rasterizedTerrainRevision = Terrain.Revision;
    }

    public void Tick(float deltaSeconds)
    {
        var totalStart = Stopwatch.GetTimestamp();
        if (Terrain.Revision != rasterizedTerrainRevision) RebuildTerrainNavigation();
        congestionRecoveryCooldown = MathF.Max(0f, congestionRecoveryCooldown - deltaSeconds);
        var recoveryAgents = Agents.MutableSpan();
        for (var i = 0; i < recoveryAgents.Length; i++)
        {
            if (!recoveryAgents[i].IsAlive) continue;
            recoveryAgents[i].CongestionYieldSeconds = MathF.Max(
                0f,
                recoveryAgents[i].CongestionYieldSeconds - deltaSeconds);
            recoveryAgents[i].CrowdPressureSeconds = MathF.Max(
                0f,
                recoveryAgents[i].CrowdPressureSeconds - deltaSeconds);
            recoveryAgents[i].AbandonedApertureSeconds = MathF.Max(
                0f,
                recoveryAgents[i].AbandonedApertureSeconds - deltaSeconds);
            recoveryAgents[i].HadAgentContactThisTick = false;
            recoveryAgents[i].CrowdedArrivalBlockedThisTick = false;
            recoveryAgents[i].ArrivedThisTick = false;
            recoveryAgents[i].SteeringStepRejectedThisTick = false;
            recoveryAgents[i].PreferredStepRejectedThisTick = false;
            recoveryAgents[i].AvoidanceBlockedThisTick = false;
        }
        agentIndex.ResetRebuildCounters();
        var congestionStart = Stopwatch.GetTimestamp();
        Congestion.Update(Navigation, Agents.All, deltaSeconds);
        Timings.Record(SimulationPhase.Congestion, Stopwatch.GetTimestamp() - congestionStart);
        routePlansThisTick = 0;
        pathfindingTicksThisTick = 0;

        var phaseStart = Stopwatch.GetTimestamp();
        ApplyCommands();
        Timings.Record(SimulationPhase.Commands, Stopwatch.GetTimestamp() - phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        UpdateJobs(deltaSeconds);
        Timings.Record(SimulationPhase.Jobs, Stopwatch.GetTimestamp() - phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        UpdateBehaviors(deltaSeconds);
        UpdateGroupFormations();
        Timings.Record(SimulationPhase.Behaviors, Stopwatch.GetTimestamp() - phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        RefreshInvalidPaths();
        Timings.Record(SimulationPhase.NavigationRefresh, Stopwatch.GetTimestamp() - phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        PreparePreferredVelocities();
        Timings.Record(SimulationPhase.PreferredVelocity, Stopwatch.GetTimestamp() - phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        steeringSystem.Update(Agents, agentIndex, pathService, deltaSeconds);
        Timings.Record(SimulationPhase.LocalSteering, Stopwatch.GetTimestamp() - phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        IntegrateMovement(deltaSeconds);
        Timings.Record(SimulationPhase.Integration, Stopwatch.GetTimestamp() - phaseStart);

        var syncTicks = 0L;
        phaseStart = Stopwatch.GetTimestamp();
        SyncAgentColliders();
        syncTicks += Stopwatch.GetTimestamp() - phaseStart;

        phaseStart = Stopwatch.GetTimestamp();
        LastContactCount = collisionSystem.Resolve(Agents, Colliders, agentIndex, pathService, Terrain);
        ConstrainAgentsToTerrain();
        Timings.Record(SimulationPhase.CollisionResolution, Stopwatch.GetTimestamp() - phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        SyncAgentColliders();
        syncTicks += Stopwatch.GetTimestamp() - phaseStart;
        Timings.Record(SimulationPhase.ColliderSync, syncTicks);

        phaseStart = Stopwatch.GetTimestamp();
        UpdateStuckAgents(deltaSeconds);
        Timings.Record(SimulationPhase.CongestionRecovery, Stopwatch.GetTimestamp() - phaseStart);
        // Recorded once per tick, like every other phase. It used to be recorded
        // per pathfinding call, so the column showed the average cost of one A*
        // query sitting alongside per-tick figures — directly comparable in
        // appearance and not at all in meaning. It read as 4.3ms at fifty agents
        // and 2.9ms at five hundred, which is nonsense as a tick cost and exactly
        // what you would expect of a per-call one.
        Timings.Record(SimulationPhase.Pathfinding, pathfindingTicksThisTick);
        // A subset of steering and collision, not a phase alongside them: the index is
        // rebuilt inside both, so this column double-counts against them by design and
        // exists to say how much of them is bucket sweeping rather than avoidance.
        Timings.Record(SimulationPhase.AgentIndex, agentIndex.RebuildTicks);
        TickNumber++;
        Timings.Record(SimulationPhase.TotalTick, Stopwatch.GetTimestamp() - totalStart);
    }

    private void ApplyCommands()
    {
        var placementChanged = false;
        while (commands.TryDequeue(out var command))
        {
            switch (command)
            {
                case MoveGroupCommand move:
                    ApplyMove(move);
                    break;
                case StopGroupCommand stop:
                    ApplyStop(stop.Agents);
                    break;
                case FollowGroupCommand follow:
                    ApplyTargetBehavior(follow.Agents, follow.Target, AgentLocomotionState.Follow);
                    break;
                case PatrolGroupCommand patrol:
                    ApplyPatrol(patrol);
                    break;
                case ChaseGroupCommand chase:
                    ApplyTargetBehavior(chase.Agents, chase.Target, AgentLocomotionState.Chase);
                    break;
                case FleeGroupCommand flee:
                    ApplyTargetBehavior(flee.Agents, flee.Target, AgentLocomotionState.Flee);
                    break;
                case AssignGroupCommand assign:
                    ApplyAssignment(assign);
                    break;
                case ToggleObstacleCommand toggle:
                    placementChanged |= ApplyObstacleToggle(toggle.Cell);
                    break;
            }

            // Anything that is an order rather than a commitment interrupts whatever the unit
            // was committed to, and leaves the commitment alone. This is the single place that
            // happens: a unit walking to its own workplace goes through BeginSoloMove and
            // never comes past here, so the jobs layer cannot interrupt itself.
            if (command is AssignGroupCommand or ToggleObstacleCommand) continue;
            foreach (var id in OrderedAgents(command))
            {
                if (Agents.Contains(id)) JobSystem.Interrupt(ref Agents.Get(id));
            }
        }
        if (placementChanged) RebuildTerrainNavigation();
    }

    /// <summary>Who an order was addressed to, whatever kind of order it is.</summary>
    private static IEnumerable<AgentId> OrderedAgents(AgentCommand command) => command switch
    {
        MoveGroupCommand move => move.Agents,
        StopGroupCommand stop => stop.Agents,
        FollowGroupCommand follow => follow.Agents,
        PatrolGroupCommand patrol => patrol.Agents,
        ChaseGroupCommand chase => chase.Agents,
        FleeGroupCommand flee => flee.Agents,
        _ => Array.Empty<AgentId>(),
    };

    private void ApplyAssignment(AssignGroupCommand assign)
    {
        foreach (var id in assign.Agents)
        {
            if (!Agents.Contains(id)) continue;
            ref var agent = ref Agents.Get(id);
            JobSystem.Assign(ref agent, assign.Assignment);
            // A unit taken off work stops where it stands rather than finishing the walk it
            // was on. Being given work, on the other hand, does not need a halt: the jobs
            // layer will send it where it is now needed on this same tick.
            if (assign.Assignment.Kind == AssignmentKind.None && agent.HasDestination)
            {
                HaltMovement(ref agent);
            }
        }
    }

    /// <summary>
    /// Runs the jobs layer for every unit, and carries out whatever it asks for.
    /// </summary>
    /// <remarks>
    /// Between commands and behaviours, which is the only place it can go: after orders, so
    /// that an order issued this tick suspends the assignment before it can act on it, and
    /// before locomotion, so that a unit sent somewhere new this tick spends no frame standing
    /// still. A unit with no assignment costs one branch.
    /// </remarks>
    private void UpdateJobs(float deltaSeconds)
    {
        var agents = Agents.MutableSpan();
        for (var i = 0; i < agents.Length; i++)
        {
            if (!agents[i].IsAlive) continue;
            var place = JobSystem.AsksAboutItsPlace(in agents[i])
                ? ClassifyPlace(in agents[i])
                : PlaceCondition.Open;
            var request = JobSystem.Advance(ref agents[i], deltaSeconds, place);
            if (request.Step != JobStep.WalkTo) continue;
            BeginSoloMove(ref agents[i], Terrain.ClampPosition(request.Target));
        }
    }

    private void ApplyMove(MoveGroupCommand move)
    {
        var members = move.Agents
            .Where(Agents.Contains)
            .Distinct()
            .OrderBy(id => id.Value)
            .ToArray();
        if (members.Length == 0) return;

        var target = Terrain.ClampPosition(move.Target);
        var group = members.Length > 1
            ? MoveGroup.Create(++nextMoveGroupId, target, members, Agents, pathService)
            : null;
        if (group is not null) moveGroups[group.Id] = group;

        for (var slot = 0; slot < members.Length; slot++)
        {
            ref var agent = ref Agents.Get(members[slot]);
            if (group is null)
            {
                BeginSoloMove(ref agent, target);
                continue;
            }

            DetachFromMoveGroup(ref agent);
            agent.LocomotionState = AgentLocomotionState.Move;
            agent.BehaviorTarget = new AgentId(-1);
            agent.ReturningToHold = false;
            agent.CrowdedArrivalAttempts = 0;
            agent.CrowdedArrivalContactFrames = 0;
            agent.RepathCooldown = 0f;
            agent.MoveGroupId = group.Id;
            agent.GroupSlot = group.Slots[slot];
            agent.FormationOffset = group.SlotOffset(slot);
            agent.ApproachingSlot = false;
            agent.UsesFlowTransit = false;

            // Transit is a cohort behaviour. Members steer directly down one
            // shared cost field instead of each materialising a polyline through
            // it: that is a single Dijkstra for the whole group instead of N
            // path builds in the frame the order is issued, and it lets the
            // crowd split across whatever exits are actually cheapest. Slots are
            // claimed on arrival, not before.
            agent.RequestedDestination = target;
            if (!BeginFlowTransit(ref agent, target))
            {
                agent.ApproachingSlot = true;
                agent.RequestedDestination = agent.GroupSlot;
                AssignPath(ref agent, agent.GroupSlot);
            }
        }
    }

    /// <summary>What the ground where a unit's work is looks like, for the jobs layer.</summary>
    /// <remarks>
    /// The same two questions <see cref="TryReturnToHold"/> asks about a hold point, and asked
    /// the same way: the router for whether a body this size fits, then the collider world for
    /// whether somebody is already there. Only called on the tick a walk has ended short, so the
    /// collider query is paid a handful of times a second across a whole settlement rather than
    /// once per worker per tick.
    /// </remarks>
    private PlaceCondition ClassifyPlace(in AgentState agent)
    {
        var place = agent.Jobs.Place;
        if (!pathService.IsPositionNavigable(place, agent.NavigationRadius))
        {
            return PlaceCondition.Unreachable;
        }

        Colliders.QueryCircle(
            place,
            agent.Radius + 0.08f,
            new ColliderQueryFilter(
                ColliderRole.MovementSolid,
                ColliderLayer.Agent | ColliderLayer.Structure,
                RelationMask.Ally | RelationMask.Neutral | RelationMask.Enemy,
                ColliderOwner.Agent(agent.Id),
                agent.Faction),
            holdPositionHits);
        return holdPositionHits.Count > 0 ? PlaceCondition.Crowded : PlaceCondition.Open;
    }

    /// <summary>
    /// Sends one body to a point on its own — no cohort, no shared field, its own route.
    /// </summary>
    /// <remarks>
    /// Extracted from the single-member branch of <see cref="ApplyMove"/> because the jobs
    /// layer needs exactly this and must not go through the command path: a command marks the
    /// unit as interrupted, and a unit walking to its own workplace is not being interrupted
    /// by anybody. The target is expected to be clamped to the terrain already.
    /// </remarks>
    private void BeginSoloMove(ref AgentState agent, Vector2 target)
    {
        DetachFromMoveGroup(ref agent);
        agent.LocomotionState = AgentLocomotionState.Move;
        agent.BehaviorTarget = new AgentId(-1);
        agent.ReturningToHold = false;
        agent.CrowdedArrivalAttempts = 0;
        agent.CrowdedArrivalContactFrames = 0;
        agent.RepathCooldown = 0f;
        agent.MoveGroupId = 0;
        agent.GroupSlot = target;
        agent.FormationOffset = Vector2.Zero;
        agent.ApproachingSlot = true;
        agent.UsesFlowTransit = false;
        agent.RequestedDestination = target;
        AssignPath(ref agent, target);
    }

    /// <summary>
    /// Puts an agent on the shared cost field, with no stored path. Returns false
    /// if the field offers it no route, in which case the caller falls back to A*.
    /// </summary>
    private bool BeginFlowTransit(ref AgentState agent, Vector2 target)
    {
        if (pathService.SampleFlowGradient(
                agent.Position, target, agent.NavigationRadius, agentSpeed: agent.MaximumSpeed) ==
            Vector2.Zero)
        {
            return false;
        }
        CompletePath(ref agent);
        agent.UsesFlowTransit = true;
        agent.SmoothedFlow = Vector2.Zero;
        agent.AdoptedCongestionRevision = pathService.CongestionRevision;
        agent.RouteAdoptionDelay = 0f;
        agent.RouteCommitSeconds = RouteCommitmentSeconds;
        agent.Destination = target;
        agent.HasDestination = Vector2.DistanceSquared(agent.Position, target) >
                               ArrivalDistance * ArrivalDistance;
        agent.NavigationRevision = Navigation.Revision;
        // Route distance, not straight-line. Progress is judged against this figure, and
        // seeding it with the straight line understates the journey by however far round
        // the route actually goes — so a member's first second of travel scored as a large
        // gain it had not made, and later as no gain at all.
        agent.LastDestinationDistance = RemainingRouteDistance(agent);
        agent.ProgressSampleSeconds = 0f;
        agent.ProgressSampleWaypointIndex = 0;
        agent.ProgressSampleDistance = agent.LastDestinationDistance;
        agent.StuckSeconds = 0f;
        agent.CongestionYieldSeconds = 0f;
        agent.RepathRequested = false;
        agent.HasRepathAvoidance = false;
        return true;
    }

    /// <summary>
    /// Moves group members from shared transit onto their own slots once the
    /// cohort reaches the command point, and retires settled groups.
    /// </summary>
    private void UpdateGroupFormations()
    {
        if (moveGroups.Count == 0) return;
        var retired = new List<int>();

        foreach (var group in moveGroups.Values)
        {
            var settledMembers = 0;
            var liveMembers = 0;
            // Live centroid of the members still travelling together. Formation
            // steering is relative to this, not to the command point, so the
            // cohort keeps its shape while it moves instead of collapsing into a
            // column aimed at one spot.
            var transitCentroid = Vector2.Zero;
            var transitFlow = Vector2.Zero;
            var transitMembers = 0;
            foreach (var memberId in group.Members)
            {
                if (!Agents.Contains(memberId)) continue;
                ref readonly var member = ref Agents.Get(memberId);
                if (member.MoveGroupId != group.Id || !member.UsesFlowTransit) continue;
                transitCentroid += member.Position;
                transitFlow += member.SmoothedFlow;
                transitMembers++;
            }
            if (transitMembers > 0)
            {
                group.TransitCentroid = transitCentroid / transitMembers;
                group.TransitFlow = transitFlow / transitMembers;
                // Station-keeping needs somebody else to keep station with. One member left
                // in transit has only itself to average, and a formation of one is not a
                // formation.
                group.HasTransitCentroid = transitMembers > 1;
            }
            else
            {
                group.HasTransitCentroid = false;
            }

            for (var slot = 0; slot < group.Members.Length; slot++)
            {
                var id = group.Members[slot];
                if (!Agents.Contains(id)) continue;
                ref var agent = ref Agents.Get(id);
                if (agent.MoveGroupId != group.Id) continue;
                liveMembers++;
                if (!agent.HasDestination && agent.ApproachingSlot)
                {
                    settledMembers++;
                    continue;
                }
                if (agent.ApproachingSlot || agent.RepathCooldown > 0f) continue;

                // Peel off the shared route as soon as the formation envelope is
                // reached. Waiting until the command point itself would funnel the
                // whole cohort through one square metre first, which is exactly
                // the contested arrival this layer exists to avoid.
                if (Vector2.Distance(agent.Position, group.Target) > group.FormationRadius) continue;
                agent.ApproachingSlot = true;
                agent.UsesFlowTransit = false;
                agent.RequestedDestination = agent.GroupSlot;
                agent.CrowdedArrivalAttempts = 0;
                agent.CrowdedArrivalContactFrames = 0;
                if (!AssignPath(ref agent, agent.GroupSlot, preserveCurrentPathOnFailure: true))
                {
                    // An unreachable slot is not worth stalling for; settle where
                    // the body already stands and let contact resolution pack it.
                    agent.GroupSlot = agent.Position;
                    agent.RequestedDestination = agent.Position;
                }
            }

            if (liveMembers == 0)
            {
                retired.Add(group.Id);
                continue;
            }
            if (settledMembers < liveMembers)
            {
                group.SettlingTicks = 0;
                continue;
            }
            group.SettlingTicks++;
            if (group.SettlingTicks < 30) continue;

            foreach (var id in group.Members)
            {
                if (!Agents.Contains(id)) continue;
                ref var agent = ref Agents.Get(id);
                if (agent.MoveGroupId != group.Id) continue;
                DetachFromMoveGroup(ref agent);
                agent.HoldPosition = agent.Position;
                agent.HoldReturnCooldown = 0.75f;
            }
            retired.Add(group.Id);
        }

        foreach (var id in retired) moveGroups.Remove(id);
    }

    private static void DetachFromMoveGroup(ref AgentState agent)
    {
        agent.MoveGroupId = 0;
        agent.ApproachingSlot = false;
        agent.UsesFlowTransit = false;
    }

    private void ApplyStop(IEnumerable<AgentId> ids)
    {
        foreach (var id in ids)
        {
            if (!Agents.Contains(id)) continue;
            ref var agent = ref Agents.Get(id);
            DetachFromMoveGroup(ref agent);
            agent.LocomotionState = AgentLocomotionState.Idle;
            agent.BehaviorTarget = new AgentId(-1);
            HaltMovement(ref agent);
        }
    }

    private void ApplyTargetBehavior(
        IEnumerable<AgentId> ids,
        AgentId target,
        AgentLocomotionState behavior)
    {
        if (!Agents.Contains(target)) return;
        foreach (var id in ids)
        {
            if (!Agents.Contains(id) || id == target) continue;
            ref var agent = ref Agents.Get(id);
            DetachFromMoveGroup(ref agent);
            agent.LocomotionState = behavior;
            agent.BehaviorTarget = target;
            agent.ReturningToHold = false;
            agent.BehaviorUpdateCooldown = 0f;
            HaltMovement(ref agent, preserveBehavior: true);
        }
    }

    private void ApplyPatrol(PatrolGroupCommand patrol)
    {
        foreach (var id in patrol.Agents)
        {
            if (!Agents.Contains(id)) continue;
            ref var agent = ref Agents.Get(id);
            DetachFromMoveGroup(ref agent);
            agent.LocomotionState = AgentLocomotionState.Patrol;
            agent.BehaviorTarget = new AgentId(-1);
            agent.ReturningToHold = false;
            agent.PatrolStart = agent.Position;
            agent.PatrolEnd = patrol.End;
            agent.PatrolTowardEnd = true;
            agent.RequestedDestination = patrol.End;
            AssignPath(ref agent, patrol.End);
        }
    }

    private void UpdateBehaviors(float deltaSeconds)
    {
        var agents = Agents.MutableSpan();
        for (var i = 0; i < agents.Length; i++)
        {
            ref var agent = ref agents[i];
            if (!agent.IsAlive) continue;
            agent.BehaviorUpdateCooldown = MathF.Max(0f, agent.BehaviorUpdateCooldown - deltaSeconds);
            agent.HoldReturnCooldown = MathF.Max(0f, agent.HoldReturnCooldown - deltaSeconds);
            if (agent.BehaviorUpdateCooldown > 0f) continue;

            switch (agent.LocomotionState)
            {
                case AgentLocomotionState.Follow:
                    UpdateTargetBehavior(ref agent, stopDistance: 1.45f, flee: false, updatePeriod: 0.35f);
                    break;
                case AgentLocomotionState.Chase:
                    UpdateTargetBehavior(ref agent, stopDistance: 0.95f, flee: false, updatePeriod: 0.22f);
                    break;
                case AgentLocomotionState.Flee:
                    UpdateTargetBehavior(ref agent, stopDistance: 0f, flee: true, updatePeriod: 0.30f);
                    break;
                case AgentLocomotionState.Patrol when !agent.HasDestination:
                    var patrolTarget = agent.PatrolTowardEnd ? agent.PatrolEnd : agent.PatrolStart;
                    agent.RequestedDestination = patrolTarget;
                    AssignPath(ref agent, patrolTarget);
                    agent.BehaviorUpdateCooldown = 0.25f;
                    break;
                case AgentLocomotionState.Idle when !agent.HasDestination:
                    TryReturnToHold(ref agent);
                    break;
            }
        }
    }

    private void TryReturnToHold(ref AgentState agent)
    {
        if (agent.HoldReturnCooldown > 0f ||
            Vector2.DistanceSquared(agent.Position, agent.HoldPosition) <=
            HoldPositionTolerance * HoldPositionTolerance)
        {
            return;
        }

        Colliders.QueryCircle(
            agent.HoldPosition,
            agent.Radius + 0.08f,
            new ColliderQueryFilter(
                ColliderRole.MovementSolid,
                ColliderLayer.Agent | ColliderLayer.Structure,
                RelationMask.Ally | RelationMask.Neutral | RelationMask.Enemy,
                ColliderOwner.Agent(agent.Id),
                agent.Faction),
            holdPositionHits);
        if (holdPositionHits.Count > 0)
        {
            agent.HoldReturnCooldown = 0.25f;
            return;
        }

        Colliders.QueryCircle(
            agent.Position,
            1.5f,
            new ColliderQueryFilter(
                ColliderRole.MovementSolid,
                ColliderLayer.Agent,
                RelationMask.Ally | RelationMask.Neutral | RelationMask.Enemy,
                ColliderOwner.Agent(agent.Id),
                agent.Faction),
            holdPositionHits);
        foreach (var hit in holdPositionHits)
        {
            var owner = Colliders.Get(hit).Owner;
            if (owner.Kind != ColliderOwnerKind.Agent || !Agents.Contains(new AgentId(owner.Value))) continue;
            ref var nearby = ref Agents.Get(new AgentId(owner.Value));
            if (!nearby.HasDestination || nearby.ReturningToHold) continue;
            agent.HoldReturnCooldown = 0.40f;
            return;
        }

        agent.RequestedDestination = agent.HoldPosition;
        AssignPath(ref agent, agent.HoldPosition);
        agent.ReturningToHold = agent.HasDestination;
        if (!agent.ReturningToHold) agent.HoldReturnCooldown = 0.25f;
    }

    private void UpdateTargetBehavior(
        ref AgentState agent,
        float stopDistance,
        bool flee,
        float updatePeriod)
    {
        agent.BehaviorUpdateCooldown = updatePeriod;
        if (!Agents.Contains(agent.BehaviorTarget))
        {
            agent.LocomotionState = AgentLocomotionState.Idle;
            HaltMovement(ref agent);
            return;
        }

        ref var target = ref Agents.Get(agent.BehaviorTarget);
        var offset = target.Position - agent.Position;
        var distance = offset.Length();
        var direction = distance > 0.0001f ? offset / distance : StableAgentDirection(agent.Id);

        if (!flee && distance <= stopDistance)
        {
            HaltMovement(ref agent, preserveBehavior: true);
            return;
        }

        var requested = flee
            ? Terrain.ClampPosition(agent.Position - direction * 6f, agent.Radius + BodyFootprint.NavigationMargin)
            : Terrain.ClampPosition(target.Position - direction * stopDistance, agent.Radius + BodyFootprint.NavigationMargin);
        if (agent.HasDestination && Vector2.DistanceSquared(requested, agent.RequestedDestination) < 0.25f)
        {
            return;
        }
        agent.RequestedDestination = requested;
        AssignPath(ref agent, requested, preserveCurrentPathOnFailure: true);
    }

    private static Vector2 StableAgentDirection(AgentId id)
    {
        var hash = unchecked((uint)id.Value * 2654435761u);
        var angle = hash % 1024 / 1024f * MathF.Tau;
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
    }

    private bool ApplyObstacleToggle(GridCell cell)
    {
        if (!Placement.Transform.Contains(cell)) return false;
        var changed = false;
        if (Placement.IsOccupied(cell))
        {
            changed = Placement.SetOccupied(cell, false);
            if (blockColliders.Remove(cell, out var collider)) Colliders.Remove(collider);
        }
        else if (CanToggleObstacle(cell))
        {
            changed = Placement.SetOccupied(cell, true);
            if (changed)
            {
                var center = Placement.Transform.CellCenter(cell);
                var halfExtents = new Vector2(Placement.Transform.CellSize * 0.5f - 0.025f);
                blockColliders[cell] = Colliders.Add(
                    ColliderOwner.Placement(cell, Placement.Transform),
                    FactionId.None,
                    ColliderLayer.Structure,
                    ColliderRole.MovementSolid | ColliderRole.PlacementBlocker | ColliderRole.Interactable,
                    ColliderShape.Aabb(halfExtents),
                    center);
            }
        }

        return changed;
    }

    /// <summary>
    /// Lets a body on a stored route notice that the route has become congested.
    /// </summary>
    /// <remarks>
    /// Only members steering by the shared cost field were re-evaluating their
    /// route; anything on a stored path — a single unit under orders, a member
    /// closing on its formation slot, anyone who fell back to A* — kept the route
    /// it was given regardless of what happened on it afterwards. The congestion
    /// field was therefore doing nothing at all for most of the units on the map,
    /// and the only thing that ever visibly re-routed was the one body per second
    /// the deadlock breaker happened to pick.
    /// <para>
    /// Gated on actually being obstructed, and on the same commitment and
    /// staggered adoption the field followers use, so this costs a replan only
    /// for bodies with a reason to want one — and never for a whole crowd at once.
    /// </para>
    /// </remarks>
    private void ReconsiderCongestedRoute(ref AgentState agent)
    {
        if (!agent.HasDestination || agent.UsesFlowTransit || !agent.Path.IsValid) return;
        if (agent.RepathCooldown > 0f || agent.RouteCommitSeconds > 0f) return;
        // Re-planning is a budget, not an entitlement. In a dense crowd nearly
        // every body is obstructed at once, so without a ceiling this becomes
        // hundreds of A* runs a second and the dominant cost in the tick — for no
        // benefit, because a route re-planned a fraction of a second later is the
        // same route. Whoever misses out this tick is a candidate again next.
        if (routePlansThisTick >= MaxRoutePlansPerTick) return;
        if (agent.AdoptedCongestionRevision == pathService.CongestionRevision) return;
        // Nothing to route around unless this body is actually being held up.
        if (agent.CrowdPressureSeconds <= 0f && !agent.AvoidanceBlockedThisTick) return;

        if (agent.RouteAdoptionDelay <= 0f)
        {
            agent.RouteAdoptionDelay = RouteAdoptionStagger(agent.Id);
        }
        agent.RouteAdoptionDelay -= (float)FixedDeltaSeconds;
        if (agent.RouteAdoptionDelay > 0f) return;

        agent.RouteAdoptionDelay = 0f;
        agent.AdoptedCongestionRevision = pathService.CongestionRevision;
        agent.RouteCommitSeconds = RouteCommitmentSeconds;
        routePlansThisTick++;
        if (AssignPath(ref agent, agent.RequestedDestination, preserveCurrentPathOnFailure: true))
        {
            CongestionRerouteCount++;
        }
    }

    private void RefreshInvalidPaths()
    {
        var agents = Agents.MutableSpan();
        for (var i = 0; i < agents.Length; i++)
        {
            ref var agent = ref agents[i];
            if (!agent.IsAlive) continue;
            ReconsiderCongestedRoute(ref agent);
            if (!agent.HasDestination || agent.NavigationRevision == Navigation.Revision) continue;
            if (agent.UsesFlowTransit)
            {
                // Flow fields are cached per navigation revision, so a terrain or
                // placement edit already invalidates them; there is no stored
                // route here to go stale. Just re-stamp the revision.
                agent.NavigationRevision = Navigation.Revision;
                continue;
            }
            AssignPath(ref agent, agent.RequestedDestination);
        }
    }

    private bool AssignPath(
        ref AgentState agent,
        Vector2 requestedDestination,
        bool preserveCurrentPathOnFailure = false,
        Vector2? congestionAvoidanceCenter = null,
        float[]? additionalNavigationCosts = null,
        float groupReservationExclusionRadius = 0f)
    {
        // A held refusal outlives the single replan that created it: without this the body
        // is handed a route round the gap, the flag it was given expires with that one
        // call, and the very next replan routes it straight back in.
        congestionAvoidanceCenter ??= agent.AbandonedApertureSeconds > 0f
            ? agent.AbandonedAperture
            : null;
        var pathfindingStart = Stopwatch.GetTimestamp();
        var result = pathService.FindPath(
            agent.Position,
            requestedDestination,
            agent.NavigationRadius,
            congestionAvoidanceCenter,
            additionalNavigationCosts,
            agent.MaximumSpeed);
        pathfindingTicksThisTick += Stopwatch.GetTimestamp() - pathfindingStart;
        if (result is not { } path)
        {
            if (!preserveCurrentPathOnFailure)
            {
                paths.Release(agent.Path);
                agent.Path = PathHandle.None;
                agent.WaypointIndex = 0;
                agent.HasDestination = false;
            }
            return false;
        }

        if (additionalNavigationCosts is not null)
        {
            pathService.ReserveGroupRoute(
                path,
                agent.NavigationRadius,
                additionalNavigationCosts,
                groupReservationExclusionRadius);
        }

        paths.Release(agent.Path);
        agent.Path = PathHandle.None;
        agent.WaypointIndex = 0;
        // A stored route supersedes the shared field. Without this a body granted
        // a repath kept steering by the gradient and simply ignored the route it
        // had just been given — which is why a unit wedged against terrain the
        // field thought was passable stayed there through repeated recoveries.
        agent.UsesFlowTransit = false;
        agent.FlowStepRejections = 0;
        agent.NavigationRevision = Navigation.Revision;
        agent.StuckSeconds = 0f;
        agent.CongestionYieldSeconds = 0f;
        agent.RepathRequested = false;
        agent.HasRepathAvoidance = false;
        agent.Destination = path.Destination;
        agent.LastDestinationDistance = RemainingRouteDistance(agent);
        agent.Path = paths.Add(path.Waypoints);
        agent.HasDestination = Vector2.DistanceSquared(agent.Position, agent.Destination) >
                               ArrivalDistance * ArrivalDistance;
        agent.ProgressSampleSeconds = 0f;
        agent.ProgressSampleWaypointIndex = 0;
        agent.ProgressSampleDistance = path.Waypoints.Length > 0
            ? Vector2.Distance(agent.Position, path.Waypoints[0])
            : agent.LastDestinationDistance;
        if (!agent.HasDestination) CompletePath(ref agent);
        return true;
    }

    private bool AssignPathVia(
        ref AgentState agent,
        Vector2 joinPoint,
        Vector2 requestedDestination,
        Vector2 congestionAvoidanceCenter)
    {
        var pathfindingStart = Stopwatch.GetTimestamp();
        var first = pathService.FindPath(
            agent.Position,
            joinPoint,
            agent.NavigationRadius,
            congestionAvoidanceCenter,
            agentSpeed: agent.MaximumSpeed);
        var second = first is null
            ? null
            : pathService.FindPath(
                joinPoint,
                requestedDestination,
                agent.NavigationRadius,
                agentSpeed: agent.MaximumSpeed);
        pathfindingTicksThisTick += Stopwatch.GetTimestamp() - pathfindingStart;
        if (first is not { } approach || second is not { } continuation)
        {
            return false;
        }

        var combined = new Vector2[approach.Waypoints.Length + continuation.Waypoints.Length];
        approach.Waypoints.CopyTo(combined, 0);
        continuation.Waypoints.CopyTo(combined, approach.Waypoints.Length);
        paths.Release(agent.Path);
        agent.Path = paths.Add(combined);
        agent.WaypointIndex = 0;
        agent.NavigationRevision = Navigation.Revision;
        agent.StuckSeconds = 0f;
        agent.CongestionYieldSeconds = 0f;
        agent.RepathRequested = false;
        agent.HasRepathAvoidance = false;
        agent.Destination = continuation.Destination;
        agent.LastDestinationDistance = Vector2.Distance(agent.Position, agent.Destination);
        agent.HasDestination = true;
        agent.ProgressSampleSeconds = 0f;
        agent.ProgressSampleWaypointIndex = 0;
        agent.ProgressSampleDistance = Vector2.Distance(agent.Position, combined[0]);
        return true;
    }

    private void PreparePreferredVelocities()
    {
        var agents = Agents.MutableSpan();
        for (var i = 0; i < agents.Length; i++)
        {
            ref var agent = ref agents[i];
            if (!agent.IsAlive) continue;
            agent.PreviousPosition = agent.Position;

            if (!agent.HasDestination)
            {
                agent.PreferredVelocity = Vector2.Zero;
                continue;
            }

            if (agent.UsesFlowTransit)
            {
                agent.PreferredVelocity = ResolveFlowTransitVelocity(ref agent);
                continue;
            }

            if (!agent.Path.IsValid)
            {
                // Under orders with nowhere to go. Silently zeroing here left a
                // body standing still indefinitely with a live destination — and
                // because it had no preferred velocity, the stuck detector below
                // classified it as not wanting to move and never escalated. Ask
                // for a route instead; the cooldown keeps a genuinely unreachable
                // destination from re-running A* every tick.
                agent.PreferredVelocity = Vector2.Zero;
                if (agent.RepathCooldown <= 0f)
                {
                    agent.RepathCooldown = RoutelessRepathInterval;
                    AssignPath(ref agent, agent.RequestedDestination, preserveCurrentPathOnFailure: true);
                }
                continue;
            }

            var waypoints = paths.Get(agent.Path);
            if (agent.WaypointIndex >= waypoints.Length)
            {
                Arrive(ref agent);
                agent.PreferredVelocity = Vector2.Zero;
                continue;
            }

            // Local avoidance is allowed to carry a body around (and beyond)
            // a point on the polyline. Once it has crossed that waypoint's
            // forward plane, consume every subsequently visible segment. The
            // visibility check preserves static-corner safety; only the path
            // cursor advances, never the agent position.
            while (agent.WaypointIndex + 1 < waypoints.Length)
            {
                var currentWaypoint = waypoints[agent.WaypointIndex];
                var nextWaypoint = waypoints[agent.WaypointIndex + 1];
                var pathSegment = nextWaypoint - currentWaypoint;
                if (pathSegment.LengthSquared() <= 0.0001f ||
                    Vector2.Dot(agent.Position - currentWaypoint, pathSegment) <= 0f ||
                    !pathService.IsDirectPathClear(agent.Position, nextWaypoint, agent.NavigationRadius))
                {
                    break;
                }
                agent.WaypointIndex++;
            }

            var waypoint = waypoints[agent.WaypointIndex];
            var remaining = waypoint - agent.Position;
            var distance = remaining.Length();
            var requestedDistance = Vector2.Distance(agent.Position, agent.RequestedDestination);
            if (DestinationIsLocallyContested(agent, agents, requestedDistance))
            {
                if (agent.CrowdedArrivalAttempts >= CrowdedArrivalFailedAttemptLimit)
                {
                    agent.Destination = agent.Position;
                    Arrive(ref agent);
                    agent.PreferredVelocity = Vector2.Zero;
                    continue;
                }
                else
                {
                    // This is a failed local-arrival probe only if physical
                    // contact is confirmed later in the tick.
                    agent.CrowdedArrivalBlockedThisTick = true;
                    CrowdedArrivalBlockCount++;
                }
            }
            if (distance <= WaypointArrivalDistance)
            {
                AdvanceWaypoint(ref agent, waypoint, waypoints.Length);
                if (!agent.HasDestination || !agent.Path.IsValid)
                {
                    agent.PreferredVelocity = Vector2.Zero;
                    continue;
                }
                waypoints = paths.Get(agent.Path);
                waypoint = waypoints[agent.WaypointIndex];
                remaining = waypoint - agent.Position;
                distance = remaining.Length();
                if (distance <= 0.0001f)
                {
                    agent.PreferredVelocity = Vector2.Zero;
                    continue;
                }
            }

            // Brake for the destination, never for a waypoint in the middle of
            // the route. Scaling by the distance to whatever point is currently
            // being steered at meant a body decelerated to a crawl and
            // re-accelerated at every vertex of its own polyline, which is what
            // made units look like they were interpolating between points
            // instead of walking. Route vertices are passed through at speed.
            var isFinalWaypoint = agent.WaypointIndex + 1 >= waypoints.Length;
            var arrivalScale = isFinalWaypoint
                ? Math.Clamp(distance / 0.8f, 0.12f, 1f)
                : 1f;
            var heading = remaining / distance;

            // Start turning into the next leg before reaching the corner. The
            // body still passes through the vertex, but the heading changes over
            // the approach rather than snapping the instant the cursor advances.
            if (!isFinalWaypoint)
            {
                var nextLeg = waypoints[agent.WaypointIndex + 1] - waypoint;
                // Never blend over more than half the leg being travelled. A
                // route through tight terrain is smoothed into ~1 m segments, and
                // a blend range wider than the segment cuts every corner at once
                // instead of easing through one, which walks the body into walls.
                var blendRange = MathF.Min(CornerBlendDistance, nextLeg.Length() * 0.5f);
                if (nextLeg.LengthSquared() > 0.0001f && distance < blendRange)
                {
                    var nextHeading = Vector2.Normalize(nextLeg);
                    var blend = (1f - distance / blendRange) * CornerBlendStrength;
                    var blended = Vector2.Lerp(heading, nextHeading, blend);
                    if (blended.LengthSquared() > 0.0001f) heading = Vector2.Normalize(blended);
                }
            }

            var terrainSpeed = Terrain.SpeedMultiplier(agent.Position);
            agent.PreferredVelocity = heading * agent.MaximumSpeed * terrainSpeed * arrivalScale;
        }

    }

    /// <summary>
    /// Steering for a member travelling on the shared cost field: descend the
    /// field, and correct sideways toward the place this member holds in the
    /// formation.
    /// </summary>
    /// <remarks>
    /// The correction is projected perpendicular to the flow, so keeping station
    /// never fights forward progress — a unit out of position slides across the
    /// front rather than slowing down or overtaking. Without it every member
    /// descends the identical gradient and the group crosses open ground as a
    /// single-file column, which is what made units look like they were queueing
    /// for no reason.
    /// </remarks>
    private Vector2 ResolveFlowTransitVelocity(ref AgentState agent)
    {
        // Take up new routing on a per-member delay rather than the instant it is
        // published. Every member reading one live field means the whole crowd
        // changes its mind on the same tick, several times a second; spreading
        // adoption turns that step into a migration, and during the window the
        // group genuinely divides between the old route and the new one instead
        // of swinging back and forth as a block.
        // Stay on the chosen route. Re-deciding the moment a cheaper one appears
        // produces exactly the loop this is meant to avoid: the alternative looks
        // better because nobody is on it, the unit sets off, its own arrival makes
        // it no better, and by then the original has drained and looks better
        // again. A body that has committed keeps going unless it is actually
        // getting nowhere, which is the only evidence that its choice was wrong.
        agent.RouteCommitSeconds = MathF.Max(0f, agent.RouteCommitSeconds - (float)FixedDeltaSeconds);
        if (agent.StuckSeconds >= RouteReconsiderStallSeconds) agent.RouteCommitSeconds = 0f;

        if (agent.AdoptedCongestionRevision != pathService.CongestionRevision &&
            agent.RouteCommitSeconds <= 0f)
        {
            if (agent.RouteAdoptionDelay <= 0f)
            {
                agent.RouteAdoptionDelay = RouteAdoptionStagger(agent.Id);
            }
            agent.RouteAdoptionDelay -= (float)FixedDeltaSeconds;
            if (agent.RouteAdoptionDelay <= 0f)
            {
                agent.AdoptedCongestionRevision = pathService.CongestionRevision;
                agent.RouteAdoptionDelay = 0f;
                agent.RouteCommitSeconds = RouteCommitmentSeconds;
            }
        }

        if (agent.FlowStepRejections >= FlowTransitRejectionLimit)
        {
            agent.UsesFlowTransit = false;
            agent.FlowStepRejections = 0;
            AssignPath(ref agent, agent.RequestedDestination, preserveCurrentPathOnFailure: true);
            return Vector2.Zero;
        }

        var flow = pathService.SampleFlowGradient(
            agent.Position,
            agent.RequestedDestination,
            agent.NavigationRadius,
            agent.AdoptedCongestionRevision,
            agent.MaximumSpeed);
        if (flow == Vector2.Zero)
        {
            // The field no longer offers this body a route (terrain edit, or it
            // was pushed somewhere disconnected). Fall back to a real path.
            agent.UsesFlowTransit = false;
            AssignPath(ref agent, agent.RequestedDestination, preserveCurrentPathOnFailure: true);
            return Vector2.Zero;
        }

        // Low-pass the direction. The shared field is rebuilt several times a
        // second and every member reads the same one, so a rebuild that changes
        // the preferred exit turns the whole crowd at once, on one tick. Even
        // when the new route is better, that reads as a cohort of confused
        // bodies twitching in unison. Smoothing converts each step change into a
        // short turn without changing which route is eventually taken.
        agent.SmoothedFlow = agent.SmoothedFlow == Vector2.Zero
            ? flow
            : Vector2.Normalize(Vector2.Lerp(agent.SmoothedFlow, flow, FlowSmoothing));
        flow = agent.SmoothedFlow;

        // A gap ahead is aimed at along its axis, not at its mouth. This replaces the
        // gradient rather than competing with it — still one intent vector — and is what
        // turns a fan converging on an opening into a file lining up for it.
        var liningUpForGap = false;
        if (pathService.TryFindApertureApproach(agent.Position, flow, agent.NavigationRadius, out var aim))
        {
            var toAim = aim - agent.Position;
            if (toAim.LengthSquared() > 0.0001f)
            {
                flow = Vector2.Normalize(toAim);
                liningUpForGap = true;
            }
        }

        var terrainSpeed = Terrain.SpeedMultiplier(agent.Position);
        var speed = agent.MaximumSpeed * terrainSpeed;
        var desired = flow * speed;

        // No station-keeping into a gap. A formation cannot be held through an opening one
        // body wide, and the correction that tries to hold it is a sideways push applied
        // exactly where there is no sideways to go — so the body shuffles across the mouth
        // instead of going through it, and undoes the lining-up above on the way. Formation
        // is for open ground; a gap is single file.
        if (!liningUpForGap &&
            agent.MoveGroupId != 0 &&
            moveGroups.TryGetValue(agent.MoveGroupId, out var group) &&
            group.HasTransitCentroid)
        {
            // Cohesion must not fight the route. When the map sends members of one
            // group different ways — around opposite sides of an obstacle, or out
            // of two different gates — the centroid sits between those routes, and
            // pulling everyone toward it walks the whole group into the empty
            // ground in the middle. They bunch up there and then trickle out of
            // whichever exit they happen to be nearest, which is worse than either
            // route taken properly. Hold formation while we agree on a direction;
            // defer to the routing when we do not.
            var agreement = Math.Clamp(Vector2.Dot(flow, group.TransitFlow), 0f, 1f);
            if (agreement > 0.0001f)
            {
                var station = group.TransitCentroid + agent.FormationOffset;
                var error = station - agent.Position;
                var lateral = error - flow * Vector2.Dot(error, flow);
                var lateralSpeed = lateral.Length() * FormationKeepingGain * agreement;
                if (lateralSpeed > 0.0001f)
                {
                    var limit = speed * FormationLateralSpeedFraction;
                    desired += lateral / lateral.Length() * MathF.Min(lateralSpeed, limit);
                }
            }
        }

        // Ease off on the final approach to the command point so the cohort does
        // not arrive at the formation envelope at a dead run.
        var remaining = Vector2.Distance(agent.Position, agent.RequestedDestination);
        var arrivalScale = Math.Clamp(remaining / 1.5f, 0.25f, 1f);
        desired *= arrivalScale;
        if (desired.LengthSquared() > speed * speed)
        {
            desired = Vector2.Normalize(desired) * speed;
        }
        return desired;
    }

    /// <summary>
    /// Deterministic per-agent delay before adopting newly published routing.
    /// </summary>
    private static float RouteAdoptionStagger(AgentId id)
    {
        var hash = unchecked((uint)id.Value * 2654435761u);
        return hash % 1024 / 1024f * MaximumRouteAdoptionDelay;
    }

    private bool DestinationIsLocallyContested(
        in AgentState agent,
        Span<AgentState> agents,
        float distanceToDestination)
    {
        // Contacts along the route are navigation events, not failed arrival
        // attempts. Letting this budget run anywhere on the map allowed a unit
        // to bump into several travellers at a chokepoint, declare itself
        // arrived many metres early, and become a permanent idle obstruction.
        var sharedDestinationCount = 0;
        // Bodies that have already stopped between this one and where it is going. Counted
        // separately from the cohort above because the two answer different questions: that one
        // is "how many were sent here", this one is "how many are standing here now". A unit
        // arriving alone at ground a crowd settled on earlier — under its own order, with its own
        // slots — has a cohort of one and an occupied destination, and sizing its arrival
        // neighbourhood off the cohort told it the crowd was not there.
        var occupantsAhead = 0;
        for (var i = 0; i < agents.Length; i++)
        {
            ref readonly var other = ref agents[i];
            if (!other.IsAlive) continue;
            if (Vector2.DistanceSquared(
                    other.RequestedDestination,
                    agent.RequestedDestination) <= 0.04f)
            {
                sharedDestinationCount++;
            }

            if (other.Id == agent.Id || other.HasDestination ||
                other.LocomotionState != AgentLocomotionState.Idle)
            {
                continue;
            }

            if (Vector2.Distance(other.Position, agent.RequestedDestination) < distanceToDestination)
            {
                occupantsAhead++;
            }
        }

        // Whichever describes more bodies. Taking the larger can only ever widen the neighbourhood,
        // so every cohort this was tuned against behaves exactly as it did.
        var arrivalCohort = Math.Max(sharedDestinationCount, occupantsAhead);
        // Approximate the packed cluster radius, plus one body-diameter contact
        // shell. This bounds arrival propagation to the destination cohort while
        // allowing a large selection to settle at its physically reachable
        // perimeter instead of eternally pushing toward an impossible point.
        // The ideal packed-disc radius understates the perimeter reached by a
        // flowing, non-crystalline crowd. Include a two-body-diameter contact
        // shell so agents blocked at that real perimeter can spend their own
        // arrival-attempt budget instead of remaining active forever.
        var irregularTerrainShell = Terrain.Revision > 0 ? 1.2f : 0f;
        var packedNeighborhood = agent.Radius *
                                 (6.8f + irregularTerrainShell +
                                  MathF.Sqrt(arrivalCohort / 0.82f)) +
                                 0.35f;
        var arrivalNeighborhood = MathF.Max(CrowdedArrivalNeighborhood, packedNeighborhood);
        if (distanceToDestination > arrivalNeighborhood) return false;

        if (distanceToDestination <= agent.Radius * CrowdedArrivalRadiusShare &&
            !CanOccupyArrivalPosition(agent, agent.Destination)) return true;
        for (var i = 0; i < agents.Length; i++)
        {
            ref var other = ref agents[i];
            if (!other.IsAlive || !HasArrivalPriority(other, agent, distanceToDestination)) continue;
            // Reached state propagates across a physically packed frontier,
            // not a loose social-distance chain that can stretch all the way
            // back through a doorway.
            var localPackingDistance = agent.Radius + other.Radius + 0.08f;
            if (Vector2.DistanceSquared(agent.Position, other.Position) >
                localPackingDistance * localPackingDistance)
            {
                continue;
            }
            var otherTargetDistance = Vector2.Distance(other.Position, agent.RequestedDestination);
            if (otherTargetDistance + 0.10f < distanceToDestination) return true;
            if (MathF.Abs(otherTargetDistance - distanceToDestination) <= 0.10f &&
                other.Id.Value < agent.Id.Value) return true;
        }
        return false;
    }

    private static bool HasArrivalPriority(
        in AgentState other,
        in AgentState agent,
        float agentDistanceToDestination)
    {
        if (other.Id == agent.Id || other.ReturningToHold || other.HasDestination ||
            other.LocomotionState != AgentLocomotionState.Idle)
        {
            return false;
        }
        // Either it was sent where this body is going, or it has settled on the way there. The
        // second half is new, and it is what a body arriving under its own order needs: a crowd
        // that got there first was ordered to its own formation slots, not to this body's
        // destination, so on the first test alone none of them was ever *its* obstruction and the
        // whole crowded-arrival path stayed switched off. A large body feels this first because it
        // cannot squeeze to the point itself, but nothing about it is a question of size.
        // <para>
        // What keeps this from declaring arrival next to any idle bystander is not this test: it is
        // that the body must be packed against this one, must be nearer the destination, and must
        // be *settled* — a traveller passing through is none of those — and that the whole
        // mechanism is bounded by the arrival neighbourhood.
        // </para>
        if (Vector2.DistanceSquared(other.RequestedDestination, agent.RequestedDestination) > 0.04f &&
            Vector2.Distance(other.Position, agent.RequestedDestination) >= agentDistanceToDestination)
        {
            return false;
        }
        return true;
    }

    private void IntegrateMovement(float deltaSeconds)
    {
        var agents = Agents.MutableSpan();
        for (var i = 0; i < agents.Length; i++)
        {
            ref var agent = ref agents[i];
            if (!agent.IsAlive) continue;
            var displacement = agent.Velocity * deltaSeconds;
            if (agent.HasDestination && agent.UsesFlowTransit)
            {
                // No polyline to advance, but this is still a body under orders:
                // it gets the same swept static check a path follower gets.
                var flowStep = Terrain.ClampPosition(
                    agent.Position + displacement,
                    agent.Radius + BodyFootprint.NavigationMargin);
                if (pathService.IsContinuousStepClear(agent.Position, flowStep, agent.NavigationRadius))
                {
                    agent.Position = flowStep;
                    agent.FlowStepRejections = 0;
                }
                else
                {
                    agent.SteeringStepRejectedThisTick = true;
                    agent.PreferredStepRejectedThisTick = true;
                    agent.Velocity = Vector2.Zero;
                    // The cost field judges traversability between cell centres;
                    // the movement sweep tests the actual body against the actual
                    // ground, and on a ramp edge they disagree. When the field
                    // keeps asking for a step the body cannot take, stop believing
                    // it and go and get a real route, which is smoothed against
                    // the same sweep and so cannot be impossible.
                    agent.FlowStepRejections++;
                }
                if (Vector2.DistanceSquared(agent.Position, agent.Destination) <=
                    ArrivalDistance * ArrivalDistance)
                {
                    Arrive(ref agent);
                }
                continue;
            }
            if (!agent.HasDestination || !agent.Path.IsValid)
            {
                agent.Position = Terrain.ClampPosition(agent.Position + displacement, agent.Radius + BodyFootprint.NavigationMargin);
                continue;
            }

            var waypoints = paths.Get(agent.Path);
            var waypoint = waypoints[agent.WaypointIndex];
            var remaining = waypoint - agent.Position;
            var distance = remaining.Length();
            var direction = distance > 0.0001f ? remaining / distance : Vector2.Zero;

            if (distance <= WaypointArrivalDistance || Vector2.Dot(displacement, direction) >= distance)
            {
                AdvanceWaypoint(ref agent, waypoint, waypoints.Length);
                continue;
            }

            var proposed = Terrain.ClampPosition(agent.Position + displacement, agent.Radius + BodyFootprint.NavigationMargin);
            if (pathService.IsContinuousStepClear(agent.Position, proposed, agent.NavigationRadius))
            {
                agent.Position = proposed;
            }
            else
            {
                agent.SteeringStepRejectedThisTick = true;
                // Never replace a reciprocal velocity with raw path velocity:
                // doing so discards every dynamic-agent constraint and can drive
                // a body straight through a neighbour. Static rejection means
                // no integration this tick; route repair owns the recovery.
                agent.PreferredStepRejectedThisTick = true;
                agent.Velocity = Vector2.Zero;
            }
        }
    }

    private void ConstrainAgentsToTerrain()
    {
        var agents = Agents.MutableSpan();
        for (var i = 0; i < agents.Length; i++)
        {
            ref var agent = ref agents[i];
            if (!agent.IsAlive) continue;
            var positionIsValid = pathService.IsPositionNavigable(agent.Position, agent.NavigationRadius);
            if (positionIsValid) continue;
            agent.Position = agent.PreviousPosition;
            agent.Velocity = Vector2.Zero;
        }
    }

    private void UpdateStuckAgents(float deltaSeconds)
    {
        var agents = Agents.MutableSpan();
        for (var i = 0; i < agents.Length; i++)
        {
            ref var agent = ref agents[i];
            if (!agent.IsAlive) continue;
            agent.RepathCooldown = MathF.Max(0f, agent.RepathCooldown - deltaSeconds);
            if (!agent.HasDestination)
            {
                if (agent.ArrivedThisTick)
                {
                    // Arrival records state before contact resolution. The
                    // post-solver body position is the first legal hold point;
                    // returning to the pre-solver point would recreate overlap.
                    // Being *contacted* is deliberately not grounds for re-homing:
                    // now that depenetration actually reports contacts, adopting
                    // the shoved position would mean a unit pushed out of the way
                    // silently accepts wherever it was pushed and never walks back.
                    agent.HoldPosition = agent.Position;
                    agent.HoldReturnCooldown = 0f;
                }
                else if (agent.LocomotionState == AgentLocomotionState.Idle &&
                    !agent.ReturningToHold &&
                    Vector2.DistanceSquared(agent.PreviousPosition, agent.Position) > 0.000004f)
                {
                    // Collision displacement keeps restarting this grace period;
                    // the unit only returns once the passing traffic has cleared.
                    agent.HoldReturnCooldown = 0.90f;
                }
                agent.StuckSeconds = 0f;
                agent.YieldStoppedSeconds = 0f;
                agent.LastDestinationDistance = 0f;
                agent.ProgressSampleSeconds = 0f;
                continue;
            }

            if (agent.RepathCooldown <= 0f &&
                agent.Path.IsValid &&
                (Terrain.Revision > 0 || Placement.Revision > 0) &&
                (agent.StuckSeconds >= 0.35f ||
                 agent.SteeringStepRejectedThisTick ||
                 agent.PreferredStepRejectedThisTick))
            {
                var routeStepIsClear = ImmediateRouteStepIsClear(agent);
                // A valid result is still a completed diagnostic. Throttle it
                // too, otherwise any crowd-limited agent pays for the same full
                // segment sweep on every subsequent tick.
                agent.RepathCooldown = 0.40f;
                if (!routeStepIsClear)
                {
                    // An avoidance sidestep can disconnect the first segment of
                    // an old path. Repair that static invalidation immediately,
                    // without treating it as crowd congestion.
                    var repaired = AssignPath(
                        ref agent,
                        agent.RequestedDestination,
                        preserveCurrentPathOnFailure: true);
                    if (repaired)
                    {
                        ImmediateRouteRepairCount++;
                        continue;
                    }
                }
            }

            // A body under orders that is producing no intent whatsoever is broken,
            // whatever the reason. It has happened through several different
            // routes — no path and a failed replan, a cost field with no gradient
            // to offer, a route granted but ignored — and each was invisible to
            // the stall detector, because that judges bodies by whether they are
            // achieving the movement they want and this one wants nothing. Rather
            // than keep patching individual causes, hold the invariant: never
            // stand still under orders indefinitely. Push for a route first, and
            // if even that will not come, settle rather than pretend.
            if (agent.PreferredVelocity.LengthSquared() <= NoIntentSpeed * NoIntentSpeed)
            {
                agent.NoIntentSeconds += deltaSeconds;
                // The counter measures how long this body has been useless, and
                // only regaining intent clears it. Resetting it on every retry —
                // which is what it did — meant the escalation below could never be
                // reached: the unit sat there re-requesting a route that was never
                // going to come, indefinitely, while the counter sawed between a
                // third and two thirds of a second. Retries are throttled on their
                // own timer instead.
                agent.NoIntentRetryCooldown -= deltaSeconds;
                if (agent.NoIntentSeconds >= NoIntentSettleSeconds)
                {
                    // No route exists from here, and pretending otherwise is what
                    // leaves a body standing in a field for twenty seconds. Give
                    // the order up; the player can see it stopped and re-issue.
                    HaltMovement(ref agent);
                    agent.NoIntentSeconds = 0f;
                    continue;
                }
                if (agent.NoIntentSeconds >= NoIntentRepathSeconds &&
                    agent.NoIntentRetryCooldown <= 0f)
                {
                    agent.NoIntentRetryCooldown = NoIntentRepathSeconds;
                    agent.RepathCooldown = 0f;
                    agent.UsesFlowTransit = false;
                    agent.FlowStepRejections = 0;
                    AssignPath(ref agent, agent.RequestedDestination, preserveCurrentPathOnFailure: true);
                }
            }
            else
            {
                agent.NoIntentSeconds = 0f;
                agent.NoIntentRetryCooldown = 0f;
            }

            // Distance still to travel along the route, not the straight line to
            // the destination. Any body legitimately going round something — a
            // pen wall, a hill, a queue at a gap — closes no straight-line
            // distance at all while doing it, and was therefore counted as making
            // no progress while running at full speed. That is not cosmetic: this
            // number feeds the congestion field and the detour picker, so units on
            // perfectly good routes were depositing obstruction and being offered
            // reroutes they had no use for.
            var distance = RemainingRouteDistance(agent);
            var progress = agent.LastDestinationDistance - distance;
            if (agent.CrowdedArrivalBlockedThisTick)
            {
                // A high tangential ORCA velocity can orbit a saturated target
                // forever without generating a physical contact. Consecutive
                // zero-progress probes are still this agent's failed arrival
                // attempts and spend only its own budget.
                if (agent.AvoidanceBlockedThisTick ||
                    progress < agent.MaximumSpeed * deltaSeconds * AgentDefaults.ProgressShareOfStep)
                {
                    agent.CrowdedArrivalContactFrames++;
                    if (agent.CrowdedArrivalContactFrames >= CrowdedArrivalContactFramesPerAttempt)
                    {
                        agent.CrowdedArrivalAttempts++;
                        agent.CrowdedArrivalContactFrames = 0;
                    }
                }
                else
                {
                    agent.CrowdedArrivalContactFrames = Math.Max(
                        0,
                        agent.CrowdedArrivalContactFrames - 1);
                }
            }
            else
            {
                agent.CrowdedArrivalAttempts = 0;
                agent.CrowdedArrivalContactFrames = 0;
            }
            // A body that is under orders but has no route wants to move just as
            // much as one that is being blocked — it simply has no direction to
            // express it in. Judging intent purely by preferred velocity made that
            // failure invisible to every recovery path here.
            var routeless = !agent.Path.IsValid && !agent.UsesFlowTransit;
            var travellingSpeed = agent.MaximumSpeed * 0.1111f;
            var wantsMovement = routeless ||
                                agent.PreferredVelocity.LengthSquared() > travellingSpeed * travellingSpeed;
            var displacement = agent.Position - agent.PreviousPosition;
            var preferredDirection = wantsMovement
                ? Vector2.Normalize(agent.PreferredVelocity)
                : Vector2.Zero;
            var forwardProgress = Vector2.Dot(displacement, preferredDirection);
            var barelyMoving = progress < agent.MaximumSpeed * deltaSeconds * AgentDefaults.ProgressShareOfStep &&
                               forwardProgress < agent.MaximumSpeed * deltaSeconds * 0.08f;
            var yieldingWithoutProgress = wantsMovement && barelyMoving &&
                                          (agent.CrowdPressureSeconds > 0f ||
                                           agent.CongestionYieldSeconds > 0f);
            agent.YieldStoppedSeconds = yieldingWithoutProgress
                ? agent.YieldStoppedSeconds + deltaSeconds
                : MathF.Max(0f, agent.YieldStoppedSeconds - deltaSeconds * 3f);

            if (agent.ReturningToHold)
            {
                agent.StuckSeconds = wantsMovement && barelyMoving
                    ? agent.StuckSeconds + deltaSeconds
                    : MathF.Max(0f, agent.StuckSeconds - deltaSeconds * 2f);
                agent.LastDestinationDistance = distance;
                if (agent.StuckSeconds >= 0.75f) PauseHoldReturn(ref agent);
                continue;
            }

            if (wantsMovement && barelyMoving)
            {
                agent.StuckSeconds += deltaSeconds;
            }
            else
            {
                agent.StuckSeconds = MathF.Max(0f, agent.StuckSeconds - deltaSeconds * 2f);
            }

            // Long enough at the same gap with nothing moving, and the body stops
            // believing in it. This is deliberately available to bodies buried in a
            // queue, unlike the detour grant, which picks the least congested and so by
            // design never reaches the ones actually wedged. They cannot act on it
            // immediately — that is what being wedged means — but the decision is held,
            // so when the press eases they walk out of the queue instead of back into it.
            if (agent.StuckSeconds >= ApertureAbandonSeconds &&
                agent.AbandonedApertureSeconds <= 0f)
            {
                var intent = agent.PreferredVelocity.LengthSquared() > 0.0001f
                    ? agent.PreferredVelocity
                    : agent.RequestedDestination - agent.Position;
                if (pathService.TryFindObstructingAperture(
                        agent.Position, intent, agent.NavigationRadius, out var abandoned))
                {
                    agent.AbandonedAperture = abandoned;
                    agent.AbandonedApertureSeconds = ApertureAbandonHoldSeconds;
                    agent.RepathRequested = true;
                    agent.HasRepathAvoidance = true;
                    agent.RepathAvoidanceCenter = abandoned;
                    agent.StuckSeconds = 0f;
                    agent.RouteCommitSeconds = 0f;
                    continue;
                }
            }

            // Throttling a failed scan was tried here and cost five tests: the
            // cooldown it would have to set is the same one the immediate route
            // repair, the detour picker and the congestion re-plan all gate on, so
            // suppressing one scan suppressed every recovery the body had.
            if (agent.StuckSeconds >= 0.50f &&
                agent.RepathCooldown <= 0f &&
                TryAdvanceToVisibleWaypoint(ref agent))
            {
                // Resynchronize only after demonstrated loss of progress. This
                // handles a body collision-displaced beyond an old corner
                // without running speculative visibility scans every frame.
                agent.StuckSeconds = 0f;
                agent.RepathCooldown = 0.30f;
                agent.LastDestinationDistance = distance;
                continue;
            }

            if (agent.Path.IsValid)
            {
                agent.ProgressSampleSeconds += deltaSeconds;
                if (agent.ProgressSampleSeconds >= 1f)
                {
                    var waypoints = paths.Get(agent.Path);
                    var waypointIndex = Math.Min(agent.WaypointIndex, waypoints.Length - 1);
                    var waypointDistance = Vector2.Distance(agent.Position, waypoints[waypointIndex]);
                    var advancedWaypoint = agent.WaypointIndex > agent.ProgressSampleWaypointIndex;
                    var approachedWaypoint = waypointDistance < agent.ProgressSampleDistance - 0.15f;
                    if (!advancedWaypoint && !approachedWaypoint && wantsMovement)
                    {
                        // Sideways shuffling and collision orbits can contain
                        // motion without making any progress along the route.
                        agent.StuckSeconds = MathF.Max(agent.StuckSeconds, 1.25f);
                    }
                    agent.ProgressSampleSeconds = 0f;
                    agent.ProgressSampleWaypointIndex = agent.WaypointIndex;
                    agent.ProgressSampleDistance = waypointDistance;
                }
            }
            agent.LastDestinationDistance = distance;

        }

        ScheduleCongestionRecovery(agents);

        for (var i = 0; i < agents.Length; i++)
        {
            ref var agent = ref agents[i];
            if (!agent.IsAlive || !agent.RepathRequested) continue;
            agent.RepathRequested = false;
            var avoidanceCenter = agent.HasRepathAvoidance
                ? agent.RepathAvoidanceCenter
                : (Vector2?)null;
            agent.HasRepathAvoidance = false;
            var dynamicNavigationCosts = BuildDynamicNavigationCosts(agents, agent.Id);
            AssignPath(
                ref agent,
                agent.RequestedDestination,
                preserveCurrentPathOnFailure: true,
                congestionAvoidanceCenter: avoidanceCenter,
                additionalNavigationCosts: dynamicNavigationCosts);
        }
    }

    /// <summary>
    /// How far this body still has to walk, following its actual route.
    /// </summary>
    private float RemainingRouteDistance(in AgentState agent)
    {
        if (agent.Path.IsValid)
        {
            var waypoints = paths.Get(agent.Path);
            if (agent.WaypointIndex < waypoints.Length)
            {
                return Vector2.Distance(agent.Position, waypoints[agent.WaypointIndex]) +
                       paths.SuffixLength(agent.Path, agent.WaypointIndex);
            }
        }

        if (agent.UsesFlowTransit &&
            pathService.TryOptimalTravelTime(
                agent.RequestedDestination, agent.Position, agent.NavigationRadius, out var seconds))
        {
            return seconds * agent.MaximumSpeed;
        }

        return Vector2.Distance(agent.Position, agent.Destination);
    }

    private bool ImmediateRouteStepIsClear(in AgentState agent)
    {
        var route = paths.Get(agent.Path);
        if (agent.WaypointIndex >= route.Length) return true;
        return pathService.IsContinuousStepClear(
            agent.Position,
            route[agent.WaypointIndex],
            agent.NavigationRadius);
    }

    private bool TryAdvanceToVisibleWaypoint(ref AgentState agent)
    {
        if (!agent.Path.IsValid) return false;
        var route = paths.Get(agent.Path);
        for (var candidate = route.Length - 1; candidate > agent.WaypointIndex; candidate--)
        {
            if (Terrain.Revision > 0 &&
                Vector2.Distance(agent.Position, route[candidate]) >
                Navigation.Transform.CellSize * 2f)
            {
                continue;
            }
            if (!pathService.IsDirectPathClear(agent.Position, route[candidate], agent.NavigationRadius)) continue;
            agent.WaypointIndex = candidate;
            agent.ProgressSampleWaypointIndex = candidate;
            agent.ProgressSampleDistance = Vector2.Distance(agent.Position, route[candidate]);
            agent.ProgressSampleSeconds = 0f;
            return true;
        }
        return false;
    }

    private float[] BuildDynamicNavigationCosts(ReadOnlySpan<AgentState> agents, AgentId excludedAgent)
    {
        var costs = new float[Navigation.Width * Navigation.Height];
        var cellSize = Navigation.Transform.CellSize;


        for (var agentIndex = 0; agentIndex < agents.Length; agentIndex++)
        {
            ref readonly var other = ref agents[agentIndex];
            if (!other.IsAlive || other.Id == excludedAgent) continue;

            // Settled bodies are the most reliable short-term obstacle signal.
            // Moving agents remain cheap enough for paths to cross their future
            // position; queued or nearly stationary agents describe backpressure.
            var speedSquared = other.Velocity.LengthSquared();
            var baseCost = !other.HasDestination
                ? SettledBodyDelaySeconds
                : speedSquared < 0.04f
                    ? StalledBodyDelaySeconds
                    : MovingBodyDelaySeconds;
            var influenceRadius = MathF.Max(1.25f, other.Radius + 0.90f);
            var cellRadius = Math.Max(1, (int)MathF.Ceiling(influenceRadius / cellSize));
            if (!Navigation.TryWorldToCell(other.Position, out var centerCell)) continue;

            for (var z = centerCell.Z - cellRadius; z <= centerCell.Z + cellRadius; z++)
            for (var x = centerCell.X - cellRadius; x <= centerCell.X + cellRadius; x++)
            {
                var cell = new GridCell(x, z);
                if (!Navigation.Contains(cell)) continue;
                var distance = Vector2.Distance(Navigation.CellCenter(cell), other.Position);
                if (distance >= influenceRadius) continue;
                var proximity = 1f - distance / influenceRadius;
                var index = Navigation.Transform.Index(cell);
                costs[index] = MathF.Min(
                    MaximumBodyDelaySeconds,
                    costs[index] + baseCost * proximity * proximity);
            }
        }

        return costs;
    }

    /// <summary>
    /// Last-resort detour for a body that has been getting nowhere for a while.
    /// </summary>
    /// <remarks>
    /// This used to walk a chain of queue-leader relations to find the front of a
    /// jam and reroute its rearmost follower. That machinery went with the queue
    /// system, and it is no longer what spreads a crowd across routes — the
    /// congestion field does that continuously, for everyone, without anybody
    /// having to jam first. What is left is a genuine deadlock breaker: one
    /// stalled unit at a time is told to find a way around whatever is in front
    /// of it, rate-limited so a busy map cannot spend its whole budget replanning.
    /// </remarks>
    private void ScheduleCongestionRecovery(Span<AgentState> agents)
    {
        if (congestionRecoveryCooldown > 0f) return;

        // Offer the detour to bodies that can actually take one. Picking the most
        // stalled unit sounds right and is exactly wrong: the worst-stuck body is
        // the one buried deepest in the crowd, hemmed in on every side, and it
        // cannot act on a new route however good the route is. The ones at the
        // edge of a jam are barely stuck by comparison and are the only ones with
        // anywhere to go — and peeling them off is also what relieves the
        // pressure behind, which lets the buried ones move without rerouting at
        // all. Least congested ground first, therefore, not longest suffering.
        Span<int> chosen = stackalloc int[CongestionRecoveryBatch];
        Span<float> chosenScore = stackalloc float[CongestionRecoveryBatch];
        var chosenCount = 0;
        for (var i = 0; i < agents.Length; i++)
        {
            ref var candidate = ref agents[i];
            if (!candidate.IsAlive || !candidate.HasDestination) continue;
            if (candidate.RepathCooldown > 0f) continue;
            if (candidate.StuckSeconds <= CongestionRecoveryStallSeconds) continue;
            if (!Navigation.TryWorldToCell(candidate.Position, out var cell)) continue;

            var enclosure = Congestion.At(cell);
            var slot = chosenCount;
            while (slot > 0 && chosenScore[slot - 1] > enclosure)
            {
                if (slot < CongestionRecoveryBatch)
                {
                    chosen[slot] = chosen[slot - 1];
                    chosenScore[slot] = chosenScore[slot - 1];
                }
                slot--;
            }
            if (slot >= CongestionRecoveryBatch) continue;
            chosen[slot] = i;
            chosenScore[slot] = enclosure;
            if (chosenCount < CongestionRecoveryBatch) chosenCount++;
        }
        if (chosenCount == 0) return;

        for (var pick = 0; pick < chosenCount; pick++)
        {
            ref var stalled = ref agents[chosen[pick]];
            var blockedDirection = stalled.PreferredVelocity.LengthSquared() > 0.0001f
                ? Vector2.Normalize(stalled.PreferredVelocity)
                : Vector2.Normalize(stalled.Destination - stalled.Position);
            stalled.RepathRequested = true;
            stalled.HasRepathAvoidance = true;
            // Exclude the gap it is failing at, not a point just in front of it. A fixed
            // offset lands inside the queue rather than on the thing the queue is waiting
            // for, so the replan comes back with a route through the same gap.
            stalled.RepathAvoidanceCenter = pathService.TryFindObstructingAperture(
                stalled.Position,
                blockedDirection,
                stalled.Radius,
                out var aperture)
                ? aperture
                : stalled.Position + blockedDirection * 1.25f;
            stalled.RepathCooldown = 3f;
            stalled.StuckSeconds = 0f;
            stalled.RouteCommitSeconds = 0f;
            CongestionRepathCount++;
        }
        congestionRecoveryCooldown = CongestionRecoveryInterval;

        ref var leader = ref agents[chosen[0]];
        LastCongestionRoot = leader.Id;
        LastCongestionRepathAgent = leader.Id;
    }

    private void SyncAgentColliders()
    {
        foreach (ref readonly var agent in Agents.All)
        {
            if (!agent.IsAlive) continue;
            Colliders.Move(agent.Colliders.Movement, agent.Position);
            Colliders.Move(agent.Colliders.Avoidance, agent.Position);
            Colliders.Move(agent.Colliders.Placement, agent.Position);
            Colliders.Move(agent.Colliders.Interaction, agent.Position);
        }
    }

    private void AdvanceWaypoint(ref AgentState agent, Vector2 waypoint, int waypointCount)
    {
        var isFinalWaypoint = agent.WaypointIndex + 1 >= waypointCount;
        agent.WaypointIndex++;
        if (isFinalWaypoint) Arrive(ref agent);
    }

    private void Arrive(ref AgentState agent)
    {
        agent.UsesFlowTransit = false;
        // Waypoint advance fires inside the arrival tolerance, so a body would
        // otherwise stop up to WaypointArrivalDistance short of the point the
        // player actually commanded. Settle onto the destination whenever that
        // last centimetre is both statically clear and physically unoccupied.
        SnapToDestination(ref agent);
        agent.Velocity = Vector2.Zero;
        agent.HasDestination = false;
        agent.ArrivedThisTick = true;
        agent.PreferredVelocity = Vector2.Zero;
        agent.CongestionYieldSeconds = 0f;
        agent.CrowdedArrivalAttempts = 0;
        agent.CrowdedArrivalContactFrames = 0;
        agent.HasRepathAvoidance = false;
        CompletePath(ref agent);

        switch (agent.LocomotionState)
        {
            case AgentLocomotionState.Idle when agent.ReturningToHold:
                agent.ReturningToHold = false;
                agent.HoldReturnCooldown = 0f;
                // The snap only lands exactly when the hold point is physically
                // free. Inside a packed formation it is not, so accept the
                // reached position as the new hold point rather than restarting
                // the return from just outside the tolerance on the next tick.
                agent.HoldPosition = agent.Position;
                break;
            case AgentLocomotionState.Patrol:
                agent.PatrolTowardEnd = !agent.PatrolTowardEnd;
                agent.BehaviorUpdateCooldown = 0f;
                break;
            case AgentLocomotionState.Follow:
            case AgentLocomotionState.Chase:
            case AgentLocomotionState.Flee:
                agent.BehaviorUpdateCooldown = 0f;
                break;
            default:
                agent.LocomotionState = AgentLocomotionState.Idle;
                agent.HoldPosition = agent.Position;
                agent.ReturningToHold = false;
                break;
        }
    }

    private void SnapToDestination(ref AgentState agent)
    {
        var offset = agent.Destination - agent.Position;
        var distance = offset.Length();
        if (distance <= 0.0001f || distance > WaypointArrivalDistance + 0.0001f) return;
        if (!pathService.IsContinuousStepClear(agent.Position, agent.Destination, agent.NavigationRadius)) return;
        if (!CanOccupyArrivalPosition(agent, agent.Destination)) return;
        agent.Position = agent.Destination;
    }

    private bool CanOccupyArrivalPosition(in AgentState agent, Vector2 position)
    {
        if (!pathService.IsPositionNavigable(position, agent.NavigationRadius)) return false;
        foreach (ref readonly var other in Agents.All)
        {
            if (!other.IsAlive || other.Id == agent.Id) continue;
            var minimumDistance = agent.NavigationRadius + other.Radius + 0.001f;
            if (Vector2.DistanceSquared(position, other.Position) <
                minimumDistance * minimumDistance)
            {
                return false;
            }
        }
        return true;
    }

    private void HaltMovement(ref AgentState agent, bool preserveBehavior = false)
    {
        agent.UsesFlowTransit = false;
        agent.Velocity = Vector2.Zero;
        agent.PreferredVelocity = Vector2.Zero;
        agent.HasDestination = false;
        agent.StuckSeconds = 0f;
        agent.CongestionYieldSeconds = 0f;
        agent.CrowdedArrivalAttempts = 0;
        agent.CrowdedArrivalContactFrames = 0;
        agent.RepathRequested = false;
        agent.HasRepathAvoidance = false;
        CompletePath(ref agent);
        if (!preserveBehavior)
        {
            agent.LocomotionState = AgentLocomotionState.Idle;
            agent.HoldPosition = agent.Position;
            agent.ReturningToHold = false;
            agent.HoldReturnCooldown = 0f;
        }
    }

    private void PauseHoldReturn(ref AgentState agent)
    {
        CompletePath(ref agent);
        agent.Velocity = Vector2.Zero;
        agent.PreferredVelocity = Vector2.Zero;
        agent.HasDestination = false;
        agent.ReturningToHold = false;
        agent.StuckSeconds = 0f;
        agent.CongestionYieldSeconds = 0f;
        agent.RepathRequested = false;
        agent.HasRepathAvoidance = false;
        agent.HoldReturnCooldown = 0.75f;
    }

    private void CompletePath(ref AgentState agent)
    {
        paths.Release(agent.Path);
        agent.Path = PathHandle.None;
        agent.WaypointIndex = 0;
    }
}
