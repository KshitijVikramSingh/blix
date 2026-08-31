using System.Numerics;
using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Navigation;

/// <summary>
/// Cost-to-goal over the rectangle decomposition, with no tiles and no cell-level search.
/// </summary>
/// <remarks>
/// A rectangle is uniform ground by construction, so the cheapest route from a cell inside one
/// to the goal is the cheapest of <em>octile distance to a crossing, plus what the rest of the
/// journey costs from that crossing</em>. Both halves are arithmetic: the first is a clamp and
/// a distance, the second is a Dijkstra over crossings — 1,682 of them on the ridge map, which
/// is a graph small enough that solving it outright costs less than one of the 4,096-cell tile
/// searches it replaces.
/// <para>
/// Built beside the portal router rather than in place of it, so the two can be measured
/// against the same flat reference before anything switches. Congestion is deliberately absent
/// from this first cut: the fidelity comparison runs on a static map with no bodies on it, so
/// the question it answers is purely whether the decomposition gives good routes over terrain.
/// How jams reach these costs is the next decision and a harder one — see <c>plan-rts.md</c> §8.
/// </para>
/// </remarks>
internal sealed class RectangleFlowField
{
    private const float DiagonalCost = 1.41421356f;

    private readonly WalkableRectangles mesh;
    private readonly RectangleIndex index;
    private readonly float secondsPerCell;
    private readonly float bendSeconds;
    private readonly CongestionField congestion;
    private readonly float congestionSecondsPerPressure;
    // Which rectangles hold any pressure at all, walked once from the live set. A leg across
    // clear ground is not sampled, which is nearly every leg on nearly every map.
    private readonly bool[] pressured;
    private readonly bool anyPressure;
    private readonly PathService owner;
    private readonly Dictionary<int, float[]> tiles = new();

    public float AgentRadius { get; }

    /// <summary>
    /// What a second of queueing is worth to the body this field was built for.
    /// </summary>
    /// <remarks>
    /// Held on the field rather than passed to each query because a tile filled later has to
    /// charge congestion exactly as the corner graph above it did — one field that priced its
    /// abstract layer for a scout and its tiles for a villager would have a gradient that
    /// disagrees with its own routing.
    /// </remarks>
    public float CongestionSpeedScale { get; }
    public bool ChargeTurns { get; }
    /// <summary>Tiles filled so far, which is what steering has cost this field.</summary>
    public int RefinedTiles => tiles.Count;
    // Two nodes per crossing, at the ends of the shared border and on the line between the two
    // rectangles — so a half cell out from either, which is where the border actually is.
    private readonly float[] cornerX;
    private readonly float[] cornerZ;
    private readonly float[] cornerCost;

    /// <summary>Corner-pair climb charges, owned by the mesh and shared by every field built on it.</summary>
    private readonly Dictionary<(int, int, int, int), float> cornerClimb;
    private readonly GridCell goal;

    /// <summary>The cell this field routes to.</summary>
    public GridCell Goal => goal;
    private readonly int goalRectangle;

    /// <summary>Corners the search settled, which is what this field cost to build.</summary>
    public int SettledCrossings { get; private set; }

