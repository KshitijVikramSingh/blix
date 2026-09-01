using System.Diagnostics;
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
/// The routing hierarchy: an adaptive partition of walkable ground, a graph over the corners of
/// the borders between its parts, and dense tiles filled only where a body is looking.
/// </summary>
/// <remarks>
/// What this replaced, in order. First a Dijkstra over every cell of the map — 5.76M of them at
/// 1200 m, 4.1 s for the first route after a move order. Then a fixed 32 m partition with portals
/// on its borders, which removed that and left its own cost: a region whose ground was not uniform
/// had to be searched cell by cell, and a single ridge cell was enough to condemn all 4,096 of
/// them. Now the partition follows the ground, so a part of it is uniform by construction and
/// crossing one is arithmetic.
/// <para>
/// Nothing here is eager and nothing is stored per cell of the map. The corner graph is solved
/// outright because it is small; a tile is filled the first time something asks what a cell in it
/// costs, and only ever a handful per order. What a route costs is proportional to the ground
/// between the body and its goal, not to the size of the world — measured at 6.0 ms a tick with
/// 2,000 agents on 600 m, and 5.8 ms on 1200 m.
/// </para>
/// </remarks>
internal sealed partial class PathService
{
    private readonly RegionPartition partition;
    private readonly PriorityQueue<GridCell, float> regionQueue = new();



    // One region's working set, reused. Every search allocated its own and the searches
    // are the unit of work here, so this was three 16 KB arrays per crossing considered.
    private readonly bool[] regionClosed = new bool[RegionPartition.CellsPerRegion];
    private readonly int[] regionArrival = new int[RegionPartition.CellsPerRegion];
    private readonly float[] regionScratch = new float[RegionPartition.CellsPerRegion];

    /// <summary>Regions the partition divides this map into.</summary>
    public int RegionCount => partition.Count;
    /// <summary>Region-local searches run since construction.</summary>
    public long RegionSearches { get; private set; }
    /// <summary>Tiles refined since construction.</summary>
    public long TileRefinements { get; private set; }

    private static int RadiusKey(float agentRadius) => (int)MathF.Round(agentRadius * 100f);

    private readonly Dictionary<(int Nav, int Radius), (WalkableRectangles Mesh, RectangleIndex Index)>
        meshes = new();

    /// <summary>
    /// The rectangle decomposition for a body of this radius, built once per terrain edit.
    /// </summary>
    /// <remarks>
    /// Ten to seventeen milliseconds to build on a 600 m map, so it is cached against the
    /// terrain revision exactly as the portal graph was. Radius is part of the key because
    /// walkability is: a wider body has fewer rectangles and different crossings.
    /// </remarks>
    /// <summary>
    /// Climb charges between points, kept as long as the ground under them is.
    /// </summary>
    /// <remarks>
    /// <b>Keyed by the terrain revision, and it used to be keyed by the navigation revision.</b> The old
    /// comment gave the reason and it was sound and one revision too tight: <em>the corners come from the
    /// decomposition and the climb comes from the heights, and a change to either bumps the revision</em>.
    /// True — but a change to the decomposition alone should not cost anything here, and a building going up
    /// is exactly that. Measured in §114: the click after a placement paid 2.5 million height samples,
    /// twelve times a normal cold order, rebuilding answers that could not have changed.
    /// <para>
    /// Two things had to move for it to survive. The key was a pair of <em>corner indices</em>, which are
    /// positions in a mesh and mean nothing once the mesh is rebuilt; corners sit at half-cell coordinates,
    /// so twice the coordinates is an exact integer key that outlives any decomposition. And the radius
    /// bucket is gone, because the climb between two points is not a property of who is walking it — that
    /// was in the key because <em>which corners exist</em> depends on radius, which the coordinates now
    /// carry for themselves. A mixed-radius world shares one cache as a side effect.
    /// </para>
    /// <para>
    /// The eviction is what keeps it honest: the moment the ground itself changes, every answer in here is
    /// suspect and the whole table goes. That is the only thing this cache is allowed to survive.
    /// </para></remarks>
    private readonly Dictionary<int, Dictionary<(int, int, int, int), float>> cornerClimbs = new();

    /// <summary>Entries currently held, for a profile that would rather report growth than assume it.</summary>
    internal int CornerClimbEntries =>
        cornerClimbs.TryGetValue(grid.TerrainRevision, out var live) ? live.Count : 0;

    internal Dictionary<(int, int, int, int), float> CornerClimbCache()
    {
        var key = grid.TerrainRevision;
        if (cornerClimbs.TryGetValue(key, out var existing)) return existing;
        foreach (var stale in cornerClimbs.Keys.Where(k => k != key).ToArray())
        {
            cornerClimbs.Remove(stale);
        }

        var created = new Dictionary<(int, int, int, int), float>();
        cornerClimbs[key] = created;
        return created;
    }

    internal (WalkableRectangles Mesh, RectangleIndex Index) Mesh(float agentRadius)
    {
        var key = (grid.Revision, RadiusKey(agentRadius));
        if (meshes.TryGetValue(key, out var existing))
        {
            MeshCacheHits++;
            return existing;
        }

        foreach (var stale in meshes.Keys.Where(k => k.Nav != grid.Revision).ToArray())
        {
            meshes.Remove(stale);
        }

        // <b>Timed and counted, because a stall has to be able to name itself.</b> An order on a real map was
        // measured at 340 ms against 28 on flat ground, with only three region searches in it — so the cost is
        // in one of three places (this decomposition, the tiles filled off it, or the field around them) and
        // "probably the mesh" is not a diagnosis. Cache hits are counted too: a mesh rebuilt every order and a
        // mesh reused look identical from outside and want opposite fixes.
        var meshStart = Stopwatch.GetTimestamp();
        var mesh = WalkableRectangles.Build(grid, agentRadius);
        var built = (mesh, new RectangleIndex(mesh, grid.Width, grid.Height));
        MeshBuildTicks += Stopwatch.GetTimestamp() - meshStart;
        MeshBuilds++;
        MeshRectangles = mesh.Count;
        meshes[key] = built;
        return built;
    }

