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
            RebuildCore(placement, navigation, terrain, dirty: null);
        }
        finally
        {
            RebuildTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            Rebuilds++;
        }
    }

    /// <summary>
    /// Re-rasterises only the ground a placement change can have altered.
    /// </summary>
    /// <remarks>
    /// <b>Because a building cannot move the terrain.</b> Measured on the village, a full rebuild is 774 ms —
    /// 288 of it sampling surface and height for 1.44M cells that are already correct, and 459 recomputing a
    /// distance-to-nearest-obstacle for cells nowhere near what changed. A placement changes a few metres of
    /// ground, and only cells within <see cref="ObstacleIndex.Reach"/> of it can have a different clearance.
    /// <para>
    /// The window is the dirty rectangle for the terrain-derived values, and the dirty rectangle widened by
    /// the obstacle reach for clearance. Everything outside is read back from the grid unchanged, which is
    /// what makes this identical to a full rebuild rather than an approximation of one — and there is a
    /// self-test that asserts exactly that, cell by cell, because "should be identical" is the kind of claim
    /// that stops being true quietly.
    /// </para>
    /// </remarks>
    public static void RebuildWithin(
        PlacementGrid placement,
        NavigationGrid navigation,
        TerrainMap terrain,
        Vector2 dirtyMinimum,
        Vector2 dirtyMaximum)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            RebuildCore(placement, navigation, terrain, (dirtyMinimum, dirtyMaximum));
        }
        finally
        {
            RebuildTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            Rebuilds++;
            LocalRebuilds++;
        }
    }

    internal static int LocalRebuilds;

    /// <summary>Cells whose clearance was computed, and boxes the index was built over.</summary>
    internal static long ClearanceCells;

    internal static int ObstacleBoxes;

    internal static long TerrainCells;

    internal static long ClearanceWindowCells;

    private static void RebuildCore(
        PlacementGrid placement,
        NavigationGrid navigation,
        TerrainMap terrain,
        (Vector2 Minimum, Vector2 Maximum)? dirty)
    {
        bool[] blocked;
        float[] clearance;
        float[] heights;
        float[] traversalCosts;
        float[] speedMultipliers;
        int terrainLowX, terrainLowZ, terrainHighX, terrainHighZ;
        int clearanceLowX, clearanceLowZ, clearanceHighX, clearanceHighZ;
        int gatherLowX, gatherLowZ, gatherHighX, gatherHighZ;
        if (dirty is { } window)
        {
            // Everything outside the window comes back from the grid exactly as it went in.
            navigation.ReadRaster(
                out blocked, out clearance, out heights, out traversalCosts, out speedMultipliers);
            CellWindow(
                navigation, window.Minimum, window.Maximum, 0f,
                out terrainLowX, out terrainLowZ, out terrainHighX, out terrainHighZ);
            // Clearance reaches further than the change does: a cell's distance-to-nearest-obstacle can only
            // have moved if the change is within the reach it clamps at.
            CellWindow(
                navigation, window.Minimum, window.Maximum, ObstacleIndex.Reach,
                out clearanceLowX, out clearanceLowZ, out clearanceHighX, out clearanceHighZ);
            // Wider again for GATHERING boxes than for computing clearance. A cell at the edge of the
            // clearance window is itself within reach of ground outside it, so a box out there can be its
            // nearest one — and an obstacle set missing that box would give a wrong answer rather than a
            // stale one. Two reaches out, and still about two thousand cells against 1.44M.
            CellWindow(
                navigation, window.Minimum, window.Maximum, ObstacleIndex.Reach * 2f,
                out gatherLowX, out gatherLowZ, out gatherHighX, out gatherHighZ);
        }
        else
        {
            blocked = new bool[navigation.Width * navigation.Height];
            clearance = new float[blocked.Length];
            heights = new float[blocked.Length];
            traversalCosts = new float[blocked.Length];
            speedMultipliers = new float[blocked.Length];
            terrainLowX = clearanceLowX = 0;
            terrainLowZ = clearanceLowZ = 0;
            terrainHighX = clearanceHighX = gatherHighX = navigation.Width - 1;
            terrainHighZ = clearanceHighZ = gatherHighZ = navigation.Height - 1;
            gatherLowX = gatherLowZ = 0;
        }

        var obstacleBounds = GatherObstacleBounds(placement);
        // Per call, so the figure describes this pass and not the sum of every rebuild since startup — which
        // is what it did first, and reported four million cells for a four-cell wall.
        ClearanceCells = 0;
        TerrainCells = (long)(terrainHighX - terrainLowX + 1) * (terrainHighZ - terrainLowZ + 1);
        ClearanceWindowCells = (long)(clearanceHighX - clearanceLowX + 1) * (clearanceHighZ - clearanceLowZ + 1);

        var terrainStart = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var z = terrainLowZ; z <= terrainHighZ; z++)
        for (var x = terrainLowX; x <= terrainHighX; x++)
        {
            var cell = new GridCell(x, z);
            var index = navigation.Transform.Index(cell);
            var center = navigation.CellCenter(cell);
            var surface = terrain.SampleSurface(center);
            heights[index] = terrain.SampleHeight(center);
            traversalCosts[index] = TerrainSurfaceRules.PathCost(surface);
            speedMultipliers[index] = TerrainSurfaceRules.SpeedMultiplier(surface);
            // Cleared first: on a local pass this array came back from the grid, so a cell that used to be
            // blocked by an obstacle now gone would keep saying so.
            blocked[index] = false;
            if (!TerrainSurfaceRules.IsPassable(surface))
            {
                blocked[index] = true;
                navigation.Transform.CellBounds(cell, out var minimum, out var maximum);
                obstacleBounds.Add((minimum, maximum));
            }
        }

        TerrainPassTicks += System.Diagnostics.Stopwatch.GetTimestamp() - terrainStart;
        var restStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (dirty is not null)
        {
            // The full pass adds impassable terrain to the obstacle list as it samples it. A local pass never
            // sampled the ground outside its window, so it has to take those bounds from what the grid already
            // knows — otherwise a cell beside a cliff would compute its clearance as if the cliff were gone.
            // Placement cells are in the list twice as a result, which costs a comparison and cannot change a
            // minimum distance.
            for (var z = gatherLowZ; z <= gatherHighZ; z++)
            for (var x = gatherLowX; x <= gatherHighX; x++)
            {
                if (x >= terrainLowX && x <= terrainHighX && z >= terrainLowZ && z <= terrainHighZ) continue;
                var cell = new GridCell(x, z);
                if (!blocked[navigation.Transform.Index(cell)]) continue;
                navigation.Transform.CellBounds(cell, out var minimum, out var maximum);
                obstacleBounds.Add((minimum, maximum));
            }
        }

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
        // Bounded to the gather window on a local pass. This was the last whole-map loop left in it, and it
        // was 108 ms of a 196 ms rebuild — walking 1.44M cells to add cliff edges the filter below then threw
        // away, because nothing outside the window can be near anything inside it.
        for (var z = gatherLowZ; z <= gatherHighZ; z++)
        for (var x = gatherLowX; x <= gatherHighX; x++)
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

        if (dirty is not null)
        {
            // <b>And only the obstacles the window can see.</b> Merging and indexing every box on the map is
            // a fixed cost that does not care how small the change was — on a village that is twenty-five
            // thousand trees, and it was 125 ms of a 225 ms local pass. Nothing outside the window plus the
            // reach can be the nearest box to a cell inside it, so nothing outside needs to be in the index.
            var lowCorner = navigation.CellCenter(new GridCell(gatherLowX, gatherLowZ));
            var highCorner = navigation.CellCenter(new GridCell(gatherHighX, gatherHighZ));
            var slack = new Vector2(ObstacleIndex.Reach + navigation.Transform.CellSize);
            var windowMinimum = lowCorner - slack;
            var windowMaximum = highCorner + slack;
            obstacleBounds.RemoveAll(bounds =>
                bounds.Maximum.X < windowMinimum.X || bounds.Minimum.X > windowMaximum.X ||
                bounds.Maximum.Y < windowMinimum.Y || bounds.Minimum.Y > windowMaximum.Y);
        }

        ObstacleBoxes = obstacleBounds.Count;
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
        for (var tileZ = clearanceLowZ; tileZ <= clearanceHighZ; tileZ += tile)
        for (var tileX = clearanceLowX; tileX <= clearanceHighX; tileX += tile)
        {
            var highZ = Math.Min(tileZ + tile - 1, clearanceHighZ);
            var highX = Math.Min(tileX + tile - 1, clearanceHighX);
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

                    ClearanceCells++;
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

    /// <summary>The cell range covering a world-space rectangle, widened by a margin and clamped to the grid.</summary>
    private static void CellWindow(
        NavigationGrid navigation,
        Vector2 minimum,
        Vector2 maximum,
        float margin,
        out int lowX,
        out int lowZ,
        out int highX,
        out int highZ)
    {
        var size = navigation.Transform.CellSize;
        var origin = navigation.Transform.Origin;
        // One extra cell each way past the margin, because a cell's clearance is measured from its centre and
        // a box that reaches its edge is nearer than the margin suggests.
        var pad = margin + size;
        lowX = Math.Max(0, (int)MathF.Floor((minimum.X - pad - origin.X) / size));
        lowZ = Math.Max(0, (int)MathF.Floor((minimum.Y - pad - origin.Y) / size));
        highX = Math.Min(navigation.Width - 1, (int)MathF.Ceiling((maximum.X + pad - origin.X) / size));
        highZ = Math.Min(navigation.Height - 1, (int)MathF.Ceiling((maximum.Y + pad - origin.Y) / size));
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