    public RectangleFlowField(
        WalkableRectangles mesh,
        RectangleIndex index,
        GridCell goal,
        float secondsPerCell,
        float bendSeconds,
        CongestionField congestion,
        float congestionSecondsPerPressure,
        PathService owner,
        float agentRadius,
        float congestionSpeedScale,
        bool chargeTurns,
        Dictionary<(int, int, int, int), float> cornerClimb)
    {
        this.cornerClimb = cornerClimb;
        this.owner = owner;
        AgentRadius = agentRadius;
        CongestionSpeedScale = congestionSpeedScale;
        ChargeTurns = chargeTurns;
        this.mesh = mesh;
        this.index = index;
        this.secondsPerCell = secondsPerCell;
        this.bendSeconds = bendSeconds;
        this.congestion = congestion;
        this.congestionSecondsPerPressure = congestionSecondsPerPressure;
        pressured = new bool[mesh.Count];
        foreach (var entry in congestion.LiveCells)
        {
            var rectangle = index.RectangleAt(congestion.CellOf(entry));
            if (rectangle >= 0) pressured[rectangle] = true;
        }

        foreach (var flag in pressured)
        {
            if (!flag) continue;
            anyPressure = true;
            break;
        }
        this.goal = goal;
        goalRectangle = index.RectangleAt(goal);

        var corners = mesh.Crossings.Count * 2;
        cornerX = new float[corners];
        cornerZ = new float[corners];
        cornerCost = new float[corners];
        Array.Fill(cornerCost, float.PositiveInfinity);
        for (var i = 0; i < mesh.Crossings.Count; i++)
        {
            var crossing = mesh.Crossings[i];
            var vertical = crossing.MaximumX - crossing.MinimumX == 1;
            if (vertical)
            {
                // The border sits between the two columns, so a half cell out from each.
                cornerX[i * 2] = crossing.MinimumX + 0.5f;
                cornerX[i * 2 + 1] = crossing.MinimumX + 0.5f;
                cornerZ[i * 2] = crossing.MinimumZ;
                cornerZ[i * 2 + 1] = crossing.MaximumZ;
            }
            else
            {
                cornerX[i * 2] = crossing.MinimumX;
                cornerX[i * 2 + 1] = crossing.MaximumX;
                cornerZ[i * 2] = crossing.MinimumZ + 0.5f;
                cornerZ[i * 2 + 1] = crossing.MinimumZ + 0.5f;
            }
        }

        if (goalRectangle < 0) return;

        var open = new PriorityQueue<int, float>();
        var settled = new bool[corners];
        var goalGround = mesh.All[goalRectangle];
        foreach (var crossing in mesh.CrossingsOf(goalRectangle))
        {
            for (var end = 0; end < 2; end++)
            {
                var corner = crossing * 2 + end;
                var seed = LegBetween(
                    goal.X,
                    goal.Z,
                    cornerX[corner],
                    cornerZ[corner],
                    goalRectangle,
                    goalGround.TraversalCost);
                if (seed >= cornerCost[corner]) continue;
                cornerCost[corner] = seed;
                open.Enqueue(corner, seed);
            }
        }

        while (open.TryDequeue(out var current, out _))
        {
            if (settled[current]) continue;
            settled[current] = true;
            SettledCrossings++;
            var cost = cornerCost[current];
            var crossingHere = mesh.Crossings[current >> 1];
            Expand(crossingHere.RectangleA, current, cost, open);
            Expand(crossingHere.RectangleB, current, cost, open);
        }
    }

    /// <summary>
    /// Relaxes every corner of a rectangle from one of its corners, in a straight line.
    /// </summary>
    /// <remarks>
    /// This is the funnel, expressed as a graph rather than as a sweep. The shortest route
    /// through a corridor of convex cells is a polyline that bends only where a portal ends —
    /// anywhere else it would be straightenable, and therefore was not shortest. So corners are
    /// the only places a path needs to be able to turn, and a straight leg between two of them
    /// inside one open rectangle is a real path with a real length.
    /// <para>
    /// It replaces measuring border-to-border at their nearest points, which was optimistic
    /// because it let a route enter and leave a rectangle at whichever pair of points happened
    /// to be closest, regardless of where it had actually come from. That is worth nine per cent
    /// on this map and, more to the point, it made the field report costs below the shortest
    /// route that exists. Corners err the other way — a path forced through a corner it could
    /// have cut is a little long — and an over-estimate is the safe direction: it never claims a
    /// route is cheaper than it is.
    /// </para>
    /// </remarks>
    private void Expand(int rectangle, int from, float cost, PriorityQueue<int, float> open)
    {
        var ground = mesh.All[rectangle];
        foreach (var other in mesh.CrossingsOf(rectangle))
        {
            for (var end = 0; end < 2; end++)
            {
                var corner = other * 2 + end;
                if (corner == from) continue;
                var next = cost + LegBetween(
                    cornerX[from],
                    cornerZ[from],
                    cornerX[corner],
                    cornerZ[corner],
                    rectangle,
                    ground.TraversalCost,
                    from,
                    corner);
                if (next >= cornerCost[corner]) continue;
                cornerCost[corner] = next;
                open.Enqueue(corner, next);
            }
        }
    }

