using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Navigation;

/// <summary>
/// A provable lower bound on the time from any cell to one goal, over the same decomposition.
/// </summary>
/// <remarks>
/// <b>A second oracle, because the first one has the wrong sign.</b> §123: the cost field is built never to
/// report <em>below</em> the true cost — a route priced cheaper than reality is a promise the ground cannot
/// keep, and <c>--routingtest</c> exists to catch it drifting under. An A* heuristic must never report
/// <em>above</em> it. Those contracts are opposites, which is why §121's guided search returned routes up to
/// five times too long and why no amount of tuning that field would have fixed it.
/// <para>
/// So this prices the same graph in the other direction. Every term is relaxed until it cannot exceed the
/// truth:
/// </para>
/// <list type="bullet">
/// <item><b>Crossings are segments, not corners.</b> The cost field routes through the two ends of a crossing
/// because an endpoint is a definite place to aim at; that is the term carrying §123's worst case, since a
/// cell one metre from the goal in a neighbouring rectangle is sent out to a corner and back. The nearest
/// point on an axis-aligned segment is a clamp, and no path across a rectangle can be shorter than the gap
/// between the two borders it uses.</item>
/// <item><b>Distance is Euclidean.</b> The搜 search charges octile steps, where a diagonal costs about 1.414
/// cells; the straight line is never longer.</item>
/// <item><b>Ground is charged at the cheapest surface that exists.</b> The same
/// <see cref="Terrain.TerrainSurfaceRules.MinimumPathCost"/> the flat estimate uses, rather than each
/// rectangle's own cost, because a lower bound may not assume the body pays more than the minimum anywhere.</item>
/// <item><b>No climb and no bend.</b> Both are real costs and both are therefore omitted: zero is a lower
/// bound on each. Climb is where most of the tightness goes on rolling ground, and recovering it means
/// bounding the net height gain between two segments rather than the total variation along a straight line —
/// which is the obvious next tightening if this proves too loose to steer with.</item>
/// </list>
/// <para>
/// <b>Why the result is sound.</b> Any real path from a cell to the goal passes through a sequence of
/// rectangles, and consecutive rectangles in that sequence share a crossing — so the path corresponds to a
/// walk in this graph. Each of its legs is at least the straight-line gap between the two crossings it joins,
/// charged at no more than the cheapest ground. This takes the minimum over all such walks, so it cannot
/// exceed the cost of the real path.
/// </para></remarks>
internal sealed class LowerBoundField
{
    private readonly WalkableRectangles mesh;
    private readonly RectangleIndex index;
    private readonly float[] crossingCost;
    private readonly float secondsPerCell;
    private readonly float cheapestGround;
    private readonly float goalX;
    private readonly float goalZ;
    private readonly int goalRectangle;

    /// <summary>Crossings settled while solving, for a report that would rather count than assume.</summary>
    public int SettledCrossings { get; }

    /// <summary>
    /// Legs this graph prices at nothing, which is where its tightness goes.
    /// </summary>
    /// <remarks>
    /// <b>The structural weakness of the construction, counted rather than argued.</b> Each leg is priced from
    /// the nearest point of one crossing to the nearest point of the next, and nothing requires two consecutive
    /// legs to use the <em>same</em> point on the border they share — so a path may reposition along every
    /// crossing for free. Where two crossings of one rectangle touch or overlap in projection the leg between
    /// them costs zero outright, and a chain of those crosses the map for nothing. If that share is large the
    /// bound cannot be tightened by pricing the terms better; it needs the entry point carried along the walk,
    /// which is any-angle propagation and a different piece of work.
    /// </remarks>
    public int FreeLegs { get; }

    public int PricedLegs { get; }

