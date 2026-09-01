using System.Globalization;
using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Debug;

/// <summary>
/// Two settlements on one map, each feeding itself, as the precondition for a second player.
/// </summary>
/// <remarks>
/// <b>The first thing an opponent needs is somewhere to live.</b> The plan's road forward ends at an enemy
/// that plays the same game through the same verbs — construct, gather, haul, group, train — and every one of
/// those already carries a faction: nodes have one, bodies have one, and hauling, catchment and housing all
/// filter on it. What has never happened is two of them in one world at once, because the recipe that lays a
/// village down never passed a faction and so every settlement this project has built has belonged to
/// faction zero.
/// <para>
/// So this founds two and asks the only question worth asking first: <b>do they both still work?</b> Not
/// whether they fight, or trade, or know about each other — whether an economy that was written while there
/// was only ever one of it survives having a neighbour. The acceptance test is the one the year legs already
/// use, applied twice: each settlement feeds itself, and conservation stays exact across both.
/// </para>
/// <para>
/// It is deliberately a probe rather than a feature. What it is looking for is the assumptions — a pile
/// hauled to the wrong granary, a catchment that reaches across a border, a job board that hands one
/// faction's work to another's hands. Every one of those is a number correct in the layer that owns it and
/// wrong where it meets another, which §51 recorded as this project's recurring shape of bug.
/// </para></remarks>
internal static class TwoSettlementScenarios
{
    private const int TicksPerSecond = (int)(1.0 / SimulationWorld.FixedDeltaSeconds);

    public static int Run(
        float extentMeters,
        float years,
        float reliefAmplitudeMetres,
        uint mapSeed,
        bool swapFactions = false)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        var world = new SimulationWorld(extentMeters);
        if (reliefAmplitudeMetres > 0f)
        {
            world.Terrain.SetRegion(Region.Downland);
            var layout = MapLayout.Composed(Archetype.YValley, extentMeters, mapSeed, reliefAmplitudeMetres);
            ReliefPlan.FromLayout(layout, extentMeters, mapSeed).Apply(world.Terrain);
            SettlementScenarios.PaintCountry(world);
            world.RebuildTerrainNavigation();
        }

        // <b>Far apart, and the distance is the point.</b> Two settlements close enough to share ground would
        // be measuring contention before anybody has established that two can exist at all. A third of the map
        // between them means each has its own catchment, and anything that crosses is a fault rather than a
        // crowd.
        var first = SettlementScenarios.ChooseSite(world, extentMeters);
        var apart = extentMeters * 0.33f;
        var second = FarthestWalkable(world, first, apart);
        var recipe = SettlementScenarios.VillageRecipe.AsPlayed;

        // <b>Which faction founds first is a flag, because the first run left one settlement inert and the
        // symptom alone cannot say whether that follows the ORDER or the ID.</b> Trees belong to faction zero
        // by default — sixty-nine thousand of them — so "faction zero behaves differently" and "the first one
        // founded behaves differently" are both live explanations, and they want opposite fixes.
        var firstFaction = new FactionId(swapFactions ? 1 : 0);
        var secondFaction = new FactionId(swapFactions ? 0 : 1);
        var granaryA = SettlementScenarios.Populate(
            world, recipe.Farms, recipe.Woodcutters, recipe.Quarriers, recipe.Carts, recipe.Wagons,
            first, firstFaction);
        var granaryB = SettlementScenarios.Populate(
            world, recipe.Farms, recipe.Woodcutters, recipe.Quarriers, recipe.Carts, recipe.Wagons,
            second, secondFaction);

        Console.WriteLine(
            $"RTSGame two settlements — {extentMeters:F0} m, {years:F2} year(s), " +
            $"seed {mapSeed}, relief {reliefAmplitudeMetres:F0} m");
        Console.WriteLine(
            $"  faction {firstFaction.Value} at ({first.X:F0}, {first.Y:F0}) founded first, " +
            $"faction {secondFaction.Value} at ({second.X:F0}, {second.Y:F0}) second — " +
            $"{Vector2.Distance(first, second):F0} m apart");
        Console.WriteLine(
            $"  {world.Nodes.LiveCount} nodes, {world.Agents.LiveCount} people between them — " +
            $"faction 0 owns {NodesOf(world, 0, false):N0} nodes ({NodesOf(world, 0, true)} stores), " +
            $"faction 1 owns {NodesOf(world, 1, false):N0} ({NodesOf(world, 1, true)} stores)");
        Console.WriteLine();

