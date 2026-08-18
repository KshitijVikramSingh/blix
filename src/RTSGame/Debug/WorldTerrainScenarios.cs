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
/// thirty metres, drawn so two self-tests could assert on a body crossing them. It is still
/// exactly that. What it is not is a map — stretched onto six hundred metres it read as a few
/// chunky rectangles adrift in an empty plain, because every feature in it is about as wide as
/// four bodies standing abreast.
/// <para>
/// This is built out of pieces you can point at: a wall of hills across the middle with one
/// gap in it, a road through that gap, and a lake. The first attempt was smooth field
/// arithmetic — a Gaussian ridge multiplied by a Gaussian pass, a bending road, rolling swells
/// — and it produced something nobody could reason about and the renderer could not draw: a
/// diagonal smear where the ridge should be, a road that came out as a chevron, and ground that
/// disagreed with itself everywhere, so the coarse pass had nothing flat to stand on. Legible
/// beats clever, and on a greybox map legible is also the point.
/// </para>
/// <para>
/// It is the first thing here that gives portal routing real work. Everything measured so far
/// was on open ground, where a region's crossing cost is arithmetic and the search never runs.
/// A ridge with a single pass is the case the fast path deliberately does not cover, and it is
/// the case the whole hierarchy exists for: getting from one side to the other means finding
/// the gap.
/// </para>
/// </remarks>
internal static class WorldTerrainScenarios
{
    // Everything is a fraction of the extent, so this is the same map at any size.
    private const float RidgeCentre = 0.15f;
    private const float RidgeHalfWidth = 0.025f;
    private const float RidgeFlankHalfWidth = 0.038f;
    private const float RidgeHeight = 20f;
    private const float PassCentre = -0.06f;
    private const float PassHalfWidth = 0.030f;
    private const float RoadHalfWidth = 0.008f;
    private const float LakeCentreX = -0.28f;
    private const float LakeCentreY = -0.26f;
    private const float LakeRadius = 0.070f;
    private const float ShoreRadius = 0.092f;

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

        // On the road, south of the ridge, with the pass between them and their destination.
        var ids = new List<AgentId>();
        var start = new Vector2(PassCentre * extent - 2.5f, -0.05f * extent);
        for (var row = 0; row < 5; row++)
        for (var column = 0; column < 6; column++)
        {
            ids.Add(world.SpawnAgent(start + new Vector2(column * 0.9f, row * 0.9f)));
        }

        if (issueGroupMove)
        {
            world.QueueMove(ids, new Vector2(PassCentre * extent, 0.32f * extent));
        }

        return ids.ToArray();
    }

    /// <summary>
    /// Flat ground everywhere, and one wall of hills across the middle with a gap in it.
    /// </summary>
    /// <remarks>
    /// Flat is deliberate. Ground that rolls gently looks better and costs a great deal: the
    /// renderer describes open ground with one flat plate every few metres, and terrain that
    /// disagrees with itself everywhere means no plate is ever right. Height belongs where it
    /// says something — here, in the one feature a route has to solve.
    /// </remarks>
    private static float Height(Vector2 position, float extent)
    {
        var u = position.X / extent;
        var v = position.Y / extent;

        var acrossRidge = MathF.Abs(v - RidgeCentre);
        if (acrossRidge >= RidgeHalfWidth) return 0f;

        // Straight sides, flat top: a slope of about one and a third, comfortably past what a
        // body will climb, so the ridge is a wall rather than a hill that merely looks like one.
        var rise = RidgeHeight * (1f - acrossRidge / RidgeHalfWidth);

        // The gap. Cut square, because a defile with soft edges is not a defile.
        var acrossPass = MathF.Abs(u - PassCentre);
        if (acrossPass <= PassHalfWidth) return 0f;

        // A few metres of taper at the mouth so bodies are not walking into an invisible wall
        // exactly at the boundary of a cell.
        var mouth = MathF.Min(1f, (acrossPass - PassHalfWidth) / 0.012f);
        return rise * mouth;
    }

    private static TerrainSurface Surface(Vector2 position, float extent)
    {
        var u = position.X / extent;
        var v = position.Y / extent;

        var lake = MathF.Sqrt(Square(u - LakeCentreX) + Square(v - LakeCentreY));
        if (lake < LakeRadius) return TerrainSurface.Impassable;
        if (lake < ShoreRadius) return TerrainSurface.Mud;

        // The road runs north to south straight through the pass, which is the reason the pass
        // is worth anything.
        if (MathF.Abs(u - PassCentre) < RoadHalfWidth) return TerrainSurface.Road;

        // Broken ground on the ridge and its skirts: slow as well as steep.
        if (MathF.Abs(v - RidgeCentre) < RidgeFlankHalfWidth) return TerrainSurface.Rough;

        return TerrainSurface.Grass;
    }

    private static float Square(float value) => value * value;
}
