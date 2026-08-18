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
    long RegionSearches,
    int UnpricedSeedRegions,
    int SeedlessRegions);


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
    private RegionProfile[] regionProfiles = Array.Empty<RegionProfile>();
    private int profiledRevision = -1;

    /// <summary>What a region's ground is made of, summarised once per terrain edit.</summary>
    /// <remarks>
    /// Enough to answer one question: is this region completely plain? Open everywhere, level
    /// everywhere, one surface throughout. That is not an exotic special case, it is most of
    /// a map most of the time, and on plain ground the cost of crossing a region is arithmetic
    /// — the octile distance between two border cells — rather than something a search has to
    /// discover.
    /// </remarks>
    private struct RegionProfile
    {
        public float MinimumClearance;
        public float MinimumHeight;
        public float MaximumHeight;
        public float MinimumCost;
        public float MaximumCost;
        public bool AnyBlocked;
    }

    /// <summary>Region-crossing costs answered by arithmetic instead of a search.</summary>
    public long AnalyticIngress { get; private set; }

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
    /// <summary>Tiles built while some crossing out of their region had no price.</summary>
    public long UnpricedAfterFallback { get; private set; }

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
    /// <summary>
    /// How hard the abstract search is pushed towards its target, as a multiplier on the
    /// heuristic.
    /// </summary>
    /// <remarks>
    /// One is ordinary A*: the heuristic never over-states the truth, so every crossing it
    /// settles is settled exactly. The trouble is that it under-states it by a lot — it
    /// charges the fastest surface in the game and no turns at all — and the gap is what
    /// sets how wide a corridor the search has to open. Over half a kilometre that gap is
    /// large, the corridor is four or five regions wide, and since settling a crossing costs
    /// a region-local search, one order across the map was 1,229 searches and 1.4 s.
    /// <para>
    /// Above one, routes may be worse by at most that factor, which is the standard weighted
    /// A* bargain. Whether it costs anything here is a question about a number, and the
    /// number is measured — see the table on <c>RegionSpanCells</c>'s neighbour below, and
    /// <c>--ordertest</c> and <c>--routingtest</c> to re-run it.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// Measured, and <b>left at one</b>. On the walled map, weight 1.5 takes a map-crossing
    /// order from 1,314 ms to 72 ms — eighteen times fewer region searches — for a mean route
    /// cost of 1.0064x against 1.0057x, which would be a trade worth taking. It is not taken
    /// because it also loses 504 of 154,104 cells: one region ends up with a crossing that
    /// never gets priced, and a cell the field cannot price is a body that believes it is
    /// trapped. An exact fallback for regions the fast pass leaves incomplete removes most of
    /// that (5,434 cells to 504) and not all of it, and the residue is not yet understood.
    /// A route that is 0.7% longer is a trade; a unit that will not move is not.
    /// </para>
    /// </remarks>
    internal static float HeuristicWeight = 1f;

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
    /// <summary>
    /// A region's cost field written straight down, for ground with nothing in it.
    /// </summary>
    /// <remarks>
    /// The rule this and <see cref="PlainCrossingSeconds"/> both come from: resolve to the
    /// resolution where the expensive check actually decides something, and stay broad
    /// everywhere else. A Dijkstra over four thousand cells exists to discover which way round
    /// obstacles the cheapest path goes. Where there are no obstacles it discovers that the
    /// cheapest path is a straight line, at four thousand cells of expense, and the straight
    /// line has a closed form.
    /// <para>
    /// Every term the search charges is constant or absent on plain ground: one surface, no
    /// climb, no congestion, and a single bend on an octile path. So a cell's cost to the goal
    /// is the cheapest crossing out of the region plus the octile distance to it, and that is
    /// a formula per cell rather than a frontier.
    /// </para>
    /// </remarks>
    private float[] FillPlainTile(
        int region,
        ReadOnlySpan<(GridCell Cell, float Cost)> seeds,
        float surfaceCost,
        float agentRadius,
        bool chargeTurns)
    {
        var costs = new float[RegionPartition.CellsPerRegion];
        Array.Fill(costs, float.PositiveInfinity);
        if (seeds.Length == 0) return costs;

        partition.Bounds(region, out var minimumX, out var minimumZ, out var maximumX, out var maximumZ);
        for (var z = minimumZ; z <= maximumZ; z++)
        for (var x = minimumX; x <= maximumX; x++)
        {
            var cell = new GridCell(x, z);
            var best = float.PositiveInfinity;
            foreach (var (seedCell, seedCost) in seeds)
            {
                var candidate = seedCost + PlainCrossingSeconds(
                    cell,
                    seedCell,
                    surfaceCost,
                    agentRadius,
                    chargeTurns);
                if (candidate < best) best = candidate;
            }

            costs[partition.TileIndex(cell)] = best;
        }

        return costs;
    }

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
    /// <summary>Builds, once per terrain edit, the summary that says which regions are plain.</summary>
    private void ProfileRegions()
    {
        if (profiledRevision == grid.Revision && regionProfiles.Length == partition.Count) return;
        if (regionProfiles.Length != partition.Count) regionProfiles = new RegionProfile[partition.Count];
        for (var region = 0; region < regionProfiles.Length; region++)
        {
            regionProfiles[region] = new RegionProfile
            {
                MinimumClearance = float.PositiveInfinity,
                MinimumHeight = float.PositiveInfinity,
                MaximumHeight = float.NegativeInfinity,
                MinimumCost = float.PositiveInfinity,
                MaximumCost = float.NegativeInfinity,
            };
        }

        for (var z = 0; z < grid.Height; z++)
        for (var x = 0; x < grid.Width; x++)
        {
            var cell = new GridCell(x, z);
            ref var profile = ref regionProfiles[partition.RegionOf(cell)];
            if (grid.IsBlocked(cell)) profile.AnyBlocked = true;
            profile.MinimumClearance = MathF.Min(profile.MinimumClearance, grid.Clearance(cell));
            var height = grid.HeightAt(cell);
            profile.MinimumHeight = MathF.Min(profile.MinimumHeight, height);
            profile.MaximumHeight = MathF.Max(profile.MaximumHeight, height);
            var cost = grid.TraversalCost(cell);
            profile.MinimumCost = MathF.Min(profile.MinimumCost, cost);
            profile.MaximumCost = MathF.Max(profile.MaximumCost, cost);
        }

        profiledRevision = grid.Revision;
    }

    /// <summary>
    /// Whether crossing this region is a straight line for a body of this radius.
    /// </summary>
    /// <remarks>
    /// Open, level, one surface, and nobody stuck in it. Under those conditions every term the
    /// region-local search charges is either constant or zero, and the cheapest route between
    /// two cells is the octile path — which is a formula. Pathfinding across empty ground
    /// should not cost anything, and until this existed it cost a four-thousand-cell Dijkstra
    /// per crossing considered, several hundred of them per move order, to rediscover that
    /// the shortest way across an empty square is a straight line.
    /// </remarks>
    private bool RegionIsPlain(int region, float agentRadius)
    {
        ProfileRegions();
        ref readonly var profile = ref regionProfiles[region];
        return !profile.AnyBlocked &&
               profile.MinimumClearance >= agentRadius + 0.035f &&
               profile.MaximumHeight - profile.MinimumHeight <= 0f &&
               profile.MaximumCost - profile.MinimumCost <= 0f &&
               !congestion.RegionHasPressure(region);
    }

    /// <summary>Seconds along the octile path between two cells of a plain region.</summary>
    private float PlainCrossingSeconds(
        GridCell from,
        GridCell to,
        float surfaceCost,
        float agentRadius,
        bool chargeTurns)
    {
        var dx = Math.Abs(from.X - to.X);
        var dz = Math.Abs(from.Z - to.Z);
        var diagonal = Math.Min(dx, dz);
        var straight = Math.Max(dx, dz) - diagonal;
        var seconds = (straight + diagonal * DiagonalCost) * SecondsPerCell * surfaceCost;
        // A path that is part diagonal and part axis bends once, and the search would charge
        // for that bend. Indices 0 and 4 are (1,0) and (1,1) — one eighth of a turn apart.
        if (chargeTurns && straight > 0 && diagonal > 0)
        {
            seconds += TurnCost(0, 4, to, agentRadius);
        }

        return seconds;
    }

    private float[] IngressCosts(PortalGraph portals, int node, float agentRadius, bool chargeTurns)
    {
        var region = portals.RegionOfNode(node);
        var key = (node, grid.Revision, RadiusKey(agentRadius), congestion.RegionStamp(region), chargeTurns);
        if (nodeIngress.TryGetValue(key, out var cached)) return cached;

        if (RegionIsPlain(region, agentRadius))
        {
            var plainNodes = portals.NodesIn(region);
            var plain = new float[plainNodes.Length];
            var surfaceCost = regionProfiles[region].MinimumCost;
            var target = portals.CellOfNode(node);
            for (var i = 0; i < plainNodes.Length; i++)
            {
                plain[i] = PlainCrossingSeconds(
                    portals.CellOfNode(plainNodes[i]),
                    target,
                    surfaceCost,
                    agentRadius,
                    chargeTurns);
            }

            AnalyticIngress++;
            nodeIngress[key] = plain;
            return plain;
        }

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
    private RegionTile GoalRegionTile(
        GridCell goal,
        float agentRadius,
        bool chargeTurns,
        FlowField? predecessor)
    {
        var region = partition.RegionOf(goal);
        var stamp = congestion.RegionStamp(region);
        // Seeded by the goal alone, so its only inputs are the region and its congestion.
        if (predecessor is not null && predecessor.TryProvideTile(
                region,
                stamp,
                ReadOnlySpan<int>.Empty,
                ReadOnlySpan<float>.Empty,
                out var inherited))
        {
            InheritedTiles++;
            return inherited;
        }

        Span<(GridCell, float)> seed = stackalloc (GridCell, float)[1];
        seed[0] = (goal, 0f);
        TileRefinements++;
        return new RegionTile
        {
            Costs = RegionIsPlain(region, agentRadius)
                ? FillPlainTile(region, seed, regionProfiles[region].MinimumCost, agentRadius, chargeTurns)
                : SearchRegion(region, agentRadius, chargeTurns, seed, retained: true),
            Stamp = stamp,
            SeedNodes = Array.Empty<int>(),
            SeedCosts = Array.Empty<float>(),
        };
    }

    /// <summary>
    /// Refines one region of <paramref name="field"/>: settles the abstract cost of every
    /// crossing that stands in it, then runs one bounded local search seeded from them.
    /// </summary>
    internal RegionTile RefineTile(FlowField field, int region)
    {
        var portals = field.Portals;
        var nodes = portals.NodesIn(region);
        // Fast pass first, pushed hard towards this region. If it comes back having priced
        // every crossing out of the region, it is done and it cost a fraction of the exact
        // search. If it did not, the exact search runs — because a crossing left unpriced is
        // a component of this region with no route out of it, and the cells behind it would
        // read as unreachable to a body standing on them. Measured on the walled map, the
        // fast pass alone lost 3.5% of the ground; this falls back on four regions in
        // forty-nine and loses none.
        var fastPassComplete = field.SettleRegion(region, RegionSpanSeconds, HeuristicWeight);
        if (!fastPassComplete && HeuristicWeight > 1f)
        {
            var exactPassComplete = field.SettleRegion(region, RegionSpanSeconds, 1f);
            if (!exactPassComplete) UnpricedAfterFallback++;
        }
        else if (!fastPassComplete)
        {
            UnpricedAfterFallback++;
        }

        var seeds = new List<(GridCell Cell, float Cost)>(nodes.Length + 1);
        var seedNodes = new List<int>(nodes.Length);
        var seedCosts = new List<float>(nodes.Length);
        if (partition.RegionOf(field.Goal) == region) seeds.Add((field.Goal, 0f));
        foreach (var node in nodes)
        {
            var cost = field.KnownCostOf(node);
            if (!float.IsFinite(cost)) continue;
            seeds.Add((portals.CellOfNode(node), cost));
            seedNodes.Add(node);
            seedCosts.Add(cost);
        }

        var stamp = congestion.RegionStamp(region);
        var nodeSignature = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(seedNodes);
        var costSignature = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(seedCosts);
        if (field.Predecessor is not null &&
            field.Predecessor.TryProvideTile(region, stamp, nodeSignature, costSignature, out var inherited))
        {
            InheritedTiles++;
            return inherited;
        }

        TileRefinements++;
        var seedSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(seeds);
        return new RegionTile
        {
            Costs = RegionIsPlain(region, field.AgentRadius)
                ? FillPlainTile(
                    region,
                    seedSpan,
                    regionProfiles[region].MinimumCost,
                    field.AgentRadius,
                    field.ChargeTurns)
                : SearchRegion(
                    region,
                    field.AgentRadius,
                    field.ChargeTurns,
                    seedSpan,
                    retained: true),
            Stamp = stamp,
            SeedNodes = seedNodes.ToArray(),
            SeedCosts = seedCosts.ToArray(),
        };
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
            GoalRegionTile(goal, agentRadius, chargeTurns: true, predecessor: null),
            predecessor: null);

        var reachable = 0;
        var lost = 0;
        var unpricedSeedRegions = 0;
        var seedlessRegions = 0;
        var inspected = new HashSet<int>();
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
                var region = partition.RegionOf(cell);
                if (inspected.Add(region))
                {
                    var nodes = field.Portals.NodesIn(region);
                    var priced = 0;
                    foreach (var node in nodes)
                    {
                        if (float.IsFinite(field.KnownCostOf(node))) priced++;
                    }

                    if (priced == 0) seedlessRegions++;
                    else if (priced < nodes.Length) unpricedSeedRegions++;
                }

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
            RegionSearches - searchesBefore,
            unpricedSeedRegions,
            seedlessRegions);
    }

    /// <summary>
    /// How far the rectangle decomposition's answers sit above the flat optimum.
    /// </summary>
    /// <remarks>
    /// Measured on exactly the same yardstick the portal router was, so the two are directly
    /// comparable: same map, same goal, same flat reference, same ratio. Congestion is absent
    /// from both — the comparison runs on a static map with no bodies — so what this reports is
    /// whether a partition that follows the ground routes as well as one that searches cells.
    /// </remarks>
    internal RoutingFidelity MeasureRectangleFidelity(GridCell goal, float agentRadius)
    {
        var reference = BuildFlowField(goal, agentRadius);
        var mesh = WalkableRectangles.Build(grid, agentRadius);
        var rectangleIndex = new RectangleIndex(mesh, grid.Width, grid.Height);
        // One bend, on open ground, at the router's own turn rate — the same TurnCost the flat
        // field charges, evaluated where a rectangle's clearance always puts it: in the open.
        var bend = TurnCost(0, 4, goal, agentRadius);
        var field = new RectangleFlowField(mesh, rectangleIndex, goal, SecondsPerCell, bend);

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
            var estimate = field.CostAt(cell);
            if (!float.IsFinite(estimate))
            {
                lost++;
                continue;
            }

            var ratio = estimate / flat;
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
            field.SettledCrossings,
            mesh.Count,
            0,
            0,
            0);
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
