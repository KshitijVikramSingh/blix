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
    private readonly float[] crossingCost;
    private readonly GridCell goal;
    private readonly int goalRectangle;

    /// <summary>Crossings the search settled, which is what this field cost to build.</summary>
    public int SettledCrossings { get; private set; }

    public RectangleFlowField(
        WalkableRectangles mesh,
        RectangleIndex index,
        GridCell goal,
        float secondsPerCell)
    {
        this.mesh = mesh;
        this.index = index;
        this.secondsPerCell = secondsPerCell;
        this.goal = goal;
        goalRectangle = index.RectangleAt(goal);

        crossingCost = new float[mesh.Crossings.Count];
        Array.Fill(crossingCost, float.PositiveInfinity);
        if (goalRectangle < 0) return;

        var open = new PriorityQueue<int, float>();
        var settled = new bool[crossingCost.Length];
        var goalGround = mesh.All[goalRectangle];
        foreach (var crossing in mesh.CrossingsOf(goalRectangle))
        {
            var seed = Octile(goal, mesh.Crossings[crossing]) * secondsPerCell * goalGround.TraversalCost;
            if (seed >= crossingCost[crossing]) continue;
            crossingCost[crossing] = seed;
            open.Enqueue(crossing, seed);
        }

        while (open.TryDequeue(out var current, out _))
        {
            if (settled[current]) continue;
            settled[current] = true;
            SettledCrossings++;
            var cost = crossingCost[current];
            var crossingHere = mesh.Crossings[current];
            Expand(crossingHere.RectangleA, current, crossingHere, cost, open);
            Expand(crossingHere.RectangleB, current, crossingHere, cost, open);
        }
    }

    private void Expand(
        int rectangle,
        int from,
        WalkableRectangles.Crossing fromCrossing,
        float cost,
        PriorityQueue<int, float> open)
    {
        var ground = mesh.All[rectangle];
        foreach (var other in mesh.CrossingsOf(rectangle))
        {
            if (other == from) continue;
            var next = cost + Octile(fromCrossing, mesh.Crossings[other]) *
                       secondsPerCell * ground.TraversalCost;
            if (next >= crossingCost[other]) continue;
            crossingCost[other] = next;
            open.Enqueue(other, next);
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
            best = Octile(cell, goal) * secondsPerCell * ground.TraversalCost;
        }

        foreach (var crossing in mesh.CrossingsOf(rectangle))
        {
            var reach = crossingCost[crossing];
            if (!float.IsFinite(reach)) continue;
            var candidate = reach + Octile(cell, mesh.Crossings[crossing]) *
                            secondsPerCell * ground.TraversalCost;
            if (candidate < best) best = candidate;
        }

        return best;
    }

    private static float Octile(GridCell from, GridCell to)
    {
        var dx = Math.Abs(from.X - to.X);
        var dz = Math.Abs(from.Z - to.Z);
        return Cells(dx, dz);
    }

    /// <summary>
    /// Octile distance from a cell to the nearest part of a crossing.
    /// </summary>
    /// <remarks>
    /// Nearest part, not midpoint. A crossing is a whole run of shared border, and pricing a
    /// route to the middle of it would make every crowd going through a wide opening converge
    /// on its centre — which is the funnelling this decomposition exists to avoid. Clamping a
    /// point into an axis-aligned box is the whole of the geometry.
    /// </remarks>
    private static float Octile(GridCell from, WalkableRectangles.Crossing crossing)
    {
        var x = Math.Clamp(from.X, crossing.MinimumX, crossing.MaximumX);
        var z = Math.Clamp(from.Z, crossing.MinimumZ, crossing.MaximumZ);
        return Cells(Math.Abs(from.X - x), Math.Abs(from.Z - z));
    }

    private static float Octile(
        WalkableRectangles.Crossing from,
        WalkableRectangles.Crossing to)
    {
        var dx = Separation(from.MinimumX, from.MaximumX, to.MinimumX, to.MaximumX);
        var dz = Separation(from.MinimumZ, from.MaximumZ, to.MinimumZ, to.MaximumZ);
        return Cells(dx, dz);
    }

    private static int Separation(int fromLow, int fromHigh, int toLow, int toHigh)
    {
        if (toLow > fromHigh) return toLow - fromHigh;
        if (fromLow > toHigh) return fromLow - toHigh;
        return 0;
    }

    private static float Cells(int dx, int dz)
    {
        var diagonal = Math.Min(dx, dz);
        return Math.Max(dx, dz) - diagonal + diagonal * DiagonalCost;
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
