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
    /// <summary>How far a cell looks for obstacles, and the ceiling on reported clearance.</summary>
    public const float Reach = 32f;

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
        columns = Math.Max(1, (int)MathF.Ceiling(extent.X / Reach));
        rows = Math.Max(1, (int)MathF.Ceiling(extent.Y / Reach));
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

    /// <summary>Every box that could be the nearest to this point.</summary>
    public void Gather(Vector2 point, List<(Vector2 Minimum, Vector2 Maximum)> results)
    {
        results.Clear();
        var (centreX, centreZ) = Bucket(point);
        for (var z = Math.Max(0, centreZ - 1); z <= Math.Min(rows - 1, centreZ + 1); z++)
        for (var x = Math.Max(0, centreX - 1); x <= Math.Min(columns - 1, centreX + 1); x++)
        {
            var bucket = buckets[z * columns + x];
            if (bucket is null) continue;
            results.AddRange(bucket);
        }
    }

    private (int X, int Z) Bucket(Vector2 point)
    {
        var local = (point - origin) / Reach;
        return (
            Math.Clamp((int)MathF.Floor(local.X), 0, columns - 1),
            Math.Clamp((int)MathF.Floor(local.Y), 0, rows - 1));
    }
}

internal static class NavigationRasterizer
{
    public static void Rebuild(PlacementGrid placement, NavigationGrid navigation, TerrainMap terrain)
    {
        var blocked = new bool[navigation.Width * navigation.Height];
        var clearance = new float[blocked.Length];
        var heights = new float[blocked.Length];
        var traversalCosts = new float[blocked.Length];
        var speedMultipliers = new float[blocked.Length];
        var obstacleBounds = GatherObstacleBounds(placement);

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
        var nearbyBounds = new List<(Vector2 Minimum, Vector2 Maximum)>();
        for (var z = 0; z < navigation.Height; z++)
        for (var x = 0; x < navigation.Width; x++)
        {
            var cell = new GridCell(x, z);
            var cellIndex = navigation.Transform.Index(cell);
            var center = navigation.CellCenter(cell);
            var nearest = MathF.Min(
                MathF.Min(center.X - terrain.Minimum.X, terrain.Maximum.X - center.X),
                MathF.Min(center.Y - terrain.Minimum.Y, terrain.Maximum.Y - center.Y));

            obstacles.Gather(center, nearbyBounds);
            foreach (var bounds in nearbyBounds)
            {
                var delta = Vector2.Max(Vector2.Max(bounds.Minimum - center, center - bounds.Maximum), Vector2.Zero);
                nearest = MathF.Min(nearest, delta.Length());
                if (center.X >= bounds.Minimum.X && center.X <= bounds.Maximum.X &&
                    center.Y >= bounds.Minimum.Y && center.Y <= bounds.Maximum.Y)
                {
                    blocked[cellIndex] = true;
                    nearest = 0f;
                    break;
                }
            }

            clearance[cellIndex] = MathF.Min(nearest, ObstacleIndex.Reach);
        }

        navigation.ReplaceRaster(blocked, clearance, heights, traversalCosts, speedMultipliers);
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
