namespace RTSGame.Simulation.Terrain;

/// <summary>
/// Lowers whatever is too steep, so a finished surface honours a grade the plan only promised.
/// </summary>
/// <remarks>
/// <b>Why this has to exist now and did not before.</b> <c>ReliefPlan.SteepestGrade</c> is computed from the
/// shapes, and its own remarks explain the deal: "from the shapes rather than from the height field, so it
/// can be checked before a single vertex is written". That deal held while the shapes were the ground. It
/// stops holding the moment <see cref="Erosion"/> runs, because steepening valley sides and headwaters is
/// the mechanism by which erosion works rather than an accident of it — so a budget checked before erosion
/// is a budget checked on a different surface.
/// <para>
/// Five separate grade bugs were found in this file's neighbourhood by measuring the finished field against
/// the claimed number instead of trusting the claim, and the largest of them was a cliff of 4.91 grade
/// assembled entirely out of a normalisation constant. The lesson taken from that was to trust the
/// measurement; this is the same lesson applied one step earlier — make the surface obey, rather than
/// hoping the process that made it did.
/// </para>
/// <para>
/// <b>Ascending order, lowering only, and that is what makes one pass enough.</b> Walk cells from the lowest
/// upward; at each one, any neighbour that stands too far above it is pulled down to the highest it is
/// allowed to be. A cell is only ever lowered by a cell <em>below</em> it, which has already been settled,
/// and lowering a cell can only make it easier for the cells above it to comply — so nothing that has been
/// visited can be invalidated by what comes after.
/// </para>
/// <para>
/// It preserves the drainage pattern rather than flattening it: the cut is proportional to how far over
/// budget a place is, so a valley keeps its valley and only the parts that had become genuinely
/// unclimbable move. What it costs is amplitude in the steepest few per cent of the map, which is the right
/// thing to spend — a map whose high ground is a cliff is a map most of which is closed.
/// </para>
/// </remarks>
internal static class GradeLimit
{
    /// <param name="insetCells">
    /// How many cells in from the lattice border to leave alone. The map's frontier is meant to be
    /// unclimbable, so it is exempt — limiting it would cap a mountain wall at a walkable slope, and the
    /// exemption is also what lets the biome classifier tell crag from scree with a plain grade test
    /// rather than a special case: the interior cannot reach a crag grade, because this bounded it.
    /// </param>
    public static void Apply(float[] heights, int side, float cellMetres, float maxGrade, int insetCells = 0)
    {
        ArgumentNullException.ThrowIfNull(heights);
        if (maxGrade <= 0f) return;

        // A key sort rather than a delegate, and unobservably unstable for the same reason as the one in
        // Drainage.Accumulate: a cell only ever lowers a neighbour standing strictly above it, so two cells at
        // exactly the same height never act on each other and their relative order cannot be seen.
        var order = new int[heights.Length];
        var keys = new float[heights.Length];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
            keys[i] = heights[i];
        }

        Array.Sort(keys, order);

        var straight = maxGrade * cellMetres;
        var diagonal = maxGrade * cellMetres * 1.41421356f;

        var inset = Math.Max(0, insetCells);
        bool Interior(int x, int z) =>
            x >= inset && z >= inset && x < side - inset && z < side - inset;

        foreach (var index in order)
        {
            var x = index % side;
            var z = index / side;
            if (!Interior(x, z)) continue;
            var here = heights[index];
            for (var k = 0; k < 8; k++)
            {
                var nx = x + Offsets[k].X;
                var nz = z + Offsets[k].Z;
                if (nx < 0 || nz < 0 || nx >= side || nz >= side) continue;
                if (!Interior(nx, nz)) continue;
                var next = nz * side + nx;
                var allowed = here + (Offsets[k].Diagonal ? diagonal : straight);
                if (heights[next] > allowed) heights[next] = allowed;
            }
        }
    }

    /// <summary>The steepest grade actually present between adjacent lattice samples.</summary>
    /// <remarks>
    /// The measurement the sweep prints beside the claimed number. Kept next to the limiter on purpose: the
    /// thing that enforces a budget and the thing that checks it should be readable together, because the
    /// interesting failure is the two disagreeing.
    /// </remarks>
    public static float Measure(float[] heights, int side, float cellMetres)
    {
        ArgumentNullException.ThrowIfNull(heights);
        var steepest = 0f;
        for (var z = 0; z < side; z++)
        for (var x = 0; x < side; x++)
        {
            var index = z * side + x;
            for (var k = 0; k < 8; k++)
            {
                var nx = x + Offsets[k].X;
                var nz = z + Offsets[k].Z;
                if (nx < 0 || nz < 0 || nx >= side || nz >= side) continue;
                var run = cellMetres * (Offsets[k].Diagonal ? 1.41421356f : 1f);
                var grade = MathF.Abs(heights[nz * side + nx] - heights[index]) / run;
                steepest = MathF.Max(steepest, grade);
            }
        }

        return steepest;
    }

    private static readonly (int X, int Z, bool Diagonal)[] Offsets =
    {
        (1, 0, false), (-1, 0, false), (0, 1, false), (0, -1, false),
        (1, 1, true), (1, -1, true), (-1, 1, true), (-1, -1, true),
    };
}
