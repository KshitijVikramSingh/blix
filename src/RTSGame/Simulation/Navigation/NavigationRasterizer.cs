using System.Numerics;
using RTSGame.Simulation.Placement;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Simulation.Navigation;

/// <summary>
/// Obstacle boxes bucketed by position, so a cell only measures against the ones near it.
/// </summary>
/// <remarks>
/// Clearance used to be every cell against every obstacle on the map. On open ground there
/// are no obstacles and it costs nothing, which is why it survived: the 30 m laboratory has a
/// handful of blocks and a 600 m plain has none at all. Put a ridge across that plain and the
/// height edges along it are tens of thousands of boxes, each measured against 1.44M cells —
/// billions of distance tests, and a map that never finishes loading.
/// <para>
/// Same rule as everywhere else here: only resolve where the answer can change. A box further
/// away than the nearest one already found cannot be the nearest, so buckets sized to
/// <see cref="Reach"/> mean the nine around a cell contain every box that could possibly win.
/// </para>
/// <para>
/// <see cref="Reach"/> also caps the reported clearance, and is deliberately larger than the
/// whole tuned world so that nothing measured there moves: the furthest a cell on a 30 m map
/// can be from anything is fifteen metres. Every consumer of clearance compares it against a
/// couple of body radii, so the cap is two orders of magnitude clear of anything that reads it.
/// </para>
/// </remarks>
internal sealed class ObstacleIndex
{
    /// <summary>
    /// How far a cell looks for obstacles, and the ceiling on reported clearance.
    /// </summary>
    /// <remarks>
    /// <b>Six metres, and it was thirty-two.</b> Nothing has ever needed a clearance value above about
    /// three: every consumer compares it against a small multiple of a body's radius — a constriction is
    /// <c>radius x 2</c> and open ground is <c>radius x 5</c>, which is 2.75 m for the widest body in the
    /// roster; a passage axis is only sought below <c>radius x 3</c>. Above that the answer is "open", and
    /// how open does not matter to anybody.
    /// <para>
    /// The thirty-two was what made the rasteriser quadratic in obstacle density. It is the radius the
    /// nearest-obstacle search has to be prepared to walk out to before it can say "nothing near", so on a
    /// map with an impassable forest it was <b>eleven billion box comparisons and 12.2 seconds</b> for one
    /// rebuild — and a settlement pays that again every time a felling opens ground. Six metres of
    /// headroom over the widest thing anybody asks about costs nothing and bounds the search.
    /// </para>
    /// <para>
    /// It does change one reported number: the movement benchmarks print a mean clearance, and open ground
    /// that used to average out to eight or ten metres now saturates at six. The figure is a diagnostic
    /// rather than a gate, and what it is for — telling a corridor from a field — happens well below the
    /// cap.
    /// </para>
    /// </remarks>
    public const float Reach = 6f;

    /// <summary>
    /// Side of a bucket, which is no longer <see cref="Reach"/>.
    /// </summary>
    /// <remarks>
    /// It used to be, and the two are not the same thing at all: <see cref="Reach"/> is how far a clearance
    /// value may go before it is clamped, and a bucket only has to be big enough that a nearest-box search
    /// terminates quickly. Tying them meant every cell examined a 96 m neighbourhood of obstacles to find
    /// something usually a metre away — which cost nothing on a map whose obstacles were a pond and some
    /// walls, and <b>eleven billion box comparisons and 12.2 seconds</b> on one with an impassable forest in
    /// it. Two metres is a couple of cells: dense obstacles are found in the first ring, and open ground
    /// walks out a few rings and stops at the clamp.
    /// </remarks>
    private const float BucketSize = 2f;

    /// <summary>First radius a tile looks in before widening. One bucket's worth.</summary>
    public const float NearPadding = BucketSize;

    private readonly List<(Vector2 Minimum, Vector2 Maximum)>?[] buckets;
    private readonly Vector2 origin;
    private readonly int columns;
    private readonly int rows;

    public ObstacleIndex(
        List<(Vector2 Minimum, Vector2 Maximum)> bounds,
        Vector2 minimum,
        Vector2 maximum)
    {
        origin = minimum;
        var extent = maximum - minimum;
        columns = Math.Max(1, (int)MathF.Ceiling(extent.X / BucketSize));
        rows = Math.Max(1, (int)MathF.Ceiling(extent.Y / BucketSize));
        buckets = new List<(Vector2, Vector2)>?[columns * rows];

        foreach (var box in bounds)
        {
            var (lowX, lowZ) = Bucket(box.Minimum);
            var (highX, highZ) = Bucket(box.Maximum);
            for (var z = lowZ; z <= highZ; z++)
            for (var x = lowX; x <= highX; x++)
            {
                var slot = z * columns + x;
                (buckets[slot] ??= new List<(Vector2, Vector2)>()).Add(box);
            }
        }
    }

