using System.Numerics;
using System.Diagnostics;
using RTSGame.Debug;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Commands;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;
using RTSGame.Simulation.Movement;
using RTSGame.Simulation.Navigation;
using RTSGame.Simulation.Persistence;
using RTSGame.Simulation.Placement;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Threat;
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
    private readonly EconomySystem economy = new();
    private float congestionRecoveryCooldown;
    private int rasterizedTerrainRevision = -1;
    private int routePlansThisTick;
    private long pathfindingTicksThisTick;

    public AgentStore Agents { get; } = new();
    /// <summary>Places that produce and store. §6's hauling network is between these.</summary>
    public NodeStore Nodes { get; } = new();
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
    /// <summary>
    /// How big the routing partition actually is, for a given body radius.
    /// </summary>
    /// <remarks>
    /// <b>Not the same number as <c>RoutingFidelity.RefinedRegions</c>, and mistaking the two hid the
    /// answer to the question the relief sweep exists to ask.</b> Refined regions are the ones <em>one
    /// search</em> had to open; this is the whole decomposition. On a map whose partition had quietly
    /// collapsed to a rectangle per cell, a search that happened to run over uniform ground still reported
    /// a handful — so the measurement said the partition was fine while the partition was the problem.
    /// </remarks>
    internal (int Rectangles, int Crossings, long Bytes) RouteMesh(float agentRadius)
    {
        var (mesh, _) = pathService.Mesh(agentRadius);
        return (mesh.Count, mesh.Crossings.Count, mesh.ResidentBytes);
    }

    internal RoutingFidelity MeasureRectangleFidelity(Vector2 goalPosition, float agentRadius)
    {
        if (!Navigation.TryWorldToCell(Terrain.ClampPosition(goalPosition), out var goal))
        {
            throw new ArgumentOutOfRangeException(nameof(goalPosition));
        }

        return pathService.MeasureRectangleFidelity(goal, agentRadius);
    }
    public long PathQueries => pathService.PathQueries;

    /// <summary>
    /// What became of the bodies in the last orders: on the shared field, on their own route, or refused one.
    /// </summary>
    /// <remarks>
    /// The three outcomes an order can have for a body, which is the vocabulary any behaviour decision here
    /// needs. Refused is the one that reads as a bug from the chair — the body has an order and does nothing —
    /// and PathService's failure counters say which of the four refusals it was.
    /// </remarks>
    public (long Transit, long SlotPath, long Refused) OrderOutcomes =>
        (pathService.OrdersOnTransit, pathService.OrdersOnSlotPath, pathService.OrdersRefused);

    /// <summary>Why route requests came back empty, by cause. See PathService.PathNoStartCell.</summary>
    public (long NoStartCell, long NoGoalCell, long StartUnresolvable, long GoalUnresolvable,
            long SearchFoundNothing, long TruncatedToStart) RouteRefusals =>
        (pathService.PathNoStartCell, pathService.PathNoGoalCell, pathService.PathStartUnresolvable,
         pathService.PathGoalUnresolvable, pathService.PathSearchFoundNothing,
         pathService.PathTruncatedToStart);

    /// <summary>Whether a dropped body found priced ground again. See PathService.FindFieldEntry.</summary>
    /// <summary>What re-rasterising navigation has cost. See NavigationRasterizer.RebuildTicks.</summary>
    public (double Milliseconds, int Rebuilds, double TerrainMs, double RestMs, double ApplyMs) NavRasterCost =>
        (NavigationRasterizer.RebuildTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
         NavigationRasterizer.Rebuilds,
         NavigationRasterizer.TerrainPassTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
         NavigationRasterizer.RestPassTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
         NavigationRasterizer.ApplyPassTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

    /// <summary>What the abstract layer's climb term costs, in calls and height samples. See §97.</summary>
    public (long Calls, long Samples) ClimbCost => (pathService.ClimbCalls, pathService.ClimbSamples);

    /// <summary>Highest local pressure at a transit drop. See FieldEntryPressureCeiling.</summary>
    public float WorstDropPressure => pathService.WorstDropPressure;

    public (long Found, long Missed) FieldEntries =>
        (pathService.FieldEntriesFound, pathService.FieldEntriesMissed);

    /// <summary>
    /// Why bodies leave the cohort's shared field, split by cause. See §96.
    /// </summary>
    /// <remarks>
    /// Held by the path service rather than here, and that is the determinism census talking: a field on the
    /// world must be fingerprinted, argued as derived, or argued as wall-clock, and a diagnostic counter is
    /// none of the three. The service is already argued away as derived, so counters that nothing reads back
    /// live there and are forwarded through properties, which have no field for the census to find.
    /// </remarks>
    public (long Rejected, long NoGradient) FlowTransitDrops =>
        (pathService.FlowTransitDropsRejected, pathService.FlowTransitDropsNoGradient);

    /// <summary>What the cell search actually explored. See PathService.PathExpansions.</summary>
    public (long Expansions, long Worst, long Failures, int GridCells) PathSearch =>
        (pathService.PathExpansions, pathService.PathExpansionsWorst,
         pathService.PathFailures, pathService.GridCells);

    /// <summary>Where routing's time went, split three ways. See PathService.RoutingCost.</summary>
    public (double MeshMs, int MeshBuilds, int MeshCacheHits, int MeshRectangles,
            double TileMs, long TileFills, double FieldMs, int Fields,
            double TileSeedMs, double TileSearchMs, int TileSeedCells,
            long RegionRelaxations, long RegionSteps) RoutingCost =>
        pathService.RoutingCost;
    public long AvoidanceSolves => steeringSystem.Solver.Solves;
    public long AvoidanceInfeasible => steeringSystem.Solver.InfeasibleSolves;
    public long AvoidanceTerrainFallbacks => steeringSystem.Solver.TerrainFallbacks;
    public long AvoidanceTerrainDeadStops => steeringSystem.Solver.TerrainFallbackFailures;
    /// <summary>Walls a body ignored because they were its own workplace's.</summary>
    public long OwnWorkplaceSkips => steeringSystem.Solver.OwnWorkplaceSkips;
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

    /// <summary>Production, consumption, catchments and the hauling board.</summary>
    public EconomySystem Economy => economy;

    /// <summary>Where in the year this world is.</summary>
    /// <remarks>
    /// Derived from the tick number and the epoch rather than counted, so there is no second opinion
    /// about what time it is.
    /// </remarks>
    public CalendarDate Date => WorldCalendar.At(TickNumber + EpochTicks);

    /// <summary>Ticks the world had already lived through when this run of it began.</summary>
    /// <remarks>
    /// One number, and it is the whole of what §5's succession needs from the calendar: a career ends
    /// and the next begins on the same map in whatever year the map has reached. It also lets anything
    /// that wants to examine a particular season start in it rather than simulating its way there,
    /// which is how the economy self-test looks at a harvest without spending three seasons getting to
    /// one.
    /// </remarks>
    internal long EpochTicks { get; private set; }

    /// <summary>Starts this world partway through the calendar.</summary>
    internal void StartAtSeconds(float seconds) =>
        EpochTicks = (long)MathF.Round(seconds / (float)FixedDeltaSeconds);

    /// <summary>Builds a node and gives it a collider so bodies route around it.</summary>
    public NodeId AddNode(
        NodeKind kind,
        Vector2 position,
        int capacity,
        Resource produces = Resource.Grain,
        float catchmentSeconds = 60f,
        int occupancy = 0,
        FactionId? faction = null,
        bool built = true)
    {
        var resolved = faction ?? new FactionId(0);
        var at = Terrain.ClampPosition(position);
        var id = Nodes.Add(new EconomyNode
        {
            Kind = kind,
            Faction = resolved,
            Position = at,
            Capacity = capacity,
            Produces = produces,
            CatchmentSeconds = catchmentSeconds,
            Occupancy = occupancy,
            // Standing and finished unless the caller says otherwise. The default is <em>built</em> rather
            // than under construction because most callers are scenarios and tests laying out a settlement
            // that already exists — a fixture that had to wait a season for its granary would be testing
            // construction rather than whatever it was about. The game's own build key passes false.
            BuildWork = built ? Construction.LabourFor(kind) : 0f,
            MaxCondition = StructuralProjects.MaxConditionFor(kind),
            Condition = built ? StructuralProjects.MaxConditionFor(kind) : 0f,
            StructuralTarget = kind,
            // <b>What this ground is worth, asked once, here.</b> The single place nodes are made, so every
            // caller gets it — scenarios, self-tests and the player's own build key alike — and no caller has
            // to remember. A map with no relief has no soil field, which is how the flat ground every
            // calibrated scenario runs on keeps a fertility of exactly one.
            Fertility = kind == NodeKind.Farm && Terrain.Soil is { } soil
                ? soil.FertilityAt(at)
                : FertilityWithoutSoil(kind),
        });
        Placement.Transform.TryWorldToCell(at, out var cell);
        Nodes.Get(id).Collider = Colliders.Add(
            ColliderOwner.Placement(cell, Placement.Transform),
            resolved,
            ColliderLayer.Structure,
            ColliderRole.Interactable,
            ColliderShape.Aabb(new Vector2(MathF.Max(0.5f, NodeFootprint.HalfExtentOf(kind)))),
            at);

        // A building is ground nobody can walk over, and the way to say that is the placement grid:
        // both the navigation raster and the static side of the velocity solve are derived from it, so
        // occupying the cells buys routing around the building and steering around it at once, with no
        // second description of the same wall to fall out of step. The node's own collider stays
        // interactable — it is what a body queries to interact, not what stops it.
        //
        // Piles are deliberately excluded. Goods on the ground are not a wall, and re-rasterising the
        // map every time a cart is destroyed would be both wrong and expensive.
        if (NodeFootprint.Blocks(kind)) OccupyFootprint(id);
        return id;
    }

    /// <summary>Applies structural damage without deciding yet what zero condition means in combat.</summary>
    /// <remarks>
    /// This is the prepared seam, not ambient gameplay damage. Condition remains separate from construction,
    /// and a zero-condition node stays present and blocking until the later combat/destruction arc settles
    /// breach rules. Headless repair fixtures use this same entrance future weapons will use.
    /// </remarks>
    public bool DamageStructure(NodeId id, float amount)
    {
        if (amount <= 0f || !Nodes.Contains(id)) return false;
        ref var node = ref Nodes.Get(id);
        if (!node.IsBuilt || !node.IsStructure) return false;
        node.Condition = MathF.Max(0f, node.Condition - amount);
        return true;
    }

    /// <summary>Opens a physical repair project on a damaged completed structure.</summary>
    public bool BeginRepair(NodeId id)
    {
        if (!Nodes.Contains(id)) return false;
        return StructuralProjects.BeginRepair(ref Nodes.Get(id));
    }

    /// <summary>Opens the proved same-footprint upgrade while retaining the node and present wall.</summary>
    public bool BeginUpgrade(NodeId id, NodeKind target)
    {
        if (!Nodes.Contains(id)) return false;
        return StructuralProjects.BeginUpgrade(ref Nodes.Get(id), target);
    }

    /// <summary>
    /// The neutral fertility, and a refusal if neutral is a lie about this map.
    /// </summary>
    /// <remarks>
    /// <b>The one ordering this feature rests on, made impossible to get wrong instead of merely true.</b> A
    /// field reads the soil when it is placed, so the country has to be painted first — and it is, on both
    /// paths that generate relief, by two call sites neither of which mentions the other. Nothing stopped a
    /// third path from placing fields on generated ground before the soil existed, and the symptom would have
    /// been every field reporting exactly 1.00: not a crash, not a wrong-looking number, just a map that had
    /// quietly stopped mattering to the economy it was generated for.
    /// <para>
    /// The tell is precise, which is what makes it worth asserting rather than reporting: a solved drainage
    /// means relief was generated, and generated relief with no soil field means the country was never
    /// painted. Flat ground has neither, and is genuinely neutral.
    /// </para>
    /// </remarks>
    private float FertilityWithoutSoil(NodeKind kind)
    {
        if (kind == NodeKind.Farm && Terrain.Drainage is not null)
        {
            throw new InvalidOperationException(
                "A field is being placed on generated terrain before the country has been painted, so it " +
                "would take a neutral fertility on ground that is not neutral. Paint the country (which " +
                "builds the soil field) before founding anything that farms.");
        }

        return 1f;
    }

    /// <summary>
    /// Fills in the footprint of any node an assignment's places happen to sit on.
    /// </summary>
    /// <remarks>
    /// Because getting this wrong is silent and fatal, and it was: a post given at a farm without its
    /// extent has a tolerance measured in body radii — 1.11 m for a villager — while the farm's wall,
    /// once buildings occupy their cell, reaches 1.12 m. One centimetre short, forever, and the hand
    /// walks at the farm forty-seven times and never arrives. Nothing about that failure points at the
    /// missing argument.
    /// <para>
    /// So the world fills it in. A caller may still pass an extent explicitly — the hauling board does,
    /// because it has the nodes in hand — and anything left at zero is looked up here, which means every
    /// path into an assignment gets the right answer including the ones written before nodes existed.
    /// </para>
    /// </remarks>
    private Assignment ResolveNodeExtents(Assignment assignment)
    {
        if (assignment.Kind == AssignmentKind.None) return assignment;
        var reach = PlacementCellSize * 0.5f;
        if (assignment.PlaceExtent <= 0f &&
            EconomySystem.NodeAt(Nodes, assignment.Anchor, reach) is { IsValid: true } near)
        {
            ref readonly var node = ref Nodes.Get(near);
            assignment = assignment with
            {
                Anchor = node.Position,
                PlaceExtent = node.FootprintRadius,
            };
        }

        if (!assignment.HasTwoEnds || assignment.FarPlaceExtent > 0f) return assignment;
        if (EconomySystem.NodeAt(Nodes, assignment.FarAnchor, reach) is not { IsValid: true } far)
        {
            return assignment;
        }

        ref readonly var farNode = ref Nodes.Get(far);
        return assignment with
        {
            FarAnchor = farNode.Position,
            FarPlaceExtent = farNode.FootprintRadius,
        };
    }

    /// <summary>
    /// Timber a handcart is built from, in whole units.
    /// </summary>
    /// <remarks>
    /// One villager's sack, which is the smallest amount of anything anybody carries in this game and
    /// therefore the natural unit for "token". A settlement burns two thousand a year, so a cart is well
    /// under two per cent of its fuel — cheap enough that the first one is never the decision, and dear
    /// enough that thirty of them is, which is the shape a cost like this should have.
    /// </remarks>
    public static int CartTimber => UnitType.Villager.CarryCapacity;

    /// <summary>
    /// Puts a body on a hauling route, building it a cart out of the settlement's timber.
    /// </summary>
    /// <remarks>
    /// <b>Hauling is a job, not a kind of unit.</b> There is nothing to spawn: a villager is given a route,
    /// the settlement spends a sack of timber on a handcart, and the body becomes wider, slower and
    /// higher-capacity for as long as it keeps the job. Ask it to do something else and the cart goes.
    /// <para>
    /// It is refused rather than made free if the timber is not there, and that is the interesting part —
    /// <em>you cannot build a hauling network before you have a wood supply</em>, which is exactly the
    /// dependency Stage B's receding wood line creates. The wood is <see cref="ResourceTotals"/>-consumed
    /// rather than moved, because the cart is not a place goods are stored: it has been turned into a cart,
    /// and conservation should say so.
    /// </para>
    /// <para>
    /// The route is a <see cref="AssignmentKind.Carry"/> and not a <see cref="AssignmentKind.Haul"/>: the
    /// board's hauls are one round trip each so that they can be re-priced, and this is a standing
    /// commitment the player made and nobody should re-auction.
    /// </para>
    /// </remarks>
    public bool TryAssignRoute(AgentId body, NodeId source, NodeId sink)
    {
        if (!Agents.Contains(body) || !Nodes.Contains(source) || !Nodes.Contains(sink)) return false;
        if (source == sink) return false;
        if (!TryChooseRouteCargo(source, sink, Resource.Grain, out var cargo)) return false;
        ref var agent = ref Agents.Get(body);
        // Already carting: the cart is bought and paid for, so a new route is free. Changing where
        // somebody drives is not a new cart.
        if (!agent.HasCart && !TryBuildCart(ref agent)) return false;

        ref readonly var from = ref Nodes.Get(source);
        ref readonly var to = ref Nodes.Get(sink);
        JobSystem.Assign(ref agent, Assignment.Carry(
            source, from.Position, sink, to.Position, cargo, EconomySystem.HandoverSeconds,
            from.FootprintRadius, to.FootprintRadius));
        return true;
    }

    /// <summary>
    /// Chooses the next useful load for a player-authored route.
    /// </summary>
    /// <remarks>
    /// A route is a commitment between two places, not one permanent commodity. A building site asks for
    /// whichever missing material the source can best answer; an ordinary store takes whichever available
    /// stock makes the fullest useful load. If the source is empty, the route keeps the best outstanding
    /// demand and waits rather than disappearing, so a forward depot may be routed before its next load
    /// arrives. Ties retain the current cargo, then fall back to resource order, which makes the decision
    /// stable and deterministic.
    /// </remarks>
    public bool TryChooseRouteCargo(
        NodeId source,
        NodeId sink,
        Resource preferred,
        out Resource cargo)
    {
        cargo = preferred;
        if (!Nodes.Contains(source) || !Nodes.Contains(sink) || source == sink) return false;
        return TryChooseRouteCargo(in Nodes.Get(source), in Nodes.Get(sink), preferred, out cargo);
    }

    private static bool TryChooseRouteCargo(
        in EconomyNode source,
        in EconomyNode sink,
        Resource preferred,
        out Resource cargo)
    {
        cargo = preferred;
        var found = false;
        var bestAvailable = -1;
        var bestDemand = -1;
        foreach (var resource in Resources.All)
        {
            var demand = sink.HasStructuralProject ? sink.Wanted(resource) : sink.RoomFor(resource);
            if (demand <= 0) continue;
            var available = Math.Min(source.Stock[resource], demand);
            var keepsCurrent = resource == preferred;
            var better = available > bestAvailable ||
                         available == bestAvailable && demand > bestDemand ||
                         available == bestAvailable && demand == bestDemand && keepsCurrent && cargo != preferred;
            if (!better) continue;
            cargo = resource;
            bestAvailable = available;
            bestDemand = demand;
            found = true;
        }

        return found;
    }

    /// <summary>
    /// A villager is born beside their house, with nothing to do.
    /// </summary>
    /// <remarks>
    /// <b>Idle on purpose.</b> A new person could be sent to the nearest field that wants hands, and that
    /// would be the game playing itself: posting people is the decision §2 says attention is for, and
    /// auto-assigning them would quietly convert the one interesting choice in the settlement into a
    /// notification. So they stand outside their house until somebody gives them a job — and the report's
    /// spare-hands count is the prompt.
    /// <para>
    /// Beside the house rather than in it, and the spawn nudges off built ground, because a house is a
    /// 4.5 m building and its own position is inside its walls.
    /// </para>
    /// </remarks>
    private void BornAt(NodeId house, Vector2 position)
    {
        var body = UnitType.Villager;
        // Just clear of the wall, on the side away from the settlement's middle so a newcomer does not
        // appear in the middle of the traffic between store and field.
        var outward = position.LengthSquared() > 0.001f ? Vector2.Normalize(position) : Vector2.UnitX;
        var at = position + outward * (NodeFootprint.HalfExtentOf(NodeKind.House) + body.Radius + 0.6f);
        var born = SpawnAgent(at, body);
        // Bound to the house that produced them straight away rather than waiting for the next rebind, so
        // the house's occupancy reflects the birth on the tick it happened and cannot briefly overshoot
        // into a second one.
        ref var agent = ref Agents.Get(born);
        agent.Home.House = house;
        agent.Home.Revision = Nodes.Revision;
        agent.Home.RebindSeconds = EconomySystem.RebindSeconds;
    }

    /// <summary>
    /// Books a body's load as gone from the world, and clears its hands.
    /// </summary>
    /// <remarks>
    /// For the one case where goods leave without being eaten and without being dropped: carried over the
    /// edge of the map. From the settlement's books that is indistinguishable from being eaten — the units
    /// are gone — so it goes through the same door as a loaf rather than getting a term of its own, which
    /// would be a hole in the conservation identity dressed up as bookkeeping.
    /// <para>
    /// It is separate from despawning on purpose: a body <em>killed</em> drops what it was carrying, which
    /// is the whole of why intercepting a loaded raider is worth doing. Only a body that got away takes it.
    /// </para>
    /// </remarks>
    public void TakeOutOfTheWorld(AgentId body)
    {
        if (!Agents.Contains(body)) return;
        ref var agent = ref Agents.Get(body);
        if (agent.Jobs.CarriedUnits <= 0) return;
        economy.RecordConsumed(agent.Jobs.Carrying, agent.Jobs.CarriedUnits);
        agent.Jobs.CarriedUnits = 0;
    }

    /// <summary>
    /// Sends one body somewhere because of a threat, as an interrupt rather than a new job.
    /// </summary>
    /// <remarks>
    /// Queued rather than applied, so it lands next tick through the same path a player's right-click takes
    /// — which is what makes the assignment survive it. It is also the honest test of whether those orders
    /// are enough to play the game with: if defence needs a private channel into the movement layer, so
    /// would a player.
    /// </remarks>
    private void MarchAgainstThreat(AgentId body, Vector2 toward) =>
        QueueMove(new[] { body }, Terrain.ClampPosition(toward));

    /// <summary>
    /// Puts a body inside a building, out of every query, and takes it back out again.
    /// </summary>
    /// <remarks>
    /// Reversible absence, which is a different thing from a despawn: the four proxies are disabled rather
    /// than removed, so the body comes back as itself with every handle anybody held still valid, and it
    /// stays in the roster the whole time so the settlement can go on knowing its granary is being robbed.
    /// <para>
    /// It stops moving because it has nowhere to be and nothing can push it — with its proxies dark, the
    /// avoidance solve does not see it and neither does depenetration, so a crowd walks over the doorway it
    /// went in at rather than shouldering it around the yard.
    /// </para>
    /// </remarks>
    public bool EnterShelter(AgentId id)
    {
        if (!Agents.Contains(id)) return false;
        ref var agent = ref Agents.Get(id);
        if (agent.Sheltered) return true;
        agent.Sheltered = true;
        CompletePath(ref agent);
        agent.Velocity = Vector2.Zero;
        SetBodyPresent(in agent, false);
        return true;
    }

    /// <summary>Back out into the world, at a place, and visible to everything again.</summary>
    public bool LeaveShelter(AgentId id, Vector2 at)
    {
        if (!Agents.Contains(id)) return false;
        ref var agent = ref Agents.Get(id);
        if (!agent.Sheltered) return true;
        agent.Sheltered = false;
        // Out onto open ground, not into the wall it came through.
        agent.Position = NudgeOutOfBuildings(Terrain.ClampPosition(at), agent.Radius);
        SetBodyPresent(in agent, true);
        // The proxies could not follow it while they were dark, so they are where it went in. Put them
        // where it came out, or it collides at the door for a tick and is depenetrated somewhere odd.
        Colliders.Move(agent.Colliders.Movement, agent.Position);
        Colliders.Move(agent.Colliders.Avoidance, agent.Position);
        Colliders.Move(agent.Colliders.Placement, agent.Position);
        Colliders.Move(agent.Colliders.Interaction, agent.Position);
        return true;
    }

    private void SetBodyPresent(in AgentState agent, bool present)
    {
        Colliders.SetEnabled(agent.Colliders.Movement, present);
        Colliders.SetEnabled(agent.Colliders.Avoidance, present);
        Colliders.SetEnabled(agent.Colliders.Placement, present);
        Colliders.SetEnabled(agent.Colliders.Interaction, present);
    }

    /// <summary>The danger has passed: drop the walk, and let the jobs layer have the body back.</summary>
    private void StandDown(AgentId body) => QueueStop(new[] { body });

    /// <summary>Go at a body and keep going at it, which is what the chase order already does.</summary>
    private void ChargeThreat(AgentId body, AgentId target) => QueueChase(new[] { body }, target);

    /// <summary>
    /// Starts an errand to put a load somewhere before its carrier goes to fight.
    /// </summary>
    /// <remarks>
    /// <b>Hands first, and it is not a courtesy.</b> A villager who runs at a raider with forty grain on
    /// its back is carrying the raider's prize into its reach: it loses the fight, the goods change hands
    /// on the spot, and the raid is paid for by the defence. So a defender with a load deals with it and
    /// then joins, in one order of preference:
    /// <list type="number">
    /// <item><b>A store away from the trouble</b>, if one is within a settlement's width. Not just any
    /// store — one outside the reach of whatever is causing this, or stowing walks the load into the
    /// fight.</item>
    /// <item><b>The ground where it stands</b>, otherwise, which is instant. A heap in the open can be
    /// looted and that is the honest cost of the choice; a heap is at least stationary and nobody has to
    /// die holding it.</item>
    /// </list>
    /// <para>
    /// Handing a load into a store is a physical act here rather than a delivery, deliberately: deliveries
    /// belong to an assignment's legs, and this is an interrupt. An interrupt that rewrote an assignment to
    /// borrow its machinery would break the jobs model's one prohibition, so the units move and the shift
    /// is left exactly as it was — the villager goes back to the same field afterwards.
    /// </para>
    /// <para>
    /// Returns true while the body still has a load to deal with, which is the defence's signal not to send
    /// it anywhere yet. The errand itself is run by <see cref="AdvanceStowing"/>, not from here.
    /// </para>
    /// </remarks>
    private bool StowBeforeFighting(AgentId body, Vector2 danger)
    {
        if (!Agents.Contains(body)) return false;
        ref var agent = ref Agents.Get(body);
        if (agent.Jobs.CarriedUnits <= 0) return false;
        if (agent.PuttingDown) return true;

        var haven = HavenFor(in agent, danger);
        if (!Nodes.Contains(haven))
        {
            // Nowhere to put it that is not into the raid's hands. Drop it where you stand and go.
            DropCargo(ref agent);
            return false;
        }

        agent.PuttingDown = true;
        agent.StowInto = haven;
        return true;
    }

    /// <summary>
    /// Runs the errand: walk to the store, hand the load in, and be done with it.
    /// </summary>
    /// <remarks>
    /// Owned here rather than by whatever started it, because it has to finish whether or not the reason
    /// still applies — a raid that moves on, or a defender that walks out of sight of it, must not leave
    /// somebody standing in a yard holding a sack. Every way it can fail ends with the load on the ground
    /// rather than on the body: the store was destroyed, or filled while the load was walking to it, or
    /// took only part of what was offered.
    /// </remarks>
    private void AdvanceStowing()
    {
        foreach (ref var agent in Agents.MutableSpan())
        {
            if (!agent.IsAlive || !agent.PuttingDown) continue;
            if (agent.Jobs.CarriedUnits <= 0)
            {
                agent.PuttingDown = false;
                continue;
            }

            if (!Nodes.Contains(agent.StowInto))
            {
                DropCargo(ref agent);
                agent.PuttingDown = false;
                continue;
            }

            ref var store = ref Nodes.Get(agent.StowInto);
            var carrying = agent.Jobs.Carrying;
            if (!store.IsAlive || store.RoomFor(carrying) <= 0)
            {
                DropCargo(ref agent);
                agent.PuttingDown = false;
                continue;
            }

            // Reaching a building means reaching its wall, on the same terms the jobs layer uses.
            var reach = store.FootprintRadius + agent.Radius +
                        JobDefaults.TouchSlack + JobDefaults.RasterReach;
            if (Vector2.DistanceSquared(agent.Position, store.Position) > reach * reach)
            {
                // Asked again only when it is not already walking, or a body re-plans every tick.
                if (!agent.HasDestination) QueueMove(new[] { agent.Id }, store.Position);
                continue;
            }

            var delivered = Math.Min(agent.Jobs.CarriedUnits, store.RoomFor(carrying));
            store.Stock.Add(carrying, delivered);
            agent.Jobs.CarriedUnits -= delivered;
            if (agent.Jobs.CarriedUnits > 0) DropCargo(ref agent);
            agent.PuttingDown = false;
        }
    }

    /// <summary>How many bodies are carrying a load out of harm's way right now.</summary>
    /// <remarks>
    /// Counted rather than kept, so it needs no place in the fingerprint's ledger: the state it reads is
    /// <c>AgentState.PuttingDown</c>, which the body schema already walks.
    /// </remarks>
    internal int PuttingDownCount
    {
        get
        {
            var busy = 0;
            foreach (ref readonly var agent in Agents.All)
            {
                if (agent.IsAlive && agent.PuttingDown) busy++;
            }

            return busy;
        }
    }

    /// <summary>
    /// How far a body will carry a load to put it somewhere safe, in metres.
    /// </summary>
    /// <remarks>
    /// <b>The settlement's own width, and it was a rally window first — which was wrong.</b> Bounding the
    /// walk by how long the fight lasts sounds right and gets the priorities backwards: it made a depot
    /// nineteen metres away too far to bother with, so the villager put forty grain on the ground beside
    /// the raid instead of carrying it to a store. The load's safety outranks this body's arrival, and it
    /// can afford to, because the surplus rule means somebody closer is already on their way. Past a
    /// settlement's width you are not stowing a load, you are leaving with it.
    /// </remarks>
    internal static float HavenMetres = 40f;

    /// <summary>The nearest store that will take this load and is not itself in the trouble.</summary>
    private NodeId HavenFor(in AgentState agent, Vector2 danger)
    {
        var best = NodeId.None;
        var bestDistance = HavenMetres;
        foreach (ref readonly var node in Nodes.All)
        {
            if (!node.IsAlive || !node.AcceptsDeliveries) continue;
            if (node.Faction != agent.Faction) continue;
            if (node.RoomFor(agent.Jobs.Carrying) <= 0) continue;
            // Outside the reach of whatever is causing this, or stowing walks the load into the fight.
            if (Vector2.Distance(node.Position, danger) <= ThreatSystem.ThreatMetres) continue;
            var distance = Vector2.Distance(node.Position, agent.Position);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = node.Id;
        }

        return best;
    }

    /// <summary>Somebody leaves for good, because their household went hungry too long.</summary>
    /// <remarks>
    /// The ordinary carrying-capacity correction, and reversible in the sense that matters: the settlement
    /// is smaller, so it eats less, so the survivors stop going short. It is not death — there is nothing
    /// to die of yet — and whatever they were carrying falls where they stood, because a resource does not
    /// stop existing because its carrier walked off over the hill.
    /// </remarks>
    private void Emigrate(AgentId body) => DespawnAgents(new[] { body });

    /// <summary>
    /// Builds a body a cart without giving it a route, or refuses for want of timber.
    /// </summary>
    /// <remarks>
    /// The role without the route: somebody who is a carter and has nothing particular to cart. The
    /// hauling board then finds them the jobs nobody would micromanage — stranded stock, and goods lying
    /// in the road — which is what a settlement's general carrier does between errands.
    /// </remarks>
    public bool TryBuildCart(AgentId body)
    {
        if (!Agents.Contains(body)) return false;
        ref var agent = ref Agents.Get(body);
        return !agent.HasCart && TryBuildCart(ref agent);
    }

    /// <summary>
    /// Spends the timber and swaps the body onto the cart's frame, or refuses.
    /// </summary>
    /// <remarks>
    /// The nearest store with the wood in it pays. Straight-line nearest rather than in route seconds,
    /// like every other short-range question a body asks: this is asked once, when the job is given, and a
    /// routing query to choose between two granaries the player can see would cost more than it settles.
    /// </remarks>
    private bool TryBuildCart(ref AgentState agent)
    {
        var yard = EconomySystem.NearestStoreWith(Nodes, Resource.Wood, CartTimber, agent.Faction, agent.Position);
        if (!Nodes.Contains(yard)) return false;
        Nodes.Get(yard).Stock.Add(Resource.Wood, -CartTimber);
        economy.RecordConsumed(Resource.Wood, CartTimber);
        WearBody(ref agent, UnitType.HaulerCart);
        agent.HasCart = true;
        return true;
    }

    /// <summary>Takes the cart away, which is what happens when a carter is given any other job.</summary>
    private void ScrapCart(ref AgentState agent)
    {
        if (!agent.HasCart) return;
        agent.HasCart = false;
        WearBody(ref agent, UnitType.Villager);
    }

    /// <summary>
    /// Swaps a live body onto a different frame: its size, its pace and what it can carry.
    /// </summary>
    /// <remarks>
    /// Not everything a <c>UnitType</c> describes — the appetite stays, because a carter is the same
    /// person and eats the same, and the navigation radius is one figure for the whole roster by design so
    /// there is nothing to change. What does change is the three things a cart actually is: how much room
    /// it takes up, how fast it goes and how much it holds. All four collider proxies are reshaped with
    /// it, or the body would be drawn and priced at one size and collide at another.
    /// </remarks>
    private void WearBody(ref AgentState agent, UnitType frame)
    {
        agent.Radius = frame.Radius;
        agent.MaximumSpeed = frame.MaximumSpeed;
        agent.TurningRadius = frame.TurningRadius;
        agent.CarryCapacity = frame.CarryCapacity;
        Colliders.Reshape(agent.Colliders.Movement, ColliderShape.Circle(frame.Radius));
        Colliders.Reshape(agent.Colliders.Avoidance, ColliderShape.Circle(frame.Radius + 0.30f));
        Colliders.Reshape(agent.Colliders.Placement, ColliderShape.Circle(frame.Radius + 0.10f));
        Colliders.Reshape(agent.Colliders.Interaction, ColliderShape.Circle(frame.Radius + 0.06f));
    }

    /// <summary>
    /// Turns a bare post that landed on a field or a tree into the job that belongs there.
    /// </summary>
    /// <remarks>
    /// A convenience of the testbed and not a mechanic: posting a villager is one key, and a player who
    /// drops one on a field means "farm this" rather than "stand here for six seconds indefinitely".
    /// Applied per body rather than per order because the shift length depends on what the body can
    /// carry — a tree is cut until the hands are full, and how long that takes is a fact about the hands.
    /// <para>
    /// It is also the only way a cutter gets its first tree. Everything after that is
    /// <see cref="TrySendBackToWork"/>, which is what makes the arrangement follow the wood line without
    /// the player re-posting anybody.
    /// </para>
    /// </remarks>
    private Assignment PostedOnAWorkSite(in AgentState agent, Assignment assignment)
    {
        if (assignment.Kind != AssignmentKind.Hold || agent.CarryCapacity <= 0) return assignment;
        var at = EconomySystem.NodeAt(Nodes, assignment.Anchor, PlacementCellSize);
        if (!Nodes.Contains(at)) return assignment;
        ref readonly var site = ref Nodes.Get(at);
        // A site first, whatever it is going to be: posting somebody on a half-built granary means "help
        // put this up", not "work here", and the same key does both because the difference is a fact about
        // the building rather than about the order.
        if (site.HasStructuralProject)
        {
            return Assignment.Build(
                at,
                site.Position,
                site.FootprintRadius,
                EconomySystem.HandoverSeconds,
                EconomySystem.WorkShiftSeconds);
        }

        return site.Kind switch
        {
            NodeKind.Farm => Assignment.Work(
                at, site.Position, site.FootprintRadius, Resource.Grain,
                EconomySystem.WorkShiftSeconds, EconomySystem.HandoverSeconds),
            // <b>Both deposits, and the shift length comes off the resource being assigned.</b> Written for a
            // moment as the body's <em>current</em> cargo, which is the cargo of the job it is leaving — so a
            // villager moving from the fields to a tree would have been given a grain shift at a trunk.
            NodeKind.Tree => Assignment.Work(
                at, site.Position, site.FootprintRadius, Resource.Wood,
                Deposits.LoadSeconds(Resource.Wood, agent.CarryCapacity),
                EconomySystem.HandoverSeconds),
            NodeKind.Outcrop => Assignment.Work(
                at, site.Position, site.FootprintRadius, Resource.Stone,
                Deposits.LoadSeconds(Resource.Stone, agent.CarryCapacity),
                EconomySystem.HandoverSeconds),
            _ => assignment,
        };
    }

    /// <summary>
    /// Moves a spawn position off built ground, if it landed on some.
    /// </summary>
    /// <remarks>
    /// Because a scenario that places a body relative to a building has to know how big the building is,
    /// and it did not: buildings went from 1.5 m across to 4.5 and 7.5, and carts that used to muster
    /// beside the granary were suddenly inside it — standing on ground with zero clearance, unable to
    /// route anywhere, for a whole simulated year. Every caller would have to be found and fixed, or this
    /// can be true once: <b>a body spawned inside a building appears beside it instead.</b> The search
    /// spirals outward by half a metre at a time and gives up rather than looping, because a body with
    /// nowhere legal to go is a scenario worth failing loudly.
    /// </remarks>
    /// <summary>
    /// Moves a body off ground it cannot legally stand on, which is not only ground with a building on it.
    /// </summary>
    /// <remarks>
    /// <b>"One villager pops in blocked by a tree every single run", diagnosed.</b> Named by
    /// <c>--placementcheck</c>: body 13 at (-18.2, 9.4), on <em>grass</em>, with no trunk within 1.61 m,
    /// and legal ground half a metre away. Nothing was on top of it. It was standing on a cell whose
    /// <em>clearance rung</em> is 0.25 m against a body that needs 0.37 — the raster quantises clearance to
    /// 0.25, 0.75 and 1.25, so a cell just inside the skirt of an impassable patch admits nobody, and the
    /// impassable patches here are forest cover rather than trunks.
    /// <para>
    /// And this only ever asked <c>IsPositionFreeOfPlacement</c> — is there a <em>building</em> here. Free
    /// of buildings and walkable are different questions, and the recurring one: a check that answers the
    /// question the layer it was written in happens to own rather than the question being asked. A body
    /// needs somewhere it can stand, and standing is the navigation layer's word.
    /// </para>
    /// <para>
    /// Both conditions now, on the candidate as well as the original — a ring search that accepts a
    /// building-free cell it still cannot walk on has only moved the problem.
    /// </para>
    /// </remarks>
    private Vector2 NudgeOutOfBuildings(Vector2 position, float radius)
    {
        if (CanStandAt(position, radius)) return position;
        for (var ring = 1; ring <= 24; ring++)
        {
            var distance = ring * NavigationCellSize;
            for (var bearing = 0; bearing < 12; bearing++)
            {
                var angle = bearing / 12f * MathF.Tau;
                var candidate = Terrain.ClampPosition(
                    position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance,
                    radius + BodyFootprint.NavigationMargin);
                if (CanStandAt(candidate, radius)) return candidate;
            }
        }

        return position;
    }

    /// <summary>Free of buildings <em>and</em> ground the router will let a body of this size occupy.</summary>
    private bool CanStandAt(Vector2 position, float radius)
    {
        if (!pathService.IsPositionFreeOfPlacement(position, radius)) return false;
        if (!Navigation.Transform.TryWorldToCell(position, out var cell)) return false;
        // Outside the raster is not a verdict: the caller clamps into the world and the terrain check above
        // has already had its say, so an unrasterised edge cell is accepted rather than refused forever.
        return !Navigation.Contains(cell) || Navigation.IsWalkable(cell, AgentDefaults.RoutingRadius);
    }

    /// <summary>
    /// Marks the placement cells a building stands on, so bodies route and steer around it.
    /// </summary>
    /// <remarks>
    /// An odd block of cells centred on the one the node's position falls in, and the node is snapped to
    /// that cell's centre so the wall, the drawing and the arrival tolerance describe the same square.
    /// The raster is rebuilt once for the whole building rather than once per cell.
    /// </remarks>
    private void OccupyFootprint(NodeId id)
    {
        var transform = Placement.Transform;
        ref var node = ref Nodes.Get(id);
        if (!transform.TryWorldToCell(node.Position, out var origin)) return;
        node.Position = transform.CellCenter(origin);
        Colliders.Move(node.Collider, node.Position);

        var span = NodeFootprint.CellsOf(node.Kind) / 2;
        var halfExtents = new Vector2(transform.CellSize * 0.5f - 0.025f);
        var changed = false;
        for (var dz = -span; dz <= span; dz++)
        for (var dx = -span; dx <= span; dx++)
        {
            var cell = new GridCell(origin.X + dx, origin.Z + dz);
            if (!transform.Contains(cell) || Placement.IsOccupied(cell)) continue;
            if (!Placement.SetOccupied(cell, true)) continue;
            changed = true;
            blockColliders[cell] = Colliders.Add(
                ColliderOwner.Placement(cell, transform),
                node.Faction,
                ColliderLayer.Structure,
                ColliderRole.MovementSolid | ColliderRole.PlacementBlocker | ColliderRole.Interactable,
                ColliderShape.Aabb(halfExtents),
                transform.CellCenter(cell));
        }

        if (changed) RefreshNavigationAfterPlacement();
    }

    /// <summary>People with no house to live in, which §6 wants named as a blocked sink.</summary>
    /// <remarks>
    /// Not an error and not starvation exactly: an unhoused body draws nothing, so a settlement with
    /// stores rising and people unhoused is the case §6 asks the interface to say out loud — "grain
    /// surplus rising, population capped by housing" — rather than one the player has to deduce.
    /// </remarks>
    public int UnhousedCount
    {
        get
        {
            var unhoused = 0;
            foreach (ref readonly var agent in Agents.All)
            {
                if (agent.IsAlive && !Nodes.Contains(agent.Home.House)) unhoused++;
            }

            return unhoused;
        }
    }

    /// <summary>Puts whatever a body is carrying on the ground where it stands.</summary>
    /// <remarks>
    /// Merged into a pile already lying there if one is close enough, so a lane where several carts were
    /// lost is a few heaps rather than one per cart. Nothing is created or destroyed: the units move
    /// from the body's back to a place, and a pile's contents are stored like any other node's — which
    /// is why dropped goods need no term in the conservation identity at all.
    /// </remarks>
    internal NodeId DropCargo(ref AgentState agent)
    {
        if (agent.Jobs.CarriedUnits <= 0) return NodeId.None;
        var resource = agent.Jobs.Carrying;
        var units = agent.Jobs.CarriedUnits;
        agent.Jobs.CarriedUnits = 0;

        foreach (ref var existing in Nodes.MutableSpan())
        {
            if (!existing.IsAlive || !existing.IsPile) continue;
            if (Vector2.DistanceSquared(existing.Position, agent.Position) > PileMergeDistanceSquared)
            {
                continue;
            }

            existing.Stock.Add(resource, units);
            existing.Capacity = Math.Max(existing.Capacity, existing.Stock[resource]);
            return existing.Id;
        }

        var pile = Nodes.Add(new EconomyNode
        {
            Kind = NodeKind.Pile,
            // Nobody's. A heap on the road is there for whoever reaches it, which is what makes looting
            // a thing that happens rather than a rule that has to be written.
            Faction = FactionId.None,
            Position = agent.Position,
            Capacity = units,
            CatchmentSeconds = 0f,
        });
        Nodes.Get(pile).Stock.Add(resource, units);
        return pile;
    }

    /// <summary>How near a heap has to be to have goods added to it rather than a new one started.</summary>
    private static float PileMergeDistanceSquared => 2.5f * 2.5f;

    /// <summary>Puts stock into a node by hand, recorded so conservation still balances.</summary>
    public void SeedStock(NodeId id, Resource resource, int units)
    {
        if (!Nodes.Contains(id) || units <= 0) return;
        ref var node = ref Nodes.Get(id);
        var accepted = Math.Min(units, node.RoomFor(resource));
        node.Stock.Add(resource, accepted);
        economy.RecordSeeded(resource, accepted);
    }

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
            type.CarryCapacity,
            type.Appetite,
            type.SightMetres,
            type.Strength,
            type.Health,
            role: UnitType.RoleOf(type));

    public AgentId SpawnAgent(
        Vector2 position,
        FactionId? faction = null,
        float radius = AgentDefaults.Radius,
        float maximumSpeed = AgentDefaults.MaximumSpeed,
        float navigationRadius = 0f,
        float turningRadius = 0f,
        int carryCapacity = 0,
        float appetite = 1f,
        float sightMetres = 22f,
        float strength = 1f,
        float health = 20f,
        bool allowEmbedded = false,
        AgentRole role = AgentRole.Villager)
    {
        position = Terrain.ClampPosition(position, radius + BodyFootprint.NavigationMargin);
        // Off built ground unless the caller is deliberately testing what happens on it. Two self-tests
        // are: expelling an embedded body is a feature, and clearance is asserted by putting a body where
        // its own radius does not fit.
        if (!allowEmbedded) position = NudgeOutOfBuildings(position, radius);
        var resolvedFaction = faction ?? new FactionId(0);
        var id = Agents.Spawn(
            position, resolvedFaction, radius, maximumSpeed, navigationRadius, turningRadius,
            carryCapacity, appetite, sightMetres, strength, health, role);
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

    /// <summary>Commits existing villagers to permanent militia conversion at a completed barracks.</summary>
    public void QueueTrainMilitia(IEnumerable<AgentId> agents, NodeId barracks)
    {
        if (!Nodes.Contains(barracks)) return;
        ref readonly var site = ref Nodes.Get(barracks);
        if (!site.IsBuilt || site.Kind != NodeKind.Barracks) return;
        QueueAssign(
            agents,
            Assignment.Train(
                barracks,
                site.Position,
                site.FootprintRadius,
                EconomySystem.HandoverSeconds,
                trainingShiftSeconds: 1f));
    }

    /// <summary>Removes units from the world and releases everything they own.</summary>
    public int DespawnAgents(IEnumerable<AgentId> ids)
    {
        var removed = 0;
        foreach (var id in ids.Distinct().OrderBy(id => id.Value))
        {
            if (!Agents.Contains(id)) continue;
            ref var agent = ref Agents.Get(id);
            // Whatever it was carrying falls where it stood. A resource is a physical thing and does
            // not stop existing because its carrier did — which is exactly what makes intercepting a
            // loaded raider worth doing, since killing it returns the grain rather than denying it.
            DropCargo(ref agent);
            CompletePath(ref agent);
            // A disabled proxy is invisible to Remove, which goes through Contains. Put the body back in
            // the world for the instant it takes to take it out properly, or its four proxies stay in the
            // store forever — dark and harmless, and still a leak.
            if (agent.Sheltered)
            {
                agent.Sheltered = false;
                SetBodyPresent(in agent, true);
            }

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

    /// <summary>
    /// Paints the inside of every stand of trees as ground nothing can walk through.
    /// </summary>
    /// <remarks>
    /// <b>Density, not trunks.</b> A cell is forest if <see cref="Woodland.CoverTrees"/> trees stand within
    /// <see cref="Woodland.CoverRadius"/> of it — so the middle of a stand is a contiguous impassable mass
    /// and its fringe is open. Blocking individual trunks was measured and is not available: the densest
    /// band scatters at 2.20 m, leaving 1.30 m between trunks, and the navigation raster quantises
    /// clearance to rungs of 0.25/0.75/1.25 — so a gap bodies physically fit through lands on the 0.25 rung
    /// and is refused to everybody. Ten thousand blocking trunks is ten thousand unroutable holes.
    /// <para>
    /// Scattered from the trees rather than gathered per cell: each tree increments the cells within its
    /// radius, and a second pass paints the ones that got enough. Ten thousand trees times eighty-five
    /// cells is under a million increments, where asking each of six hundred thousand cells which trees are
    /// near it is a spatial query per cell.
    /// </para>
    /// <para>
    /// <b>Only grass is claimed, and it is released back to grass.</b> Remembering what each cell used to be
    /// would be another one-byte-per-cell array to save and fingerprint, and the case it buys — a road under
    /// a forest — is a contradiction anyway. A road that was under trees comes back as grass, which is
    /// unlikely and harmless.
    /// </para>
    /// <para>
    /// The navigation raster is <em>not</em> rebuilt here. <see cref="Tick"/> already notices a changed
    /// terrain revision and rebuilds once, so painting a hundred thousand cells costs one rebuild rather
    /// than a hundred thousand.
    /// </para>
    /// </remarks>
    public void RefreshForestCover()
    {
        var transform = Terrain.Transform;
        var counts = new byte[transform.Width * transform.Height];
        var reach = (int)MathF.Ceiling(Woodland.CoverRadius / transform.CellSize);
        var radiusSquared = Woodland.CoverRadius * Woodland.CoverRadius;

        foreach (ref readonly var tree in Nodes.All)
        {
            if (!tree.IsAlive || !tree.IsStanding) continue;
            if (!transform.TryWorldToCell(tree.Position, out var at)) continue;
            for (var dz = -reach; dz <= reach; dz++)
            for (var dx = -reach; dx <= reach; dx++)
            {
                var cell = new GridCell(at.X + dx, at.Z + dz);
                if (!transform.Contains(cell)) continue;
                if (Vector2.DistanceSquared(transform.CellCenter(cell), tree.Position) > radiusSquared)
                {
                    continue;
                }

                var index = cell.Z * transform.Width + cell.X;
                if (counts[index] < 255) counts[index]++;
            }
        }

        for (var z = 0; z < transform.Height; z++)
        for (var x = 0; x < transform.Width; x++)
        {
            var cell = new GridCell(x, z);
            var covered = counts[z * transform.Width + x] >= Woodland.CoverTrees;
            var surface = Terrain.Surface(cell);
            if (covered && surface == TerrainSurface.Grass)
            {
                Terrain.SetSurface(cell, TerrainSurface.Forest);
            }
            else if (!covered && surface == TerrainSurface.Forest)
            {
                Terrain.SetSurface(cell, TerrainSurface.Grass);
            }
        }
    }

    /// <summary>
    /// Re-decides the cover around a felled tree, which is how the passable edge moves.
    /// </summary>
    /// <remarks>
    /// Only cells within a cover radius of where the tree stood can have changed, so only those are
    /// re-counted — a full refresh is a million increments and this is a few hundred. The trees that matter
    /// are those within two radii of the same point, gathered by one scan.
    /// <para>
    /// This is the mechanic made mechanical: fell a tree on the fringe and the ground behind it opens, which
    /// puts the next row of trees in reach. A settlement cuts its way outward, and the wood line receding is
    /// literally the passable edge moving.
    /// </para>
    /// </remarks>
    /// <summary>Where trees have lately come down, for whatever wants to draw a stump there.</summary>
    /// <remarks>
    /// <b>Cosmetic, bounded and not saved, and each of those is a decision.</b> A felled tree's node is
    /// removed outright — nothing in the simulation has any further use for it — so a stump cannot be a
    /// node without keeping thousands of dead ones alive forever in the fingerprint, the save file and
    /// every iteration over the economy. It is a ring of the last few hundred sites instead, which is
    /// about a decade of a settlement's cutting, and when it wraps the oldest clearing loses its stumps.
    /// Nothing reads it but the renderer, which is why it is argued away in the census rather than
    /// fingerprinted, and why a loaded save shows no stumps until somebody fells something.
    /// </remarks>
    internal ReadOnlySpan<Vector2> RecentFellings =>
        new(fellings, 0, Math.Min(fellingCount, fellings.Length));

    private readonly Vector2[] fellings = new Vector2[384];
    private int fellingCount;

    public void ReleaseForestCover(Vector2 where)
    {
        fellings[fellingCount % fellings.Length] = where;
        fellingCount++;
        var transform = Terrain.Transform;
        var radius = Woodland.CoverRadius;
        var radiusSquared = radius * radius;
        var gatherSquared = (radius * 2f) * (radius * 2f);

        forestNeighbours.Clear();
        foreach (ref readonly var tree in Nodes.All)
        {
            if (!tree.IsAlive || !tree.IsStanding) continue;
            if (Vector2.DistanceSquared(tree.Position, where) <= gatherSquared)
            {
                forestNeighbours.Add(tree.Position);
            }
        }

        if (!transform.TryWorldToCell(where, out var origin)) return;
        var reach = (int)MathF.Ceiling(radius / transform.CellSize);
        for (var dz = -reach; dz <= reach; dz++)
        for (var dx = -reach; dx <= reach; dx++)
        {
            var cell = new GridCell(origin.X + dx, origin.Z + dz);
            if (!transform.Contains(cell)) continue;
            var centre = transform.CellCenter(cell);
            var near = 0;
            foreach (var trunk in forestNeighbours)
            {
                if (Vector2.DistanceSquared(trunk, centre) <= radiusSquared) near++;
            }

            var covered = near >= Woodland.CoverTrees;
            var surface = Terrain.Surface(cell);
            if (covered && surface == TerrainSurface.Grass)
            {
                Terrain.SetSurface(cell, TerrainSurface.Forest);
            }
            else if (!covered && surface == TerrainSurface.Forest)
            {
                Terrain.SetSurface(cell, TerrainSurface.Grass);
            }
        }
    }

    private readonly List<Vector2> forestNeighbours = new();

    /// <summary>Harm, and shortly the decision to stand or run. See <c>ThreatSystem</c> and §28.</summary>
    private readonly ThreatSystem threat = new();

    /// <summary>Bodies killed by something hostile, and damage dealt, for the report.</summary>
    public ThreatSystem Threat => threat;

    /// <summary>
    /// Whether anybody could get to a tree, which since Stage E's forest cover is a real question.
    /// </summary>
    /// <remarks>
    /// A tree buried in the middle of a stand stands on impassable ground with impassable ground all round
    /// it, so nobody can reach it and nobody should be sent. The test is the cheap one — is any of the eight
    /// neighbouring cells walkable — rather than a routing query, because it is asked of every tree in the
    /// world each time a cutter looks for its next one and there are ten thousand of them. Cheap and
    /// slightly optimistic: a tree beside a pocket nothing can get into passes, and the jobs layer's polite
    /// retry handles that the way it handles a workplace somebody walled in.
    /// </remarks>
    internal bool CanReachTree(Vector2 position)
    {
        if (!Navigation.TryWorldToCell(position, out var at)) return false;
        // Two cells out, because a body stands off a trunk by its own radius and a bit rather than in the
        // cell next to it — and asked of the <em>raster</em> rather than of the terrain surface, because
        // "passable ground" and "ground a body of this width may occupy" are different questions and it is
        // the second that decides whether anybody can get here. A tree with nothing but a diagonal sliver
        // beside it passed the first test and failed the second, and the cutter posted on it asked for a
        // route two hundred and thirty-six times and never cut anything.
        // Three cells is a metre and a half, which covers the stand-off a body actually takes from a
        // trunk — its own radius plus the touch slack put the failing case 1.61 m out, past a two-cell
        // test that had said yes.
        const int reach = 3;
        for (var dz = -reach; dz <= reach; dz++)
        for (var dx = -reach; dx <= reach; dx++)
        {
            if (dx == 0 && dz == 0) continue;
            var cell = new GridCell(at.X + dx, at.Z + dz);
            if (Navigation.Contains(cell) && Navigation.IsWalkable(cell, AgentDefaults.RoutingRadius))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a body can see a point: within its sight, and not through a wood.
    /// </summary>
    /// <remarks>
    /// <b>Trees block sight and nothing else does</b>, which is deliberate rather than unfinished. A wood is
    /// the one thing on this map tall enough and deep enough to hide an approach, and it is the half of
    /// "forest cover" that blocking movement did not give — the gaps a settlement has cut through its own
    /// woodland are now both the ways in and the only ways it can watch. Buildings are sparse and a
    /// settlement ought to be able to see across itself; water hides nothing.
    /// <para>
    /// Sampled along the ray at raster resolution, and asked <em>per hostile</em> rather than per pair: for
    /// one raider, which of mine can see it. A handful of raiders against thirty bodies is a few hundred
    /// samples; per-pair across ten thousand nodes would not be affordable and is not the question anybody
    /// asks.
    /// </para>
    /// </remarks>
    public bool CanSee(in AgentState observer, Vector2 target) =>
        CanSee(observer.Position, observer.SightMetres, target);

    /// <summary>
    /// Whether anything standing at a place with a given reach can see a point.
    /// </summary>
    /// <remarks>
    /// <b>The same predicate, without requiring a body to ask it.</b> A building is not an agent and has no
    /// <see cref="AgentState.SightMetres"/>, and the fog of war wants to ask this of a granary and of a
    /// villager in one loop. Split out rather than copied because the copy is the whole risk: the moment two
    /// pieces of code decide independently what "can see" means, the fog shows ground the simulation calls
    /// hidden — and that disagreement is invisible until a player is standing in it.
    /// <para>
    /// Adds no state and reads nothing that is not already read, so it is outside the determinism ledger's
    /// concern by construction: a pure function of the terrain and two arguments.
    /// </para>
    /// </remarks>
    public bool CanSee(Vector2 from, float sightMetres, Vector2 target)
    {
        var toTarget = target - from;
        var distance = toTarget.Length();
        if (distance > sightMetres) return false;
        if (distance <= NavigationCellSize) return true;

        var step = toTarget / distance * NavigationCellSize;
        var at = from;
        var samples = (int)(distance / NavigationCellSize);
        for (var i = 1; i < samples; i++)
        {
            at += step;
            if (!Terrain.Transform.TryWorldToCell(at, out var cell)) continue;
            if (Terrain.Surface(cell) == TerrainSurface.Forest) return false;
        }

        return true;
    }

    public void RebuildTerrainNavigation()
    {
        NavigationRasterizer.Rebuild(Placement, Navigation, Terrain);
        Placement.ConsumeDirtyBounds(out _, out _);
        rasterizedTerrainRevision = Terrain.Revision;
    }

    /// <summary>
    /// Re-rasterises what a placement change touched, or everything if the terrain moved too.
    /// </summary>
    /// <remarks>
    /// <b>The whole reason a finished building used to cost three quarters of a second.</b> Placing or clearing
    /// a cell bumps the placement grid, which re-rasterises navigation, which used to mean sampling terrain
    /// for 1.44M cells that had not changed and recomputing clearance for cells nowhere near the change. The
    /// placement grid now says WHERE it moved, so the pass can be local — and a local pass produces the same
    /// grid as a full one, which is asserted cell by cell in the self-tests rather than assumed here.
    /// <para>
    /// Falls back to the full rebuild whenever the terrain revision has moved as well, because then the
    /// terrain-derived half of every cell really is stale.
    /// </para>
    /// </remarks>
    /// <summary>The local refresh, reachable from the self-tests that hold it to the full rebuild's answer.</summary>
    internal void RefreshNavigationForTest() => RefreshNavigationAfterPlacement();

    /// <summary>The terrain revision the raster was last built against, for the identity test's diagnosis.</summary>
    internal int RasterisedTerrainRevisionForTest => rasterizedTerrainRevision;

    private void RefreshNavigationAfterPlacement()
    {
        if (Terrain.Revision != rasterizedTerrainRevision)
        {
            RebuildTerrainNavigation();
            return;
        }

        if (!Placement.ConsumeDirtyBounds(out var minimum, out var maximum)) return;
        NavigationRasterizer.RebuildWithin(Placement, Navigation, Terrain, minimum, maximum);
    }

    /// <summary>
    /// Everything this world carries, in the order <see cref="Read"/> expects it.
    /// </summary>
    /// <remarks>
    /// This method and the ledger in <c>DeterminismCheck</c> describe the same state for two
    /// different purposes, and neither can be derived from the other: the ledger says what has to be
    /// <em>compared</em> to notice a divergence, this says what has to be <em>kept</em> to avoid one.
    /// The second is the larger set. What keeps them from drifting apart is not discipline but the
    /// round-trip self-test, which saves, loads, and then ticks both worlds forward — state left out
    /// of here shows up there as a divergence a few ticks later, named down to the field.
    /// </remarks>
    /// <summary>
    /// Forgets everything memoised about routes, leaving this world in the state a freshly loaded
    /// one is in.
    /// </summary>
    /// <remarks>
    /// For comparing a world against a saved copy of itself, which cannot be done fairly otherwise:
    /// see <c>PathService.DropRouteCaches</c> for what a cost field remembers that no save can carry.
    /// </remarks>
    internal void DropRouteCaches() => pathService.DropRouteCaches();

    internal void Write(WorldWriter writer)
    {
        writer.Long(TickNumber);
        writer.Long(EpochTicks);
        writer.Int(LastContactCount);
        writer.Int(CongestionRepathCount);
        writer.Int(ImmediateRouteRepairCount);
        writer.Int(CongestionRerouteCount);
        writer.Int(CrowdedArrivalBlockCount);
        writer.Int(LastCongestionRoot.Value);
        writer.Int(LastCongestionRepathAgent.Value);
        writer.Float(congestionRecoveryCooldown);
        writer.Int(rasterizedTerrainRevision);
        writer.Int(routePlansThisTick);
        writer.Int(nextMoveGroupId);
        writer.Int(Navigation.Revision);
        // The record of work done, which is not derived: a loaded career reports the same lifetime
        // totals as the one it continues, and the determinism check reads them as a canary.
        pathService.WriteCounters(writer);
        steeringSystem.Solver.WriteCounters(writer);
        agentIndex.WriteCounters(writer);

        Terrain.Write(writer);
        Placement.Write(writer);
        Agents.Write(writer);
        Nodes.Write(writer);
        economy.Write(writer);
        threat.Write(writer);
        Colliders.Write(writer);
        Congestion.Write(writer);
        paths.Write(writer);

        writer.Int(moveGroups.Count);
        foreach (var id in moveGroups.Keys.OrderBy(id => id)) moveGroups[id].Write(writer);

        writer.Int(blockColliders.Count);
        foreach (var (cell, collider) in blockColliders
                     .OrderBy(entry => Placement.Transform.Index(entry.Key)))
        {
            writer.Int(cell.X);
            writer.Int(cell.Z);
            writer.Int(collider.Value);
        }

        writer.Int(commands.Count);
        foreach (var command in commands) WorldSave.WriteCommand(writer, command);
    }

    internal void Read(WorldReader reader)
    {
        TickNumber = reader.Long();
        EpochTicks = reader.Long();
        LastContactCount = reader.Int();
        CongestionRepathCount = reader.Int();
        ImmediateRouteRepairCount = reader.Int();
        CongestionRerouteCount = reader.Int();
        CrowdedArrivalBlockCount = reader.Int();
        LastCongestionRoot = new AgentId(reader.Int());
        LastCongestionRepathAgent = new AgentId(reader.Int());
        congestionRecoveryCooldown = reader.Float();
        rasterizedTerrainRevision = reader.Int();
        routePlansThisTick = reader.Int();
        nextMoveGroupId = reader.Int();
        var navigationRevision = reader.Int();
        pathService.ReadCounters(reader);
        steeringSystem.Solver.ReadCounters(reader);
        agentIndex.ReadCounters(reader);

        Terrain.Read(reader);
        Placement.Read(reader);
        Agents.Read(reader);
        Nodes.Read(reader);
        economy.Read(reader);
        threat.Read(reader);
        Colliders.Read(reader);
        Congestion.Read(reader);
        paths.Read(reader);

        moveGroups.Clear();
        var groups = reader.Int();
        for (var i = 0; i < groups; i++)
        {
            var group = MoveGroup.Read(reader);
            moveGroups[group.Id] = group;
        }

        blockColliders.Clear();
        var blocks = reader.Int();
        for (var i = 0; i < blocks; i++)
        {
            var cell = new GridCell(reader.Int(), reader.Int());
            blockColliders[cell] = new ColliderId(reader.Int());
        }

        commands.Clear();
        var orders = reader.Int();
        for (var i = 0; i < orders; i++) commands.Enqueue(WorldSave.ReadCommand(reader));

        // The raster is derived, so it is rebuilt rather than stored — and then told what revision it
        // is, because the rebuild bumps it and every flow field ever cached is keyed by the number.
        // Rebuilding also has to happen after the terrain and the placement grid are back, which is
        // why it is here and not in the constructor's sequence.
        NavigationRasterizer.Rebuild(Placement, Navigation, Terrain);
        Navigation.RestoreRevision(navigationRevision);
        // The broad-phase index needs nothing: it is rebuilt from body positions inside the first
        // steering pass, which is the same thing it does on every other tick.
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
        // Before jobs, so a hauler handed a job this tick starts walking on it this tick, and so
        // production reflects who was standing where at the end of the last one.
        economy.Update(
            Nodes, Agents, Date, deltaSeconds, TryTravelSeconds, BornAt, Emigrate, ReleaseForestCover);
        Timings.Record(SimulationPhase.Economy, Stopwatch.GetTimestamp() - phaseStart);

        phaseStart = Stopwatch.GetTimestamp();
        // After the economy and before the jobs layer: harm is a fact about where bodies already are, and
        // the jobs layer is what reacts to it. A body killed this tick should not then be given work.
        var fallen = threat.Update(Agents, Colliders.Factions, deltaSeconds);
        if (fallen.Count > 0) DespawnAgents(fallen.ToArray());
        // And then the decision, which nobody has to give: see what you want to protect, weigh whether the
        // people who can see the same thing could hold it, and either stand or run toward help. It marches
        // through QueueMove — the same door a player's order goes through — so the assignment is suspended
        // and never rewritten, and a villager who fights goes back to its field afterwards.
        threat.Defend(
            Agents,
            Nodes,
            Colliders.Factions,
            deltaSeconds,
            CanSee,
            MarchAgainstThreat,
            StandDown,
            StowBeforeFighting,
            ChargeThreat);
        // The errand outlives the reason for it, so it is advanced whatever the defence decided this tick.
        AdvanceStowing();
        Timings.Record(SimulationPhase.Threat, Stopwatch.GetTimestamp() - phaseStart);

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
        if (placementChanged) RefreshNavigationAfterPlacement();
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
        var resolved = ResolveNodeExtents(assign.Assignment);
        foreach (var id in assign.Agents)
        {
            if (!Agents.Contains(id)) continue;
            ref var agent = ref Agents.Get(id);
            var job = PostedOnAWorkSite(in agent, resolved);
            if (job.Kind is AssignmentKind.Work or AssignmentKind.Build or AssignmentKind.Train &&
                agent.Role != AgentRole.Villager)
            {
                continue;
            }
            // Asked to do something that is not carrying: the cart goes. Which is the whole of "they keep
            // hauling until they are asked to do something else" — an interrupt is not being asked to do
            // something else, because an interrupt never touches the assignment, so a carter given a
            // direct order walks where it is told and comes back to its route with its cart.
            if (!job.MovesCargo) ScrapCart(ref agent);
            JobSystem.Assign(ref agent, job);
            if (job.Kind == AssignmentKind.Build)
            {
                // Begin with the useful trip, not a ceremonial visit to the site. A carried recipe load
                // still lands first and material already waiting can be worked immediately; otherwise the
                // same planner used between shifts reserves a load and sends this builder to its source.
                ChooseBuilderNextStep(ref agent, agent.Jobs.Project);
            }
            else if (job.Kind == AssignmentKind.Train)
            {
                ChooseTraineeNextStep(ref agent, agent.Jobs.Project);
            }
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
            switch (request.Step)
            {
                case JobStep.WalkTo:
                    if (TryApproachPoint(in agents[i], out var approach))
                    {
                        BeginSoloMove(ref agents[i], approach);
                    }

                    break;
                case JobStep.LegFinished:
                    Handover(ref agents[i], request.Leg);
                    break;
            }
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
            if (BeginFlowTransit(ref agent, target))
            {
                pathService.OrdersOnTransit++;
            }
            else
            {
                agent.ApproachingSlot = true;
                agent.RequestedDestination = agent.GroupSlot;
                // <b>Counted, because a body that is refused here stands still and says nothing.</b> That is
                // the reported symptom — an order given across the map and nobody moves — and it is invisible
                // from inside the tick: the order was accepted, the group was formed, the slot was assigned,
                // and the route came back empty.
                if (AssignPath(ref agent, agent.GroupSlot))
                {
                    pathService.OrdersOnSlotPath++;
                }
                else
                {
                    pathService.OrdersRefused++;
                }
            }
        }
    }

    /// <summary>
    /// Moves cargo on or off a hauler that has just finished a leg.
    /// </summary>
    /// <remarks>
    /// The one place units change hands, so it is the one place conservation could be broken. Loading
    /// takes from the source's stock and adds to the body; unloading does the reverse; neither invents
    /// or discards a unit, and what will not fit stays where it was. A body carrying cargo it cannot
    /// deliver keeps it and the board leaves it alone until it has.
    /// </remarks>
    private void Handover(ref AgentState agent, int leg)
    {
        if (agent.Jobs.Assignment.Kind == AssignmentKind.Build)
        {
            BuilderHandover(ref agent, leg);
            return;
        }

        if (agent.Jobs.Assignment.Kind == AssignmentKind.Train)
        {
            TraineeHandover(ref agent, leg);
            return;
        }

        if (agent.Jobs.Assignment.Kind == AssignmentKind.Work)
        {
            WorkHandover(ref agent, leg);
            return;
        }

        ref var jobs = ref agent.Jobs;
        // Both kinds of cargo run: the board's one-trip haul and the player's standing route. The
        // transfer is identical — what differs is who decided on it and whether it repeats — so there is
        // one place units change hands and therefore one place conservation could be broken.
        if (!jobs.Assignment.MovesCargo) return;
        var nodeId = jobs.Assignment.NodeOfLeg(leg);
        if (!Nodes.Contains(nodeId)) return;
        ref var node = ref Nodes.Get(nodeId);
        var cargo = jobs.Assignment.Cargo;

        if (nodeId == jobs.Assignment.Source)
        {
            if (jobs.CarriedUnits > 0) return;
            if (jobs.Assignment.Kind == AssignmentKind.Carry)
            {
                if (!TryChooseRouteCargo(
                        jobs.Assignment.Source,
                        jobs.Assignment.Sink,
                        jobs.Assignment.Cargo,
                        out cargo))
                {
                    // No useful demand just now. This is a standing route, so stay at the source and ask
                    // again after another handover interval rather than driving an empty leg or silently
                    // deleting the player's arrangement.
                    JobSystem.Retarget(ref agent, jobs.Assignment, leg: 0);
                    return;
                }

                if (cargo != jobs.Assignment.Cargo)
                {
                    jobs.Assignment = jobs.Assignment with { Cargo = cargo };
                }
            }

            var taken = Math.Min(agent.CarryCapacity, node.Stock[cargo]);
            if (taken <= 0)
            {
                if (jobs.Assignment.Kind == AssignmentKind.Carry)
                {
                    // The route may be authored before the forward store's next load arrives. Wait here,
                    // preserving both endpoints and the currently preferred cargo.
                    JobSystem.Retarget(ref agent, jobs.Assignment, leg: 0);
                }

                return;
            }
            node.Stock.Add(cargo, -taken);
            jobs.Carrying = cargo;
            jobs.CarriedUnits = taken;
            return;
        }

        if (jobs.CarriedUnits <= 0) return;
        var delivered = Math.Min(jobs.CarriedUnits, node.RoomFor(jobs.Carrying));
        node.Stock.Add(jobs.Carrying, delivered);
        jobs.CarriedUnits -= delivered;
        if (jobs.CarriedUnits <= 0) return;

        // The store filled while this load was on its way. The job is not over — there are units on the
        // cart — so it is pointed at whatever else has room, or left pointed here to try again. Either
        // way the cargo stays on the cart, which is the only answer that keeps the books.
        var elsewhere = EconomySystem.EmptiestStoreWithRoom(Nodes, jobs.Carrying, node.Faction, nodeId);
        var target = Nodes.Contains(elsewhere) ? elsewhere : nodeId;
        JobSystem.Retarget(
            ref agent,
            jobs.Assignment with
            {
                Sink = target,
                FarAnchor = Nodes.Get(target).Position,
                FarPlaceExtent = Nodes.Get(target).FootprintRadius,
            },
            leg: 1);
    }

    /// <summary>Closes one builder's collect, deliver, build and cleanup loop.</summary>
    /// <remarks>
    /// The project is stable in <see cref="AgentJobs.Project"/> while the assignment endpoints describe
    /// this trip. Leg zero collects one resource from one physical source; leg one is normally the site,
    /// and becomes a store only while surplus is being returned. All choices are made here, in agent-id
    /// order through <see cref="UpdateJobs"/>, so reservations made by an earlier builder are visible to
    /// the next one on the same tick.
    /// </remarks>
    private void BuilderHandover(ref AgentState agent, int leg)
    {
        ref var jobs = ref agent.Jobs;
        var projectId = jobs.Project;
        if (!Nodes.Contains(projectId))
        {
            ReturnBuilderCargoOrClear(ref agent);
            return;
        }

        ref readonly var project = ref Nodes.Get(projectId);
        if (!project.HasStructuralProject)
        {
            CleanupBuilder(ref agent, leg);
            return;
        }

        // Surplus from an active project is a temporary outward trip. Put it down, then resume the same
        // stable project rather than reading this store visit as another construction shift.
        if (leg % 2 != 0 && jobs.Assignment.Sink != projectId)
        {
            if (Nodes.Contains(jobs.Assignment.Sink) && jobs.CarriedUnits > 0)
            {
                ref var store = ref Nodes.Get(jobs.Assignment.Sink);
                var delivered = Math.Min(jobs.CarriedUnits, store.RoomFor(jobs.Carrying));
                store.Stock.Add(jobs.Carrying, delivered);
                jobs.CarriedUnits -= delivered;
            }

            if (jobs.CarriedUnits > 0) SendBuilderCargoToStore(ref agent, projectId);
            else ChooseBuilderNextStep(ref agent, projectId);
            return;
        }

        if (leg % 2 == 0)
        {
            // The claimed source has been reached. Claims prevent deliberate over-fetching, but stock may
            // still have moved since the claim was made, so the physical source remains the final authority.
            if (jobs.CarriedUnits <= 0 && jobs.ReservedUnits > 0 && Nodes.Contains(jobs.Assignment.Source))
            {
                ref var source = ref Nodes.Get(jobs.Assignment.Source);
                var taken = Math.Min(
                    jobs.ReservedUnits,
                    Math.Min(agent.CarryCapacity, source.Stock[jobs.Assignment.Cargo]));
                if (taken > 0)
                {
                    source.Stock.Add(jobs.Assignment.Cargo, -taken);
                    jobs.Carrying = jobs.Assignment.Cargo;
                    jobs.CarriedUnits = taken;
                }
            }

            jobs.ReservedUnits = 0;
            if (jobs.CarriedUnits > 0)
            {
                RetargetBuilderToProject(ref agent, projectId);
                return;
            }

            ChooseBuilderNextStep(ref agent, projectId);
            return;
        }

        // Builder loads are deposited on arrival by EconomySystem, before their labour is counted. Anything
        // left in hand is therefore surplus and has to remain physical on the way back to storage.
        if (jobs.CarriedUnits > 0)
        {
            SendBuilderCargoToStore(ref agent, projectId);
            return;
        }

        ChooseBuilderNextStep(ref agent, projectId);
    }

    /// <summary>Delivers held material, works an available front, claims a load, or waits at the project.</summary>
    private void ChooseBuilderNextStep(ref AgentState agent, NodeId projectId)
    {
        if (!Nodes.Contains(projectId))
        {
            ReturnBuilderCargoOrClear(ref agent);
            return;
        }

        ref readonly var project = ref Nodes.Get(projectId);
        if (!project.HasStructuralProject)
        {
            CleanupBuilder(ref agent, leg: 1);
            return;
        }

        // Assignment replacement preserves physical cargo. A useful load therefore goes directly to the
        // project; an unrelated one goes back to the nearest store before this decision is made again.
        // Doing this here, rather than only in ApplyAssignment, keeps the decision seam honest if a future
        // interruption or cancellation re-enters it with something still in hand.
        if (agent.Jobs.CarriedUnits > 0)
        {
            if (project.Wanted(agent.Jobs.Carrying) > 0)
            {
                RetargetBuilderToProject(ref agent, projectId);
            }
            else
            {
                SendBuilderCargoToStore(ref agent, projectId);
            }

            return;
        }

        if (ProjectHasBuildFront(in project))
        {
            RetargetBuilderToProject(ref agent, projectId);
            return;
        }

        if (TryClaimBuilderLoad(ref agent, projectId)) return;

        // There is demand but no reachable held stock, or every missing unit is already inbound with another
        // body. Stay committed at the site and reconsider after one shift rather than becoming idle or
        // walking empty laps.
        RetargetBuilderToProject(ref agent, projectId);
    }

    /// <summary>Whether delivered material permits at least another fraction of a labour-second.</summary>
    private static bool ProjectHasBuildFront(in EconomyNode project)
    {
        var labour = StructuralProjects.LabourFor(in project);
        var limit = labour;
        var cost = StructuralProjects.CostFor(in project);
        foreach (var resource in Resources.All)
        {
            var required = cost[resource];
            if (required <= 0) continue;
            var available = StructuralProjects.ConsumedFor(in project, resource) + project.Stock[resource];
            limit = MathF.Min(limit, labour * available / required);
        }

        return limit > StructuralProjects.WorkFor(in project) + 0.0001f;
    }

    /// <summary>Claims one load, preferring the least-covered material and then the nearest source.</summary>
    private bool TryClaimBuilderLoad(ref AgentState agent, NodeId projectId)
    {
        ref readonly var project = ref Nodes.Get(projectId);
        var cost = StructuralProjects.CostFor(in project);
        var bestSource = NodeId.None;
        var bestCargo = Resource.Grain;
        var bestUnits = 0;
        var bestCoverage = float.PositiveInfinity;
        var bestSeconds = float.PositiveInfinity;

        foreach (var resource in Resources.All)
        {
            var required = cost[resource];
            if (required <= 0) continue;
            var incoming = IncomingToProject(projectId, resource, agent.Id);
            var remaining = project.Wanted(resource) - incoming;
            if (remaining <= 0) continue;
            var coverage = (StructuralProjects.ConsumedFor(in project, resource) +
                            project.Stock[resource] + incoming) /
                           (float)required;

            foreach (ref readonly var source in Nodes.All)
            {
                if (!source.IsAlive || source.Id == projectId || source.Faction != agent.Faction) continue;
                if (!source.Stores && !source.IsPile) continue;
                var available = source.Stock[resource] - ReservedAtSource(source.Id, resource, agent.Id);
                if (available <= 0) continue;
                if (!TryTravelSeconds(
                        agent.Position,
                        source.Position,
                        agent.NavigationRadius,
                        out var seconds))
                {
                    continue;
                }

                var units = Math.Min(agent.CarryCapacity, Math.Min(remaining, available));
                if (units <= 0) continue;
                var better = coverage < bestCoverage - 0.0001f ||
                             MathF.Abs(coverage - bestCoverage) <= 0.0001f && seconds < bestSeconds - 0.0001f ||
                             MathF.Abs(coverage - bestCoverage) <= 0.0001f &&
                             MathF.Abs(seconds - bestSeconds) <= 0.0001f &&
                             ((int)resource < (int)bestCargo ||
                              resource == bestCargo && source.Id.Value < bestSource.Value);
                if (!better) continue;
                bestSource = source.Id;
                bestCargo = resource;
                bestUnits = units;
                bestCoverage = coverage;
                bestSeconds = seconds;
            }
        }

        if (!bestSource.IsValid) return false;
        ref readonly var from = ref Nodes.Get(bestSource);
        ref readonly var site = ref Nodes.Get(projectId);
        agent.Jobs.ReservedUnits = bestUnits;
        var assignment = agent.Jobs.Assignment with
        {
            Source = bestSource,
            Anchor = from.Position,
            PlaceExtent = from.FootprintRadius,
            Sink = projectId,
            FarAnchor = site.Position,
            FarPlaceExtent = site.FootprintRadius,
            Cargo = bestCargo,
        };
        JobSystem.Retarget(ref agent, assignment, leg: 0);
        return true;
    }

    /// <summary>Material already on its way to one project, in bodies or in their explicit claims.</summary>
    private int IncomingToProject(NodeId project, Resource resource, AgentId except)
    {
        var incoming = 0;
        foreach (ref readonly var body in Agents.All)
        {
            if (!body.IsAlive || body.Id == except) continue;
            if (body.Jobs.Assignment.Kind == AssignmentKind.Build && body.Jobs.Project == project)
            {
                if (body.Jobs.CarriedUnits > 0 && body.Jobs.Carrying == resource)
                {
                    incoming += body.Jobs.CarriedUnits;
                }
                else if (body.Jobs.Assignment.Cargo == resource)
                {
                    incoming += body.Jobs.ReservedUnits;
                }

                continue;
            }

            if (!body.Jobs.Assignment.MovesCargo || body.Jobs.Assignment.Sink != project ||
                body.Jobs.Assignment.Cargo != resource)
            {
                continue;
            }

            incoming += body.Jobs.CarriedUnits > 0
                ? body.Jobs.CarriedUnits
                : Math.Min(
                    body.CarryCapacity,
                    Nodes.Contains(body.Jobs.Assignment.Source)
                        ? Nodes.Get(body.Jobs.Assignment.Source).Stock[resource]
                        : 0);
        }

        return incoming;
    }

    /// <summary>Stock at a source already promised to another builder or cargo run.</summary>
    private int ReservedAtSource(NodeId source, Resource resource, AgentId except)
    {
        var reserved = 0;
        foreach (ref readonly var body in Agents.All)
        {
            if (!body.IsAlive || body.Id == except || body.Jobs.CarriedUnits > 0) continue;
            if (body.Jobs.Assignment.Source != source || body.Jobs.Assignment.Cargo != resource) continue;
            if (body.Jobs.Assignment.Kind is AssignmentKind.Build or AssignmentKind.Train)
            {
                reserved += body.Jobs.ReservedUnits;
            }
            else if (body.Jobs.Assignment.MovesCargo)
            {
                reserved += body.CarryCapacity;
            }
        }

        return reserved;
    }

    private void RetargetBuilderToProject(ref AgentState agent, NodeId projectId)
    {
        ref readonly var site = ref Nodes.Get(projectId);
        var assignment = agent.Jobs.Assignment with
        {
            Sink = projectId,
            FarAnchor = site.Position,
            FarPlaceExtent = site.FootprintRadius,
        };
        JobSystem.Retarget(ref agent, assignment, leg: 1);
    }

    /// <summary>Returns a builder's surplus to the nearest generic store with room.</summary>
    private void SendBuilderCargoToStore(ref AgentState agent, NodeId projectId)
    {
        ref var jobs = ref agent.Jobs;
        if (jobs.CarriedUnits <= 0)
        {
            ChooseBuilderNextStep(ref agent, projectId);
            return;
        }

        var store = EconomySystem.NearestStoreWithRoom(
            Nodes, jobs.Carrying, agent.Faction, agent.Position);
        if (!Nodes.Contains(store))
        {
            RetargetBuilderToProject(ref agent, projectId);
            return;
        }

        ref readonly var target = ref Nodes.Get(store);
        var assignment = jobs.Assignment with
        {
            Source = projectId,
            Anchor = Nodes.Get(projectId).Position,
            PlaceExtent = Nodes.Get(projectId).FootprintRadius,
            Sink = store,
            FarAnchor = target.Position,
            FarPlaceExtent = target.FootprintRadius,
        };
        JobSystem.Retarget(ref agent, assignment, leg: 1);
    }

    /// <summary>Clears surplus after completion, leaving stock in a newly completed store where it lies.</summary>
    private void CleanupBuilder(ref AgentState agent, int leg)
    {
        ref var jobs = ref agent.Jobs;
        var projectId = jobs.Project;

        if (leg % 2 == 0 && jobs.Assignment.Source == projectId && jobs.ReservedUnits > 0 &&
            Nodes.Contains(projectId))
        {
            ref var source = ref Nodes.Get(projectId);
            var taken = Math.Min(
                jobs.ReservedUnits,
                Math.Min(agent.CarryCapacity, source.Stock[jobs.Assignment.Cargo]));
            jobs.ReservedUnits = 0;
            if (taken > 0)
            {
                source.Stock.Add(jobs.Assignment.Cargo, -taken);
                jobs.Carrying = jobs.Assignment.Cargo;
                jobs.CarriedUnits = taken;
                JobSystem.Retarget(ref agent, jobs.Assignment, leg: 1);
                return;
            }
        }

        // A cleanup delivery has reached its store.
        if (leg % 2 != 0 && jobs.Assignment.Sink != projectId && Nodes.Contains(jobs.Assignment.Sink) &&
            jobs.CarriedUnits > 0)
        {
            ref var store = ref Nodes.Get(jobs.Assignment.Sink);
            var delivered = Math.Min(jobs.CarriedUnits, store.RoomFor(jobs.Carrying));
            store.Stock.Add(jobs.Carrying, delivered);
            jobs.CarriedUnits -= delivered;
        }

        if (jobs.CarriedUnits > 0 && Nodes.Contains(projectId) && Nodes.Get(projectId).Stores)
        {
            ref var completedStore = ref Nodes.Get(projectId);
            var delivered = Math.Min(jobs.CarriedUnits, completedStore.RoomFor(jobs.Carrying));
            completedStore.Stock.Add(jobs.Carrying, delivered);
            jobs.CarriedUnits -= delivered;
        }

        if (jobs.CarriedUnits > 0)
        {
            SendBuilderCargoToStore(ref agent, projectId);
            return;
        }

        if (!Nodes.Contains(projectId) || Nodes.Get(projectId).Stores)
        {
            JobSystem.Assign(ref agent, Assignment.None);
            return;
        }

        // Exact reservations normally leave no surplus. This path is for a project completed by another
        // worker while a final load was inbound, and for future cancellation: physical stock is cleared one
        // resource per trip, never refunded or deleted.
        ref readonly var project = ref Nodes.Get(projectId);
        foreach (var resource in Resources.All)
        {
            var available = project.Stock[resource] - ReservedAtSource(projectId, resource, agent.Id);
            if (available <= 0) continue;
            var store = EconomySystem.NearestStoreWithRoom(
                Nodes, resource, agent.Faction, project.Position);
            if (!Nodes.Contains(store)) break;
            ref readonly var target = ref Nodes.Get(store);
            jobs.ReservedUnits = Math.Min(agent.CarryCapacity, available);
            var assignment = jobs.Assignment with
            {
                Source = projectId,
                Anchor = project.Position,
                PlaceExtent = project.FootprintRadius,
                Sink = store,
                FarAnchor = target.Position,
                FarPlaceExtent = target.FootprintRadius,
                Cargo = resource,
            };
            JobSystem.Retarget(ref agent, assignment, leg: 0);
            return;
        }

        JobSystem.Assign(ref agent, Assignment.None);
    }

    private void ReturnBuilderCargoOrClear(ref AgentState agent)
    {
        if (agent.Jobs.CarriedUnits <= 0)
        {
            JobSystem.Assign(ref agent, Assignment.None);
            return;
        }

        var store = EconomySystem.NearestStoreWithRoom(
            Nodes, agent.Jobs.Carrying, agent.Faction, agent.Position);
        if (!Nodes.Contains(store)) return;
        ref readonly var target = ref Nodes.Get(store);
        // Once the project is gone there is no building commitment to resume. Turn the physical load into
        // an ordinary one-trip delivery with no live source; carried cargo makes ReleaseStaleHauls leave it
        // alone until Handover puts it down, and the non-repeating haul then clears itself.
        var assignment = Assignment.Haul(
            NodeId.None,
            agent.Position,
            store,
            target.Position,
            agent.Jobs.Carrying,
            EconomySystem.HandoverSeconds,
            0f,
            target.FootprintRadius);
        JobSystem.Assign(ref agent, assignment);
        JobSystem.Retarget(ref agent, assignment, leg: 1);
    }

    /// <summary>Closes one villager's fetch, equip and train loop.</summary>
    private void TraineeHandover(ref AgentState agent, int leg)
    {
        ref var jobs = ref agent.Jobs;
        var barracksId = jobs.Project;
        if (!IsUsableBarracks(barracksId))
        {
            ReturnTraineeCargoOrClear(ref agent);
            return;
        }

        if (leg % 2 == 0)
        {
            if (jobs.CarriedUnits <= 0 && jobs.ReservedUnits > 0 && Nodes.Contains(jobs.Assignment.Source))
            {
                ref var source = ref Nodes.Get(jobs.Assignment.Source);
                var taken = Math.Min(
                    jobs.ReservedUnits,
                    Math.Min(agent.CarryCapacity, source.Stock[jobs.Assignment.Cargo]));
                if (taken > 0)
                {
                    source.Stock.Add(jobs.Assignment.Cargo, -taken);
                    jobs.Carrying = jobs.Assignment.Cargo;
                    jobs.CarriedUnits = taken;
                }
            }

            jobs.ReservedUnits = 0;
            ChooseTraineeNextStep(ref agent, barracksId);
            return;
        }

        ref var barracks = ref Nodes.Get(barracksId);
        if (jobs.CarriedUnits > 0)
        {
            var useful = Math.Max(0, TrainingDemand(barracksId, jobs.Carrying, agent.Id));
            var delivered = Math.Min(jobs.CarriedUnits, useful);
            barracks.Stock.Add(jobs.Carrying, delivered);
            jobs.CarriedUnits -= delivered;
            if (jobs.CarriedUnits > 0)
            {
                ReturnTraineeCargoOrClear(ref agent);
                return;
            }
        }
        else if (EquipmentIsReadyForTrainees(barracksId))
        {
            jobs.TrainingWork = MathF.Min(
                MilitiaTraining.Seconds,
                jobs.TrainingWork + jobs.Assignment.DwellOfLeg(leg));
            if (jobs.TrainingWork >= MilitiaTraining.Seconds && MilitiaTraining.CanPay(in barracks))
            {
                foreach (var resource in Resources.All)
                {
                    var cost = MilitiaTraining.CostOf(resource);
                    if (cost <= 0) continue;
                    barracks.Stock.Add(resource, -cost);
                    economy.RecordConsumed(resource, cost);
                }

                BecomeMilitia(ref agent);
                return;
            }
        }

        ChooseTraineeNextStep(ref agent, barracksId);
    }

    /// <summary>Uses carried equipment, claims a missing load, or stays at the barracks to train.</summary>
    private void ChooseTraineeNextStep(ref AgentState agent, NodeId barracksId)
    {
        if (!IsUsableBarracks(barracksId) || agent.Role != AgentRole.Villager)
        {
            ReturnTraineeCargoOrClear(ref agent);
            return;
        }

        if (agent.Jobs.CarriedUnits > 0)
        {
            if (MilitiaTraining.CostOf(agent.Jobs.Carrying) > 0 &&
                TrainingDemand(barracksId, agent.Jobs.Carrying, agent.Id) > 0)
            {
                RetargetTraineeToBarracks(ref agent, barracksId);
            }
            else
            {
                ReturnTraineeCargoOrClear(ref agent);
            }
            return;
        }

        if (TryClaimTrainingLoad(ref agent, barracksId)) return;
        RetargetTraineeToBarracks(ref agent, barracksId);
    }

    private bool TryClaimTrainingLoad(ref AgentState agent, NodeId barracksId)
    {
        var bestSource = NodeId.None;
        var bestCargo = Resource.Grain;
        var bestUnits = 0;
        var bestSeconds = float.PositiveInfinity;
        foreach (var resource in Resources.All)
        {
            var remaining = TrainingDemand(barracksId, resource, agent.Id);
            if (remaining <= 0) continue;
            foreach (ref readonly var source in Nodes.All)
            {
                if (!source.IsAlive || source.Id == barracksId || source.Faction != agent.Faction) continue;
                if (!source.Stores && !source.IsPile) continue;
                var available = source.Stock[resource] - ReservedAtSource(source.Id, resource, agent.Id);
                if (available <= 0) continue;
                if (!TryTravelSeconds(agent.Position, source.Position, agent.NavigationRadius, out var seconds))
                {
                    continue;
                }

                var units = Math.Min(agent.CarryCapacity, Math.Min(remaining, available));
                var better = seconds < bestSeconds - 0.0001f ||
                             MathF.Abs(seconds - bestSeconds) <= 0.0001f &&
                             ((int)resource < (int)bestCargo ||
                              resource == bestCargo && source.Id.Value < bestSource.Value);
                if (!better) continue;
                bestSource = source.Id;
                bestCargo = resource;
                bestUnits = units;
                bestSeconds = seconds;
            }
        }

        if (!bestSource.IsValid || bestUnits <= 0) return false;
        ref readonly var sourceNode = ref Nodes.Get(bestSource);
        ref readonly var barracks = ref Nodes.Get(barracksId);
        agent.Jobs.ReservedUnits = bestUnits;
        var assignment = agent.Jobs.Assignment with
        {
            Source = bestSource,
            Anchor = sourceNode.Position,
            PlaceExtent = sourceNode.FootprintRadius,
            Sink = barracksId,
            FarAnchor = barracks.Position,
            FarPlaceExtent = barracks.FootprintRadius,
            Cargo = bestCargo,
        };
        JobSystem.Retarget(ref agent, assignment, leg: 0);
        return true;
    }

    private int TrainingDemand(NodeId barracks, Resource resource, AgentId except)
    {
        if (!Nodes.Contains(barracks)) return 0;
        var trainees = 0;
        var incoming = 0;
        foreach (ref readonly var body in Agents.All)
        {
            if (!body.IsAlive || body.Jobs.Assignment.Kind != AssignmentKind.Train ||
                body.Jobs.Project != barracks)
            {
                continue;
            }

            trainees++;
            if (body.Id == except) continue;
            if (body.Jobs.CarriedUnits > 0 && body.Jobs.Carrying == resource)
                incoming += body.Jobs.CarriedUnits;
            else if (body.Jobs.Assignment.Cargo == resource)
                incoming += body.Jobs.ReservedUnits;
        }

        return Math.Max(
            0,
            trainees * MilitiaTraining.CostOf(resource) - Nodes.Get(barracks).Stock[resource] - incoming);
    }

    private bool EquipmentIsReadyForTrainees(NodeId barracks)
    {
        if (!Nodes.Contains(barracks)) return false;
        var trainees = 0;
        foreach (ref readonly var body in Agents.All)
        {
            if (body.IsAlive && body.Jobs.Assignment.Kind == AssignmentKind.Train &&
                body.Jobs.Project == barracks)
            {
                trainees++;
            }
        }

        ref readonly var node = ref Nodes.Get(barracks);
        foreach (var resource in Resources.All)
        {
            if (node.Stock[resource] < trainees * MilitiaTraining.CostOf(resource)) return false;
        }

        return trainees > 0;
    }

    private bool IsUsableBarracks(NodeId id) =>
        Nodes.Contains(id) && Nodes.Get(id).Kind == NodeKind.Barracks && Nodes.Get(id).IsBuilt;

    private void RetargetTraineeToBarracks(ref AgentState agent, NodeId barracksId)
    {
        ref readonly var barracks = ref Nodes.Get(barracksId);
        var assignment = agent.Jobs.Assignment with
        {
            Sink = barracksId,
            FarAnchor = barracks.Position,
            FarPlaceExtent = barracks.FootprintRadius,
        };
        JobSystem.Retarget(ref agent, assignment, leg: 1);
    }

    private void ReturnTraineeCargoOrClear(ref AgentState agent)
    {
        if (agent.Jobs.CarriedUnits <= 0)
        {
            JobSystem.Assign(ref agent, Assignment.None);
            return;
        }

        var store = EconomySystem.NearestStoreWithRoom(
            Nodes, agent.Jobs.Carrying, agent.Faction, agent.Position);
        if (!Nodes.Contains(store)) return;
        ref readonly var target = ref Nodes.Get(store);
        var assignment = Assignment.Haul(
            NodeId.None,
            agent.Position,
            store,
            target.Position,
            agent.Jobs.Carrying,
            EconomySystem.HandoverSeconds,
            0f,
            target.FootprintRadius);
        JobSystem.Assign(ref agent, assignment);
        JobSystem.Retarget(ref agent, assignment, leg: 1);
    }

    /// <summary>Changes the body frame and durable role without changing its stable id or household.</summary>
    private void BecomeMilitia(ref AgentState agent)
    {
        ScrapCart(ref agent);
        WearBody(ref agent, UnitType.Militia);
        agent.Role = AgentRole.Militia;
        agent.Appetite = UnitType.Militia.Appetite;
        agent.SightMetres = UnitType.Militia.SightMetres;
        agent.Strength = UnitType.Militia.Strength;
        agent.Health = UnitType.Militia.Health;
        JobSystem.Assign(ref agent, Assignment.None);
        HaltMovement(ref agent);
    }

    /// <summary>
    /// Somewhere a body of this size can actually stand next to its place.
    /// </summary>
    /// <remarks>
    /// Searched rather than computed, and that is the point. The first version put the body at
    /// <c>extent + radius + slack</c> along its own bearing and trusted the arithmetic — which works for
    /// a villager and fails for a wagon, because a 0.90 m body needs 1.96 m of clearance from the corner
    /// of a building's cell and the navigation raster answers to the nearest half-metre cell centre on
    /// top of that. The wagon asked for a route to a point inside a wall two hundred and twenty-eight
    /// times and never went anywhere.
    /// <para>
    /// So the ring is walked outward from as near as touching allows, trying the body's own bearing
    /// first at each radius and then to either side, and the first point the router says a body this size
    /// can occupy wins. Nearest-first means it walks to the near side of the building; bearing-first
    /// means it does not cross to the far side for a metre's advantage. It removes a tuned number
    /// instead of adding one, and it is asked only on the tick a walk is issued.
    /// </para>
    /// </remarks>
    private bool TryApproachPoint(in AgentState agent, out Vector2 point)
    {
        var place = agent.Jobs.Place;
        var extent = agent.Jobs.PlaceExtent;
        if (extent <= 0f)
        {
            point = Terrain.ClampPosition(place);
            return CanRouteTo(point, in agent);
        }

        var toward = agent.Position - place;
        var bearing = toward.LengthSquared() > 0.000001f
            ? Vector2.Normalize(toward)
            : Vector2.UnitX;
        var halfWidth = agent.Jobs.PlaceHalfWidth;
        var standoff = agent.Radius + JobDefaults.TouchSlack * 0.5f;
        var step = NavigationCellSize;
        // A couple of body widths of searching outward, and no further: past that the place is genuinely
        // walled in and retrying politely is the right answer.
        var furthest = standoff + agent.Radius * 2f + 2f;

        for (var outward = standoff; outward <= furthest; outward += step)
        for (var turn = 0; turn < ApproachBearings.Length; turn++)
        {
            var angle = ApproachBearings[turn];
            var direction = new Vector2(
                bearing.X * MathF.Cos(angle) - bearing.Y * MathF.Sin(angle),
                bearing.X * MathF.Sin(angle) + bearing.Y * MathF.Cos(angle));
            // Measured out from the building's wall along this bearing, not from a circle drawn round it.
            // The circle version aimed 2.2 m past the face of a granary while arrival was measured at
            // 0.8 m from it, so a cart never arrived: it retried twice, gave up, and settled wherever it
            // happened to be standing — which is why everything stood a couple of metres off its work.
            var candidate = Terrain.ClampPosition(
                place + direction * (BoundaryAlong(direction, halfWidth) + outward));
            if (!CanRouteTo(candidate, in agent)) continue;
            point = candidate;
            return true;
        }

        point = Terrain.ClampPosition(place);
        return false;
    }

    /// <summary>
    /// Whether the router will accept this point as a destination for this body, and the body can
    /// actually stand on it.
    /// </summary>
    /// <remarks>
    /// <b>Both tests, because they are different questions and using the wrong one cost a session.</b>
    /// <c>IsPositionNavigable</c> asks whether this body, as a circle, overlaps anything — a continuous
    /// test against terrain and placement boxes. <c>IsWalkable</c> asks whether the raster's clearance at
    /// this <em>cell centre</em> admits a body of this radius, which is what every route search actually
    /// consults and is quantised to the clearance rungs.
    /// <para>
    /// They disagree, and they disagree most for the widest bodies. A 0.90 m wagon can physically stand
    /// half a metre from a building's wall while the cell it is standing in has a clearance of 0.75 and is
    /// therefore not routable at 0.90. Choosing an approach point with the body test and then handing it
    /// to the router produced a wagon that asked for a route to a legal position two hundred and
    /// twenty-eight times and was refused every time, sitting thirty metres away in the movement layer's
    /// limbo state — <c>Move</c> with no destination. <c>plan-rts.md</c> already warns that cell clearance
    /// cannot bound where a body is; the converse is just as true, and this is where it bites.
    /// </para>
    /// </remarks>
    private bool CanRouteTo(Vector2 point, in AgentState agent) =>
        Navigation.TryWorldToCell(point, out var cell) &&
        Navigation.IsWalkable(cell, agent.NavigationRadius) &&
        pathService.IsPositionNavigable(point, agent.NavigationRadius);

    /// <summary>
    /// How far the wall of a square of this half-width is from its centre, along a direction.
    /// </summary>
    /// <remarks>
    /// The half-width along a face, the half-diagonal into a corner, and the right answer everywhere
    /// between. One line, and it is the difference between a body walking up to a building and a body
    /// stopping at the radius of a circle that happens to contain it.
    /// </remarks>
    private static float BoundaryAlong(Vector2 direction, float halfWidth)
    {
        var dominant = MathF.Max(MathF.Abs(direction.X), MathF.Abs(direction.Y));
        return dominant <= 0.0001f ? halfWidth : halfWidth / dominant;
    }

    /// <summary>Bearings tried around a place, the body's own first and then either side of it.</summary>
    private static readonly float[] ApproachBearings =
    {
        0f, MathF.PI / 4f, -MathF.PI / 4f, MathF.PI / 2f, -MathF.PI / 2f,
        3f * MathF.PI / 4f, -3f * MathF.PI / 4f, MathF.PI,
    };

    /// <summary>Whether a body of this size could stand anywhere it needs to be.</summary>
    private bool IsPlaceApproachable(in AgentState agent) => TryApproachPoint(in agent, out _);

    /// <summary>
    /// Sends a producer home with what it is holding, and back to work when its hands are empty.
    /// </summary>
    /// <remarks>
    /// The producer's loop, closed here because only the world knows where the stores are. A shift at the
    /// field ends with something in hand, so the body is pointed at the nearest store that will take it; a
    /// shift at the store ends with it empty, so the body is pointed back at its field. Nothing is created
    /// or destroyed on either leg — the grain was already counted as produced when it was reaped into the
    /// reaper's hands, which is why <b>a settlement's food total does not move when a farmer walks in</b>.
    /// <para>
    /// A body holding grain nobody has room for keeps holding it. That is not a stall: it is a full
    /// granary, and the answer is another granary.
    /// </para>
    /// </remarks>
    private void WorkHandover(ref AgentState agent, int leg)
    {
        ref var jobs = ref agent.Jobs;

        // Coming off the work site with a load: find somewhere to put it and walk there.
        if (leg % 2 == 0)
        {
            var store = jobs.CarriedUnits > 0
                ? EconomySystem.NearestStoreWithRoom(Nodes, jobs.Carrying, agent.Faction, agent.Position)
                : NodeId.None;
            if (Nodes.Contains(store))
            {
                ref readonly var target = ref Nodes.Get(store);
                JobSystem.Retarget(
                    ref agent,
                    jobs.Assignment with
                    {
                        Sink = store,
                        FarAnchor = target.Position,
                        FarPlaceExtent = target.FootprintRadius,
                    },
                    leg: 1);
                return;
            }

            // Empty-handed, or nowhere to put it: stay where the work is. Falling through to the
            // delivery leg sent a farmer with nothing to carry walking to the granary and back all
            // summer, which cost it its own field — the settlement lost a third of a harvest to twelve
            // people commuting to deliver nothing.
            if (!TrySendBackToWork(ref agent)) JobSystem.Assign(ref agent, Assignment.None);
            return;
        }

        // At the store: put it down, then go back out.
        if (Nodes.Contains(jobs.Assignment.Sink) && jobs.CarriedUnits > 0)
        {
            ref var store = ref Nodes.Get(jobs.Assignment.Sink);
            var delivered = Math.Min(jobs.CarriedUnits, store.RoomFor(jobs.Carrying));
            store.Stock.Add(jobs.Carrying, delivered);
            jobs.CarriedUnits -= delivered;
        }

        if (!TrySendBackToWork(ref agent)) JobSystem.Assign(ref agent, Assignment.None);
    }

    /// <summary>
    /// Points a producer back at something to work, and says so if there is nothing.
    /// </summary>
    /// <remarks>
    /// A field is the same field every time, so this is nearly a no-op for a farmer: the field it was
    /// assigned to is the field it goes back to, and the assignment only ends if somebody demolished it.
    /// <para>
    /// <b>A cutter is the interesting case, and it is where the wood line lives.</b> A tree is finite, so
    /// the site changes several times a season, and the cutter picks the nearest one within
    /// <see cref="Woodland.ReachMetres"/> <em>of the store it just delivered to</em> rather than of
    /// itself. That one choice is the whole mechanic:
    /// </para>
    /// <list type="bullet">
    /// <item>Trees near the granary, and a cutter based at the granary walks a few metres a load. No cart
    /// is involved, because the producer carries its own output — the settlement completes a year with
    /// zero hauling journeys.</item>
    /// <item>Those trees felled, and no store can reach a tree any more. The cutters say so out loud
    /// rather than quietly walking further and further, which is the settlement being told it has
    /// outgrown its arrangement.</item>
    /// <item>A forward depot built at the tree line, and the cutters re-base onto it by themselves — the
    /// nearest store with a tree in reach. Their wood now piles up in a building no household draws from,
    /// and the hauling board collects stranded stock, so carts appear <em>because of the geometry</em>
    /// and not because anything was told about lumber camps.</item>
    /// </list>
    /// </remarks>
    private bool TrySendBackToWork(ref AgentState agent)
    {
        var assignment = agent.Jobs.Assignment;
        if (Deposits.IsDeposit(assignment.Cargo))
        {
            // <b>One chain for both deposits, asking Deposits which reach applies.</b> Copying it per resource
            // would have made the re-basing rule below — the thing that turns a depot into a working quarry —
            // two rules that have to be kept in step by hand.
            var reach = Deposits.ReachMetres(assignment.Cargo);
            // Base the search on the store just delivered to, falling back to where the body is standing
            // on the very first shift, when it has not delivered anything yet.
            var from = Nodes.Contains(assignment.Sink)
                ? Nodes.Get(assignment.Sink).Position
                : agent.Position;
            var tree = EconomySystem.NearestDeposit(
                Nodes, Agents, from, assignment.Cargo, reach, agent.Id, CanReachTree);
            if (!tree.IsValid)
            {
                var (store, elsewhere) = EconomySystem.NearestBaseWithDeposit(
                    Nodes, Agents, agent.Faction, agent.Position, assignment.Cargo, reach, agent.Id,
                    CanReachTree);
                if (!elsewhere.IsValid) return false;
                tree = elsewhere;
                assignment = assignment with { Sink = store };
            }

            ref readonly var trunk = ref Nodes.Get(tree);
            JobSystem.Retarget(
                ref agent,
                assignment with
                {
                    Source = tree,
                    Anchor = trunk.Position,
                    PlaceExtent = trunk.FootprintRadius,
                },
                leg: 0);
            return true;
        }

        if (!Nodes.Contains(assignment.Source)) return false;
        ref readonly var field = ref Nodes.Get(assignment.Source);
        JobSystem.Retarget(
            ref agent,
            assignment with { Anchor = field.Position, PlaceExtent = field.FootprintRadius },
            leg: 0);
        return true;
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
        // A building's own cells are built on, so asking whether its centre is navigable would call
        // every farm in the world unreachable and leave every hand politely retrying forever. What
        // matters for a place with an extent is whether there is ground to stand on around it.
        if (!IsPlaceApproachable(in agent))
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
        agent.SeekingFieldEntry = false;
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

    /// <summary>
    /// Puts a body that fell off the cohort's field back on it, once it is standing somewhere priced.
    /// </summary>
    /// <remarks>
    /// <b>Without this, escaping a pocket is a one-way trip.</b> The fallback sends a dropped body to the
    /// nearest cell the field can serve; arriving there and then walking the rest of the way on its own route
    /// would be the very cross-map path the fallback exists to avoid. So the moment the field can price where
    /// the body is standing, it goes back on the shared route.
    /// <para>
    /// The condition carries no new state: a body on an escape path has a Destination that is not its
    /// RequestedDestination, which no other path in this file produces. Retried on a stagger rather than every
    /// tick, because the check samples the field and a hundred bodies asking every tick is its own stall.
    /// </para>
    /// </remarks>
    private void RejoinFieldTransit(ref AgentState agent)
    {
        if (!agent.SeekingFieldEntry) return;
        if (agent.MoveGroupId == 0 || agent.ApproachingSlot) return;
        // <b>Only a body with no route left, and that precision matters.</b> The first version asked whether
        // Destination differed from RequestedDestination, on the reasoning that only an escape path produces
        // that — which is false: AssignPath resolves an unwalkable goal to a nearby cell and leaves exactly the
        // same difference. So it also fired for bodies on deliberate individual routes, pulled them back onto
        // the shared gradient, and broke both pen-distribution tests. A body still holding a path is going
        // somewhere on purpose; a group member with no path and its target still ahead of it is the escape
        // case and nothing else.
        if (agent.Path != PathHandle.None) return;
        if (Vector2.DistanceSquared(agent.Position, agent.RequestedDestination) <=
            ArrivalDistance * ArrivalDistance)
        {
            return;
        }
        if ((TickNumber + agent.Id.Value) % FieldRejoinIntervalTicks != 0) return;
        if (pathService.SampleFlowGradient(
                agent.Position,
                agent.RequestedDestination,
                agent.NavigationRadius,
                agent.AdoptedCongestionRevision,
                agent.MaximumSpeed) == Vector2.Zero)
        {
            return;
        }

        agent.SeekingFieldEntry = false;
        pathService.FieldRejoins++;
        BeginFlowTransit(ref agent, agent.RequestedDestination);
    }

    /// <summary>
    /// Local pressure above which a dropped body solves its own route instead of rejoining the field.
    /// </summary>
    /// <remarks>
    /// <b>Measured, not chosen.</b> The village's cross-map drops sit at 0.083 — twenty bodies with room,
    /// standing near each other because they were ordered together. A pen holding fifty against a single-cell
    /// gate is an order of magnitude above that. Half sits between the two with room on both sides, and the
    /// first attempt at "any pressure at all" (0.01) blocked precisely the case this exists to fix, which is
    /// what measuring the drop site rather than guessing at it is for.
    /// </remarks>
    private const float FieldEntryPressureCeiling = 0.5f;

    /// <summary>How often an escaping body is asked whether the field will take it back.</summary>
    private const int FieldRejoinIntervalTicks = 10;

    /// <summary>Bodies that rejoined the shared field after escaping a pocket. See RejoinFieldTransit.</summary>
    public long FieldRejoins => pathService.FieldRejoins;

    private static void DetachFromMoveGroup(ref AgentState agent)
    {
        agent.SeekingFieldEntry = false;
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
                    // <b>Straight at it, and re-aimed constantly. Both, or neither works.</b> Measured on
                    // --fightbench, which is the only instrument that could see it:
                    //
                    //   stop 0.95 m, re-aim every 0.5 m of drift -> settles 1.92 m off, 0% in reach
                    //   stop 0.00 m, re-aim every 0.5 m of drift -> settles 0.96 m off, 0% in reach
                    //   stop 0.95 m, re-aim on any drift         -> settles 1.70 m off, 0% in reach
                    //   stop 0.00 m, re-aim on any drift         -> closes to 0.00 m, 92% in reach, kills
                    //
                    // Neither change alone does anything at all, which is why single changes judged one at
                    // a time against a six-minute raid found nothing for a whole session: a stop distance
                    // holds the body off, and a stale goal holds it off by as much again, so removing
                    // either leaves the other doing the job. Together they are the difference between a
                    // pursuit that has never once landed a blow and one that catches a quarry going at
                    // seven tenths of its pace in twenty-two seconds.
                    //
                    // Follow keeps both: following somebody about is not the act of running them down, and
                    // it does not want to end in contact.
                    UpdateTargetBehavior(ref agent, stopDistance: 0f, flee: false, updatePeriod: 0.22f);
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
        // <b>How stale the goal may get before it is re-asked, and a chase cannot afford what a follow
        // can.</b> Half a metre of slack is right for following somebody about; in a chase it is half a
        // metre of permanent lag, because the body walks to where the quarry was, arrives, halts, and waits
        // to be re-aimed. Measured on --fightbench: the closest a chase ever got was stop-distance plus a
        // constant 0.96 m, at every stop distance including zero, and this threshold is where that constant
        // comes from.
        var staleness = agent.LocomotionState == AgentLocomotionState.Chase ? 0.04f : 0.25f;
        if (agent.HasDestination &&
            Vector2.DistanceSquared(requested, agent.RequestedDestination) < staleness)
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
        agent.SeekingFieldEntry = false;
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

            // Before the destination check, because the body this serves has just arrived at its entry point
            // and therefore has no destination left to hold it in the loop.
            RejoinFieldTransit(ref agent);

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
            // Counted separately from the no-gradient drop below, because they are different faults with the
            // same symptom: this one is a body the field keeps pointing into something it cannot walk through,
            // and that one is a field with no answer for where the body is standing. §96.
            pathService.FlowTransitDropsRejected++;
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
            // The field no longer offers this body a route: it is standing where the region tile could not
            // price it. §96 — the old answer searched the whole map to the final goal, a quarter of a million
            // expansions, when the field already knew the way from anywhere it HAD priced. So ask the cheap
            // question instead: where is the nearest cell this field can serve. A short path there, and the
            // body rejoins the cohort's route rather than replacing it.
            pathService.FlowTransitDropsNoGradient++;
            agent.UsesFlowTransit = false;
            // <b>Under crowd pressure the old answer is the right one, and that is not a compromise.</b>
            // A body that solves its own route is a body that can pick a different exit, and in a pen that
            // diversity is the whole behaviour — both pen self-tests assert it, and they failed when every
            // dropped body was put back on one shared gradient. In the open there is nothing to diversify
            // around and the field is simply correct: the village's freeze was twenty bodies crossing empty
            // ground, each running a quarter-million-expansion search to reach a field they were standing one
            // cell away from. Pressure is what tells those two situations apart.
            var pressure = Navigation.TryWorldToCell(agent.Position, out var pressureCell)
                ? Congestion.At(pressureCell)
                : 0f;
            pathService.WorstDropPressure = MathF.Max(pathService.WorstDropPressure, pressure);
            var crowded = pressure >= FieldEntryPressureCeiling;
            var entry = crowded
                ? null
                : pathService.FindFieldEntry(
                    agent.Position,
                    agent.RequestedDestination,
                    agent.NavigationRadius,
                    agent.AdoptedCongestionRevision,
                    agent.MaximumSpeed);
            // Destination becomes the entry point while RequestedDestination stays the real target, and that
            // difference is what marks a body as escaping — no new state to save or fingerprint. See
            // RejoinFieldTransit.
            AssignPath(
                ref agent,
                entry ?? agent.RequestedDestination,
                preserveCurrentPathOnFailure: true);
            // Set after the assignment, because AssignPath clears it: an ordinary route means the body is no
            // longer looking for the field.
            agent.SeekingFieldEntry = entry is not null;
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
