using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Navigation;

/// <summary>
/// Cost-to-goal for every cell that has been asked about, and nothing else. What used
/// to be a <c>float[]</c> over the whole map is now a goal-directed search over portal
/// sides plus a handful of region-sized tiles, refined the first time something samples
/// one.
/// </summary>
/// <remarks>
/// The interface is deliberately the same shape it replaces — ask a cell, get seconds,
/// get <see cref="float.PositiveInfinity"/> when there is no route — because every reader
/// of it is steering code that has been tuned against those semantics and none of it
/// should have to know a hierarchy exists.
/// <para>
/// A tile is a region's worth of costs seeded from the crossings that stand in that
/// region, each carrying what the rest of the journey costs from there. So the field is
/// piecewise: exact within a region, and joined at the borders by an abstract price that
/// was computed with the same per-step costs. It is an approximation in exactly one way —
/// a route that leaves a region and comes back cannot be expressed — which is the standard
/// bargain and is measured rather than assumed, in the boundary self-test.
/// </para>
/// </remarks>
internal sealed class FlowField
{
    private readonly PathService owner;
    private readonly Dictionary<int, float[]> tiles = new();
    // Exact seconds to the goal for crossings the search has settled. Kept across runs: a
    // settled crossing's cost does not depend on which region was being refined when it
    // was found, so the next refinement resumes from it rather than rediscovering it.
    private readonly float[] settledCost;
    // Cheapest cost ever offered for a crossing, settled or not. An unsettled entry is an
    // upper bound, which is exactly what a tile can be seeded with: it makes that crossing
    // look no cheaper than it is, so the field under-prefers it rather than inventing a
    // route. That is what lets the search stop as soon as the best way out of a region is
    // certain, instead of waiting for every alternative to be certain too.
    private readonly float[] bestKnownCost;
    private readonly bool[] settled;
    private readonly List<int> settledOrder = new();
    // Per-run search state. A stamp equal to the run means "queued", the negation of the
    // run means "expanded in this run", and anything else means untouched — which is what
    // lets a run start without clearing two arrays the size of the whole portal graph.
    private readonly float[] runCost;
    private readonly int[] runStamp;
    private int run;
    private readonly PriorityQueue<int, float> open = new();
    private readonly int[] goalSeeds;
    private readonly float[] goalSeedCosts;
    private int targetMinimumX;
    private int targetMinimumZ;
    private int targetMaximumX;
    private int targetMaximumZ;

    public PortalGraph Portals { get; }
    public GridCell Goal { get; }
    public float AgentRadius { get; }
    public bool ChargeTurns { get; }

    /// <summary>Regions refined so far, which is what this field has cost to date.</summary>
    public int RefinedRegions => tiles.Count;
    /// <summary>Portal sides whose distance to the goal has been settled.</summary>
    public int SettledNodes => settledOrder.Count;

    public FlowField(
        PathService owner,
        PortalGraph portals,
        GridCell goal,
        float agentRadius,
        bool chargeTurns,
        float[] goalRegionTile)
    {
        this.owner = owner;
        Portals = portals;
        Goal = goal;
        AgentRadius = agentRadius;
        ChargeTurns = chargeTurns;

        var partition = portals.Partition;
        var goalRegion = partition.RegionOf(goal);
        // The goal's own region is refined up front and seeded by the goal alone. It has
        // to be: every crossing out of that region is priced by how far it is from the
        // goal, so this tile is what the search starts from. It also breaks what would
        // otherwise be a circle — tiles need crossing costs, crossing costs need the goal
        // tile.
        tiles[goalRegion] = goalRegionTile;

        settledCost = new float[portals.NodeCount];
        bestKnownCost = new float[portals.NodeCount];
        settled = new bool[portals.NodeCount];
        runCost = new float[portals.NodeCount];
        runStamp = new int[portals.NodeCount];
        Array.Fill(settledCost, float.PositiveInfinity);
        Array.Fill(bestKnownCost, float.PositiveInfinity);

        var seeds = new List<int>();
        var seedCosts = new List<float>();
        foreach (var node in portals.NodesIn(goalRegion))
        {
            var seconds = goalRegionTile[partition.TileIndex(portals.CellOfNode(node))];
            if (!float.IsFinite(seconds)) continue;
            seeds.Add(node);
            seedCosts.Add(seconds);
        }

        goalSeeds = seeds.ToArray();
        goalSeedCosts = seedCosts.ToArray();
    }