    /// <summary>Seconds from this cell to the goal, or infinity if it cannot get there.</summary>
    /// <remarks>
    /// Read from a dense tile rather than computed, and that is not an optimisation — it is the
    /// difference between a field a body can follow and one it cannot. <see cref="AnalyticCostAt"/>
    /// is the minimum over a rectangle's corners, so its gradient points at a corner instead of
    /// smoothly towards the goal, and a body descending it walks to a corner and then turns.
    /// Corners are materialised waypoints, which is the failure §1 of <c>plan-rts.md</c> records
    /// and the reason a continuous field exists at all. Wiring the analytic version straight into
    /// the steering layer was tried and took the suite from 42 passing to 37, every failure in
    /// constricted ground.
    /// <para>
    /// So the mesh answers what routing searches and a tile answers what steering reads, which
    /// is what the design said all along. The tile is seeded from the analytic field around the
    /// region's edge and filled inwards by the same local search the fine layer has always used,
    /// so within it every term is exact — congestion, turns, elevation — and the gradient is
    /// continuous. The saving stands regardless: the tiles are a handful per order, where the
    /// portal router needed one region search per crossing it settled.
    /// </para>
    /// </remarks>
    public float CostAt(GridCell cell)
    {
        var partition = mesh.Partition;
        var region = partition.RegionOf(cell);
        if (!tiles.TryGetValue(region, out var tile))
        {
            tile = owner.FillRectangleTile(this, region);
            tiles[region] = tile;
        }

        return tile[partition.TileIndex(cell)];
    }

    /// <summary>The corner-graph answer, exact in cost and unusable as a gradient.</summary>
    public float AnalyticCostAt(GridCell cell)
    {
        var rectangle = index.RectangleAt(cell);
        if (rectangle < 0) return float.PositiveInfinity;
        var ground = mesh.All[rectangle];
        var best = float.PositiveInfinity;
        if (rectangle == goalRectangle)
        {
            best = LegBetween(cell.X, cell.Z, goal.X, goal.Z, rectangle, ground.TraversalCost);
        }

        foreach (var crossing in mesh.CrossingsOf(rectangle))
        {
            for (var end = 0; end < 2; end++)
            {
                var corner = crossing * 2 + end;
                var reach = cornerCost[corner];
                if (!float.IsFinite(reach)) continue;
                var candidate = reach + LegBetween(
                    cell.X,
                    cell.Z,
                    cornerX[corner],
                    cornerZ[corner],
                    rectangle,
                    ground.TraversalCost);
                if (candidate < best) best = candidate;
            }
        }

        return best;
    }

    /// <summary>Seconds for one leg across uniform ground, including the bend in it.</summary>
    /// <remarks>
    /// An octile path is a diagonal run and a straight run, so it bends exactly once — unless
    /// it is purely diagonal or purely axial, in which case it does not bend at all. The flat
    /// field this is measured against charges for time spent changing heading and the first cut
    /// of this did not, which is most of why it came out nine per cent under the shortest route
    /// it was approximating. One bend per leg is the cheap answer and it is charged here rather
    /// than added afterwards, so every path through the graph pays for its own corners.
    /// </remarks>
    private float Leg(float dx, float dz, float traversalCost)
    {
        var seconds = Cells(dx, dz) * secondsPerCell * traversalCost;
        return dx > 0f && dz > 0f ? seconds + bendSeconds : seconds;
    }