    /// <summary>
    /// Every box that could be nearest to any point in a region, within <see cref="Reach"/>.
    /// </summary>
    /// <remarks>
    /// Asked once per tile of cells rather than once per cell, which is the point: the sixteen cells in a
    /// two-metre tile have almost the same neighbourhood, and gathering it sixteen times over 1.44M cells is
    /// sixteen times the work for the same answer. In open country the result is empty and all sixteen cells
    /// take the clamp without looking at anything.
    /// </remarks>
    public void GatherWithin(
        Vector2 minimum,
        Vector2 maximum,
        float padding,
        List<(Vector2 Minimum, Vector2 Maximum)> results)
    {
        results.Clear();
        var pad = new Vector2(padding);
        var (lowX, lowZ) = Bucket(minimum - pad);
        var (highX, highZ) = Bucket(maximum + pad);
        for (var z = lowZ; z <= highZ; z++)
        for (var x = lowX; x <= highX; x++)
        {
            var bucket = buckets[z * columns + x];
            if (bucket is null) continue;
            results.AddRange(bucket);
        }
    }

    private (int X, int Z) Bucket(Vector2 point)
    {
        var local = (point - origin) / BucketSize;
        return (
            Math.Clamp((int)MathF.Floor(local.X), 0, columns - 1),
            Math.Clamp((int)MathF.Floor(local.Y), 0, rows - 1));
    }
}

internal static class NavigationRasterizer
{
    /// <summary>
    /// What re-rasterising has cost, cumulatively. Static, so the determinism census skips it — it walks
    /// instance fields, and a diagnostic counter that nothing reads back has no business on the world.
    /// </summary>
    internal static long RebuildTicks;

    internal static int Rebuilds;

    /// <summary>The terrain-sampling pass, and everything after it. See §99.</summary>
    internal static long TerrainPassTicks;

    internal static long RestPassTicks;

    internal static long ApplyPassTicks;