        var faults = new List<string>();
        var totalTicks = (int)(years * WorldCalendar.YearSeconds * TicksPerSecond);
        var reported = world.Date.Season;
        Console.WriteLine(
            "        date        | f0 grain | f0 wood | f0 people | f1 grain | f1 wood | f1 people | drift");
        for (var tick = 1; tick <= totalTicks; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);

            // Conservation across BOTH, every tick, exactly as the one-village year leg does it. A unit of
            // grain moving from one faction's books to the other's would net to zero here and has to be
            // caught by the per-faction stores below instead.
            var drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero)
            {
                faults.Add($"conservation broke on tick {tick} ({world.Date})");
                break;
            }

            if (world.Date.Season == reported) continue;
            reported = world.Date.Season;
            Report(world, granaryA, granaryB);
        }

        Report(world, granaryA, granaryB);
        Console.WriteLine();

        // <b>The test is that each still feeds itself.</b> A settlement that starves next to a neighbour when
        // it would not have starved alone is the whole reason to run this before building anything on top.
        foreach (var (label, faction) in new[] { ("faction 0", 0), ("faction 1", 1) })
        {
            var people = PeopleOf(world, faction);
            var grain = StockOf(world, faction, Resource.Grain);
            if (people == 0) faults.Add($"{label} has nobody left");
            else if (grain <= 0) faults.Add($"{label} ended with no grain and {people} mouths");
        }

        foreach (var fault in faults) Console.WriteLine($"  FAULT: {fault}");
        if (faults.Count == 0)
        {
            Console.WriteLine("  both settlements fed themselves, and the books balanced every tick");
        }

        return faults.Count > 0 ? 1 : 0;
    }

    private static void Report(SimulationWorld world, NodeId granaryA, NodeId granaryB)
    {
        Console.WriteLine(
            $"  {world.Date,-18} | {StockOf(world, 0, Resource.Grain),8:N0} | " +
            $"{StockOf(world, 0, Resource.Wood),7:N0} | {PeopleOf(world, 0),9} | " +
            $"{StockOf(world, 1, Resource.Grain),8:N0} | {StockOf(world, 1, Resource.Wood),7:N0} | " +
            $"{PeopleOf(world, 1),9} | " +
            $"{(world.Economy.Discrepancy(world.Nodes, world.Agents).IsZero ? "ok" : "BROKEN")}");
    }

    /// <summary>
    /// What a faction has put by — its stores, not everything it owns.
    /// </summary>
    /// <remarks>
    /// <b>Stores only, and the first version of this counted every node.</b> Trees are nodes and they belong
    /// to faction zero by default, so the first run of this probe reported six million wood for one settlement
    /// and eight hundred for the other — a forest, tallied as a granary. The number a settlement lives on is
    /// what is in its stores, which is what the economy's own catchment and hauling read.
    /// </remarks>
    private static int StockOf(SimulationWorld world, int faction, Resource resource)
    {
        var total = 0;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || node.Faction.Value != faction) continue;
            if (!node.Stores) continue;
            total += node.Stock[resource];
        }

        return total;
    }

    /// <summary>Every tree on the map belongs to somebody, and it matters who. See §129.</summary>
    private static int NodesOf(SimulationWorld world, int faction, bool storesOnly)
    {
        var total = 0;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || node.Faction.Value != faction) continue;
            if (storesOnly && !node.Stores) continue;
            total++;
        }

        return total;
    }

    private static int PeopleOf(SimulationWorld world, int faction)
    {
        var total = 0;
        foreach (ref readonly var body in world.Agents.All)
        {
            if (body.IsAlive && body.Faction.Value == faction) total++;
        }

        return total;
    }

    /// <summary>Ground about <paramref name="apart"/> metres from the first site that a body can stand on.</summary>
    private static Vector2 FarthestWalkable(SimulationWorld world, Vector2 from, float apart)
    {
        var best = from;
        var bestScore = float.NegativeInfinity;
        for (var i = 0; i < 64; i++)
        {
            var angle = i / 64f * MathF.Tau;
            var at = from + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * apart;
            if (!world.Terrain.Contains(at, 8f)) continue;
            if (!world.Navigation.TryWorldToCell(at, out var cell)) continue;
            if (!world.Navigation.IsWalkable(cell, Simulation.Agents.AgentDefaults.RoutingRadius)) continue;
            // Flattest wins, because a settlement wants a floor and this is the same judgement ChooseSite
            // makes — without repeating its scoring, which is about farmland and backdrop and is not what
            // this probe is measuring.
            var score = -world.Terrain.SampleGrade(at);
            if (score <= bestScore) continue;
            bestScore = score;
            best = at;
        }

        return best;
    }
}
