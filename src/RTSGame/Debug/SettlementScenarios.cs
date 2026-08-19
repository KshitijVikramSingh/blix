using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;

namespace RTSGame.Debug;

/// <summary>
/// A settlement that produces, stores, hauls and consumes, run headless for as long as you like.
/// </summary>
/// <remarks>
/// This is Session 6's gate: <em>a settlement runs a full year with no counter drifting and no unit
/// permanently stalled.</em> Both halves are asserted rather than eyeballed, and the first one is exact
/// — stock is counted in whole units, so <c>seeded + produced − consumed</c> must equal what is stored
/// plus what is on somebody's back, with no tolerance. A single unit appearing or vanishing anywhere in
/// 162,000 ticks fails it and says which resource.
/// <para>
/// It is also §15's per-PR soak gate: a year is about four minutes at this population, which covers
/// three of §8's five stress points — a quiet season, the harvest crunch, and the winter drain.
/// </para>
/// </remarks>
internal static class SettlementScenarios
{
    private const int TicksPerSecond = 30;

    /// <summary>Farms, at one pair of hands each, which is the efficient staffing.</summary>
    /// <remarks>
    /// Lean rather than piled, because output has diminishing returns in hands at one place: twelve farms
    /// of one hand out-produce six of two by forty per cent for the same labour. That is §2's identity
    /// as a layout decision — lean staffing is efficient and wants attention at every season boundary,
    /// overstaffing is autonomous and wasteful — and the trace is where a player would see it.
    /// </remarks>
    private const int Farms = 12;

    private const int Woodcutters = 7;

    /// <summary>Hauler carts, at 0.55 m and 40 units, all of them Foot class.</summary>
    private const int Carts = 7;

    /// <summary>People per household. Houses are the clocked sinks; nothing else eats.</summary>
    private const int Occupancy = 4;

    /// <summary>
    /// Wagons, at 0.90 m — <b>zero, and that is a finding rather than a preference.</b>
    /// </summary>
    /// <remarks>
    /// Debt 7 wants many haulers of differing sizes sharing routes, and two wagons were in here for
    /// exactly that. They cannot work, and the reason is not the economy: <b>a Heavy-class body cannot be
    /// routed to a point beside a 1.5 m building on this map at all.</b> Measured — the wagon asks, is
    /// refused, and sits in the movement layer's limbo state; over a year, two of them accumulated a
    /// thousand refusals between them while a cost field priced the same journey at 89 seconds, so the
    /// route exists and the hierarchical search will not find it. Take the wagons out and the stalls go
    /// to zero with nothing else changed.
    /// <para>
    /// That is a routing question and it belongs with debt 6, which already flags that placing a building
    /// re-rasterises and that what it should do to the decomposition is undecided. Twenty scattered
    /// 1.5 m buildings is a case <c>--routingtest</c>'s staggered walls do not cover, and its <c>lost</c>
    /// column — cells the hierarchy cannot price — is the number to look at. Until then the settlement
    /// runs on carts, and debt 7 stays open for the second reason in a row that is not the one it expected.
    /// </para>
    /// </remarks>
    private const int Wagons = 0;

    /// <summary>Units of one resource a producer's yard holds before production stops.</summary>
    /// <remarks>
    /// The harvest crunch, in one number. A farm at the spike brings in 0.41 units a second and a
    /// hauler clears 40 units a round trip, so a yard this size fills in six minutes if nobody comes
    /// for it — and production then stops rather than grain being counted and dropped on the floor.
    /// §2 wanted harvest failure graded rather than binary: you bring in what your standing arrangement
    /// can carry, and this is where that happens.
    /// </remarks>
    private const int YardCapacity = 150;

