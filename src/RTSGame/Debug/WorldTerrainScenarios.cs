using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Debug;

/// <summary>
/// A terrain map at the size the game is actually played on.
/// </summary>
/// <remarks>
/// <see cref="TerrainStressScenarios"/> is a laboratory: a ramp, two hills and a pond inside
/// thirty metres, drawn so that two self-tests could assert on a body crossing them. It is
/// still exactly that, and it is still what those tests run against. What it is not is a map.
/// Stretched onto six hundred metres it read as a few chunky rectangles adrift in an empty
/// plain, because every feature in it is about as wide as four bodies standing abreast.
/// <para>
/// This is drawn to the scale the design argues in. A catchment is about a hundred metres of
/// walking, a player's territory a few of those, so terrain has to be legible at that size to
/// mean anything: a ridge is something you route a hauling line around, not something you
/// step over. Everything here is expressed as a fraction of the extent, so it is the same map
/// at any size.
/// </para>
/// <para>
/// It is also the first thing in this project that gives the routing hierarchy real work.
/// Portal routing was measured on open ground, where a region's crossing cost is arithmetic
/// and the search never runs. Ridges, a lake and a river put obstacles in perhaps a fifth of
/// the regions, which is the case the fast path deliberately does not cover.
/// </para>
/// </remarks>
internal static class WorldTerrainScenarios
{
    public static AgentId[] Populate(SimulationWorld world, bool issueGroupMove = true)
    {
        var terrain = world.Terrain;
        var grid = terrain.Transform;
        var extent = world.ExtentMeters;

        for (var z = 0; z <= grid.Height; z++)
        for (var x = 0; x <= grid.Width; x++)
        {
            var position = grid.Origin + new Vector2(x * grid.CellSize, z * grid.CellSize);
            terrain.SetVertexHeight(x, z, Height(position, extent));
        }

        for (var z = 0; z < grid.Height; z++)
        for (var x = 0; x < grid.Width; x++)
        {
            var cell = new GridCell(x, z);
            terrain.SetSurface(cell, Surface(grid.CellCenter(cell), extent));
        }

        world.RebuildTerrainNavigation();

        // On the road, west of the pass, with somewhere worth walking to on the far side.
        var ids = new List<AgentId>();
        var start = new Vector2(-extent * 0.30f, extent * 0.02f);
        for (var row = 0; row < 5; row++)
        for (var column = 0; column < 6; column++)
        {
            ids.Add(world.SpawnAgent(start + new Vector2(column * 0.9f, row * 0.9f)));
        }

        if (issueGroupMove) world.QueueMove(ids, new Vector2(extent * 0.32f, extent * 0.06f));
        return ids.ToArray();
    }

    /// <summary>
    /// A ridge across the map with one pass through it, a shallow basin, and rolling ground.
    /// </summary>
    /// <remarks>
    /// The ridge is the point of the map. It runs most of the way across, it is too steep to
    /// climb, and there is exactly one gap in it — so a route from one side to the other has
    /// to find that gap, which is the whole reason a portal graph exists. The rest is gentle
    /// enough to walk anywhere, because a map where every metre is a decision is not a map
    /// either.
    /// </remarks>
    private static float Height(Vector2 position, float extent)
    {
        var u = position.X / extent;
        var v = position.Y / extent;

        // Long ridge on a slight diagonal, with a saddle a fifth of the way north of centre.
        var alongRidge = v - 0.18f + u * 0.10f;
        var ridge = 26f * Falloff(alongRidge / 0.035f);
        var pass = Falloff((u + 0.06f) / 0.045f);
        ridge *= 1f - 0.97f * pass;

        // A broad basin in the south-west that the river drains into, and long rolling swells
        // so that open ground is not literally a plane.
        var basin = -6f * Falloff((u + 0.30f) / 0.16f) * Falloff((v + 0.28f) / 0.16f);
        var swell = 1.6f * MathF.Sin(u * 7.5f) * MathF.Cos(v * 6.1f);

        return ridge + basin + swell;
    }

    private static TerrainSurface Surface(Vector2 position, float extent)
    {
        var u = position.X / extent;
        var v = position.Y / extent;

        // The lake sits in the basin and cannot be entered.
        var lake = MathF.Sqrt(Square((u + 0.30f) / 0.085f) + Square((v + 0.28f) / 0.070f));
        if (lake < 1f) return TerrainSurface.Impassable;
        if (lake < 1.35f) return TerrainSurface.Mud;

        // A road running west to east through the pass — the reason the pass matters.
        var road = MathF.Abs(v - RoadCentre(u));
        if (road < 0.006f) return TerrainSurface.Road;

        // Rough ground on the ridge's flanks, so going over is slow as well as steep.
        var alongRidge = MathF.Abs(v - 0.18f + u * 0.10f);
        if (alongRidge < 0.055f) return TerrainSurface.Rough;

        return TerrainSurface.Grass;
    }

    /// <summary>The road bends north to meet the pass, then straightens again.</summary>
    private static float RoadCentre(float u) => 0.02f + 0.16f * Falloff((u + 0.06f) / 0.22f);

    /// <summary>Smooth bump, one at the centre and effectively nothing past about two.</summary>
    private static float Falloff(float t) => MathF.Exp(-t * t);

    private static float Square(float value) => value * value;
}