    /// <summary>Seconds for a straight leg across a rectangle, jams included.</summary>
    /// <remarks>
    /// A rectangle is uniform ground and has no notion of a jam, so congestion arrives by
    /// sampling the field along the line the leg actually takes. The fine layer charges mean
    /// pressure times the cells crossed; so does this, at a sample every few metres, with the
    /// same directional factor — joining a queue going your way costs the queue's speed, pushing
    /// into one coming the other way is dear.
    /// <para>
    /// Without it the route cost would ignore jams entirely, which is not a small inaccuracy: it
    /// deletes the behaviour invariant 15 exists to protect, and a crowd would stop splitting
    /// across exits. It is an approximation in one way — the true path might dodge a jam inside
    /// the rectangle where this charges for crossing it — and that errs toward routing around,
    /// which is the direction this coefficient was tuned to encourage anyway.
    /// </para>
    /// </remarks>
    private float LegBetween(
        float fromX,
        float fromZ,
        float toX,
        float toZ,
        int rectangle,
        float traversalCost,
        int fromCorner = -1,
        int toCorner = -1)
    {
        var dx = MathF.Abs(fromX - toX);
        var dz = MathF.Abs(fromZ - toZ);
        // Climb, on the same terms the fine field charges it. Without this the abstract layer is cheaper
        // than the ground it abstracts wherever the ground rolls, which breaks the invariant Expand is
        // written around — see PathService.ClimbSecondsAlong.
        //
        // <b>And it is the same answer every time, which is what §97 is about.</b> Between two fixed corners
        // the climb is a fact about the terrain, and the terrain is fixed for as long as the mesh is — both
        // are keyed by the navigation revision. Measured: 496,040 of these a click, costing 4.6M height
        // samples, recomputed in full for every field built on the same mesh. Corner pairs are cached; a leg
        // from a goal or a cell is not, because those move.
        var climb = fromCorner >= 0 && toCorner >= 0
            ? CachedCornerClimb(fromCorner, toCorner, fromX, fromZ, toX, toZ)
            : owner.ClimbSecondsAlong(fromX, fromZ, toX, toZ);
        var seconds = Leg(dx, dz, traversalCost) + climb;
        if (!anyPressure || !pressured[rectangle]) return seconds;

        var cells = Cells(dx, dz);
        if (cells <= 0f) return seconds;
        var samples = Math.Clamp((int)MathF.Ceiling(cells / CellsPerSample), 1, MaximumSamples);
        var travel = new Vector2(toX - fromX, toZ - fromZ);
        if (travel.LengthSquared() > 0.0001f) travel = Vector2.Normalize(travel);

        var pressure = 0f;
        for (var i = 0; i < samples; i++)
        {
            var t = (i + 0.5f) / samples;
            var cell = new GridCell(
                (int)MathF.Floor(fromX + (toX - fromX) * t),
                (int)MathF.Floor(fromZ + (toZ - fromZ) * t));
            var here = congestion.At(cell);
            if (here <= 0f) continue;
            pressure += here * congestion.DirectionalFactor(cell, travel);
        }

        return seconds + pressure / samples * cells * congestionSecondsPerPressure;
    }

    /// <summary>
    /// The climb between two corners, computed once per mesh and remembered.
    /// </summary>
    /// <remarks>
    /// <b>Directed, and the symmetric version cost eight times what it saved.</b> A climb charge is the sum of
    /// absolute height differences along a line, so in exact arithmetic reversing the line changes nothing —
    /// but it is <em>sampled</em>, from one end, so the two directions disagree by a sampling artefact.
    /// Sharing one entry between them made a leg cheaper one way than the other, which is precisely the
    /// invariant Expand is written around, and the Dijkstra answered by settling corners over and over: the
    /// field went from 290 ms to 2,353 while the climb calls it was meant to save fell by a factor of
    /// thirty-three. Twice the entries is the price of an answer that does not depend on which way it was
    /// asked.
    /// <para>
    /// The cache belongs to the mesh rather than to this field: every field built on the same decomposition
    /// asks the same questions, and the whole point is that the second field pays nothing for what the first
    /// one learned.
    /// </para>
    /// </remarks>
    private float CachedCornerClimb(
        int fromCorner,
        int toCorner,
        float fromX,
        float fromZ,
        float toX,
        float toZ)
    {
        // <b>A tuple key, because a packed long collided catastrophically.</b> The obvious key is
        // (from << 32) | to, and .NET hashes a long by folding its halves with XOR — which for two corner
        // indices under 2^16 is from ^ to, so thousands of distinct pairs share a bucket. With half a million
        // entries the lookups degrade to chain walks and the field went from 290 ms to 4,487. A ValueTuple
        // hashes through HashCode.Combine, which mixes.
        //
        // <b>And the coordinates rather than the corner indices, so the answer outlives the mesh.</b> A
        // corner index is a position in one decomposition; the climb is a property of the two points. Every
        // corner sits at a half-cell — see the constructor, which places them at MinimumX + 0.5f and the
        // like — so twice the coordinate is an exact integer and the pair is an exact key. §114 measured
        // what the old key cost: a placement change rebuilt the mesh, renumbered every corner, and made the
        // next click re-sample two and a half million heights that had not moved.
        var key = (Round2(fromX), Round2(fromZ), Round2(toX), Round2(toZ));
        if (cornerClimb.TryGetValue(key, out var cached)) return cached;
        var climbed = owner.ClimbSecondsAlong(fromX, fromZ, toX, toZ);
        cornerClimb[key] = climbed;
        return climbed;
    }

