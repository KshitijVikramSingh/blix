using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Navigation;

/// <summary>What the routing hierarchy costs in route quality, against the flat search.</summary>
internal readonly record struct RoutingFidelity(
    int ReachableCells,
    int UnreachableCells,
    float MeanRatio,
    float NinetyNinthRatio,
    float WorstRatio,
    GridCell WorstCell,
    int SettledNodes,
    int RefinedRegions,
    long RegionSearches);


/// <summary>
/// The routing hierarchy: region partition, portal graph, an abstract search over portal
/// sides, and cost fields stored as one region-sized tile at a time.
/// </summary>
/// <remarks>
/// The flat field this replaces was a Dijkstra over every cell of the map, allocated in
/// full, thrown away on every navigation edit and rebuilt on every congestion revision. On
/// the 30 m world that is 3,600 cells and free. Measured on a 1200 m world it is 5.76M
/// cells, 4.1 s for the first route after a single move order, and ~390 ms of every
/// subsequent tick — the number that made Session 1's two area-scaled costs look like
/// rounding error.
/// <para>
/// Nothing here is eager. The abstract search is a Dijkstra from the goal that is resumed
/// only as far as some caller actually needs, region ingress costs are computed the first
/// time a node is expanded and cached against that region's congestion, and a tile is
/// refined the first time something asks what a cell in it costs. What a route costs is
/// therefore proportional to how far the bodies asking are from the goal, rather than to
/// how big the map happens to be.
/// </para>
/// </remarks>
internal sealed partial class PathService
{
    private readonly RegionPartition partition;
    private readonly Dictionary<(int Nav, int Radius), PortalGraph> portalGraphs = new();
    // Cost from every node of a region to one node of it, without leaving the region.
    // Keyed by that region's own congestion stamp, so a jam in one corner of the map does
    // not invalidate the ingress costs of every region on it.
    private readonly Dictionary<(int Node, int Nav, int Radius, int Stamp, bool Turns), float[]>
        nodeIngress = new();
    private readonly PriorityQueue<GridCell, float> regionQueue = new();
    // One region's working set, reused. Every search allocated its own and the searches
    // are the unit of work here, so this was three 16 KB arrays per crossing considered.
    private readonly bool[] regionClosed = new bool[RegionPartition.CellsPerRegion];
    private readonly int[] regionArrival = new int[RegionPartition.CellsPerRegion];
    private readonly float[] regionScratch = new float[RegionPartition.CellsPerRegion];

    /// <summary>Regions the partition divides this map into.</summary>
    public int RegionCount => partition.Count;
    /// <summary>Crossings in the portal graph for a standard body, for diagnostics.</summary>
    public int PortalCountFor(float agentRadius) => Portals(agentRadius).PortalCount;
    /// <summary>Region ingress searches currently held.</summary>
    public int CachedIngressCount => nodeIngress.Count;
    /// <summary>Region-local searches run since construction.</summary>
    public long RegionSearches { get; private set; }
    /// <summary>Tiles refined since construction.</summary>
    public long TileRefinements { get; private set; }

    private static int RadiusKey(float agentRadius) => (int)MathF.Round(agentRadius * 100f);

    /// <summary>
    /// Loosest bound on what crossing one region can cost, used as the horizon the
    /// abstract search is allowed to stop at.
    /// </summary>
    /// <remarks>
    /// A region's diagonal walked at reference speed, with half again for the fact that a
    /// route inside a region is not a straight line. It is what separates "this crossing
    /// might still turn out to be the best way out of here" from "this crossing is so far
    /// behind that no cell in this region could prefer it", and the search stops at the
    /// second — see <see cref="FlowField.SettleRegion"/> for why being exact instead is
    /// unaffordable.
    /// </remarks>
    /// <summary>
    /// How much further past the cheapest way out of a region the abstract search keeps
    /// looking, in fine cells. Zero stops the moment that crossing is certain.
    /// </summary>
    /// <remarks>
    /// It started at a region and a half — the geometric bound on what crossing a region
    /// could cost, so that no alternative crossing which might beat the cheapest one was
    /// ever missed. Then it was measured, on a 200 m map of staggered walls, against the
    /// flat search it approximates:
    /// <code>
    /// span   0 cells | mean 1.0064 | p99 1.0621 | worst 1.678 | lost 0 | 643 searches
    /// span  32 cells | mean 1.0051 | p99 1.0621 | worst 1.678 | lost 0 | 685 searches
    /// span  96 cells | mean 1.0051 | p99 1.0621 | worst 1.678 | lost 0 | 693 searches
    /// </code>
    /// The horizon buys thirteen ten-thousandths of mean route cost and changes neither the
    /// tail nor the worst case, because a tile is seeded from every crossing the search has
    /// <em>priced</em>, not only those it has settled, and an unsettled price is an upper
    /// bound that is already close. On a 1200 m map it cost four and a half times the work:
    /// a move order at 460 ms against 102 ms. So it is zero, and the dial is left here with
    /// its numbers rather than deleted, because the next person to wonder whether the search
    /// is stopping too early should re-run <c>--routingtest</c> rather than re-reason it.
    /// </remarks>
    internal static float RegionSpanCells;

