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
    /// <summary>A run of uniform walkable ground, in inclusive cell coordinates.</summary>
    /// <remarks>
    /// <b>The height is gone, and so is the tolerance that used to gate merging on it.</b> A rectangle held
    /// the height of its first cell and nothing ever read it; what the field did do was justify a rule that
    /// two cells could only share a rectangle if their heights agreed to within five centimetres. Five
    /// centimetres is exactly what a tenth grade climbs across one half-metre cell, so the rule said
    /// <em>flat</em> where it meant <em>no unclimbable step</em> — and measured on a 600 m map, one rectangle
    /// became 53,133 at three metres of relief and 250,796 at twenty-four, with the worst route in the map
    /// 52x longer than it needed to be.
    /// <para>
    /// The rule it meant is a local one and the raster already knows it: two cells may share a rectangle if
    /// a body can step between them. So a hillside of even gradient is one rectangle and a cliff still
    /// splits one, which is what the tolerance was reaching for by proxy. What is given up is that crossing
    /// a rectangle is priced on the flat: over ground of gradient <c>g</c> that under-prices by about
    /// <c>g²/2</c>, which is a per cent at a tenth grade and six at the steepest band — and the bands are
    /// what bound it, since a rectangle cannot span two of them.
    /// </para>
    /// </remarks>
    public readonly record struct Rectangle(
        int MinimumX,
        int MinimumZ,
        int MaximumX,
        int MaximumZ,
        float TraversalCost)
    {
        public int Width => MaximumX - MinimumX + 1;
        public int Depth => MaximumZ - MinimumZ + 1;
        public int Area => Width * Depth;
    }

    /// <summary>
    /// A shared border between two rectangles: the whole run of it, not a point on it.
    /// </summary>
    /// <remarks>
    /// Kept as a segment because a portal reduced to its midpoint is an approximation that
    /// costs real route quality — every route through a wide opening detours to the middle of
    /// it. Segments are axis-aligned and integer, so the nearest point on one is a clamp.
    /// </remarks>
    public readonly record struct Crossing(
        int RectangleA,
        int RectangleB,
        int MinimumX,
        int MinimumZ,
        int MaximumX,
        int MaximumZ);

    private readonly List<Rectangle> rectangles = new();
    private readonly List<Crossing> crossings = new();
    private int[] crossingStart = Array.Empty<int>();
    private int[] rectangleCrossings = Array.Empty<int>();

    /// <summary>The fixed partition the steering tiles are stored on.</summary>
    public RegionPartition Partition { get; private set; } = null!;

    public IReadOnlyList<Rectangle> All => rectangles;
    public int Count => rectangles.Count;
    public IReadOnlyList<Crossing> Crossings => crossings;

    /// <summary>Crossings on the border of one rectangle, in ascending crossing order.</summary>
    /// <summary>
    /// Which connected region of walkable ground each rectangle belongs to.
    /// </summary>
    /// <remarks>
    /// <b>Reachability, which this decomposition already knew and never said.</b> Two rectangles joined by a
    /// crossing are walkable one to the other, so the connected components of the crossing graph are exactly
    /// the islands of ground a body can move within — and asking "can I get there from here" becomes two array
    /// reads instead of a search. §102 measured what not having this costs: an order to a clearing inside a
    /// wood sent twenty bodies to exhaust a quarter of a million cells each, eighteen seconds, to discover
    /// something a component label answers immediately.
    /// <para>
    /// Union-find with path halving, built once with the mesh and shared by every field on it. About seventeen
    /// thousand crossings on a village, which is a millisecond of work to save eighteen seconds of searching.
    /// </para>
    /// </remarks>
    public int ComponentOf(int rectangle)
    {
        components ??= BuildComponents();
        return rectangle >= 0 && rectangle < components.Length ? components[rectangle] : -1;
    }

    private int[]? components;

    private int[] BuildComponents()
    {
        var parent = new int[rectangles.Count];
        for (var i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int node)
        {
            while (parent[node] != node)
            {
                parent[node] = parent[parent[node]];
                node = parent[node];
            }

            return node;
        }

        foreach (var crossing in crossings)
        {
            var a = Find(crossing.RectangleA);
            var b = Find(crossing.RectangleB);
            if (a != b) parent[a] = b;
        }

        // Flattened to representatives so a caller comparing two labels is comparing numbers.
        var labels = new int[parent.Length];
        for (var i = 0; i < labels.Length; i++) labels[i] = Find(i);
        return labels;
    }

    public ReadOnlySpan<int> CrossingsOf(int rectangle) => rectangleCrossings.AsSpan(
        crossingStart[rectangle],
        crossingStart[rectangle + 1] - crossingStart[rectangle]);
    /// <summary>Walkable cells the decomposition covers, which must be all of them.</summary>
    public int CoveredCells { get; private set; }

    /// <summary>
    /// Bytes this decomposition holds, for the question of what a second body radius costs.
    /// </summary>
    /// <remarks>
    /// The payload rather than the allocation: a rectangle is four cell indices and two floats, a
    /// crossing is six cell indices, and the two index arrays are one entry per rectangle and two
    /// per crossing. List capacity slack is left out because the number this exists to answer is
    /// how the structure scales with the number of body radii, not what the allocator rounded up to.
    /// </remarks>
    public long ResidentBytes =>
        (long)rectangles.Count * 24 +
        (long)crossings.Count * 24 +
        (long)crossingStart.Length * sizeof(int) +
        (long)rectangleCrossings.Length * sizeof(int);
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
        var openCount = 0;

        var runStart = new int[grid.Width];
        var runEnd = new int[grid.Width];
        var runCost = new float[grid.Width];
        // Whether each run can be reached from the row above it, cell by cell. A run that cannot is the
        // bottom of a step, and merging it upward would let a route walk off a cliff for free.
        var runCarries = new bool[grid.Width];

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
                var end = x;
                while (end + 1 < grid.Width)
                {
                    var next = new GridCell(end + 1, z);
                    if (!grid.IsWalkable(next, agentRadius)) break;
                    if (grid.TraversalCost(next) != cost) break;
                    // The step, not the height. See the remarks on Rectangle.
                    if (!grid.CanTraverse(new GridCell(end, z), next, agentRadius)) break;
                    end++;
                }

                // Asked once per run rather than per candidate match below: whether this row's ground is
                // reachable from the row above is a property of the ground, not of which open rectangle
                // happens to be sitting over it.
                var carries = z > 0;
                for (var column = x; column <= end && carries; column++)
                {
                    carries = grid.CanTraverse(
                        new GridCell(column, z - 1), new GridCell(column, z), agentRadius);
                }

                runStart[runCount] = x;
                runEnd[runCount] = end;
                runCost[runCount] = cost;
                runCarries[runCount] = carries;
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
            var matched = new bool[openCount];

            for (var run = 0; run < runCount; run++)
            {
                var extended = -1;
                for (var open = 0; open < openCount; open++)
                {
                    if (matched[open]) continue;
                    if (openStart[open] != runStart[run] || openEnd[open] != runEnd[run]) continue;
                    if (openCost[open] != runCost[run]) continue;
                    if (!runCarries[run]) continue;
                    extended = open;
                    break;
                }

                carriedStart[carriedCount] = runStart[run];
                carriedEnd[carriedCount] = runEnd[run];
                carriedCost[carriedCount] = runCost[run];
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
                    openCost[open]));
            }

            for (var i = 0; i < carriedCount; i++)
            {
                openStart[i] = carriedStart[i];
                openEnd[i] = carriedEnd[i];
                openTop[i] = carriedTop[i];
                openCost[i] = carriedCost[i];
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
                openCost[open]));
        }

        result.Partition = new RegionPartition(grid.Transform);
        result.FindCrossings(grid, agentRadius);
        return result;
    }

    /// <summary>
    /// Finds every run of border two rectangles share and a body can step across.
    /// </summary>
    /// <remarks>
    /// Built from an owner map — cell to rectangle — walked along each rectangle's four
    /// borders, so it costs the total perimeter rather than anything quadratic in the number
    /// of rectangles. The owner map is a build-time structure and goes when the build ends.
    /// <para>
    /// A run is broken wherever the neighbour changes or the step is not traversable, which
    /// matters: a rectangle above a ridge shares a border with the ridge's rectangles, but the
    /// height difference means a body cannot use it, and a crossing nobody can cross is worse
    /// than no crossing at all.
    /// </para>
    /// </remarks>
    private void FindCrossings(NavigationGrid grid, float agentRadius)
    {
        var owner = new int[grid.Width * grid.Height];
        Array.Fill(owner, -1);
        for (var index = 0; index < rectangles.Count; index++)
        {
            var rectangle = rectangles[index];
            for (var z = rectangle.MinimumZ; z <= rectangle.MaximumZ; z++)
            for (var x = rectangle.MinimumX; x <= rectangle.MaximumX; x++)
            {
                owner[z * grid.Width + x] = index;
            }
        }

        // Only two of the four sides are walked, because a border shared by two rectangles is
        // one crossing and would otherwise be found twice.
        for (var index = 0; index < rectangles.Count; index++)
        {
            var rectangle = rectangles[index];
            EmitSide(grid, agentRadius, owner, index, rectangle, horizontal: false);
            EmitSide(grid, agentRadius, owner, index, rectangle, horizontal: true);
        }

        var counts = new int[rectangles.Count + 1];
        foreach (var crossing in crossings)
        {
            counts[crossing.RectangleA + 1]++;
            counts[crossing.RectangleB + 1]++;
        }

        for (var i = 0; i < rectangles.Count; i++) counts[i + 1] += counts[i];
        crossingStart = counts;
        rectangleCrossings = new int[crossings.Count * 2];
        var cursor = new int[rectangles.Count];
        for (var i = 0; i < crossings.Count; i++)
        {
            var a = crossings[i].RectangleA;
            var b = crossings[i].RectangleB;
            rectangleCrossings[crossingStart[a] + cursor[a]++] = i;
            rectangleCrossings[crossingStart[b] + cursor[b]++] = i;
        }
    }

    private void EmitSide(
        NavigationGrid grid,
        float agentRadius,
        int[] owner,
        int index,
        Rectangle rectangle,
        bool horizontal)
    {
        // The right-hand side, or the lower one: each border is walked from one of its two
        // rectangles only.
        var alongStart = horizontal ? rectangle.MinimumX : rectangle.MinimumZ;
        var alongEnd = horizontal ? rectangle.MaximumX : rectangle.MaximumZ;
        var fixedLine = horizontal ? rectangle.MaximumZ : rectangle.MaximumX;
        var runNeighbour = -1;
        var runStart = 0;

        for (var along = alongStart; along <= alongEnd + 1; along++)
        {
            var neighbour = -1;
            if (along <= alongEnd)
            {
                var here = horizontal ? new GridCell(along, fixedLine) : new GridCell(fixedLine, along);
                var there = horizontal
                    ? new GridCell(along, fixedLine + 1)
                    : new GridCell(fixedLine + 1, along);
                if (grid.Contains(there) && grid.CanTraverse(here, there, agentRadius))
                {
                    var candidate = owner[there.Z * grid.Width + there.X];
                    if (candidate != index) neighbour = candidate;
                }
            }

            if (neighbour == runNeighbour) continue;
            if (runNeighbour >= 0)
            {
                crossings.Add(horizontal
                    ? new Crossing(index, runNeighbour, runStart, fixedLine, along - 1, fixedLine + 1)
                    : new Crossing(index, runNeighbour, fixedLine, runStart, fixedLine + 1, along - 1));
            }

            runNeighbour = neighbour;
            runStart = along;
        }
    }
}