    public static int Run(float extentMeters, float years)
    {
        var world = Build(extentMeters, out var granary);
        var totalTicks = (int)(years * WorldCalendar.YearSeconds * TicksPerSecond);
        var faults = new List<string>();

        Console.WriteLine(
            $"RTSGame settlement — {world.ExtentMeters:F0} m, {world.Agents.LiveCount} people, " +
            $"{world.Nodes.LiveCount} nodes, {years:F2} year(s)");
        Console.WriteLine(
            $"  {Farms} farms and {Woodcutters} woodcutters at one hand each, {Carts} carts and " +
            $"{Wagons} wagons, one granary of {world.Nodes.Get(granary).Capacity:N0}, " +
            $"housing for {Occupancy} per household");
        Console.WriteLine(
            "        date        | grain | wood  | hands | hauls | carrying | grain-left | wood-left | " +
            "short | unhoused | stalled | jam | ms/tick");

        var reported = Season.Winter;
        for (var tick = 1; tick <= totalTicks; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);

            // Conservation is checked every tick, not every season. It is a handful of additions, and
            // the value of an exact ledger is knowing the tick a unit went missing on.
            var drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (drift.Grain != 0 || drift.Wood != 0)
            {
                faults.Add(
                    $"conservation broke on tick {tick} ({world.Date}): " +
                    $"{drift.Grain:+#;-#;0} grain, {drift.Wood:+#;-#;0} wood unaccounted for");
                break;
            }

            if (world.Date.Season == reported) continue;
            reported = world.Date.Season;
            Report(world, faults);
        }

        if (world.Date.Season != reported) Report(world, faults);
        Summarise(world, years);

        foreach (var fault in faults) Console.WriteLine($"  FAULT: {fault}");
        return faults.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// A settlement laid out inside one catchment, because that is what a catchment is for.
    /// </summary>
    /// <remarks>
    /// Producers are placed in a ring around the granary at a little over half the catchment radius, so
    /// every one of them is comfortably inside it and the haul legs are the length §6 says they should
    /// be. Everybody who works stands at their node under a <c>Hold</c> assignment, which is what makes
    /// them count as hands — so the economy is driven by the jobs layer rather than by a parallel notion
    /// of employment.
    /// </remarks>
    private static SimulationWorld Build(float extentMeters, out NodeId granary)
    {
        var world = new SimulationWorld(extentMeters);
        granary = Populate(world, Farms, Woodcutters, Carts, Wagons, ringRadius: 36f);
        return world;
    }