    private float RegionSpanSeconds => RegionSpanCells * SecondsPerCell;

    /// <summary>
    /// Optimistic seconds from a cell to the nearest cell of a region, for the abstract
    /// search's heuristic.
    /// </summary>
    /// <remarks>
    /// Octile distance over the fastest ground the game has — road, at 1.1x — and nothing
    /// else. Every other term a real route pays is additive and positive: rough ground,
    /// climbs, turns, congestion. So this can never over-state the true cost, which is the
    /// one property the heuristic has to have for the crossings it settles to be settled
    /// exactly rather than merely plausibly.
    /// </remarks>
    internal float RegionApproachSeconds(
        GridCell from,
        int minimumX,
        int minimumZ,
        int maximumX,
        int maximumZ)
    {
        var dx = Math.Max(0, Math.Max(minimumX - from.X, from.X - maximumX));
        var dz = Math.Max(0, Math.Max(minimumZ - from.Z, from.Z - maximumZ));
        var straight = Math.Max(dx, dz);
        var diagonal = Math.Min(dx, dz);
        var cells = straight - diagonal + diagonal * DiagonalCost;
        return cells * SecondsPerCell * FastestSurfaceCost;
    }

    /// <summary>Cheapest per-cell surface multiplier the terrain can offer.</summary>
    private static readonly float FastestSurfaceCost =
        Terrain.TerrainSurfaceRules.FastestPathCost;

    internal PortalGraph Portals(float agentRadius)
    {
        var key = (grid.Revision, RadiusKey(agentRadius));
        if (portalGraphs.TryGetValue(key, out var existing)) return existing;

        // A terrain edit moves where the crossings are, so older graphs are unreachable
        // rather than merely stale and there is nothing to age out.
        foreach (var stale in portalGraphs.Keys.Where(k => k.Nav != grid.Revision).ToArray())
        {
            portalGraphs.Remove(stale);
        }

        var graph = PortalGraph.Build(grid, partition, agentRadius);
        portalGraphs[key] = graph;
        return graph;
    }

    private static int DirectionIndexOf(int deltaX, int deltaZ)
    {
        for (var i = 0; i < NeighborOffsets.Length; i++)
        {
            if (NeighborOffsets[i].X == deltaX && NeighborOffsets[i].Z == deltaZ) return i;
        }

        return -1;
    }