    /// <summary>Where an order's time actually goes, split three ways. See Mesh and FillRectangleTile.</summary>
    internal long MeshBuildTicks;
    internal int MeshBuilds;
    internal int MeshCacheHits;
    internal int MeshRectangles;
    internal long TileFillTicks;

    /// <summary>
    /// Fills one region's tile from the rectangle field, by seeding its edge and searching in.
    /// </summary>
    /// <remarks>
    /// A region in the middle of a large rectangle contains no crossings at all, so seeding from
    /// crossings would leave it empty. Its edge is where the answer arrives from, and the
    /// analytic field can price any cell — so the perimeter is seeded with what the corner graph
    /// says and the interior is filled by the same bounded search the fine layer has always used.
    /// Inside the tile every term is exact and the gradient is continuous; outside it, routing is
    /// still arithmetic over corners.
    /// </remarks>
    internal float[] FillRectangleTile(RectangleFlowField field, int region)
    {
        var fillStart = Stopwatch.GetTimestamp();
        try
        {
            return FillRectangleTileCore(field, region);
        }
        finally
        {
            TileFillTicks += Stopwatch.GetTimestamp() - fillStart;
        }
    }

    private float[] FillRectangleTileCore(RectangleFlowField field, int region)
    {
        var seedStart = Stopwatch.GetTimestamp();
        partition.Bounds(region, out var minimumX, out var minimumZ, out var maximumX, out var maximumZ);
        var seeds = new List<(GridCell Cell, float Cost)>();
        if (partition.RegionOf(field.Goal) == region) seeds.Add((field.Goal, 0f));

        for (var z = minimumZ; z <= maximumZ; z++)
        for (var x = minimumX; x <= maximumX; x++)
        {
            if (x != minimumX && x != maximumX && z != minimumZ && z != maximumZ) continue;
            var cell = new GridCell(x, z);
            TileSeedCells++;
            var cost = field.AnalyticCostAt(cell);
            if (!float.IsFinite(cost)) continue;
            seeds.Add((cell, cost));
        }

        TileRefinements++;
        // Split, because a tile fill is two different jobs: pricing the perimeter through the corner graph,
        // and searching inward from it. §98.
        TileSeedTicks += Stopwatch.GetTimestamp() - seedStart;
        var searchStart = Stopwatch.GetTimestamp();
        try
        {
            return SearchRegion(
            region,
            field.AgentRadius,
            field.ChargeTurns,
            field.CongestionSpeedScale,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(seeds),
                retained: true);
        }
        finally
        {
            TileSearchTicks += Stopwatch.GetTimestamp() - searchStart;
        }
    }

    /// <summary>A tile fill's two halves: seeding the perimeter, and searching in from it.</summary>
    internal long TileSeedTicks;

    internal long TileSearchTicks;

    internal int TileSeedCells;

    /// <summary>Neighbour visits attempted, and those that survived the traversal test, in region searches.</summary>
    internal long RegionRelaxations;

    internal long RegionSteps;

    /// <summary>Seconds a body loses to the single bend an octile leg contains.</summary>
    internal float BendSeconds(GridCell near, float agentRadius, bool chargeTurns) =>
        chargeTurns ? TurnCost(0, 4, near, agentRadius) : 0f;








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
        float congestionSpeedScale,
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
                RegionRelaxations++;
                if (!CanTraverseFlow(previous, current, agentRadius)) continue;
                RegionSteps++;
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
                    congestionSpeedScale,
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
    /// How far the rectangle decomposition's answers sit above the flat optimum.
    /// </summary>
    /// <remarks>
    /// Measured on exactly the same yardstick the portal router was, so the two are directly
    /// comparable: same map, same goal, same flat reference, same ratio. Congestion is absent
    /// from both — the comparison runs on a static map with no bodies — so what this reports is
    /// whether a partition that follows the ground routes as well as one that searches cells.
    /// </remarks>
    /// <summary>
    /// How far the rectangle decomposition's answers sit above the flat optimum, with terms removable.
    /// </summary>
    /// <remarks>
    /// <b>The overrides exist because §122 found this 28% out and could not say on which term.</b> An
    /// estimate is a sum — legs across uniform ground, one bend per leg that turns, and the climb along the
    /// straight line between two corners — and the only way to attribute an error in a sum is to price the
    /// same ground with each part removed. Nothing in the game passes them.
    /// </remarks>
    internal RoutingFidelity MeasureRectangleFidelity(
        GridCell goal,
        float agentRadius,
        float? bendOverride = null,
        bool chargeClimb = true)
    {
        var reference = BuildFlowField(goal, agentRadius);
        var mesh = WalkableRectangles.Build(grid, agentRadius);
        var rectangleIndex = new RectangleIndex(mesh, grid.Width, grid.Height);
        // One bend, on open ground, at the router's own turn rate — the same TurnCost the flat
        // field charges, evaluated where a rectangle's clearance always puts it: in the open.
        var bend = TurnCost(0, 4, goal, agentRadius);
        var field = new RectangleFlowField(
            mesh,
            rectangleIndex,
            goal,
            SecondsPerCell,
            bendOverride ?? bend,
            chargeClimb,
            congestion,
            CongestionSecondsPerPressure,
            this,
            agentRadius,
            1f,
            chargeTurns: true,
            CornerClimbCache());

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
            var estimate = field.AnalyticCostAt(cell);
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