    public LowerBoundField(
        WalkableRectangles mesh,
        RectangleIndex index,
        GridCell goal,
        float secondsPerCell,
        float cheapestGround)
    {
        this.mesh = mesh;
        this.index = index;
        this.secondsPerCell = secondsPerCell;
        this.cheapestGround = cheapestGround;
        goalX = goal.X + 0.5f;
        goalZ = goal.Z + 0.5f;
        goalRectangle = index.RectangleAt(goal);

        crossingCost = new float[mesh.Crossings.Count];
        Array.Fill(crossingCost, float.PositiveInfinity);
        if (goalRectangle < 0) return;

        var queue = new PriorityQueue<int, float>();
        foreach (var crossing in mesh.CrossingsOf(goalRectangle))
        {
            var seeded = PointToCrossing(goalX, goalZ, crossing);
            if (seeded >= crossingCost[crossing]) continue;
            crossingCost[crossing] = seeded;
            queue.Enqueue(crossing, seeded);
        }

        var closed = new bool[crossingCost.Length];
        var settled = 0;
        var free = 0;
        var priced = 0;
        while (queue.TryDequeue(out var current, out var cost))
        {
            if (closed[current]) continue;
            closed[current] = true;
            settled++;
            var crossing = mesh.Crossings[current];
            // Both sides: a crossing is a border, and a path may arrive at it from either rectangle.
            foreach (var rectangle in new[] { crossing.RectangleA, crossing.RectangleB })
            {
                if (rectangle < 0) continue;
                foreach (var next in mesh.CrossingsOf(rectangle))
                {
                    if (closed[next]) continue;
                    var leg = CrossingToCrossing(current, next);
                    if (leg <= 0f) free++;
                    else priced++;
                    var relaxed = cost + leg;
                    if (relaxed >= crossingCost[next]) continue;
                    crossingCost[next] = relaxed;
                    queue.Enqueue(next, relaxed);
                }
            }
        }

        SettledCrossings = settled;
        FreeLegs = free;
        PricedLegs = priced;
    }

    /// <summary>Seconds no route from this cell to the goal can beat, or infinity if it is unplaced.</summary>
    public float At(GridCell cell)
    {
        var rectangle = index.RectangleAt(cell);
        if (rectangle < 0) return float.PositiveInfinity;
        var x = cell.X + 0.5f;
        var z = cell.Z + 0.5f;
        // In the goal's own rectangle the straight line is available and is the tightest bound there is.
        var best = rectangle == goalRectangle
            ? Seconds(Distance(x, z, goalX, goalZ))
            : float.PositiveInfinity;
        foreach (var crossing in mesh.CrossingsOf(rectangle))
        {
            if (!float.IsFinite(crossingCost[crossing])) continue;
            var candidate = PointToCrossing(x, z, crossing) + crossingCost[crossing];
            if (candidate < best) best = candidate;
        }

        return best;
    }

    private float Seconds(float cells) => cells * secondsPerCell * cheapestGround;

    private static float Distance(float fromX, float fromZ, float toX, float toZ)
    {
        var dx = fromX - toX;
        var dz = fromZ - toZ;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Seconds to the nearest point of a crossing, which for an axis-aligned run is a clamp.</summary>
    private float PointToCrossing(float x, float z, int crossing)
    {
        var segment = mesh.Crossings[crossing];
        var nearestX = Math.Clamp(x, segment.MinimumX + 0.5f, segment.MaximumX + 0.5f);
        var nearestZ = Math.Clamp(z, segment.MinimumZ + 0.5f, segment.MaximumZ + 0.5f);
        return Seconds(Distance(x, z, nearestX, nearestZ));
    }

    /// <summary>
    /// Seconds between the nearest points of two crossings.
    /// </summary>
    /// <remarks>
    /// Both runs are axis-aligned, so the gap between them is the gap between their projections on each axis
    /// taken independently — no segment-intersection arithmetic, and zero where they touch or overlap.
    /// </remarks>
    private float CrossingToCrossing(int from, int to)
    {
        var a = mesh.Crossings[from];
        var b = mesh.Crossings[to];
        var dx = AxisGap(a.MinimumX, a.MaximumX, b.MinimumX, b.MaximumX);
        var dz = AxisGap(a.MinimumZ, a.MaximumZ, b.MinimumZ, b.MaximumZ);
        return Seconds(MathF.Sqrt(dx * dx + dz * dz));
    }

    private static float AxisGap(int fromLow, int fromHigh, int toLow, int toHigh)
    {
        if (toLow > fromHigh) return toLow - fromHigh;
        if (fromLow > toHigh) return fromLow - toHigh;
        return 0f;
    }
}