    /// <summary>
    /// Cost-to-goal field over one region, as a full-size tile indexed by
    /// <see cref="RegionPartition.TileIndex"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately the same search as the flat one it replaces, bounded to a region and
    /// allowed more than one seed: same neighbour order, same closed-set handling, same
    /// per-step cost, same one-heading-per-cell approximation. On the tuned 30 m world the
    /// partition is a single region with no borders, so this <em>is</em> the flat search
    /// and every number the existing suite asserts on is arrived at the same way.
    /// </remarks>
    private float[] SearchRegion(
        int region,
        float agentRadius,
        bool chargeTurns,
        ReadOnlySpan<(GridCell Cell, float Cost)> seeds,
        bool retained,
        ReadOnlySpan<GridCell> until = default)
    {
        RegionSearches++;
        // A tile is kept and read for the rest of the field's life; an ingress search is
        // read once, at the end, and thrown away. Only the first needs its own array.
        var costs = retained ? new float[RegionPartition.CellsPerRegion] : regionScratch;
        var closed = regionClosed;
        var arrival = regionArrival;
        Array.Fill(costs, float.PositiveInfinity);
        Array.Clear(closed);
        Array.Fill(arrival, -1);

        // Hoisted out of the neighbour loop. This is eight bounds tests per cell across
        // four thousand cells, and computing them from the region index each time means a
        // division per test to answer a question that is constant for the whole search.
        partition.Bounds(region, out var minimumX, out var minimumZ, out var maximumX, out var maximumZ);

        regionQueue.Clear();
        foreach (var (cell, cost) in seeds)
        {
            var index = partition.TileIndex(cell);
            if (cost >= costs[index]) continue;
            costs[index] = cost;
            regionQueue.Enqueue(cell, cost);
        }

        // An ingress search only wants the region's own crossings, and Dijkstra settles in
        // cost order, so once the last of them closes nothing further can change any of
        // them. A tile passes nothing here and runs to exhaustion, because it is asked
        // about every cell it covers.
        var remaining = until.Length;
        while (regionQueue.TryDequeue(out var current, out _))
        {
            var currentIndex = partition.TileIndex(current);
            if (closed[currentIndex]) continue;
            closed[currentIndex] = true;
            if (remaining > 0)
            {
                foreach (var target in until)
                {
                    if (target != current) continue;
                    remaining--;
                    break;
                }

                if (remaining == 0) break;
            }

            for (var directionIndex = 0; directionIndex < NeighborOffsets.Length; directionIndex++)
            {
                var offset = NeighborOffsets[directionIndex];
                var previousX = current.X + offset.X;
                var previousZ = current.Z + offset.Z;
                // The one difference from the flat search: a route may not leave the
                // region. What lies beyond is the abstract layer's business, and it has
                // already been priced into whichever portal seeded this tile.
                if (previousX < minimumX || previousX > maximumX ||
                    previousZ < minimumZ || previousZ > maximumZ)
                {
                    continue;
                }

                var previous = new GridCell(previousX, previousZ);
                if (!CanTraverseFlow(previous, current, agentRadius)) continue;
                var previousIndex = partition.TileIndex(previous);
                if (closed[previousIndex]) continue;
                var nextCost = FlowStepCost(
                    costs[currentIndex],
                    current,
                    previous,
                    directionIndex,
                    arrival[currentIndex],
                    agentRadius,
                    chargeTurns,
                    out var travelDirection);
                if (nextCost >= costs[previousIndex]) continue;
                costs[previousIndex] = nextCost;
                arrival[previousIndex] = travelDirection;
                regionQueue.Enqueue(previous, nextCost);
            }
        }

        return costs;
    }

    /// <summary>
    /// Seconds to reach <paramref name="node"/> from each node of its region without
    /// leaving that region, aligned with <see cref="PortalGraph.NodesIn"/>.
    /// </summary>
    /// <remarks>
    /// This is where congestion reaches the abstract graph, and it has to: the local
    /// search charges <see cref="CongestionCost"/> exactly as the fine layer does, so a
    /// jam inside a region raises the price of crossing it and a route several regions
    /// long can still decide to go round. Without this the congestion layer would be
    /// silently deleted for every route longer than one region — the failure this session
    /// was most likely to ship without noticing, because nothing on a 30 m map can show it.
    /// </remarks>
    private float[] IngressCosts(PortalGraph portals, int node, float agentRadius, bool chargeTurns)
    {
        var region = portals.RegionOfNode(node);
        var key = (node, grid.Revision, RadiusKey(agentRadius), congestion.RegionStamp(region), chargeTurns);
        if (nodeIngress.TryGetValue(key, out var cached)) return cached;

        Span<(GridCell, float)> seed = stackalloc (GridCell, float)[1];
        seed[0] = (portals.CellOfNode(node), 0f);
        var nodes = portals.NodesIn(region);
        var targets = new GridCell[nodes.Length];
        for (var i = 0; i < nodes.Length; i++) targets[i] = portals.CellOfNode(nodes[i]);
        var tile = SearchRegion(region, agentRadius, chargeTurns, seed, retained: false, targets);

        var costs = new float[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            costs[i] = tile[partition.TileIndex(targets[i])];
        }

        nodeIngress[key] = costs;
        return costs;
    }

    /// <summary>Seconds for the single step across a portal, arriving at its far side.</summary>
    private float CrossingSeconds(PortalGraph portals, int node, float agentRadius, bool chargeTurns)
    {
        var here = portals.CellOfNode(node);
        var there = portals.CellOfNode(PortalGraph.OppositeNode(node));
        var directionIndex = DirectionIndexOf(there.X - here.X, there.Z - here.Z);
        if (directionIndex < 0) return float.PositiveInfinity;
        return FlowStepCost(0f, here, there, directionIndex, -1, agentRadius, chargeTurns, out _);
    }

    /// <summary>Builds the goal-region tile that seeds an abstract search.</summary>
    private float[] GoalRegionTile(GridCell goal, float agentRadius, bool chargeTurns)
    {
        Span<(GridCell, float)> seed = stackalloc (GridCell, float)[1];
        seed[0] = (goal, 0f);
        TileRefinements++;
        return SearchRegion(partition.RegionOf(goal), agentRadius, chargeTurns, seed, retained: true);
    }