    /// <summary>
    /// Lays a working settlement into an existing world, and returns its granary.
    /// </summary>
    /// <remarks>
    /// Shared by the headless gate and the live game, deliberately: two settlement definitions would
    /// drift, and the one thing worth being able to say about the thing on screen is that it is the
    /// same arrangement the year-long run asserts about.
    /// </remarks>
    public static NodeId Populate(
        SimulationWorld world,
        int farms,
        int woodcutters,
        int carts,
        int wagons,
        float ringRadius,
        Vector2 centre = default)
    {
        var granary = world.AddNode(NodeKind.Granary, centre, capacity: 4000);

        // Spring harvests nothing, so a settlement that starts in spring starts on its stores: 22
        // mouths eat about 1,320 grain before the first crop is tended. Seeded rather than conjured —
        // the ledger records it, so conservation still balances.
        world.SeedStock(granary, Resource.Grain, 2000);
        world.SeedStock(granary, Resource.Wood, 1000);

        // Houses ring the granary well inside its catchment, because a household outside every catchment
        // is a household that goes hungry however full the stores are. The ring is sized so the gaps
        // between houses are at least a couple of building widths: buildings are 4.5 m across now, and a
        // ring that fitted them when they were 1.5 m puts them shoulder to shoulder with no room for a
        // cart to pass between.
        var people = farms + woodcutters + carts + wagons;
        var households = (people + Occupancy - 1) / Occupancy;
        var houseWidth = NodeFootprint.HalfExtentOf(NodeKind.House) * 2f;
        var houseRing = MathF.Max(
            ringRadius * 0.4f,
            households * houseWidth * 2f / MathF.Tau);
        for (var i = 0; i < households; i++)
        {
            var angle = (i + 0.5f) / households * MathF.Tau;
            world.AddNode(
                NodeKind.House,
                centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * houseRing,
                capacity: 0,
                occupancy: Occupancy);
        }

        var producers = new List<(NodeId Node, Vector2 At)>();
        var count = farms + woodcutters;
        // Same rule for the producers: whatever the caller asked for, or far enough out that the ring has
        // two building widths of gap between neighbours, whichever is larger.
        var yardWidth = NodeFootprint.HalfExtentOf(NodeKind.Farm) * 2f;
        var producerRing = MathF.Max(ringRadius, count * yardWidth * 2f / MathF.Tau);
        for (var i = 0; i < count; i++)
        {
            var angle = i / (float)count * MathF.Tau;
            var at = centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * producerRing;
            var farm = i < farms;
            producers.Add((
                world.AddNode(
                    farm ? NodeKind.Farm : NodeKind.Woodcutter,
                    at,
                    YardCapacity,
                    farm ? Resource.Grain : Resource.Wood),
                at));
        }

        // One hand per producer, posted. Staggered dwell, so the settlement does not breathe in unison
        // — see the note on the stagger below.
        for (var i = 0; i < producers.Count; i++)
        {
            var (node, _) = producers[i];
            // Read the node's position back rather than using the one it was asked for: a building is
            // snapped to its placement cell, which can move it by up to half a cell diagonal, and a hand
            // spawned relative to the original point could end up standing inside its own farm's wall.
            // Two of nineteen did exactly that, and the settlement lost a fifth of its harvest to it.
            var placed = world.Nodes.Get(node).Position;
            var extent = world.Nodes.Get(node).FootprintRadius;
            // Outward from the centre, so hands stand on the far side of the yard from the traffic.
            var outward = placed - centre;
            outward = outward.LengthSquared() > 0.001f ? Vector2.Normalize(outward) : Vector2.UnitX;
            var hand = world.SpawnAgent(
                placed + outward * (extent + 1.3f), UnitType.Villager);
            world.QueueAssign(
                new[] { hand },
                Assignment.Hold(placed, Stagger(20f, i, producers.Count), extent));
        }

        for (var i = 0; i < carts + wagons; i++)
        {
            var angle = i / (float)(carts + wagons) * MathF.Tau;
            world.SpawnAgent(
                centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 5f, UnitType.HaulerCart);
        }

        return granary;
    }

    /// <summary>
    /// Spreads a dwell across a cohort so they do not all finish at once.
    /// </summary>
    /// <remarks>
    /// Debt 11 from the jobs trace: sixteen units given the same assignment in the same tick arrive,
    /// dwell and leave together forever, and a lane's throughput pulses between nothing and everything
    /// instead of settling. A tenth of a period of spread is enough to break the lockstep, and it is the
    /// caller's business rather than the jobs layer's — the layer is right to be deterministic, and
    /// whoever hands out the work is the one who knows how many are being handed it.
    /// </remarks>
    private static float Stagger(float period, int index, int count) =>
        period * (1f + 0.1f * (index / (float)Math.Max(1, count) - 0.5f));

    private static float ClearanceAt(SimulationWorld world, Vector2 position) =>
        world.Navigation.TryWorldToCell(position, out var cell) ? world.Navigation.Clearance(cell) : -1f;