    public static void Rebuild(PlacementGrid placement, NavigationGrid navigation, TerrainMap terrain)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            RebuildCore(placement, navigation, terrain);
        }
        finally
        {
            RebuildTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            Rebuilds++;
        }
    }

    private static void RebuildCore(PlacementGrid placement, NavigationGrid navigation, TerrainMap terrain)
    {
        var blocked = new bool[navigation.Width * navigation.Height];
        var clearance = new float[blocked.Length];
        var heights = new float[blocked.Length];
        var traversalCosts = new float[blocked.Length];
        var speedMultipliers = new float[blocked.Length];
        var obstacleBounds = GatherObstacleBounds(placement);

        var terrainStart = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var z = 0; z < navigation.Height; z++)
        for (var x = 0; x < navigation.Width; x++)
        {
            var cell = new GridCell(x, z);
            var index = navigation.Transform.Index(cell);
            var center = navigation.CellCenter(cell);
            var surface = terrain.SampleSurface(center);
            heights[index] = terrain.SampleHeight(center);
            traversalCosts[index] = TerrainSurfaceRules.PathCost(surface);
            speedMultipliers[index] = TerrainSurfaceRules.SpeedMultiplier(surface);
            if (!TerrainSurfaceRules.IsPassable(surface))
            {
                blocked[index] = true;
                navigation.Transform.CellBounds(cell, out var minimum, out var maximum);
                obstacleBounds.Add((minimum, maximum));
            }
        }

        TerrainPassTicks += System.Diagnostics.Stopwatch.GetTimestamp() - terrainStart;
        var restStart = System.Diagnostics.Stopwatch.GetTimestamp();

        // <b>Slope's cost is not here, and putting it here was a mistake worth recording.</b> A slope band
        // per cell was added to this pass, folded into the traversal cost, on the reasoning that the router
        // could not otherwise see that a hill is slow. It could: PathService.FlowStepCost has charged
        // |height change| x ClimbSecondsPerMetre on every edge since long before there was any relief to
        // charge it on, which at the constants in force is a 30% penalty at a tenth grade and 63% at a
        // fifth — steeper than the band table it was being stacked on top of.
        //
        // So the band was a second source of truth for how fast ground is, which is the exact mistake this
        // file's own surface table records having made once and fixed. Worse, the edge term is the better
        // model: it is path-dependent, so a contour route pays almost nothing where a direct climb pays the
        // lot, and a band per cell cannot express that at all — a switchback and a straight climb would
        // cost the same.
        //
        // What was actually wrong is one layer up, in what the <em>hierarchy</em> charges: see
        // RectangleFlowField, which prices a crossing as a straight line and knew nothing about climbing.
        // A cliff is an obstacle boundary even though both cells on either side
        // may have passable surface paint. Include those edges in clearance so
        // A* keeps the agent's body—not merely its center—away from ramp corners.
        var halfCell = navigation.Transform.CellSize * 0.5f;
        for (var z = 0; z < navigation.Height; z++)
        for (var x = 0; x < navigation.Width; x++)
        {
            var cell = new GridCell(x, z);
            var index = navigation.Transform.Index(cell);
            var center = navigation.CellCenter(cell);
            if (x + 1 < navigation.Width)
            {
                var right = new GridCell(x + 1, z);
                var rightIndex = navigation.Transform.Index(right);
                if (!blocked[index] && !blocked[rightIndex] &&
                    IsNonTraversableHeightEdge(heights[index], heights[rightIndex], navigation.Transform.CellSize))
                {
                    var edgeX = center.X + halfCell;
                    obstacleBounds.Add((
                        new Vector2(edgeX, center.Y - halfCell),
                        new Vector2(edgeX, center.Y + halfCell)));
                }
            }
            if (z + 1 < navigation.Height)
            {
                var below = new GridCell(x, z + 1);
                var belowIndex = navigation.Transform.Index(below);
                if (!blocked[index] && !blocked[belowIndex] &&
                    IsNonTraversableHeightEdge(heights[index], heights[belowIndex], navigation.Transform.CellSize))
                {
                    var edgeY = center.Y + halfCell;
                    obstacleBounds.Add((
                        new Vector2(center.X - halfCell, edgeY),
                        new Vector2(center.X + halfCell, edgeY)));
                }
            }
        }

        var obstacles = new ObstacleIndex(
            MergeColinear(obstacleBounds),
            terrain.Minimum,
            terrain.Maximum);
        // Tiled, and the tile is the reason this is affordable. Clearance is a distance to the nearest
        // obstacle box, so every cell needs the boxes near it — and gathering them per cell over 1.44M
        // cells is where the twelve seconds went. A tile of sixteen cells shares one gather, and in open
        // country that gather is empty and all sixteen take the clamp having looked at nothing.
        const int tile = 4;
        var candidates = new List<(Vector2 Minimum, Vector2 Maximum)>();
        for (var tileZ = 0; tileZ < navigation.Height; tileZ += tile)
        for (var tileX = 0; tileX < navigation.Width; tileX += tile)
        {
            var highZ = Math.Min(tileZ + tile - 1, navigation.Height - 1);
            var highX = Math.Min(tileX + tile - 1, navigation.Width - 1);
            navigation.Transform.CellBounds(new GridCell(tileX, tileZ), out var tileMinimum, out _);
            navigation.Transform.CellBounds(new GridCell(highX, highZ), out _, out var tileMaximum);

            // Widened only as far as it has to be. If every cell in the tile found something inside the
            // padding then the answer is exact — any nearer box would have been within the padding too —
            // and among trees that is true at the first, smallest radius. Padding straight to the full
            // reach pulls in forty-nine buckets to answer a question about something a metre away.
            for (var padding = ObstacleIndex.NearPadding; ; padding *= 2f)
            {
                // The last pass has gathered everything inside the clamp, so whatever it finds is the
                // answer — there is no wider pass to defer to and nothing beyond the clamp matters.
                var last = padding >= ObstacleIndex.Reach;
                var exact = true;
                obstacles.GatherWithin(tileMinimum, tileMaximum, padding, candidates);
                for (var z = tileZ; z <= highZ && exact; z++)
                for (var x = tileX; x <= highX; x++)
                {
                    var cell = new GridCell(x, z);
                    var cellIndex = navigation.Transform.Index(cell);
                    var center = navigation.CellCenter(cell);
                    var edge = MathF.Min(
                        MathF.Min(center.X - terrain.Minimum.X, terrain.Maximum.X - center.X),
                        MathF.Min(center.Y - terrain.Minimum.Y, terrain.Maximum.Y - center.Y));
                    var nearest = edge;
                    var solid = false;

                    foreach (var bounds in candidates)
                    {
                        var delta = Vector2.Max(
                            Vector2.Max(bounds.Minimum - center, center - bounds.Maximum), Vector2.Zero);
                        var distance = delta.Length();
                        if (distance < nearest) nearest = distance;
                        if (distance > 0f) continue;
                        solid = true;
                        nearest = 0f;
                        break;
                    }

                    // Not settled: something outside the padding could still be nearer, so widen — unless
                    // this is the last pass, which has already gathered everything inside the clamp.
                    //
                    // Both halves of that were got wrong once and each cost a distinct failure. Treating
                    // "nothing found, so the map edge is nearest" as settled reports clearance
                    // <em>larger</em> than the truth, which is the direction that tells a body it fits
                    // where it does not; the movement benchmarks caught it as a mean clearance that had
                    // gone up rather than down. And bailing out of the cell loop on the last pass leaves
                    // the rest of the tile never written at all — clearance zero, which reads as solid
                    // ground — and on open terrain that is most of the map. Nineteen tests failed at once.
                    if (!last && nearest > padding)
                    {
                        exact = false;
                        break;
                    }

                    if (solid) blocked[cellIndex] = true;
                    clearance[cellIndex] = MathF.Min(nearest, ObstacleIndex.Reach);
                }

                if (exact || last) break;
            }
        }

        RestPassTicks += System.Diagnostics.Stopwatch.GetTimestamp() - restStart;
        var applyStart = System.Diagnostics.Stopwatch.GetTimestamp();
        navigation.ReplaceRaster(blocked, clearance, heights, traversalCosts, speedMultipliers);
        ApplyPassTicks += System.Diagnostics.Stopwatch.GetTimestamp() - applyStart;
    }

    private static bool IsNonTraversableHeightEdge(float first, float second, float distance)
    {
        var delta = MathF.Abs(second - first);
        return delta > TerrainMap.MaximumStepHeight ||
               delta / MathF.Max(distance, 0.0001f) > TerrainMap.MaximumTraversableGrade;
    }

    private static List<(Vector2 Minimum, Vector2 Maximum)> GatherObstacleBounds(PlacementGrid placement)
    {
        var result = new List<(Vector2, Vector2)>();
        foreach (var cell in placement.OccupiedCells)
        {
            placement.Transform.CellBounds(cell, out var minimum, out var maximum);
            result.Add((minimum, maximum));
        }

        return result;
    }

    /// <summary>
    /// Joins boxes that lie end to end into single long ones.
    /// </summary>
    /// <remarks>
    /// A cliff is added to this list one cell edge at a time, so a ridge running the width of
    /// a 600 m map arrives as tens of thousands of half-metre segments lying in a straight
    /// line. Every one of them is a separate thing for every nearby cell to measure its
    /// distance against, and they all give the same answer as the single long box they
    /// obviously are. Merging first turns a wall into a handful of boxes and leaves the
    /// clearance values it produces identical, because the distance to a run of touching
    /// segments is the distance to the run.
    /// </remarks>
    private static List<(Vector2 Minimum, Vector2 Maximum)> MergeColinear(
        List<(Vector2 Minimum, Vector2 Maximum)> boxes)
    {
        var merged = MergeAlong(boxes, horizontally: true);
        return MergeAlong(merged, horizontally: false);
    }

    private static List<(Vector2 Minimum, Vector2 Maximum)> MergeAlong(
        List<(Vector2 Minimum, Vector2 Maximum)> boxes,
        bool horizontally)
    {
        var result = new List<(Vector2 Minimum, Vector2 Maximum)>();
        var groups = boxes.GroupBy(box => horizontally
            ? (box.Minimum.Y, box.Maximum.Y)
            : (box.Minimum.X, box.Maximum.X));

        foreach (var group in groups)
        {
            var ordered = group
                .OrderBy(box => horizontally ? box.Minimum.X : box.Minimum.Y)
                .ToList();
            var current = ordered[0];
            for (var i = 1; i < ordered.Count; i++)
            {
                var next = ordered[i];
                var currentEnd = horizontally ? current.Maximum.X : current.Maximum.Y;
                var nextStart = horizontally ? next.Minimum.X : next.Minimum.Y;
                if (nextStart <= currentEnd + 0.0001f)
                {
                    current = (
                        Vector2.Min(current.Minimum, next.Minimum),
                        Vector2.Max(current.Maximum, next.Maximum));
                    continue;
                }

                result.Add(current);
                current = next;
            }

            result.Add(current);
        }

        return result;
    }
}
