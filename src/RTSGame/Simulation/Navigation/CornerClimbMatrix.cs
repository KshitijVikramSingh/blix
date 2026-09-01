namespace RTSGame.Simulation.Navigation;

/// <summary>
/// Climb charges between the corners of one rectangle, as a flat array rather than a hash.
/// </summary>
/// <remarks>
/// <b>Because the field solve turned out to be a dictionary.</b> §126 measured 480,048 lookups per pair of
/// field builds and found them costing 120 ns each in Release — 72% of the whole solve, about a fifth of the
/// click after a placement change. §127 then found the cost was the key type rather than the hashing: a mixed
/// 64-bit key took the Release solve from 76 ms to 43, and made Debug's <em>worse</em>, 48 to 79. A structure
/// whose speed depends on which build you are in is a structure to stop using.
/// <para>
/// Every leg the corner Dijkstra prices joins two corners of one rectangle, and both are named by their
/// position in that rectangle's own crossing list — so the answers form a small square matrix per rectangle
/// and need no hash at all. The whole set is around eight hundred kilobytes on a 600 m map, held against the
/// mesh it is indexed by, and a lookup is an array read with the locality to match: one rectangle's legs are
/// contiguous.
/// </para>
/// <para>
/// <b>Filled from the durable cache, not from the ground.</b> The coordinate-keyed table survives a mesh
/// rebuild and that is the whole of §115 — a placement change must not resample two and a half million
/// heights that did not move. So a miss here consults that table, and only a miss there samples terrain. The
/// matrix is the fast path for the second and every later field built on one mesh; the table is what makes the
/// first one cheap.
/// </para></remarks>
internal sealed class CornerClimbMatrix
{
    private readonly int[] offset;
    private readonly int[] width;
    private readonly float[] values;

    /// <summary>Entries the matrix can hold, for a report that would rather state its size than imply it.</summary>
    public int Capacity => values.Length;

    public CornerClimbMatrix(WalkableRectangles mesh)
    {
        offset = new int[mesh.Count];
        width = new int[mesh.Count];
        var total = 0;
        for (var rectangle = 0; rectangle < mesh.Count; rectangle++)
        {
            var corners = mesh.CrossingsOf(rectangle).Length * 2;
            offset[rectangle] = total;
            width[rectangle] = corners;
            total += corners * corners;
        }

        values = new float[total];
        // NaN as "not yet known", because zero is a real climb charge on flat ground and the commonest one.
        Array.Fill(values, float.NaN);
    }

    /// <summary>The climb between two of a rectangle's corners, or NaN if nobody has asked yet.</summary>
    public float At(int rectangle, int fromLocal, int toLocal) =>
        values[offset[rectangle] + fromLocal * width[rectangle] + toLocal];

    public void Set(int rectangle, int fromLocal, int toLocal, float climb) =>
        values[offset[rectangle] + fromLocal * width[rectangle] + toLocal] = climb;
}