    /// <summary>Seconds from this cell to the goal, or infinity if it cannot get there.</summary>
    public float CostAt(GridCell cell)
    {
        var region = Portals.Partition.RegionOf(cell);
        if (!tiles.TryGetValue(region, out var tile))
        {
            tile = owner.RefineTile(this, region);
            tiles[region] = tile;
        }

        return tile[Portals.Partition.TileIndex(cell)];
    }

    /// <summary>
    /// Best seconds known from a portal side to the goal — exact once settled, an upper
    /// bound while the search is still working, infinite if nothing has reached it.
    /// </summary>
    public float KnownCostOf(int node) => bestKnownCost[node];

    /// <summary>
    /// Searches the portal graph from the goal towards <paramref name="region"/> until
    /// every crossing standing in it is settled, or the rest are too far behind to matter.
    /// </summary>
    /// <remarks>
    /// Goal-directed, and that is the whole difference between a hierarchy that pays for
    /// itself and one that does not. A plain Dijkstra from the goal settles every crossing
    /// nearer than the one it is looking for — a disc, where a route needs a corridor — and
    /// since settling a crossing costs a region-local search, that disc measured 759
    /// searches and 1.1 s for one sixty-metre move order on a 1200 m map. The heuristic is
    /// octile distance to the target region over the fastest ground there is, which can
    /// never over-state what a route really costs, so the crossings it settles are settled
    /// exactly rather than approximately.
    /// <para>
    /// Runs resume rather than restart: every crossing settled by an earlier refinement is
    /// re-offered at its known cost, so the second region a crowd walks into re-uses the
    /// corridor found for the first instead of paying for it twice.
    /// </para>
    /// </remarks>
    public void SettleRegion(int region, float regionSpanSeconds)
    {
        var nodes = Portals.NodesIn(region);
        if (nodes.Length == 0) return;
        var pending = 0;
        foreach (var node in nodes)
        {
            if (!settled[node]) pending++;
        }

        if (pending == 0) return;

        run++;
        open.Clear();
        Portals.Partition.Bounds(
            region,
            out targetMinimumX,
            out targetMinimumZ,
            out targetMaximumX,
            out targetMaximumZ);

        for (var i = 0; i < goalSeeds.Length; i++) Offer(goalSeeds[i], goalSeedCosts[i]);
        foreach (var node in settledOrder) Offer(node, settledCost[node]);

        var bestInRegion = float.PositiveInfinity;
        while (pending > 0 && open.TryDequeue(out var current, out var estimate))
        {
            if (runStamp[current] == -run) continue;
            var cost = runCost[current];
            runStamp[current] = -run;

            if (!settled[current])
            {
                settled[current] = true;
                settledCost[current] = cost;
                bestKnownCost[current] = cost;
                settledOrder.Add(current);
            }

            owner.ExpandAbstract(this, current, cost);

            if (Portals.RegionOfNode(current) != region) continue;
            pending--;
            bestInRegion = MathF.Min(bestInRegion, cost);
            if (regionSpanSeconds <= 0f) break;
            // Everything still queued is at least this far out, and a crossing further
            // from the goal than the cheapest one here — plus what crossing this region
            // could cost — cannot give any cell in it a better route. PathService says why
            // that bound is geometric and therefore optimistic on slow ground.
            if (estimate > bestInRegion + regionSpanSeconds) break;
        }
    }

    /// <summary>Offers a crossing to the current run's frontier at a known cost.</summary>
    internal void Relax(int node, float cost) => Offer(node, cost);

    private void Offer(int node, float cost)
    {
        if (!float.IsFinite(cost)) return;
        if (runStamp[node] == -run) return;
        if (runStamp[node] == run && cost >= runCost[node]) return;
        runStamp[node] = run;
        runCost[node] = cost;
        if (cost < bestKnownCost[node]) bestKnownCost[node] = cost;
        open.Enqueue(node, cost + owner.RegionApproachSeconds(
            Portals.CellOfNode(node),
            targetMinimumX,
            targetMinimumZ,
            targetMaximumX,
            targetMaximumZ));
    }
}
