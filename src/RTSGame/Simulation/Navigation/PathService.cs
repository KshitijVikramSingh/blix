using System.Numerics;
using RTSGame.Simulation.Placement;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Simulation.Navigation;

/// <summary>Axis-aligned footprint of a piece of static geometry.</summary>
internal readonly record struct StaticBox(Vector2 Minimum, Vector2 Maximum);

internal readonly record struct PathResult(
    Vector2[] Waypoints,
    Vector2 Destination,
    GridCell[] RouteCells);

internal sealed class PathService
{
    public bool HasTerrainVariation => terrain.Revision > 0;
    private const float DiagonalCost = 1.41421356f;
    private const float CongestionAvoidanceRadius = 3f;
    /// <summary>How far ahead a stalled body looks for the constriction blocking it.</summary>
    internal static float ApertureSearchDistance = 4.5f;
    /// <summary>
    /// Peak delay charged at the centre of a granted detour's avoidance bubble.
    /// </summary>
    /// <remarks>
    /// Route cost is seconds, and this is one of them: what it says is what going that
    /// way is expected to cost. Walking round a three-metre bubble is about 1.7 m, or
    /// 0.4 s, so half a second summed over the cells crossing it decides the matter
    /// without overstating it. Enforcement, where it is needed, is
    /// <see cref="SegmentAvoidsCongestion"/> forbidding the crossing outright — this
    /// number only has to bias the search early enough not to need forbidding late.
    /// <para>
    /// It was ten seconds: ninety cells of detour, a barrier wearing the costume of a
    /// cost. Restating it honestly on its own made things worse — the pen stopped using
    /// its alternate exits at all — which looked like proof that the barrier was load
    /// bearing. It was not. It was compensating for a congestion field that took 4.5 s
    /// to forget a jam, so honest costs were competing against pressure that had already
    /// gone. With the field's fade shortened to match how fast a jam actually clears
    /// (see CongestionField.DecaySeconds) an honest half second is not merely adequate,
    /// it is better than the barrier was on every measure: two exits instead of four,
    /// routes at 1.06x optimal instead of 1.19x, and the pen clears in 13.4 s
    /// instead of 19.2 s. Two symptoms, one cause.
    /// </para>
    /// </remarks>
    internal static float DetourAvoidanceSeconds = 0.5f;
    /// <summary>
    /// Nominal unit speed used to express route cost as travel time.
    /// </summary>
    /// <remarks>
    /// A unit's own top speed scales every leg of a route by the same factor, so
    /// it cannot change which route is cheapest and one shared field stays
    /// correct for all of them. Congestion delay is the exception — a jam costs
    /// every unit the same wall-clock seconds regardless of how fast it runs — so
    /// once units genuinely differ in speed, the fastest ones will slightly
    /// under-value detours. Noted rather than solved; every unit is 4.5 m/s today.
    /// </remarks>
    private const float ReferenceSpeed = 4.5f;
    /// <summary>Seconds of delay represented by one unit of measured backpressure.</summary>
    /// <remarks>
    /// Deliberately large. It looks like it should send units on absurd detours,
    /// and would if the input were crowd density — but the field only accumulates
    /// where bodies want to move and are not moving, so a crowd flowing normally
    /// through a gap deposits almost nothing and costs almost nothing. The
    /// coefficient has to be this size for a jam spread thinly over a handful of
    /// cells to outweigh a detour of ten or twenty. Measured: at 1.5 a symmetric
    /// two-gap wall still sent 29 of 30 units through one gap; at this value they
    /// split, and every scenario's completion time improved rather than regressed.
    /// </remarks>
    internal static float CongestionSecondsPerPressure = 0.40f;
    /// <summary>Extra seconds charged per metre of climb.</summary>
    internal static float ClimbSecondsPerMetre = 0.60f;
    /// <summary>
    /// Nominal turn rate, in radians per second, that route cost prices turning at.
    /// </summary>
    /// <remarks>
    /// The same device as <see cref="ReferenceSpeed"/> and carrying the same caveat: one
    /// shared field stays correct for every unit only while they all turn at the same
    /// rate. Mirrors <c>AgentDefaults.MaximumTurnSpeed</c> rather than referencing it, so
    /// the navigation layer does not need to know what an agent is; if the two ever
    /// diverge, routes will be planned for a body that does not exist.
    /// </remarks>
    internal static float ReferenceTurnSpeed = 4.0f;
    /// <summary>Seconds to cross one cell of open ground at the reference speed.</summary>
    private float SecondsPerCell => grid.Transform.CellSize / ReferenceSpeed;
    /// <summary>Travel time an unreachable cell reports when sampling the flow field.</summary>
    /// <remarks>
    /// Not a predicted cost but a boundary condition: it exists so bilinear sampling of
    /// the cost field near a wall leans away from it instead of returning infinity and
    /// poisoning the gradient with a NaN. It has to outweigh any genuine cost difference
    /// across one probe ring, and stay finite.
    /// <para>
    /// A hundred and eight cells looks like far more than that argument needs, and it is
    /// not: how steeply the field leans away from a wall is also what decides how wide a
    /// body swings round it. Re-measured at 20 / 50 / 108 after the congestion tuning —
    /// 20 gives marginally shorter routes (1.04x against 1.06x) and is 2.5 s slower to
    /// clear the pen, and cost is denominated in time, so this wins. Stated in cells
    /// rather than seconds because it is a boundary value, not a claim about how long
    /// blocked ground takes to cross.
    /// </para>
    /// </remarks>
    private float BlockedFlowSeconds => BlockedFlowCells * SecondsPerCell;