    /// <summary>
    /// Twice a half-cell coordinate, which is exactly an integer.
    /// </summary>
    /// <remarks>
    /// Rounded rather than truncated because the value is exact and truncation of an exact 3.0 that arrived
    /// as 2.9999998 is off by one — and off by one here is two different points sharing a cached climb,
    /// which is the one way this cache could be wrong rather than merely cold.
    /// </remarks>
    private static int Round2(float coordinate) => (int)MathF.Round(coordinate * 2f);

    /// <summary>Cells between samples along a leg, and the ceiling on how many.</summary>
    private const float CellsPerSample = 4f;
    private const int MaximumSamples = 24;






    private static int Separation(int fromLow, int fromHigh, int toLow, int toHigh)
    {
        if (toLow > fromHigh) return toLow - fromHigh;
        if (fromLow > toHigh) return fromLow - toHigh;
        return 0;
    }

    private static float Cells(float dx, float dz)
    {
        var diagonal = MathF.Min(dx, dz);
        return MathF.Max(dx, dz) - diagonal + diagonal * DiagonalCost;
    }
}

/// <summary>
/// Which rectangle a cell belongs to, as one sorted run list per row.
/// </summary>
/// <remarks>
/// A cell-to-rectangle array would be four bytes a cell — 5.8 MB at 600 m — which is the same
/// uniform spend the decomposition exists to stop. Rectangles are built as row runs, so the runs
/// are already there: one row holds a handful of them and a lookup is a binary search over that
/// row. On the ridge map the whole index is a few thousand entries.
/// </remarks>
internal sealed class RectangleIndex
{
    private readonly int[] rowStart;
    private readonly int[] runLow;
    private readonly int[] runHigh;
    private readonly int[] runRectangle;
    private readonly int rows;

    public int RunCount => runLow.Length;

    public RectangleIndex(WalkableRectangles mesh, int width, int height)
    {
        rows = height;
        var counts = new int[height + 1];
        for (var i = 0; i < mesh.Count; i++)
        {
            var rectangle = mesh.All[i];
            for (var z = rectangle.MinimumZ; z <= rectangle.MaximumZ; z++) counts[z + 1]++;
        }

        for (var z = 0; z < height; z++) counts[z + 1] += counts[z];
        rowStart = counts;
        var total = rowStart[height];
        runLow = new int[total];
        runHigh = new int[total];
        runRectangle = new int[total];

        var cursor = new int[height];
        var order = new int[total];
        for (var i = 0; i < mesh.Count; i++)
        {
            var rectangle = mesh.All[i];
            for (var z = rectangle.MinimumZ; z <= rectangle.MaximumZ; z++)
            {
                var slot = rowStart[z] + cursor[z]++;
                runLow[slot] = rectangle.MinimumX;
                runHigh[slot] = rectangle.MaximumX;
                runRectangle[slot] = i;
                order[slot] = slot;
            }
        }

        // Rows are filled in rectangle order, which is not left to right, and the lookup is a
        // binary search — so each row is sorted by where its runs start. Sorting an index and
        // permuting afterwards keeps the three parallel arrays in step.
        for (var z = 0; z < height; z++)
        {
            var from = rowStart[z];
            var length = cursor[z];
            if (length <= 1) continue;
            var keys = new int[length];
            var slots = new int[length];
            for (var i = 0; i < length; i++)
            {
                keys[i] = runLow[from + i];
                slots[i] = from + i;
            }

            Array.Sort(keys, slots);
            var lows = new int[length];
            var highs = new int[length];
            var owners = new int[length];
            for (var i = 0; i < length; i++)
            {
                lows[i] = runLow[slots[i]];
                highs[i] = runHigh[slots[i]];
                owners[i] = runRectangle[slots[i]];
            }

            Array.Copy(lows, 0, runLow, from, length);
            Array.Copy(highs, 0, runHigh, from, length);
            Array.Copy(owners, 0, runRectangle, from, length);
        }
    }

    public int RectangleAt(GridCell cell)
    {
        if (cell.Z < 0 || cell.Z >= rows) return -1;
        var low = rowStart[cell.Z];
        var high = rowStart[cell.Z + 1] - 1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            if (cell.X < runLow[middle]) high = middle - 1;
            else if (cell.X > runHigh[middle]) low = middle + 1;
            else return runRectangle[middle];
        }

        return -1;
    }
}
