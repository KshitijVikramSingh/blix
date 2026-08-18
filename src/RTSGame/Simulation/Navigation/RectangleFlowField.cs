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
    // Two nodes per crossing, at the ends of the shared border and on the line between the two
    // rectangles — so a half cell out from either, which is where the border actually is.
    private readonly float[] cornerX;
    private readonly float[] cornerZ;
    private readonly float[] cornerCost;
    private readonly GridCell goal;
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
        float congestionSecondsPerPressure)
    {
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
                    ground.TraversalCost);
                if (next >= cornerCost[corner]) continue;
                cornerCost[corner] = next;
                open.Enqueue(corner, next);
            }
        }
    }

    /// <summary>Seconds from this cell to the goal, or infinity if it cannot get there.</summary>
    public float CostAt(GridCell cell)
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
        float traversalCost)
    {
        var dx = MathF.Abs(fromX - toX);
        var dz = MathF.Abs(fromZ - toZ);
        var seconds = Leg(dx, dz, traversalCost);
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
