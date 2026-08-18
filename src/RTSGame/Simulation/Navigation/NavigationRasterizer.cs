using System.Numerics;
using RTSGame.Simulation.Placement;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Simulation.Navigation;

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

        for (var z = 0; z < navigation.Height; z++)
        for (var x = 0; x < navigation.Width; x++)
        {
            var cell = new GridCell(x, z);
            var index = navigation.Transform.Index(cell);
            var center = navigation.CellCenter(cell);
            var nearest = MathF.Min(
                MathF.Min(center.X - terrain.Minimum.X, terrain.Maximum.X - center.X),
                MathF.Min(center.Y - terrain.Minimum.Y, terrain.Maximum.Y - center.Y));

            foreach (var bounds in obstacleBounds)
            {
                var delta = Vector2.Max(Vector2.Max(bounds.Minimum - center, center - bounds.Maximum), Vector2.Zero);
                nearest = MathF.Min(nearest, delta.Length());
                if (center.X >= bounds.Minimum.X && center.X <= bounds.Maximum.X &&
                    center.Y >= bounds.Minimum.Y && center.Y <= bounds.Maximum.Y)
                {
                    blocked[index] = true;
                    nearest = 0f;
                    break;
                }
            }
            clearance[index] = nearest;
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
        for (var z = 0; z < placement.Transform.Height; z++)
        for (var x = 0; x < placement.Transform.Width; x++)
        {
            var cell = new GridCell(x, z);
            if (!placement.IsOccupied(cell)) continue;
            placement.Transform.CellBounds(cell, out var minimum, out var maximum);
            result.Add((minimum, maximum));
        }
        return result;
    }
}
