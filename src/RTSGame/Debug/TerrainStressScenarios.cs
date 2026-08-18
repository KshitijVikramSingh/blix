using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Debug;

internal static class TerrainStressScenarios
{
    /// <summary>
    /// A rolling landscape with a walkable ramp, a hill that can be crossed, a
    /// hill that must be gone around, and a pond that cannot be entered.
    /// </summary>
    /// <remarks>
    /// Every height here is continuous. The previous version stepped from ground
    /// level to 1.65 m across a single vertex wherever the ramp corridor ended,
    /// which is a vertical cliff — bodies could stand right against a face they
    /// had no way to cross, and the routing grid (which samples cell centres)
    /// disagreed with the movement sweep (which samples the body) about whether
    /// the ground there was usable at all. That mismatch is a fair test of the
    /// navigation code and a terrible test of everything else, because it
    /// produced wedged units that had nothing to do with crowding. Slopes now
    /// vary smoothly, so where a body cannot go it is because the ground is
    /// genuinely too steep rather than because it is standing on a seam.
    /// </remarks>
    public static AgentId[] Populate(SimulationWorld world, bool issueGroupMove = true)
    {
        var terrain = world.Terrain;
        var grid = terrain.Transform;

        for (var z = 0; z <= grid.Height; z++)
        for (var x = 0; x <= grid.Width; x++)
        {
            var position = grid.Origin + new Vector2(x * grid.CellSize, z * grid.CellSize);
            terrain.SetVertexHeight(x, z, Height(position) * LabWindow(position));
        }

        for (var z = 0; z < grid.Height; z++)
        for (var x = 0; x < grid.Width; x++)
        {
            var cell = new GridCell(x, z);
            var center = grid.CellCenter(cell);
            var surface = TerrainSurface.Grass;
            if (!InsideLab(center))
            {
                terrain.SetSurface(cell, surface);
                continue;
            }

            if (MathF.Abs(center.Y) < 1.15f) surface = TerrainSurface.Road;
            else if (center.Y < -3.5f && center.X > -8f && center.X < 10f) surface = TerrainSurface.Mud;
            else if (center.Y > 4f) surface = TerrainSurface.Rough;
            if (center.X is > 5f and < 8.5f && center.Y is > -8.5f and < -5.5f)
                surface = TerrainSurface.Impassable;
            terrain.SetSurface(cell, surface);
        }

        world.RebuildTerrainNavigation();
        var ids = new List<AgentId>();
        for (var row = 0; row < 5; row++)
        for (var column = 0; column < 6; column++)
        {
            ids.Add(world.SpawnAgent(new Vector2(-10.5f + column * 0.88f, -2.0f + row * 0.88f)));
        }
        if (issueGroupMove) world.QueueMove(ids, new Vector2(10f, 0f));
        return ids.ToArray();
    }

    /// <summary>Half-width of the laboratory, which is the tuned world it was drawn for.</summary>
    /// <remarks>
    /// Every rule below is written in absolute world coordinates against a 30 m square:
    /// "road where |y| &lt; 1.15", "rough where y &gt; 4", a ramp that smoothsteps between
    /// x = -6 and x = 4 and is flat either side of that. On the world it was drawn for those
    /// describe a compact set of features. On a 600 m one they describe a landscape — rough
    /// ground over half the map, a road running the full width, and a plateau covering
    /// everything east of the ramp — because none of them has a far edge.
    /// <para>
    /// So the lab is bounded to the square it was drawn for. Inside <see cref="LabHalfWidth"/>
    /// nothing changes at all, which is what keeps the two terrain self-tests measuring the
    /// scenario they were calibrated against; beyond it the ground is plain grass, and the
    /// heights fade out over <see cref="LabFade"/> rather than ending in a cliff nobody meant
    /// to build.
    /// </para>
    /// </remarks>
    private const float LabHalfWidth = 15f;

    /// <summary>Distance beyond the lab over which its heights return to zero.</summary>
    private const float LabFade = 10f;

    private static bool InsideLab(Vector2 position) =>
        MathF.Abs(position.X) <= LabHalfWidth && MathF.Abs(position.Y) <= LabHalfWidth;

    private static float LabWindow(Vector2 position)
    {
        var reach = MathF.Max(MathF.Abs(position.X), MathF.Abs(position.Y));
        if (reach <= LabHalfWidth) return 1f;
        if (reach >= LabHalfWidth + LabFade) return 0f;
        return 1f - SmoothStep(LabHalfWidth, LabHalfWidth + LabFade, reach);
    }

    private static float Height(Vector2 position)
    {
        // A long, shallow rise west to east — the ramp the crowd has to climb.
        var ramp = SmoothStep(-6f, 4f, position.X) * 1.35f;
        // A broad shoulder to the north, giving the corner route something to
        // round without ever presenting an edge.
        var shoulder = SmoothStep(1.5f, 6.5f, position.Y) * 0.90f;
        // Crossable: gentle enough that a body can walk straight over it.
        var lowHill = Bump(position, new Vector2(2.5f, -4.5f), 3.4f, 1.50f);
        // Not crossable: the middle exceeds the traversable grade, but its skirt
        // is smooth, so routes bend around it instead of stopping dead at a face.
        var steepHill = Bump(position, new Vector2(-3.5f, 5.5f), 1.7f, 2.60f);
        return ramp + shoulder + lowHill + steepHill;
    }

    private static float Bump(Vector2 position, Vector2 center, float spread, float amplitude)
    {
        var distanceSquared = Vector2.DistanceSquared(position, center);
        return amplitude * MathF.Exp(-distanceSquared / (2f * spread * spread));
    }

    private static float SmoothStep(float from, float to, float value)
    {
        var amount = Math.Clamp((value - from) / (to - from), 0f, 1f);
        return amount * amount * (3f - 2f * amount);
    }
}
