using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Debug;

/// <summary>
/// What a catchment actually covers on the map the game is played on, rather than what a disc of
/// the derived radius would cover.
/// </summary>
/// <remarks>
/// §6 derives the catchment as sixty seconds at hauler pace, 66 m, and then converts that to
/// <b>6.58 catchments per territory</b> by treating it as a disc — and that ratio is the whole
/// mechanic. At 1.10 catchments the second granary is never forced and the hauling network is
/// decorative, which is exactly the failure the re-derivation was fixing. So the number the
/// mechanic rests on is an area, and the area was never measured; it was assumed round.
/// <para>
/// It will not be round. A catchment is a cost field, so it follows roads and stops at ridges, and
/// §6 makes a design claim out of that: <em>"roads literally grow usable territory, and settlements
/// form ribbons along them — nobody has to author that."</em> A road is 1.10x on this map. Whether
/// 10% produces a ribbon or a barely perceptible bulge is a measurement, and one worth making before
/// Session 6 builds a distribution network on top of it.
/// </para>
/// </remarks>
internal static class CatchmentScenarios
{
    /// <summary>§6's budget: how long a hauler may spend getting to the edge of a catchment.</summary>
    private const float BudgetSecondsAtHaulerPace = 60f;

    /// <summary>Bearings sampled to find how far the catchment reaches in each direction.</summary>
    private const int Bearings = 32;

    public static int Run(float extentMeters, float budgetSeconds)
    {
        var world = new SimulationWorld(extentMeters);
        WorldTerrainScenarios.Shape(world);
        var extent = world.ExtentMeters;
        var hauler = UnitType.HaulerCart;

        // The conversion that has to happen and is easy to leave out. Route seconds are at the
        // router's reference pace; the budget is at the hauler's. Skipping this would size the
        // catchment at 107 m instead of 66 — the mechanic switched off again, by a units error
        // rather than by a wrong opinion.
        var reference = SimulationWorld.RouteReferenceSpeed;
        var budgetInRouteSeconds = budgetSeconds * hauler.MaximumSpeed / reference;
        var nominalRadius = budgetSeconds * hauler.MaximumSpeed;
        var nominalArea = MathF.PI * nominalRadius * nominalRadius;
        var territoryArea = extent * extent / 4f;

        Console.WriteLine(
            $"RTSGame catchment measurement — {extent:F0} m, {hauler.Name} at {hauler.MaximumSpeed:F2} m/s, " +
            $"budget {budgetSeconds:F0} s at its pace");
        Console.WriteLine(
            $"  route seconds are at the router's {reference:F2} m/s, so the budget is " +
            $"{budgetInRouteSeconds:F1} of them — {nominalRadius:F0} m of open ground");
        Console.WriteLine(
            $"  nominal disc {nominalArea:N0} m², territory {territoryArea:N0} m², " +
            $"so §6's {territoryArea / nominalArea:F2} catchments per territory");
        Console.WriteLine(
            "  placement          |  cells |  area m² | eff. r |  far |  near | stretch | per terr. | build ms");

        var pass = WorldTerrainScenarios.PassCentreOf(extent);
        var placements = new (string Name, Vector2 At)[]
        {
            ("open ground", new Vector2(0.18f * extent, -0.20f * extent)),
            ("on the road", new Vector2(pass.X, -0.10f * extent)),
            ("beside the ridge", new Vector2(0.06f * extent, 0.115f * extent)),
            ("inside the pass", pass),
            ("by the lake shore", new Vector2(-0.28f * extent, -0.26f * extent + 0.085f * extent)),
        };

        var worst = 0f;
        foreach (var (name, at) in placements)
        {
            var granary = world.Terrain.ClampPosition(at);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var measured = Measure(world, granary, hauler.NavigationRadius, budgetInRouteSeconds, nominalRadius);
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var perTerritory = measured.Area <= 0f ? 0f : territoryArea / measured.Area;
            worst = MathF.Max(worst, measured.Stretch);
            Console.WriteLine(
                $"  {name,-18} | {measured.Cells,6:N0} | {measured.Area,8:N0} | " +
                $"{measured.EffectiveRadius,6:F1} | {measured.Far,4:F0} | {measured.Near,5:F0} | " +
                $"{measured.Stretch,7:F2} | {perTerritory,9:F2} | {elapsed,8:F1}");
        }

        Console.WriteLine(
            "  eff. r is the radius of a disc of the same area; far and near are the longest and " +
            $"shortest reach over {Bearings} bearings, in metres");
        Console.WriteLine(
            $"  the widest stretch measured is {worst:F2}x. A road at " +
            $"{TerrainSurfaceRules.SpeedMultiplier(TerrainSurface.Road):F2}x can only ever produce " +
            $"{TerrainSurfaceRules.SpeedMultiplier(TerrainSurface.Road):F2}x of reach along it, so " +
            "anything above that is terrain refusing ground rather than a road granting it");
        return 0;
    }

    private readonly record struct Catchment(
        int Cells,
        float Area,
        float EffectiveRadius,
        float Far,
        float Near,
        float Stretch);

    private static Catchment Measure(
        SimulationWorld world,
        Vector2 granary,
        float navigationRadius,
        float budgetInRouteSeconds,
        float nominalRadius)
    {
        // A catchment cannot reach further than the budget times the reference pace, which is the
        // nominal radius, so the sweep is bounded by it with a margin rather than by the map.
        var reach = nominalRadius * 1.25f;
        var transform = world.Navigation.Transform;
        var cellArea = transform.CellSize * transform.CellSize;
        var cells = 0;
        var steps = (int)MathF.Ceiling(reach / transform.CellSize);
        if (!transform.TryWorldToCell(granary, out var origin)) return default;

        for (var dz = -steps; dz <= steps; dz++)
        for (var dx = -steps; dx <= steps; dx++)
        {
            var cell = new GridCell(origin.X + dx, origin.Z + dz);
            if (!transform.Contains(cell)) continue;
            if (!world.TryTravelSeconds(
                    transform.CellCenter(cell), granary, navigationRadius, out var seconds))
            {
                continue;
            }

            if (seconds <= budgetInRouteSeconds) cells++;
        }

        var area = cells * cellArea;
        var far = 0f;
        var near = float.PositiveInfinity;
        for (var bearing = 0; bearing < Bearings; bearing++)
        {
            var angle = bearing / (float)Bearings * MathF.Tau;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var distance = 0f;
            for (var step = transform.CellSize; step <= reach; step += transform.CellSize)
            {
                if (!world.TryTravelSeconds(
                        granary + direction * step, granary, navigationRadius, out var seconds))
                {
                    break;
                }

                if (seconds > budgetInRouteSeconds) break;
                distance = step;
            }

            far = MathF.Max(far, distance);
            near = MathF.Min(near, distance);
        }

        return new Catchment(
            cells,
            area,
            MathF.Sqrt(area / MathF.PI),
            far,
            near,
            near <= 0f ? float.PositiveInfinity : far / near);
    }

    public static int Run(float extentMeters) => Run(extentMeters, BudgetSecondsAtHaulerPace);
}