    private static void Report(SimulationWorld world, List<string> faults)
    {
        var grain = world.Economy.Outlook(Resource.Grain, world.Nodes, world.Agents, world.Date.Season);
        var wood = world.Economy.Outlook(Resource.Wood, world.Nodes, world.Agents, world.Date.Season);
        var carried = EconomySystem.CarriedTotal(world.Agents);
        var hands = 0;
        foreach (ref readonly var node in world.Nodes.All) hands += node.Hands;

        var stalled = 0;
        foreach (ref readonly var agent in world.Agents.All)
        {
            if (!agent.IsAlive || !agent.Jobs.CannotReachWork) continue;
            stalled++;
            // The numbers that identify the cause: how far it is against how near it has to be, and
            // what it thinks it is standing next to. Guessing at this cost two rounds.
            faults.Add(
                $"{agent.Id} (r={agent.Radius:F2}) cannot reach its {agent.Jobs.Assignment.Kind} at " +
                $"({agent.Jobs.Place.X:F1},{agent.Jobs.Place.Y:F1}) extent={agent.Jobs.PlaceExtent:F2}: " +
                $"it is {Vector2.Distance(agent.Position, agent.Jobs.Place):F2} m away and needs " +
                $"{JobDefaults.AtPlaceDistance(agent.Radius, agent.Jobs.PlaceExtent):F2}, " +
                $"{agent.Jobs.Retries} tries, state={agent.LocomotionState} " +
                $"moving={agent.HasDestination} route={world.GetRemainingPath(agent.Id).Length} " +
                $"navigable-here={world.IsAgentGeometryValid(agent.Id)} " +
                $"navRadius={agent.NavigationRadius:F2} " +
                $"clearance-here={ClearanceAt(world, agent.Position):F2} " +
                $"clearance-at-place={ClearanceAt(world, agent.Jobs.Place):F2} " +
                $"priced={world.TryTravelSeconds(agent.Position, agent.Jobs.Place, agent.NavigationRadius, out var seconds)}/{seconds:F0}s, " +
                $"at {world.Date}");
        }

        Console.WriteLine(
            $"  {world.Date,-18} | {grain.Stored,5:N0} | {wood.Stored,5:N0} | {hands,5} | " +
            $"{world.Economy.HaulsAssigned,5:N0} | {carried.Total,8:N0} | " +
            $"{Seasons(grain.Seasons),10} | {Seasons(wood.Seasons),9} | " +
            $"{world.Economy.Unmet.Grain + world.Economy.Unmet.Wood,5:N0} | " +
            $"{world.UnhousedCount,8} | {stalled,7} | " +
            $"{world.Congestion.Peak,3:F0} | " +
            $"{world.Timings.Format(world.Agents.Count, world.TickNumber).Split("total ")[1].Split(" ms")[0]}");
    }

    /// <summary>§8's autonomy time, rendered as the one thing the HUD says: how long.</summary>
    private static string Seasons(float seasons) => float.IsPositiveInfinity(seasons)
        ? "growing"
        : $"{seasons:F1} seas";

    private static void Summarise(SimulationWorld world, float years)
    {
        var economy = world.Economy;
        Console.WriteLine(
            $"  over {years:F2} year(s): produced {economy.Produced.Grain:N0} grain and " +
            $"{economy.Produced.Wood:N0} wood, ate {economy.Consumed.Grain:N0} and " +
            $"{economy.Consumed.Wood:N0}, went short {economy.Unmet.Grain:N0} and {economy.Unmet.Wood:N0}");
        Console.WriteLine(
            $"  hauling: {economy.HaulsAssigned:N0} jobs given out, {economy.HaulsAbandoned:N0} dropped " +
            "when the source emptied or the sink filled");

        // What the year cost per person, against what the rates say it should have. A settlement that
        // ate less than its appetite went short somewhere, and the shortfall column says where.
        var mouths = world.Agents.LiveCount;
        Console.WriteLine(
            $"  per person per year: {economy.Consumed.Grain / MathF.Max(1f, mouths * years):N0} grain " +
            $"against a nominal {EconomyRates.GrainPerVillagerPerYear:N0}, " +
            $"{economy.Consumed.Wood / MathF.Max(1f, mouths * years):N0} wood against " +
            $"{EconomyRates.WoodPerVillagerPerYear:N0}");
    }
}
