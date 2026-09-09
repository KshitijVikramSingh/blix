using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Debug;

/// <summary>Which ground a scenario is being measured on.</summary>
/// <remarks>
/// <b>Shared, because two harnesses now need it and one definition of "a real map" is the point.</b> §201.
/// It began inside <c>--fightbench</c> for §189, where the chair asked that every combat case run on flat
/// ground and on relief and dense maps — "to see how aggression/defence plays when geography, geometry,
/// other non-participating units and buildings are around". That axis then explained more than anything
/// else in the arc, and group cohesion needs exactly the same three grounds for exactly the same reason.
/// <para>
/// Deliberately NOT copied into the movement benchmarks. Two spellings of "a rough map" would drift, and a
/// figure quoted from one harness would stop being comparable with the other — which is the whole value of
/// having the axis at all.
/// </para>
/// </remarks>
internal enum ScenarioGround
{
    /// <summary>An empty plain, which is where every constant in this game was measured.</summary>
    Flat,

    /// <summary>Thirty metres of relief with the country painted on it, and woodland scattered.</summary>
    Rough,

    /// <summary>Rough, plus a working settlement: buildings, trees and busy bystanders.</summary>
    Village,
}

/// <summary>Builds the ground a scenario runs on, and says where on it there is room.</summary>
internal static class Grounds
{
    private const int TicksPerSecond = 30;
    private static readonly FactionId Ours = new(0);

    /// <summary>
    /// Builds the ground, and says where on it there is room to work.
    /// </summary>
    /// <remarks>
    /// The centre comes from <c>SettlementScenarios.ChooseSite</c> rather than being picked, because that is
    /// the routine the game itself uses to find ground level enough to stand a settlement on — so a
    /// scenario placed there is on ground somebody would plausibly be using, and not in a river.
    /// </remarks>
    public static SimulationWorld Build(ScenarioGround ground, float extent, out Vector2 centre)
    {
        if (ground == ScenarioGround.Flat)
        {
            centre = Vector2.Zero;
            return new SimulationWorld(extent);
        }

        // <b>Quiet during setup.</b> Founding a settlement prints its country, its site reasoning and five
        // lines of economy survey, and a ground axis calls it many times over — which buried the tables
        // these harnesses exist to print under two hundred lines of scenery.
        var spoke = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            return Quietly(ground, extent, out centre);
        }
        finally
        {
            Console.SetOut(spoke);
        }
    }

    private static SimulationWorld Quietly(ScenarioGround ground, float extent, out Vector2 centre)
    {
        var world = new SimulationWorld(extent);
        world.Terrain.SetRegion(Region.Downland);
        var layout = MapLayout.Composed(Archetype.SplitValley, extent, 0x5EED1234u, 30f);
        ReliefPlan.FromLayout(layout, extent, 0x5EED1234u).Apply(world.Terrain);
        SettlementScenarios.PaintCountry(world);
        world.RebuildTerrainNavigation();
        centre = SettlementScenarios.ChooseSite(world, extent);

        if (ground == ScenarioGround.Village)
        {
            // Somebody else's day going on around the scenario: buildings to path around, woodland over the
            // whole map, and a dozen bodies with jobs of their own to happen through.
            SettlementScenarios.Populate(
                world, farms: 4, woodcutters: 3, quarriers: 1, carts: 2, wagons: 1,
                centre: centre, faction: Ours);
            for (var t = 0; t < 30 * TicksPerSecond; t++)
            {
                world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            }
        }

        return world;
    }
}
