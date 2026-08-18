using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Navigation;

/// <summary>
/// Walkable ground decomposed into maximal axis-aligned rectangles of uniform ground.
/// </summary>
/// <remarks>
/// The first piece of the adaptive partition. What the router has today is a fixed 32 m grid
/// and a test for whether a region happens to be uniform, and where one is, crossing it is
/// arithmetic rather than a search. That works so well on open ground that an empty 600 m map
/// routes with no searches at all — and falls over the moment a feature appears, because a
/// single ridge cell makes its whole 4,096-cell region non-uniform. On the ridge map more than
/// half the regions fail the test, and that is the entire 5–161 ms a move order costs.
/// <para>
/// The fix is not a different algorithm, it is a partition that follows the ground instead of a
/// grid drawn over it. A rectangle here is uniform by construction, so crossing one is always
/// the arithmetic case; the obstacle gets its own small rectangles rather than condemning the
/// region around it.
/// </para>
/// <para>
/// Rectangles rather than triangles, deliberately. Every coordinate is an integer cell index,
/// so the decomposition has no floating-point tie-breaks to make deterministic — and lockstep
/// LAN and career-to-career persistence both rest on bit-identical simulation. It also keeps
/// the fine raster underneath meaning exactly what it meant, which is what stops every tuned
/// constant in <c>plan-rts.md</c> needing re-derivation.
/// </para>
/// </remarks>
internal sealed class WalkableRectangles
{
    /// <summary>Height difference two cells may have and still count as the same ground.</summary>
    /// <remarks>
    /// Crossing a rectangle is priced as flat, so this bounds the error that approximation can
    /// make. Five centimetres over a rectangle is well inside the noise of a route quoted in
    /// seconds, and it is small enough that a ridge never merges into the plain beside it.
    /// </remarks>
    public const float HeightTolerance = 0.05f;

    /// <summary>A run of uniform walkable ground, in inclusive cell coordinates.</summary>
    public readonly record struct Rectangle(
        int MinimumX,
        int MinimumZ,
        int MaximumX,
        int MaximumZ,
        float TraversalCost,
        float Height)
    {
        public int Width => MaximumX - MinimumX + 1;
        public int Depth => MaximumZ - MinimumZ + 1;
        public int Area => Width * Depth;
    }

    private readonly List<Rectangle> rectangles = new();

    public IReadOnlyList<Rectangle> All => rectangles;
    public int Count => rectangles.Count;
    /// <summary>Walkable cells the decomposition covers, which must be all of them.</summary>
    public int CoveredCells { get; private set; }
    /// <summary>Body radius this decomposition is valid for.</summary>
    public float AgentRadius { get; }
    /// <summary>Terrain revision it was built against.</summary>
    public int NavigationRevision { get; }

    private WalkableRectangles(float agentRadius, int navigationRevision)
    {
        AgentRadius = agentRadius;
        NavigationRevision = navigationRevision;
    }

    /// <summary>
    /// Greedy row runs, merged downwards while the run above matches exactly.
    /// </summary>
    /// <remarks>
    /// The standard maximal-rectangle sweep, and it is chosen over anything cleverer for the
    /// same reason as rectangles over triangles: it is integer arithmetic in a fixed order, so
    /// two machines given the same raster produce the same rectangles in the same sequence.
    /// It does not produce the <em>fewest</em> rectangles — an optimal decomposition is a much
    /// harder problem and buys little here, because the shape that matters is a long open run
    /// and this finds those.
    /// </remarks>
    public static WalkableRectangles Build(NavigationGrid grid, float agentRadius)
    {
        var result = new WalkableRectangles(agentRadius, grid.Revision);

        // One open rectangle per column-run carried down from the previous row.
        var openStart = new int[grid.Width];
        var openEnd = new int[grid.Width];
        var openTop = new int[grid.Width];
        var openCost = new float[grid.Width];
        var openHeight = new float[grid.Width];
        var openCount = 0;

        var runStart = new int[grid.Width];
        var runEnd = new int[grid.Width];
        var runCost = new float[grid.Width];
        var runHeight = new float[grid.Width];

        for (var z = 0; z < grid.Height; z++)
        {
            var runCount = 0;
            var x = 0;
            while (x < grid.Width)
            {
                var cell = new GridCell(x, z);
                if (!grid.IsWalkable(cell, agentRadius))
                {
                    x++;
                    continue;
                }

                var cost = grid.TraversalCost(cell);
                var height = grid.HeightAt(cell);
                var end = x;
                while (end + 1 < grid.Width)
                {
                    var next = new GridCell(end + 1, z);
                    if (!grid.IsWalkable(next, agentRadius)) break;
                    if (grid.TraversalCost(next) != cost) break;
                    if (MathF.Abs(grid.HeightAt(next) - height) > HeightTolerance) break;
                    end++;
                }

                runStart[runCount] = x;
                runEnd[runCount] = end;
                runCost[runCount] = cost;
                runHeight[runCount] = height;
                runCount++;
                result.CoveredCells += end - x + 1;
                x = end + 1;
            }

            // Carry a run down only when it matches an open one exactly. Anything else closes
            // the open rectangle where it stood — a rectangle that widened or narrowed would
            // no longer be a rectangle.
            var carriedCount = 0;
            var carriedStart = new int[runCount];
            var carriedEnd = new int[runCount];
            var carriedTop = new int[runCount];
            var carriedCost = new float[runCount];
            var carriedHeight = new float[runCount];
            var matched = new bool[openCount];

            for (var run = 0; run < runCount; run++)
            {
                var extended = -1;
                for (var open = 0; open < openCount; open++)
                {
                    if (matched[open]) continue;
                    if (openStart[open] != runStart[run] || openEnd[open] != runEnd[run]) continue;
                    if (openCost[open] != runCost[run]) continue;
                    if (MathF.Abs(openHeight[open] - runHeight[run]) > HeightTolerance) continue;
                    extended = open;
                    break;
                }

                carriedStart[carriedCount] = runStart[run];
                carriedEnd[carriedCount] = runEnd[run];
                carriedCost[carriedCount] = runCost[run];
                carriedHeight[carriedCount] = runHeight[run];
                if (extended >= 0)
                {
                    matched[extended] = true;
                    carriedTop[carriedCount] = openTop[extended];
                }
                else
                {
                    carriedTop[carriedCount] = z;
                }

                carriedCount++;
            }

            for (var open = 0; open < openCount; open++)
            {
                if (matched[open]) continue;
                result.rectangles.Add(new Rectangle(
                    openStart[open],
                    openTop[open],
                    openEnd[open],
                    z - 1,
                    openCost[open],
                    openHeight[open]));
            }

            for (var i = 0; i < carriedCount; i++)
            {
                openStart[i] = carriedStart[i];
                openEnd[i] = carriedEnd[i];
                openTop[i] = carriedTop[i];
                openCost[i] = carriedCost[i];
                openHeight[i] = carriedHeight[i];
            }

            openCount = carriedCount;
        }

        for (var open = 0; open < openCount; open++)
        {
            result.rectangles.Add(new Rectangle(
                openStart[open],
                openTop[open],
                openEnd[open],
                grid.Height - 1,
                openCost[open],
                openHeight[open]));
        }

        return result;
    }
}