    /// <summary>
    /// Refines one region of <paramref name="field"/>: settles the abstract cost of every
    /// crossing that stands in it, then runs one bounded local search seeded from them.
    /// </summary>
    internal float[] RefineTile(FlowField field, int region)
    {
        var portals = field.Portals;
        var nodes = portals.NodesIn(region);
        field.SettleRegion(region, RegionSpanSeconds);
        var seeds = new List<(GridCell Cell, float Cost)>(nodes.Length + 1);
        if (partition.RegionOf(field.Goal) == region) seeds.Add((field.Goal, 0f));
        foreach (var node in nodes)
        {
            var cost = field.KnownCostOf(node);
            if (!float.IsFinite(cost)) continue;
            seeds.Add((portals.CellOfNode(node), cost));
        }

        TileRefinements++;
        return SearchRegion(
            region,
            field.AgentRadius,
            field.ChargeTurns,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(seeds),
            retained: true);
    }

    /// <summary>Expands one node of an abstract search, relaxing everything that reaches it.</summary>
    internal void ExpandAbstract(FlowField field, int node, float settledCost)
    {
        var portals = field.Portals;
        var opposite = PortalGraph.OppositeNode(node);
        var crossing = CrossingSeconds(portals, node, field.AgentRadius, field.ChargeTurns);
        if (float.IsFinite(crossing)) field.Relax(opposite, settledCost + crossing);

        var region = portals.RegionOfNode(node);
        var nodes = portals.NodesIn(region);
        if (nodes.Length <= 1) return;
        var ingress = IngressCosts(portals, node, field.AgentRadius, field.ChargeTurns);
        for (var i = 0; i < nodes.Length; i++)
        {
            if (nodes[i] == node || !float.IsFinite(ingress[i])) continue;
            field.Relax(nodes[i], settledCost + ingress[i]);
        }
    }

    /// <summary>
    /// How far the hierarchy's answers sit above the flat optimum, cell by cell.
    /// </summary>
    /// <remarks>
    /// The one number that says whether portal routing is honest. Every cost the hierarchy
    /// reports is an over-estimate of the flat search's, because a route that leaves a
    /// region and comes back cannot be expressed and because crossings settled only as
    /// upper bounds are priced high — so the ratio is at least one everywhere, and how far
    /// above one it goes is exactly the price of the whole architecture. Reported rather
    /// than asserted in the abstract: the thresholds in the self-test were read off this.
    /// </remarks>
    internal RoutingFidelity MeasureFidelity(GridCell goal, float agentRadius)
    {
        var reference = BuildFlowField(goal, agentRadius);
        var searchesBefore = RegionSearches;
        var field = new FlowField(
            this,
            Portals(agentRadius),
            goal,
            agentRadius,
            chargeTurns: true,
            GoalRegionTile(goal, agentRadius, chargeTurns: true));

        var reachable = 0;
        var lost = 0;
        var ratioSum = 0.0;
        var worst = 1f;
        var worstCell = goal;
        var ratios = new List<float>();
        for (var z = 0; z < grid.Height; z++)
        for (var x = 0; x < grid.Width; x++)
        {
            var cell = new GridCell(x, z);
            var flat = reference[grid.Transform.Index(cell)];
            if (!float.IsFinite(flat) || flat <= 0f) continue;
            reachable++;
            var hierarchical = field.CostAt(cell);
            if (!float.IsFinite(hierarchical))
            {
                lost++;
                continue;
            }

            var ratio = hierarchical / flat;
            ratioSum += ratio;
            ratios.Add(ratio);
            if (ratio <= worst) continue;
            worst = ratio;
            worstCell = cell;
        }

        ratios.Sort();
        var measured = Math.Max(1, ratios.Count);
        return new RoutingFidelity(
            reachable,
            lost,
            (float)(ratioSum / measured),
            ratios.Count > 0 ? ratios[Math.Min((int)(ratios.Count * 0.99f), ratios.Count - 1)] : 1f,
            worst,
            worstCell,
            field.SettledNodes,
            field.RefinedRegions,
            RegionSearches - searchesBefore);
    }

    /// <summary>
    /// The flat whole-map field, kept only so the hierarchy can be measured against it.
    /// </summary>
    /// <remarks>
    /// Not used for routing at any size. It is the reference the region-boundary test
    /// compares to, because "the hierarchy is a good enough approximation" is a claim about
    /// a number, and the only way to have that number is to still be able to compute the
    /// thing being approximated.
    /// </remarks>
    internal float[] BuildReferenceFlowField(GridCell goal, float agentRadius, bool chargeTurns = true) =>
        BuildFlowField(goal, agentRadius, chargeTurns);
}