    /// <summary>Cells of open ground the blocked-cell boundary value is worth.</summary>
    internal static float BlockedFlowCells = 108f;
    /// <summary>How many past congestion revisions stay resident for staggered adoption.</summary>
    private const int RetainedCongestionRevisions = 3;
    /// <summary>Directions probed when reading the cost field's downhill.</summary>
    private const int FlowProbeCount = 16;
    /// <summary>How much further than the straight line a slot may be, before it is judged unconnected.</summary>
    /// <summary>
    /// Delay charged for taking a bottleneck another member of the same group has
    /// already reserved — what waiting a turn there is expected to cost.
    /// </summary>
    internal static float BottleneckReservationSeconds = 0.5f;
    private const float SlotDetourTolerance = 2.0f;
    private const float SlotDetourSlack = 0.45f;
    private const float Epsilon = 0.00001f;
    private static readonly GridCell[] NeighborOffsets =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        new(1, 1), new(1, -1), new(-1, 1), new(-1, -1),
    };

    /// <summary>
    /// Seconds lost to changing heading between two step directions, indexed
    /// <c>[from, to]</c>, for a body arcing through the turn at speed and for one
    /// pivoting on the spot.
    /// </summary>
    /// <remarks>
    /// Route cost is time, and turning takes time, but until now it was free here: a
    /// hairpin priced exactly like a straight line. That is why making bodies turn more
    /// slowly did not make them prefer routes with gentler corners — the router had no
    /// idea turning had become more expensive. It also means a route could be optimal on
    /// paper and unfollowable in practice, which is the most expensive kind of wrong,
    /// because the body discovers it by failing.
    /// <para>
    /// Both tables are derived, not tuned. A body that can arc through a turn keeps its
    /// speed and loses only the difference between the arc it travels and the straight
    /// line it wanted: at turn rate w that is <c>(θ - 2·sin(θ/2)) / w</c> seconds, which
    /// is almost nothing for a gentle bend and rises sharply toward a reversal. A body
    /// that cannot arc — because the walls are closer than its turning circle — has to
    /// stop and turn, costing the full <c>θ / w</c>. Which of the two applies is a
    /// property of the ground, so the cost is interpolated by local clearance.
    /// </para>
    /// </remarks>
    // Radians, not seconds: the turn rate they are divided by is live-tunable, and baking
    // it in made the tables silently stale the moment somebody moved the slider.
    private static readonly float[,] ArcTurnRadians = BuildTurnTable(pivot: false);
    private static readonly float[,] PivotTurnRadians = BuildTurnTable(pivot: true);

    private static float[,] BuildTurnTable(bool pivot)
    {
        var table = new float[NeighborOffsets.Length, NeighborOffsets.Length];
        for (var from = 0; from < NeighborOffsets.Length; from++)
        for (var to = 0; to < NeighborOffsets.Length; to++)
        {
            var a = Vector2.Normalize(new Vector2(NeighborOffsets[from].X, NeighborOffsets[from].Z));
            var b = Vector2.Normalize(new Vector2(NeighborOffsets[to].X, NeighborOffsets[to].Z));
            var theta = MathF.Acos(Math.Clamp(Vector2.Dot(a, b), -1f, 1f));
            table[from, to] = pivot ? theta : theta - 2f * MathF.Sin(theta * 0.5f);
        }
        return table;
    }

    /// <summary>
    /// Seconds charged for arriving at <paramref name="cell"/> heading
    /// <paramref name="fromDirection"/> and leaving it heading <paramref name="toDirection"/>.
    /// </summary>
    private float TurnCost(int fromDirection, int toDirection, GridCell cell, float agentRadius)
    {
        // Either heading unrecorded means there is no turn to charge: the route either
        // starts here and is free to face anywhere, or ends here and turns no further.
        if (fromDirection < 0 || toDirection < 0 || fromDirection == toDirection) return 0f;
        var turnRate = MathF.Max(0.05f, ReferenceTurnSpeed);
        var arc = ArcTurnRadians[fromDirection, toDirection] / turnRate;
        var pivot = PivotTurnRadians[fromDirection, toDirection] / turnRate;
        if (pivot <= arc) return arc;

        // Whether the body can carry its speed through the turn depends on whether its
        // turning circle fits. Same clearance ramp the congestion field uses to decide
        // whether a stalled body is an obstruction or a nuisance.
        var clearance = grid.Clearance(cell);
        var turningRadius = ReferenceSpeed / ReferenceTurnSpeed;
        var tight = agentRadius + turningRadius * 0.5f;
        var open = agentRadius + turningRadius * 1.5f;
        var confinement = clearance <= tight
            ? 1f
            : clearance >= open
                ? 0f
                : 1f - (clearance - tight) / (open - tight);
        return float.Lerp(arc, pivot, confinement);
    }

    private readonly TerrainMap terrain;
    private readonly PlacementGrid placement;
    private readonly NavigationGrid grid;
    private readonly CongestionField congestion;
    private readonly Dictionary<(int Goal, int Radius, int Nav, int Congestion, bool Turns), float[]> flowFields = new();
    /// <summary>
    /// Per-cell memo of whether a body of a given radius fits at the cell centre,
    /// as 0 unknown / 1 admitted / 2 refused, keyed by radius in centimetres.
    /// </summary>
    /// <remarks>
    /// Every route search asks this of eight neighbours per expansion, and the
    /// honest answer costs a body-shaped terrain sample — nine grade probes, each
    /// four bilinear height lookups — plus a placement test. That is the single
    /// most expensive thing A* did, and it was recomputing an answer that cannot
    /// change until the navigation raster does: cell centres are a fixed, finite
    /// set of positions. Keyed on the raster revision, which is bumped by terrain
    /// edits and by placement edits alike, so the memo cannot outlive its inputs.
    /// </remarks>
    private readonly Dictionary<int, byte[]> cellCenterAdmission = new();
    private int cellCenterAdmissionRevision = -1;
    private readonly Dictionary<(int Cell, int Radius), Vector2> passageAxes = new();
    private int passageAxisRevision = -1;
    private int[] searchCameFrom = Array.Empty<int>();
    private float[] searchCost = Array.Empty<float>();
    private bool[] searchClosed = Array.Empty<bool>();
    private int[] searchArrival = Array.Empty<int>();
    private readonly PriorityQueue<GridCell, float> searchQueue = new();

    /// <summary>Flow fields built since construction. A proxy for route churn:
    /// if this climbs steeply the congestion field is invalidating routes faster
    /// than the crowd can act on them.</summary>
    public int FlowFieldBuilds { get; private set; }
    /// <summary>A* queries served, for attributing pathfinding cost.</summary>
    public long PathQueries { get; private set; }

    /// <summary>Congestion revision currently published to routing.</summary>
    public int CongestionRevision => congestion.Revision;

    public PathService(
        TerrainMap terrain,
        PlacementGrid placement,
        NavigationGrid grid,
        CongestionField congestion)
    {
        this.terrain = terrain;
        this.placement = placement;
        this.grid = grid;
        this.congestion = congestion;
    }

    /// <summary>
    /// Extra traversal cost for routing through cells where movement is failing.
    /// </summary>
    /// <remarks>
    /// This is what makes a crowd spread across alternative routes. As one exit
    /// saturates, the pressure deposited by the bodies stalled in it raises its
    /// cost, and the next route built prefers a longer but clear way round. It is
    /// predictive at the group level rather than reactive per agent: nobody has to
    /// jam and then individually replan.
    /// </remarks>
    private float CongestionCost(GridCell from, GridCell to, float turnSeconds)
    {
        var travel = grid.CellCenter(to) - grid.CellCenter(from);
        if (travel.LengthSquared() > 0.0001f) travel = Vector2.Normalize(travel);
        var pressure = congestion.At(from) * congestion.DirectionalFactor(from, travel) +
                       congestion.At(to) * congestion.DirectionalFactor(to, travel);
        return pressure * 0.5f * CongestionSecondsPerPressure * ManoeuvreAmplification(turnSeconds);
    }

    /// <summary>
    /// How much longer a queue takes to drain through ground that is awkward to
    /// manoeuvre through, as a multiple of the same queue on a straight run.
    /// </summary>
    /// <remarks>
    /// Waiting behind a queue costs the queue's length times what one body takes to get
    /// clear — and what one body takes to get clear is crossing the cell <em>plus</em>
    /// whatever turn the cell demands. Charging a flat rate per unit of pressure says
    /// every jam drains at the same speed, which is exactly wrong at the place jams
    /// happen: a one-cell gap set at right angles to the approach makes thirty bodies
    /// pivot, one at a time, and none of that appears in the cost of going that way.
    /// <para>
    /// This is the group-level reading of a turn, and it is what makes tightening the
    /// turn rate divert traffic. A body's own turn is far too cheap to matter — 0.039 s
    /// in the open, a third of a cell, against alternate routes sixty cells further — so
    /// charging it individually can never move a route choice and must not be inflated
    /// until it does. Multiplied by the number of bodies that have to perform it, the
    /// same honest figure is decisive: an open bend amplifies a wait by about 1.35x, a
    /// pivot in a gap by 4.5x, and halving the turn rate takes that to 8x.
    /// </para>
    /// <para>
    /// It costs nothing where nothing is queueing, which is the property that matters —
    /// pressure only accumulates where bodies want to move and are not moving, so an
    /// empty awkward corner stays as cheap as it should be.
    /// </para>
    /// <para>
    /// Charged at full derived strength. A scaling factor was measured at 1.0 / 0.5 / 0.25
    /// against no amplification at all, and full strength is decisively the best on the
    /// metric this exists to move — dead stops at a one-cell gate read 5 / 23 / 72 / 56
    /// across those four — while route length and direction stability are flat across all
    /// of them. So there is no knob here; the derivation is the value.
    /// </para>
    /// </remarks>
    private float ManoeuvreAmplification(float turnSeconds) =>
        turnSeconds <= 0f ? 1f : 1f + turnSeconds / SecondsPerCell;

    /// <summary>
    /// Whether a formation slot is genuinely reachable from its command point.
    /// </summary>
    /// <remarks>
    /// A slot only has to be standable to be offered, which is not the same as
    /// being connected: a formation is routinely wider than the enclosure it is
    /// sent into, so slots land on perfectly good ground on the far side of a
    /// wall. The member then paths out of the enclosure, around, and back in,
    /// which is where the long looping routes came from. Comparing the field's
    /// travel time against the straight-line time catches that for free — the
    /// field is built for the group's transit anyway, and both are in seconds.
    /// </remarks>
    /// <summary>
    /// Best possible travel time from a point to a goal, in seconds, or false if
    /// no route exists. Diagnostics only — this is the denominator for judging
    /// how much further than necessary a body actually walked.
    /// </summary>
    /// <remarks>
    /// Deliberately measured without charging for turns, even though routing does charge
    /// for them. The number it feeds is <c>walked / optimal</c>, a ratio of two distances,
    /// and once the denominator started including turn time it stopped being one: the
    /// figure fell as low as 0.90, a body apparently walking less far than the shortest
    /// route, which is what a corrupted metric looks like rather than a good result. It
    /// would also have silently flattered the turn-cost change and broken comparability
    /// with every baseline recorded before it.
    /// </remarks>
    public bool TryOptimalTravelTime(Vector2 goalPosition, Vector2 from, float agentRadius, out float seconds)
    {
        seconds = 0f;
        if (!grid.TryWorldToCell(goalPosition, out var goalCell)) return false;
        if (!grid.TryWorldToCell(from, out var fromCell)) return false;
        var resolved = grid.IsWalkable(goalCell, agentRadius)
            ? goalCell
            : FindNearestWalkable(goalCell, agentRadius);
        if (resolved is not { } goal) return false;
        var costs = GetFlowField(goal, agentRadius, congestion.Revision, chargeTurns: false);
        var cost = costs[grid.Transform.Index(fromCell)];
        if (!float.IsFinite(cost)) return false;
        seconds = cost;
        return true;
    }

    public bool IsSlotReachable(Vector2 target, Vector2 slot, float agentRadius)
    {
        if (!grid.TryWorldToCell(target, out var goalCell)) return false;
        if (!grid.TryWorldToCell(slot, out var slotCell)) return false;
        var resolved = grid.IsWalkable(goalCell, agentRadius)
            ? goalCell
            : FindNearestWalkable(goalCell, agentRadius);
        if (resolved is not { } goal) return false;

        var costs = GetFlowField(goal, agentRadius, congestion.Revision);
        var cost = costs[grid.Transform.Index(slotCell)];
        if (!float.IsFinite(cost)) return false;
        var direct = Vector2.Distance(slot, target) / ReferenceSpeed;
        return cost <= direct * SlotDetourTolerance + SlotDetourSlack;
    }

    public PathResult? FindPath(
        Vector2 start,
        Vector2 requestedGoal,
        float agentRadius,
        Vector2? congestionAvoidanceCenter = null,
        float[]? additionalNavigationCosts = null)
    {
        PathQueries++;
        requestedGoal = terrain.ClampPosition(requestedGoal, agentRadius + 0.035f);
        if (!grid.TryWorldToCell(start, out var startCell) || !grid.TryWorldToCell(requestedGoal, out var requestedCell))
        {
            return null;
        }
        var startCellCenter = grid.CellCenter(startCell);
        var startCellIsConnected = grid.IsWalkable(startCell, agentRadius) &&
                                   IsInitialBodyStepClear(start, startCellCenter, agentRadius);
        var pathStartCell = startCellIsConnected
            ? startCell
            : FindRecoveryStart(
                startCell,
                agentRadius,
                start,
                requestedGoal,
                grid.Transform.CellSize * 2.25f);
        if (pathStartCell is not { } resolvedStartCell) return null;
        var startWasAdjusted = resolvedStartCell != startCell;

        if (additionalNavigationCosts is null &&
            terrain.Revision == 0 &&
            !startWasAdjusted &&
            grid.IsWalkable(requestedCell, agentRadius) &&
            IsDirectPathClear(start, requestedGoal, agentRadius) &&
            SegmentAvoidsCongestion(start, requestedGoal, congestionAvoidanceCenter))
        {
            return new PathResult(
                new[] { requestedGoal },
                requestedGoal,
                new[] { startCell, requestedCell });
        }

        var goalCell = grid.IsWalkable(requestedCell, agentRadius)
            ? requestedCell
            : FindNearestWalkable(requestedCell, agentRadius);
        if (goalCell is not { } goal) return null;
        var cells = FindCellPath(
            resolvedStartCell,
            goal,
            agentRadius,
            congestionAvoidanceCenter,
            additionalNavigationCosts);
        if (cells is null) return null;

        var destination = goal == requestedCell ? requestedGoal : grid.CellCenter(goal);
        var waypoints = SmoothPath(
            start,
            destination,
            cells,
            agentRadius,
            congestionAvoidanceCenter,
            additionalNavigationCosts,
            includeFirstCell: startWasAdjusted || terrain.Revision > 0);
        return waypoints.Length == 0 ||
               !IsInitialBodyStepClear(start, waypoints[0], agentRadius)
            ? null
            : new PathResult(waypoints, destination, cells.ToArray());
    }

    public void ReserveGroupRoute(
        PathResult path,
        float agentRadius,
        float[] routeCosts,
        float destinationExclusionRadius = 0f)
    {
        if (routeCosts.Length != grid.Width * grid.Height)
        {
            throw new ArgumentException("Group route-cost dimensions do not match the navigation grid.");
        }

        foreach (var cell in path.RouteCells)
        {
            if (!grid.Contains(cell)) continue;
            if (destinationExclusionRadius > 0f &&
                Vector2.Distance(grid.CellCenter(cell), path.Destination) <= destinationExclusionRadius)
            {
                continue;
            }
            var clearanceMargin = grid.Clearance(cell) - agentRadius;
            // Shared intent should not fan an army across an open field. Reserve
            // actual bottlenecks only; the arrival system handles convergence at
            // the common destination without manufacturing private approach lanes.
            var bottleneckSeconds = clearanceMargin < 0.80f ? BottleneckReservationSeconds : 0f;
            routeCosts[grid.Transform.Index(cell)] += bottleneckSeconds;
        }
    }

    public bool IsDirectPathClear(Vector2 start, Vector2 end, float agentRadius)
    {
        var expansion = agentRadius + 0.035f;
        if (!terrain.Contains(start, expansion) || !terrain.Contains(end, expansion)) return false;
        if (!IsContinuousBodyPathClear(start, end, agentRadius)) return false;

        if (terrain.Revision > 0)
        {
            var segment = end - start;
            var distance = segment.Length();
            var samples = Math.Max(1, (int)MathF.Ceiling(distance / (grid.Transform.CellSize * 0.40f)));
            GridCell? previousCell = null;
            for (var sample = 0; sample <= samples; sample++)
            {
                var position = Vector2.Lerp(start, end, sample / (float)samples);
                if (!grid.TryWorldToCell(position, out var cell) || !grid.IsWalkable(cell, agentRadius)) return false;
                if (previousCell is { } previous && previous != cell && !CanTraverse(previous, cell, agentRadius)) return false;
                previousCell = cell;
            }
        }

        // Segment against block, not just the sampled points. This looks like a
        // duplicate of the swept check above and is not: it uses a slightly wider
        // margin, and removing it let smoothed routes hug blocks closely enough to
        // fail both the wall-detour and chokepoint-separation tests. Only cells the
        // segment can actually reach are consulted.
        var halfCell = placement.Transform.CellSize * 0.5f + expansion;
        PlacementCellRange(
            Vector2.Min(start, end), Vector2.Max(start, end), halfCell,
            out var low, out var high);
        for (var z = low.Z; z <= high.Z; z++)
        for (var x = low.X; x <= high.X; x++)
        {
            var cell = new GridCell(x, z);
            if (!placement.IsOccupied(cell)) continue;
            var center = placement.Transform.CellCenter(cell);
            var minimum = center - new Vector2(halfCell);
            var maximum = center + new Vector2(halfCell);
            if (SegmentIntersectsAabb(start, end, minimum, maximum)) return false;
        }
        return true;
    }

    public bool IsStepClear(Vector2 start, Vector2 end, float agentRadius)
    {
        var expansion = agentRadius + 0.035f;
        if (!terrain.Contains(end, expansion) || !terrain.CanTraverse(start, end)) return false;
        if (!terrain.IsBodyTraversable(end, agentRadius)) return false;
        // Simulation steps are short enough that static collision cannot be
        // tunneled. Test the body's final footprint here; expanding an entire
        // segment against placement boxes makes a valid path stick forever on
        // the exact clearance boundary chosen by A*.
        return IsPositionFreeOfPlacement(end, agentRadius);
    }

    private bool IsContinuousBodyPathClear(
        Vector2 start,
        Vector2 end,
        float agentRadius,
        bool allowPlacementEscape = false)
    {
        // A body spawned or shoved inside a block must be allowed to walk out of
        // it. Validating that first step with the same predicate that declares
        // the start invalid rejects every candidate, so the unit is handed no
        // route at all and stands in the wall forever. When escaping, the
        // placement test becomes a segment-level "strictly moving outward" rule.
        if (allowPlacementEscape &&
            !SegmentAvoidsPlacement(start, end, agentRadius, allowEscapeFromStart: true))
        {
            return false;
        }

        var distance = Vector2.Distance(start, end);
        // Spacing is a fraction of the body, not of the grid. The old value worked
        // out near four centimetres, which was chosen to catch terrain seams
        // thinner than a navigation cell — a real hazard when height fields had
        // vertical steps in them, and eight times more sampling than a smooth
        // one needs. A body cannot pass through anything it does not overlap at
        // a third of its own radius.
        var sampleSpacing = MathF.Max(agentRadius * 0.35f, 0.05f);
        var samples = Math.Max(1, (int)MathF.Ceiling(distance / sampleSpacing));
        var previous = start;
        for (var sample = 1; sample <= samples; sample++)
        {
            var position = Vector2.Lerp(start, end, sample / (float)samples);
            if (!terrain.IsBodyTraversable(position, agentRadius) ||
                !terrain.CanTraverse(previous, position) ||
                !allowPlacementEscape && !IsPositionFreeOfPlacement(position, agentRadius))
            {
                return false;
            }
            previous = position;
        }
        return true;
    }

    public bool IsContinuousStepClear(
        Vector2 start,
        Vector2 end,
        float agentRadius,
        bool allowPlacementEscape = false)
    {
        var expansion = agentRadius + 0.035f;
        return terrain.Contains(start, expansion) &&
               terrain.Contains(end, expansion) &&
               IsContinuousBodyPathClear(start, end, agentRadius, allowPlacementEscape);
    }

    private bool IsInitialBodyStepClear(Vector2 start, Vector2 end, float agentRadius)
    {
        var escaping = !IsPositionFreeOfPlacement(start, agentRadius);
        var offset = end - start;
        var distance = offset.Length();
        if (distance <= 0.0001f) return IsPositionNavigable(start, agentRadius);
        var prefix = start + offset / distance * MathF.Min(0.25f, distance);
        return IsContinuousStepClear(start, prefix, agentRadius, escaping) &&
               (distance <= 0.25f || IsContinuousStepClear(start, end, agentRadius, escaping));
    }

    /// <summary>
    /// Static geometry within <paramref name="reach"/> of a point, as boxes.
    /// </summary>
    /// <remarks>
    /// The velocity solve needs walls as constraints rather than as a veto applied to
    /// its answer, and this is how it sees them. Navigation cells are the source
    /// because they are the one representation carrying both kinds of static
    /// obstruction — placed blocks and impassable or too-steep ground — as the same
    /// fact, and because the placement grid is cell-aligned with them, so a block is
    /// described exactly rather than approximately.
    /// <para>
    /// Boxes are reported at full cell extent, deliberately not inset the way
    /// <see cref="IsPositionFreeOfPlacement"/> insets its test. That inset is a
    /// tolerance on a point test and not a description of geometry: applying it here
    /// detaches every block from its neighbour by five centimetres, so a solid wall
    /// acquires a slot every cell and the solve steers bodies at gaps that are not
    /// there. Measured, that alone cost four times the dead stops at a one-cell gate.
    /// A wall is contiguous; the tolerance belongs to whoever measures against it.
    /// </para>
    /// <para>
    /// Runs of blocked cells along a row are merged, so a long wall costs the solver
    /// one constraint per row rather than one per cell.
    /// </para>
    /// </remarks>
    public int GatherBlockingBoxes(Vector2 center, float reach, Span<StaticBox> boxes)
    {
        var transform = grid.Transform;
        var lowLocal = (center - new Vector2(reach) - transform.Origin) / transform.CellSize;
        var highLocal = (center + new Vector2(reach) - transform.Origin) / transform.CellSize;
        var lowX = (int)MathF.Floor(lowLocal.X);
        var lowZ = (int)MathF.Floor(lowLocal.Y);
        var highX = (int)MathF.Ceiling(highLocal.X);
        var highZ = (int)MathF.Ceiling(highLocal.Y);

        var count = 0;
        for (var z = lowZ; z <= highZ && count < boxes.Length; z++)
        {
            var runStart = int.MinValue;
            for (var x = lowX; x <= highX + 1; x++)
            {
                // One past the end closes any open run. Cells outside the grid read
                // as blocked, which is correct: the map edge is a wall.
                var blocking = x <= highX && grid.IsBlocked(new GridCell(x, z));
                if (blocking)
                {
                    if (runStart == int.MinValue) runStart = x;
                    continue;
                }
                if (runStart == int.MinValue) continue;
                transform.CellBounds(new GridCell(runStart, z), out var minimum, out _);
                transform.CellBounds(new GridCell(x - 1, z), out _, out var maximum);
                boxes[count++] = new StaticBox(minimum, maximum);
                runStart = int.MinValue;
                if (count >= boxes.Length) break;
            }
        }
        return count;
    }

    /// <summary>
    /// Finds the constriction a body is leaning on: the tightest ground ahead of it, along
    /// the way it is trying to go. False if there is nothing narrow enough to blame.
    /// </summary>
    /// <remarks>
    /// A granted detour is only as good as the region it is told to avoid, and that region
    /// used to be a fixed distance in front of the body's nose. Against a queue several
    /// bodies deep wedged in a corner that is useless — the replan simply routes round the
    /// bubble and back into the same queue a metre later, which is why the unit that was
    /// offered a way out visibly fails to take one. What has to be excluded is the gap
    /// itself, wherever along the route it happens to be.
    /// <para>
    /// Searched along the body's own intent rather than along its stored route, because a
    /// body in this state frequently has no usable route left and its intent is the more
    /// honest statement of where it keeps trying to go. Straight-line, so a constriction
    /// round a bend is missed; the fallback then behaves as before.
    /// </para>
    /// </remarks>
    public bool TryFindObstructingAperture(
        Vector2 position,
        Vector2 direction,
        float agentRadius,
        out Vector2 aperture)
    {
        aperture = default;
        if (direction.LengthSquared() <= Epsilon) return false;
        direction = Vector2.Normalize(direction);

        var step = grid.Transform.CellSize * 0.5f;
        var steps = Math.Max(1, (int)MathF.Ceiling(ApertureSearchDistance / step));
        // Only ground tight enough to serialise a crowd counts as an aperture; a merely
        // crowded patch of open field is not something to route around.
        var constrictionLimit = agentRadius * 3f;
        var tightest = float.PositiveInfinity;
        var found = false;
        for (var i = 1; i <= steps; i++)
        {
            var sample = position + direction * (i * step);
            if (!grid.TryWorldToCell(sample, out var cell)) break;
            var clearance = grid.Clearance(cell);
            if (clearance >= constrictionLimit || clearance >= tightest) continue;
            tightest = clearance;
            aperture = grid.CellCenter(cell);
            found = true;
        }
        return found;
    }

    /// <summary>
    /// The axis a constriction actually lets a body through on, or false in open ground.
    /// </summary>
    /// <remarks>
    /// Found by asking, in each of eight directions, how far a body could travel before the
    /// ground stops taking it, and keeping the opposed pair with the longest shorter run —
    /// which for a gap in a wall is the way through it. Used to judge whether bodies are
    /// arriving at gaps square or oblique; a body that reaches a one-body-wide gap at forty
    /// degrees has to reorient inside the one place there is no room to.
    /// </remarks>
    public bool TryFindPassageAxis(Vector2 position, float agentRadius, out Vector2 axis)
    {
        axis = default;
        if (!grid.TryWorldToCell(position, out var cell)) return false;
        if (grid.Clearance(cell) >= agentRadius * 3f) return false;

        // Memoized: which way a gap lets a body through is a property of the cell and the
        // raster, not of the body standing in it, and probing eight directions per body per
        // tick would not be affordable otherwise.
        if (passageAxisRevision != grid.Revision)
        {
            passageAxes.Clear();
            passageAxisRevision = grid.Revision;
        }
        var key = (grid.Transform.Index(cell), (int)MathF.Round(agentRadius * 100f));
        if (passageAxes.TryGetValue(key, out var cached))
        {
            axis = cached;
            return cached != Vector2.Zero;
        }
        var found = ComputePassageAxis(position, agentRadius, out axis);
        passageAxes[key] = found ? axis : Vector2.Zero;
        return found;
    }

    private bool ComputePassageAxis(Vector2 position, float agentRadius, out Vector2 axis)
    {
        axis = default;
        var step = grid.Transform.CellSize * 0.5f;
        var reach = grid.Transform.CellSize * 8f;
        var bestRun = 0f;
        for (var i = 0; i < 4; i++)
        {
            var angle = i * MathF.PI / 4f;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var forward = FreeRun(position, direction, agentRadius, step, reach);
            var backward = FreeRun(position, -direction, agentRadius, step, reach);
            var run = MathF.Min(forward, backward);
            if (run <= bestRun) continue;
            bestRun = run;
            axis = direction;
        }
        return bestRun > 0f;
    }

    private float FreeRun(Vector2 from, Vector2 direction, float agentRadius, float step, float reach)
    {
        var travelled = 0f;
        while (travelled < reach)
        {
            travelled += step;
            var sample = from + direction * travelled;
            if (!grid.TryWorldToCell(sample, out var cell) || !grid.IsWalkable(cell, agentRadius))
            {
                return travelled - step;
            }
        }
        return reach;
    }

    /// <summary>
    /// Standoff, in metres, at which a body lines up on a gap's axis. Zero disables it.
    /// </summary>
    /// <remarks>
    /// Off by default, and deliberately: it does what it says — the gate's mean approach
    /// angle falls from 33 to 28 degrees and route length in the pen from 1.10x to 1.00x —
    /// and it costs stall time and direction stability to get there (pen red 37 to 62
    /// agent-seconds, gate dead stops 29 to 74, mean turn 4.8 to 5.8 degrees a tick).
    /// <para>
    /// That is a trade between how a crowd looks going into a gap and how long it takes to
    /// get through, and no headless measurement settles it. It ships as a slider rather than
    /// as a decision, because whoever is watching can weigh it and the benchmark cannot.
    /// </para>
    /// </remarks>
    internal static float ApertureApproachStandoff;

    /// <summary>
    /// Where a body should aim when a constriction lies ahead: a point on the gap's axis,
    /// rather than the gap's mouth.
    /// </summary>
    /// <remarks>
    /// Descending a cost field aims a body at the cheapest ground next to it, and next to a
    /// gap that is the gap's opening — so bodies converge on the mouth from every angle and
    /// arrive oblique. Measured at 33 degrees mean at a one-cell gate with 44% of samples
    /// past thirty, and a body at that angle presents nearly a fifth more width than it has
    /// and must reorient inside the one place there is no room to.
    /// <para>
    /// Aiming instead at a staging point on the axis, one standoff short of the gap, makes
    /// the body line up before it commits and then go straight through. It also stabilises
    /// the intent: a fixed geometric target does not flip between near-tied probe directions
    /// the way the cheapest-of-sixteen answer does, which is what made bodies cast about at a
    /// chokepoint instead of committing.
    /// </para>
    /// </remarks>
    public bool TryFindApertureApproach(
        Vector2 position,
        Vector2 heading,
        float agentRadius,
        out Vector2 aimPoint)
    {
        aimPoint = default;
        if (ApertureApproachStandoff <= 0f) return false;
        if (!TryFindObstructingAperture(position, heading, agentRadius, out var aperture)) return false;
        if (!TryFindPassageAxis(aperture, agentRadius, out var axis)) return false;
        // The axis is undirected; take the end the body is actually travelling toward.
        if (Vector2.Dot(axis, heading) < 0f) axis = -axis;
        var along = Vector2.Dot(aperture - position, axis);
        // Short of the gap, line up on its axis. At or inside it, aim through and out.
        aimPoint = along > ApertureApproachStandoff
            ? aperture - axis * ApertureApproachStandoff
            : aperture + axis * ApertureApproachStandoff;
        return true;
    }

    public bool IsPositionNavigable(Vector2 position, float agentRadius)
    {
        if (!terrain.IsBodyTraversable(position, agentRadius))
        {
            return false;
        }
        return IsPositionFreeOfPlacement(position, agentRadius);
    }

    public Vector2 FlowDirection(Vector2 position, Vector2 requestedGoal, float agentRadius)
    {
        if (!grid.TryWorldToCell(position, out var current) ||
            !grid.TryWorldToCell(requestedGoal, out var requestedGoalCell))
        {
            return Vector2.Zero;
        }
        var goal = grid.IsWalkable(requestedGoalCell, agentRadius)
            ? requestedGoalCell
            : FindNearestWalkable(requestedGoalCell, agentRadius);
        if (goal is not { } resolvedGoal) return Vector2.Zero;

        var costs = GetFlowField(resolvedGoal, agentRadius, congestion.Revision);

        var currentCost = costs[grid.Transform.Index(current)];
        var best = current;
        var bestCost = currentCost;
        foreach (var offset in NeighborOffsets)
        {
            var next = new GridCell(current.X + offset.X, current.Z + offset.Z);
            if (!CanTraverse(current, next, agentRadius)) continue;
            var nextCost = costs[grid.Transform.Index(next)];
            if (nextCost >= bestCost - 0.0001f) continue;
            best = next;
            bestCost = nextCost;
        }

        if (best == current)
        {
            var direct = requestedGoal - position;
            return direct.LengthSquared() > 0.0001f ? Vector2.Normalize(direct) : Vector2.Zero;
        }
        var direction = grid.CellCenter(best) - position;
        return direction.LengthSquared() > 0.0001f ? Vector2.Normalize(direction) : Vector2.Zero;
    }

    /// <summary>
    /// Smooth downhill direction of the shared cost field at an arbitrary point,
    /// or zero if the point has no route to the goal.
    /// </summary>
    /// <remarks>
    /// <see cref="FlowDirection"/> picks the cheapest neighbouring cell, so its
    /// answer is quantised to eight directions and flips the instant a body
    /// crosses a cell boundary — following it directly reproduces exactly the
    /// jerky, snapping motion that materialised waypoints used to cause. This
    /// bilinearly samples the field and takes a central difference instead, which
    /// is continuous everywhere. Unreachable cells read as a fixed penalty above
    /// the local cost rather than infinity, so the gradient also leans bodies away
    /// from walls instead of producing a NaN at the boundary.
    /// </remarks>
    public Vector2 SampleFlowGradient(
        Vector2 position,
        Vector2 requestedGoal,
        float agentRadius,
        int congestionRevision = int.MaxValue)
    {
        if (!grid.TryWorldToCell(position, out var current) ||
            !grid.TryWorldToCell(requestedGoal, out var requestedGoalCell))
        {
            return Vector2.Zero;
        }
        var goal = grid.IsWalkable(requestedGoalCell, agentRadius)
            ? requestedGoalCell
            : FindNearestWalkable(requestedGoalCell, agentRadius);
        if (goal is not { } resolvedGoal) return Vector2.Zero;

        var costs = GetFlowField(resolvedGoal, agentRadius, congestionRevision);
        var centerCost = costs[grid.Transform.Index(current)];
        if (!float.IsFinite(centerCost)) return Vector2.Zero;

        var blockedCost = centerCost + BlockedFlowSeconds;
        var probe = grid.Transform.CellSize;

        // Search directions rather than differentiating. A central difference
        // averages what it straddles, and between two comparable routes the cost
        // field has a ridge: sampling across it cancels the two descents and
        // leaves a resultant pointing *along* the ridge — into the empty ground
        // between the exits. A whole group following that walks into the corner
        // between two gates, bunches up, and then trickles out of whichever one
        // it ended up nearest. Taking the cheapest of a ring of probes cannot
        // average across a ridge, because it never combines two samples.
        var bestIndex = -1;
        var bestCost = centerCost;
        Span<float> ringCost = stackalloc float[FlowProbeCount];
        for (var i = 0; i < FlowProbeCount; i++)
        {
            var angle = i * MathF.Tau / FlowProbeCount;
            var offset = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * probe;
            ringCost[i] = SampleFlowCost(costs, position + offset, blockedCost);
            if (ringCost[i] >= bestCost) continue;
            bestCost = ringCost[i];
            bestIndex = i;
        }

        if (bestIndex >= 0)
        {
            // Refine between probes so the answer is continuous as the body moves.
            // The parabola through the winner and its neighbours shifts away from
            // whichever side is more expensive, which is the ridge if there is one.
            var previous = ringCost[(bestIndex - 1 + FlowProbeCount) % FlowProbeCount];
            var next = ringCost[(bestIndex + 1) % FlowProbeCount];
            var denominator = previous - 2f * bestCost + next;
            var shift = MathF.Abs(denominator) > Epsilon
                ? Math.Clamp(0.5f * (previous - next) / denominator, -0.5f, 0.5f)
                : 0f;
            var angle = (bestIndex + shift) * MathF.Tau / FlowProbeCount;
            return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }

        // Flat patch (equidistant routes, or the goal cell itself). Fall back to
        // the discrete answer so the body still commits to a direction.
        return FlowDirection(position, requestedGoal, agentRadius);
    }

    private float SampleFlowCost(float[] costs, Vector2 position, float blockedCost)
    {
        var local = (position - grid.Transform.Origin) / grid.Transform.CellSize -
                    new Vector2(0.5f);
        var x0 = (int)MathF.Floor(local.X);
        var z0 = (int)MathF.Floor(local.Y);
        var tx = local.X - x0;
        var tz = local.Y - z0;

        var c00 = FlowCostAt(costs, x0, z0, blockedCost);
        var c10 = FlowCostAt(costs, x0 + 1, z0, blockedCost);
        var c01 = FlowCostAt(costs, x0, z0 + 1, blockedCost);
        var c11 = FlowCostAt(costs, x0 + 1, z0 + 1, blockedCost);
        return float.Lerp(float.Lerp(c00, c10, tx), float.Lerp(c01, c11, tx), tz);
    }

    private float FlowCostAt(float[] costs, int x, int z, float blockedCost)
    {
        var cell = new GridCell(
            Math.Clamp(x, 0, grid.Width - 1),
            Math.Clamp(z, 0, grid.Height - 1));
        var cost = costs[grid.Transform.Index(cell)];
        return float.IsFinite(cost) ? cost : blockedCost;
    }

    /// <summary>
    /// Cost field for a goal, preferring a specific congestion revision.
    /// </summary>
    /// <remarks>
    /// Recent revisions are retained rather than discarded so that members of one
    /// crowd can be reading slightly different fields at the same moment. That is
    /// the point: with a single live field every unit adopts a new route on the
    /// same tick, and a crowd re-deciding in lockstep several times a second
    /// reads as indecision even when each decision is correct. A caller that asks
    /// for a revision no longer held simply gets the current one.
    /// </remarks>
    private float[] GetFlowField(
        GridCell goal,
        float agentRadius,
        int congestionRevision,
        bool chargeTurns = true)
    {
        var goalIndex = grid.Transform.Index(goal);
        var radiusKey = (int)MathF.Round(agentRadius * 100f);
        if (flowFields.TryGetValue(
                (goalIndex, radiusKey, grid.Revision, congestionRevision, chargeTurns),
                out var retained))
        {
            return retained;
        }

        var key = (goalIndex, radiusKey, grid.Revision, congestion.Revision, chargeTurns);
        if (flowFields.TryGetValue(key, out var costs)) return costs;
        // A navigation edit makes every older field unreachable, so those go
        // immediately. Congestion revisions are aged out instead of dropped, to
        // keep the staggered-adoption window above alive.
        foreach (var existing in flowFields.Keys
                     .Where(existing => existing.Nav != grid.Revision ||
                                        existing.Congestion < congestion.Revision - RetainedCongestionRevisions)
                     .ToArray())
        {
            flowFields.Remove(existing);
        }
        costs = BuildFlowField(goal, agentRadius, chargeTurns);
        FlowFieldBuilds++;
        flowFields[key] = costs;
        return costs;
    }

    private float[] BuildFlowField(GridCell goal, float agentRadius, bool chargeTurns = true)
    {
        var costs = new float[grid.Width * grid.Height];
        var closed = new bool[costs.Length];
        // The heading a cell's best-known route leaves it with. Exact turn-aware routing
        // wants the heading in the search state, which multiplies it by eight and puts
        // the group-order hitch back; recording one heading per cell keeps the search the
        // size it was. It is therefore an approximation — a route that would rather reach
        // a cell more slowly but better aligned cannot express that — and what it is for
        // is producing routes a body can follow, not proving optimality.
        var arrival = new int[costs.Length];
        Array.Fill(arrival, -1);
        Array.Fill(costs, float.PositiveInfinity);
        var goalIndex = grid.Transform.Index(goal);
        costs[goalIndex] = 0f;
        var open = new PriorityQueue<GridCell, float>();
        open.Enqueue(goal, 0f);

        while (open.TryDequeue(out var current, out _))
        {
            var currentIndex = grid.Transform.Index(current);
            if (closed[currentIndex]) continue;
            closed[currentIndex] = true;
            for (var directionIndex = 0; directionIndex < NeighborOffsets.Length; directionIndex++)
            {
                var offset = NeighborOffsets[directionIndex];
                var previous = new GridCell(current.X + offset.X, current.Z + offset.Z);
                if (!CanTraverseFlow(previous, current, agentRadius)) continue;
                var previousIndex = grid.Transform.Index(previous);
                if (closed[previousIndex]) continue;
                var stepCost = offset.X != 0 && offset.Z != 0 ? DiagonalCost : 1f;
                var surfaceCost = (grid.TraversalCost(current) + grid.TraversalCost(previous)) * 0.5f;
                var elevationCost = MathF.Abs(grid.HeightAt(previous) - grid.HeightAt(current)) *
                                    ClimbSecondsPerMetre;
                // Built backwards from the goal, so a body travelling this edge moves from
                // `previous` to `current` and the heading it carries into `current` is the
                // reverse of the offset being explored.
                var travelDirection = OppositeDirection(directionIndex);
                var turnSeconds = chargeTurns
                    ? TurnCost(travelDirection, arrival[currentIndex], current, agentRadius)
                    : 0f;
                var nextCost = costs[currentIndex] + stepCost * SecondsPerCell * surfaceCost +
                               elevationCost + CongestionCost(current, previous, turnSeconds) +
                               turnSeconds;
                if (nextCost >= costs[previousIndex]) continue;
                costs[previousIndex] = nextCost;
                arrival[previousIndex] = travelDirection;
                open.Enqueue(previous, nextCost);
            }
        }
        return costs;
    }

    private bool CanTraverseFlow(GridCell from, GridCell to, float agentRadius)
    {
        if (!grid.CanTraverse(from, to, agentRadius)) return false;
        var diagonal = from.X != to.X && from.Z != to.Z;
        if (!diagonal) return true;
        var firstCorner = new GridCell(to.X, from.Z);
        var secondCorner = new GridCell(from.X, to.Z);
        return grid.CanTraverse(from, firstCorner, agentRadius) &&
               grid.CanTraverse(from, secondCorner, agentRadius) &&
               grid.CanTraverse(firstCorner, to, agentRadius) &&
               grid.CanTraverse(secondCorner, to, agentRadius);
    }

    /// <summary>
    /// Whether a body at <paramref name="position"/> clears every placed block.
    /// </summary>
    /// <remarks>
    /// This is the single hottest predicate in the simulation: it is consulted per
    /// sample of every swept step, per neighbour of every A* expansion, and per
    /// candidate of the solver's terrain fallback. It used to walk a cached list of
    /// every occupied cell on the map, so its cost scaled with how much had been
    /// built rather than with how much was nearby — a few hundred box tests to
    /// answer a question about one square metre. The placement grid can say which
    /// cells could possibly reach the body, and only those are tested.
    /// </remarks>
    public bool IsPositionFreeOfPlacement(Vector2 position, float agentRadius)
    {
        var halfExtent = placement.Transform.CellSize * 0.5f - 0.025f;
        PlacementCellRange(position, position, halfExtent + agentRadius, out var low, out var high);
        for (var z = low.Z; z <= high.Z; z++)
        for (var x = low.X; x <= high.X; x++)
        {
            var cell = new GridCell(x, z);
            if (!placement.IsOccupied(cell)) continue;
            var center = placement.Transform.CellCenter(cell);
            var closest = Vector2.Clamp(
                position,
                center - new Vector2(halfExtent),
                center + new Vector2(halfExtent));
            if (Vector2.DistanceSquared(position, closest) <
                agentRadius * agentRadius - 0.000001f)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Range of placement cells whose boxes, grown by <paramref name="reach"/>, can
    /// overlap the given world bounds. Clamped to the grid, so cells outside the map
    /// are skipped rather than treated as occupied.
    /// </summary>
    private void PlacementCellRange(
        Vector2 minimum,
        Vector2 maximum,
        float reach,
        out GridCell low,
        out GridCell high)
    {
        var transform = placement.Transform;
        var lowLocal = (minimum - new Vector2(reach) - transform.Origin) / transform.CellSize;
        var highLocal = (maximum + new Vector2(reach) - transform.Origin) / transform.CellSize;
        low = new GridCell(
            Math.Max(0, (int)MathF.Floor(lowLocal.X)),
            Math.Max(0, (int)MathF.Floor(lowLocal.Y)));
        high = new GridCell(
            Math.Min(transform.Width - 1, (int)MathF.Ceiling(highLocal.X)),
            Math.Min(transform.Height - 1, (int)MathF.Ceiling(highLocal.Y)));
    }

    private bool SegmentAvoidsPlacement(
        Vector2 start,
        Vector2 end,
        float expansion,
        bool allowEscapeFromStart)
    {
        var halfCell = placement.Transform.CellSize * 0.5f + expansion;
        PlacementCellRange(
            Vector2.Min(start, end), Vector2.Max(start, end), halfCell,
            out var low, out var high);
        for (var z = low.Z; z <= high.Z; z++)
        for (var x = low.X; x <= high.X; x++)
        {
            var cell = new GridCell(x, z);
            if (!placement.IsOccupied(cell)) continue;
            var center = placement.Transform.CellCenter(cell);
            var minimum = center - new Vector2(halfCell);
            var maximum = center + new Vector2(halfCell);
            if (allowEscapeFromStart && PointInsideAabb(start, minimum, maximum))
            {
                var startOffset = start - center;
                var endOffset = end - center;
                if (endOffset.LengthSquared() > startOffset.LengthSquared() + 0.000001f) continue;
            }
            if (SegmentIntersectsAabb(start, end, minimum, maximum)) return false;
        }
        return true;
    }

    private static bool PointInsideAabb(Vector2 point, Vector2 minimum, Vector2 maximum) =>
        point.X >= minimum.X && point.X <= maximum.X &&
        point.Y >= minimum.Y && point.Y <= maximum.Y;

    private GridCell? FindNearestWalkable(
        GridCell origin,
        float agentRadius,
        Vector2? searchCenter = null,
        float maximumDistance = float.PositiveInfinity)
    {
        var visited = new bool[grid.Width * grid.Height];
        var queue = new Queue<GridCell>();
        queue.Enqueue(origin);
        visited[grid.Transform.Index(origin)] = true;

        while (queue.TryDequeue(out var current))
        {
            var withinSearchRadius = searchCenter is not { } center ||
                                     Vector2.Distance(grid.CellCenter(current), center) <= maximumDistance;
            if (withinSearchRadius && grid.IsWalkable(current, agentRadius)) return current;
            foreach (var offset in NeighborOffsets.Take(4))
            {
                var next = new GridCell(current.X + offset.X, current.Z + offset.Z);
                if (!grid.Contains(next)) continue;
                if (searchCenter is { } boundedCenter &&
                    Vector2.Distance(grid.CellCenter(next), boundedCenter) >
                    maximumDistance + grid.Transform.CellSize)
                {
                    continue;
                }
                var index = grid.Transform.Index(next);
                if (visited[index]) continue;
                visited[index] = true;
                queue.Enqueue(next);
            }
        }
        return null;
    }

    private GridCell? FindRecoveryStart(
        GridCell origin,
        float agentRadius,
        Vector2 start,
        Vector2 goal,
        float maximumDistance)
    {
        var searchCells = (int)MathF.Ceiling(maximumDistance / grid.Transform.CellSize);
        var goalDirection = goal - start;
        goalDirection = goalDirection.LengthSquared() > 0.0001f
            ? Vector2.Normalize(goalDirection)
            : Vector2.Zero;
        GridCell? best = null;
        var bestScore = float.PositiveInfinity;

        for (var z = origin.Z - searchCells; z <= origin.Z + searchCells; z++)
        for (var x = origin.X - searchCells; x <= origin.X + searchCells; x++)
        {
            var candidate = new GridCell(x, z);
            if (!grid.IsWalkable(candidate, agentRadius)) continue;
            var delta = grid.CellCenter(candidate) - start;
            var distance = delta.Length();
            if (distance > maximumDistance) continue;
            if (!IsInitialBodyStepClear(start, grid.CellCenter(candidate), agentRadius)) continue;

            // A recovery cell is not merely the closest valid cell: at a narrow
            // portal that can make two displaced units converge on its center.
            // Prefer equally-near cells that make progress toward the destination.
            var score = distance - Vector2.Dot(delta, goalDirection) * 1.10f;
            if (score > bestScore + 0.0001f) continue;
            if (MathF.Abs(score - bestScore) <= 0.0001f && best is { } currentBest &&
                grid.Transform.Index(candidate) >= grid.Transform.Index(currentBest))
            {
                continue;
            }
            best = candidate;
            bestScore = score;
        }
        return best;
    }

    private List<GridCell>? FindCellPath(
        GridCell start,
        GridCell goal,
        float agentRadius,
        Vector2? congestionAvoidanceCenter,
        float[]? additionalNavigationCosts)
    {
        // Reused across calls. A* was allocating three full-grid arrays every
        // time it ran, and once every obstructed unit started re-planning against
        // congestion that became the most expensive phase in the tick.
        var count = grid.Width * grid.Height;
        if (searchCameFrom.Length != count)
        {
            searchCameFrom = new int[count];
            searchCost = new float[count];
            searchClosed = new bool[count];
            searchArrival = new int[count];
        }
        var cameFrom = searchCameFrom;
        var cost = searchCost;
        var closed = searchClosed;
        Array.Fill(cameFrom, -1);
        Array.Fill(cost, float.PositiveInfinity);
        Array.Fill(searchArrival, -1);
        Array.Clear(closed);
        searchQueue.Clear();

        var startIndex = grid.Transform.Index(start);
        var goalIndex = grid.Transform.Index(goal);
        var open = searchQueue;
        cost[startIndex] = 0f;
        // Costs are seconds now, so the heuristic has to be too: cells to go,
        // at the speed of the quickest ground that exists. Anything larger stops
        // being a lower bound and A* would return non-optimal routes.
        var heuristicScale = SecondsPerCell *
                             (terrain.Revision == 0 ? 1f : TerrainSurfaceRules.MinimumPathCost);
        open.Enqueue(start, Heuristic(start, goal) * heuristicScale);

        while (open.TryDequeue(out var current, out _))
        {
            var currentIndex = grid.Transform.Index(current);
            if (closed[currentIndex]) continue;
            closed[currentIndex] = true;
            if (currentIndex == goalIndex) return Reconstruct(cameFrom, startIndex, goalIndex);

            for (var directionIndex = 0; directionIndex < NeighborOffsets.Length; directionIndex++)
            {
                var offset = NeighborOffsets[directionIndex];
                var next = new GridCell(current.X + offset.X, current.Z + offset.Z);
                if (!CanTraverse(current, next, agentRadius)) continue;
                var nextIndex = grid.Transform.Index(next);
                if (closed[nextIndex]) continue;
                var stepCost = offset.X != 0 && offset.Z != 0 ? DiagonalCost : 1f;
                var surfaceCost = (grid.TraversalCost(current) + grid.TraversalCost(next)) * 0.5f;
                var elevationCost = MathF.Abs(grid.HeightAt(next) - grid.HeightAt(current)) *
                                    ClimbSecondsPerMetre;
                var additionalCost = additionalNavigationCosts is null ? 0f : additionalNavigationCosts[nextIndex];
                var turnSeconds = TurnCost(
                    searchArrival[currentIndex], directionIndex, current, agentRadius);
                var nextCost = cost[currentIndex] + stepCost * SecondsPerCell * surfaceCost +
                               elevationCost +
                               PointCongestionCost(next, congestionAvoidanceCenter) +
                               CongestionCost(current, next, turnSeconds) +
                               additionalCost +
                               turnSeconds;
                if (nextCost >= cost[nextIndex]) continue;
                cost[nextIndex] = nextCost;
                searchArrival[nextIndex] = directionIndex;
                cameFrom[nextIndex] = currentIndex;
                open.Enqueue(next, nextCost + Heuristic(next, goal) * heuristicScale);
            }
        }
        return null;
    }

    /// <summary>
    /// Whether a body of <paramref name="agentRadius"/> stands clear of terrain and
    /// placed blocks at the centre of <paramref name="cell"/>. Memoized per raster
    /// revision; see <see cref="cellCenterAdmission"/>.
    /// </summary>
    private bool CellCenterAdmitsBody(GridCell cell, float agentRadius)
    {
        if (!grid.Contains(cell)) return false;
        if (cellCenterAdmissionRevision != grid.Revision)
        {
            cellCenterAdmission.Clear();
            cellCenterAdmissionRevision = grid.Revision;
        }
        var radiusKey = (int)MathF.Round(agentRadius * 100f);
        if (!cellCenterAdmission.TryGetValue(radiusKey, out var memo))
        {
            cellCenterAdmission[radiusKey] = memo = new byte[grid.Width * grid.Height];
        }

        var index = grid.Transform.Index(cell);
        if (memo[index] != 0) return memo[index] == 1;
        var center = grid.CellCenter(cell);
        var admitted = terrain.IsBodyTraversable(center, agentRadius) &&
                       IsPositionFreeOfPlacement(center, agentRadius);
        memo[index] = admitted ? (byte)1 : (byte)2;
        return admitted;
    }

    private bool CanTraverse(GridCell from, GridCell to, float agentRadius)
    {
        if (!CellCenterAdmitsBody(to, agentRadius)) return false;
        if (terrain.Revision == 0)
        {
            if (!grid.IsWalkable(to, agentRadius)) return false;
            var flatDiagonal = from.X != to.X && from.Z != to.Z;
            return !flatDiagonal ||
                   grid.IsWalkable(new GridCell(to.X, from.Z), agentRadius) &&
                   grid.IsWalkable(new GridCell(from.X, to.Z), agentRadius);
        }

        if (!grid.CanTraverse(from, to, agentRadius)) return false;
        var diagonal = from.X != to.X && from.Z != to.Z;
        if (!diagonal) return true;
        var firstCorner = new GridCell(to.X, from.Z);
        var secondCorner = new GridCell(from.X, to.Z);
        return grid.CanTraverse(from, firstCorner, agentRadius) &&
               grid.CanTraverse(from, secondCorner, agentRadius) &&
               grid.CanTraverse(firstCorner, to, agentRadius) &&
               grid.CanTraverse(secondCorner, to, agentRadius);
    }

    private Vector2[] SmoothPath(
        Vector2 start,
        Vector2 destination,
        IReadOnlyList<GridCell> cells,
        float agentRadius,
        Vector2? congestionAvoidanceCenter,
        float[]? additionalNavigationCosts,
        bool includeFirstCell,
        float maximumSegmentLength = float.PositiveInfinity)
    {
        var candidates = new List<Vector2>(cells.Count + 1);
        for (var i = includeFirstCell ? 0 : 1; i < cells.Count; i++) candidates.Add(grid.CellCenter(cells[i]));
        if (candidates.Count == 0 || candidates[^1] != destination) candidates.Add(destination);

        // A body recovering from inside a block starts its route overlapping
        // one. Its very first segment has to be judged by the escape rule, or
        // smoothing rejects every candidate and discards an otherwise valid A*
        // route, leaving the unit permanently embedded with no destination.
        var escapingStart = !IsPositionFreeOfPlacement(start, agentRadius);
        var result = new List<Vector2>();
        var anchor = start;
        var cursor = 0;
        while (cursor < candidates.Count)
        {
            var farthest = -1;
            for (var candidate = cursor; candidate < candidates.Count; candidate++)
            {
                var segmentCap = terrain.Revision > 0
                    ? MathF.Min(maximumSegmentLength, grid.Transform.CellSize * 2f)
                    : maximumSegmentLength;
                if (Vector2.Distance(anchor, candidates[candidate]) > segmentCap) break;
                if (!SegmentIsBodySafe(
                        anchor, candidates[candidate], agentRadius,
                        includeFirstCell, cursor, candidate, escapingStart) ||
                    !SegmentPreservesTerrainCost(anchor, candidates[candidate]) ||
                    !SegmentAvoidsAdditionalCost(anchor, candidates[candidate], additionalNavigationCosts) ||
                    !SegmentAvoidsCongestion(anchor, candidates[candidate], congestionAvoidanceCenter)) break;
                farthest = candidate;
            }

            if (farthest < cursor)
            {
                // Cost, reservation and congestion filters express a preference:
                // they exist so smoothing cannot erase a bend A* chose on purpose.
                // They must not be able to destroy the route. A diagonal hop
                // between two road cells clips a cheaper neighbour's corner, and
                // failing the whole path there stranded the unit with no route at
                // all. The single-cell step A* already validated stays available.
                if (!SegmentIsBodySafe(
                        anchor, candidates[cursor], agentRadius,
                        includeFirstCell, cursor, cursor, escapingStart))
                {
                    // Never manufacture a waypoint that the body cannot actually
                    // reach from the current anchor. The caller may retry from
                    // another recovery cell, but an invalid first segment causes a
                    // permanent steer/replan loop at terrain corners.
                    return Array.Empty<Vector2>();
                }
                farthest = cursor;
            }
            result.Add(candidates[farthest]);
            anchor = candidates[farthest];
            cursor = farthest + 1;
        }
        return result.ToArray();
    }

    private bool SegmentIsBodySafe(
        Vector2 anchor,
        Vector2 candidate,
        float agentRadius,
        bool includeFirstCell,
        int cursor,
        int candidateIndex,
        bool allowPlacementEscape) =>
        includeFirstCell && cursor == 0 && candidateIndex == 0
            ? IsContinuousStepClear(anchor, candidate, agentRadius, allowPlacementEscape)
            : IsDirectPathClear(anchor, candidate, agentRadius);

    private bool SegmentAvoidsAdditionalCost(Vector2 start, Vector2 end, float[]? additionalNavigationCosts)
    {
        if (additionalNavigationCosts is null) return true;
        if (additionalNavigationCosts.Length != grid.Width * grid.Height)
        {
            throw new ArgumentException("Additional navigation-cost dimensions do not match the navigation grid.");
        }

        // A* may deliberately bend around a crowded patch. Do not let geometric
        // smoothing erase that decision by drawing a straight segment back
        // through the occupied cells. Endpoints are excluded so an agent can
        // still leave a crowded start or approach a contested destination.
        var distance = Vector2.Distance(start, end);
        var samples = Math.Max(1, (int)MathF.Ceiling(distance / (grid.Transform.CellSize * 0.40f)));
        for (var sample = 1; sample < samples; sample++)
        {
            var position = Vector2.Lerp(start, end, sample / (float)samples);
            if (!grid.TryWorldToCell(position, out var cell)) return false;
            if (additionalNavigationCosts[grid.Transform.Index(cell)] > 0.25f) return false;
        }
        return true;
    }

    private bool SegmentPreservesTerrainCost(Vector2 start, Vector2 end)
    {
        if (terrain.Revision == 0) return true;
        var allowedCost = MathF.Max(terrain.PathCost(start), terrain.PathCost(end)) + 0.001f;
        var distance = Vector2.Distance(start, end);
        var samples = Math.Max(1, (int)MathF.Ceiling(distance / (grid.Transform.CellSize * 0.40f)));
        for (var sample = 1; sample < samples; sample++)
        {
            var position = Vector2.Lerp(start, end, sample / (float)samples);
            if (terrain.PathCost(position) > allowedCost) return false;
        }
        return true;
    }

    private float PointCongestionCost(GridCell cell, Vector2? center)
    {
        if (center is not { } congestionCenter) return 0f;
        var distance = Vector2.Distance(grid.CellCenter(cell), congestionCenter);
        if (distance >= CongestionAvoidanceRadius) return 0f;
        var proximity = 1f - distance / CongestionAvoidanceRadius;
        return proximity * proximity * DetourAvoidanceSeconds;
    }

    private static bool SegmentAvoidsCongestion(Vector2 start, Vector2 end, Vector2? center)
    {
        if (center is not { } congestionCenter) return true;
        var radiusSquared = CongestionAvoidanceRadius * CongestionAvoidanceRadius;
        var startOffset = start - congestionCenter;
        var startDistanceSquared = startOffset.LengthSquared();
        if (startDistanceSquared < radiusSquared)
        {
            var direction = end - start;
            return Vector2.Dot(direction, startOffset) >= 0f &&
                   Vector2.DistanceSquared(end, congestionCenter) > startDistanceSquared;
        }

        if (Vector2.DistanceSquared(end, congestionCenter) < radiusSquared) return false;
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared < 0.000001f) return startDistanceSquared >= radiusSquared;
        var time = Math.Clamp(Vector2.Dot(congestionCenter - start, segment) / lengthSquared, 0f, 1f);
        var closest = start + segment * time;
        return Vector2.DistanceSquared(closest, congestionCenter) >= radiusSquared;
    }

    private List<GridCell> Reconstruct(int[] cameFrom, int startIndex, int goalIndex)
    {
        var reversed = new List<GridCell>();
        var current = goalIndex;
        while (current != startIndex)
        {
            reversed.Add(grid.Transform.Cell(current));
            current = cameFrom[current];
            if (current < 0) return new List<GridCell>();
        }
        reversed.Add(grid.Transform.Cell(startIndex));
        reversed.Reverse();
        return reversed;
    }

    private static bool SegmentIntersectsAabb(Vector2 start, Vector2 end, Vector2 minimum, Vector2 maximum)
    {
        var direction = end - start;
        var minimumTime = 0f;
        var maximumTime = 1f;
        return ClipAxis(start.X, direction.X, minimum.X, maximum.X, ref minimumTime, ref maximumTime) &&
               ClipAxis(start.Y, direction.Y, minimum.Y, maximum.Y, ref minimumTime, ref maximumTime);
    }

    private static bool ClipAxis(float start, float direction, float minimum, float maximum, ref float minimumTime, ref float maximumTime)
    {
        if (MathF.Abs(direction) < 0.00001f) return start >= minimum && start <= maximum;
        var first = (minimum - start) / direction;
        var second = (maximum - start) / direction;
        if (first > second) (first, second) = (second, first);
        minimumTime = MathF.Max(minimumTime, first);
        maximumTime = MathF.Min(maximumTime, second);
        return minimumTime <= maximumTime;
    }

    /// <summary>Index of the step direction opposite the given one.</summary>
    private static int OppositeDirection(int directionIndex)
    {
        var offset = NeighborOffsets[directionIndex];
        for (var i = 0; i < NeighborOffsets.Length; i++)
        {
            if (NeighborOffsets[i].X == -offset.X && NeighborOffsets[i].Z == -offset.Z) return i;
        }
        return directionIndex;
    }

    private static float Heuristic(GridCell from, GridCell to)
    {
        var dx = Math.Abs(from.X - to.X);
        var dz = Math.Abs(from.Z - to.Z);
        return Math.Max(dx, dz) + (DiagonalCost - 1f) * Math.Min(dx, dz);
    }
}
