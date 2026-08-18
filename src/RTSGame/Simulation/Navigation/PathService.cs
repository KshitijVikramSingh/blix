using System.Numerics;
using RTSGame.Simulation.Placement;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Simulation.Navigation;

internal readonly record struct PathResult(
    Vector2[] Waypoints,
    Vector2 Destination,
    GridCell[] RouteCells);

internal sealed class PathService
{
    public bool HasTerrainVariation => terrain.Revision > 0;
    private const float DiagonalCost = 1.41421356f;
    private const float CongestionAvoidanceRadius = 3f;
    private const float CongestionPenalty = 10f;
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
    private const float CongestionSecondsPerPressure = 0.40f;
    /// <summary>Extra seconds charged per metre of climb.</summary>
    private const float ClimbSecondsPerMetre = 0.60f;
    /// <summary>Seconds to cross one cell of open ground at the reference speed.</summary>
    private float SecondsPerCell => grid.Transform.CellSize / ReferenceSpeed;
    /// <summary>Cost an unreachable cell reports when sampling the flow field.</summary>
    private const float BlockedFlowPenalty = 12f;
    /// <summary>How many past congestion revisions stay resident for staggered adoption.</summary>
    private const int RetainedCongestionRevisions = 3;
    /// <summary>Directions probed when reading the cost field's downhill.</summary>
    private const int FlowProbeCount = 16;
    /// <summary>How much further than the straight line a slot may be, before it is judged unconnected.</summary>
    private const float SlotDetourTolerance = 2.0f;
    private const float SlotDetourSlack = 0.45f;
    private const float Epsilon = 0.00001f;
    private static readonly GridCell[] NeighborOffsets =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
        new(1, 1), new(1, -1), new(-1, 1), new(-1, -1),
    };

    private readonly TerrainMap terrain;
    private readonly PlacementGrid placement;
    private readonly NavigationGrid grid;
    private readonly CongestionField congestion;
    private readonly List<Vector2> occupiedPlacementCenters = new();
    private readonly Dictionary<(int Goal, int Radius, int Nav, int Congestion), float[]> flowFields = new();
    private int cachedPlacementRevision = -1;
    private int[] searchCameFrom = Array.Empty<int>();
    private float[] searchCost = Array.Empty<float>();
    private bool[] searchClosed = Array.Empty<bool>();
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
    private float CongestionCost(GridCell from, GridCell to)
    {
        var travel = grid.CellCenter(to) - grid.CellCenter(from);
        if (travel.LengthSquared() > 0.0001f) travel = Vector2.Normalize(travel);
        var pressure = congestion.At(from) * congestion.DirectionalFactor(from, travel) +
                       congestion.At(to) * congestion.DirectionalFactor(to, travel);
        return pressure * 0.5f * CongestionSecondsPerPressure;
    }

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
    public bool TryOptimalTravelTime(Vector2 goalPosition, Vector2 from, float agentRadius, out float seconds)
    {
        seconds = 0f;
        if (!grid.TryWorldToCell(goalPosition, out var goalCell)) return false;
        if (!grid.TryWorldToCell(from, out var fromCell)) return false;
        var resolved = grid.IsWalkable(goalCell, agentRadius)
            ? goalCell
            : FindNearestWalkable(goalCell, agentRadius);
        if (resolved is not { } goal) return false;
        var costs = GetFlowField(goal, agentRadius, congestion.Revision);
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
            var bottleneckCost = clearanceMargin < 0.80f ? 2.25f : 0f;
            routeCosts[grid.Transform.Index(cell)] += bottleneckCost;
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
        // fail both the wall-detour and chokepoint-separation tests. What it does
        // not need is to walk the whole placement grid — only cells that are
        // actually occupied can block anything, and that list is already cached.
        RefreshOccupiedPlacementCenters();
        var halfCell = placement.Transform.CellSize * 0.5f + expansion;
        foreach (var center in occupiedPlacementCenters)
        {
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

        var blockedCost = centerCost + BlockedFlowPenalty;
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
    private float[] GetFlowField(GridCell goal, float agentRadius, int congestionRevision)
    {
        var goalIndex = grid.Transform.Index(goal);
        var radiusKey = (int)MathF.Round(agentRadius * 100f);
        if (flowFields.TryGetValue((goalIndex, radiusKey, grid.Revision, congestionRevision), out var retained))
        {
            return retained;
        }

        var key = (goalIndex, radiusKey, grid.Revision, congestion.Revision);
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
        costs = BuildFlowField(goal, agentRadius);
        FlowFieldBuilds++;
        flowFields[key] = costs;
        return costs;
    }

    private float[] BuildFlowField(GridCell goal, float agentRadius)
    {
        var costs = new float[grid.Width * grid.Height];
        var closed = new bool[costs.Length];
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
            foreach (var offset in NeighborOffsets)
            {
                var previous = new GridCell(current.X + offset.X, current.Z + offset.Z);
                if (!CanTraverseFlow(previous, current, agentRadius)) continue;
                var previousIndex = grid.Transform.Index(previous);
                if (closed[previousIndex]) continue;
                var stepCost = offset.X != 0 && offset.Z != 0 ? DiagonalCost : 1f;
                var surfaceCost = (grid.TraversalCost(current) + grid.TraversalCost(previous)) * 0.5f;
                var elevationCost = MathF.Abs(grid.HeightAt(previous) - grid.HeightAt(current)) *
                                    ClimbSecondsPerMetre;
                var nextCost = costs[currentIndex] + stepCost * SecondsPerCell * surfaceCost +
                               elevationCost + CongestionCost(current, previous);
                if (nextCost >= costs[previousIndex]) continue;
                costs[previousIndex] = nextCost;
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

    public bool IsPositionFreeOfPlacement(Vector2 position, float agentRadius)
    {
        RefreshOccupiedPlacementCenters();
        var halfExtent = placement.Transform.CellSize * 0.5f - 0.025f;
        foreach (var center in occupiedPlacementCenters)
        {
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

    private bool SegmentAvoidsPlacement(
        Vector2 start,
        Vector2 end,
        float expansion,
        bool allowEscapeFromStart)
    {
        RefreshOccupiedPlacementCenters();
        var halfCell = placement.Transform.CellSize * 0.5f + expansion;
        foreach (var center in occupiedPlacementCenters)
        {
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

    private void RefreshOccupiedPlacementCenters()
    {
        if (cachedPlacementRevision == placement.Revision) return;
        occupiedPlacementCenters.Clear();
        for (var z = 0; z < placement.Transform.Height; z++)
        for (var x = 0; x < placement.Transform.Width; x++)
        {
            var cell = new GridCell(x, z);
            if (placement.IsOccupied(cell))
                occupiedPlacementCenters.Add(placement.Transform.CellCenter(cell));
        }
        cachedPlacementRevision = placement.Revision;
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
        }
        var cameFrom = searchCameFrom;
        var cost = searchCost;
        var closed = searchClosed;
        Array.Fill(cameFrom, -1);
        Array.Fill(cost, float.PositiveInfinity);
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

            foreach (var offset in NeighborOffsets)
            {
                var next = new GridCell(current.X + offset.X, current.Z + offset.Z);
                if (!CanTraverse(current, next, agentRadius)) continue;
                var nextIndex = grid.Transform.Index(next);
                if (closed[nextIndex]) continue;
                var stepCost = offset.X != 0 && offset.Z != 0 ? DiagonalCost : 1f;
                var surfaceCost = (grid.TraversalCost(current) + grid.TraversalCost(next)) * 0.5f;
                var elevationCost = MathF.Abs(grid.HeightAt(next) - grid.HeightAt(current)) *
                                    ClimbSecondsPerMetre;
                var additionalCost = additionalNavigationCosts is null ? 0f : additionalNavigationCosts[nextIndex];
                var nextCost = cost[currentIndex] + stepCost * SecondsPerCell * surfaceCost +
                               elevationCost +
                               PointCongestionCost(next, congestionAvoidanceCenter) +
                               CongestionCost(current, next) +
                               additionalCost;
                if (nextCost >= cost[nextIndex]) continue;
                cost[nextIndex] = nextCost;
                cameFrom[nextIndex] = currentIndex;
                open.Enqueue(next, nextCost + Heuristic(next, goal) * heuristicScale);
            }
        }
        return null;
    }

    private bool CanTraverse(GridCell from, GridCell to, float agentRadius)
    {
        var destinationCenter = grid.CellCenter(to);
        if (!terrain.IsBodyTraversable(destinationCenter, agentRadius) ||
            !IsPositionFreeOfPlacement(destinationCenter, agentRadius))
        {
            return false;
        }
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
        return proximity * proximity * CongestionPenalty;
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

    private static float Heuristic(GridCell from, GridCell to)
    {
        var dx = Math.Abs(from.X - to.X);
        var dz = Math.Abs(from.Z - to.Z);
        return Math.Max(dx, dz) + (DiagonalCost - 1f) * Math.Min(dx, dz);
    }
}
