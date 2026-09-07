using System.Globalization;
using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;
using RTSGame.AI;
using RTSGame.Simulation.Jobs;
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
        bool swapFactions = false,
        bool onlyOne = false,
        bool dressTwice = false,
        bool bot = false,
        bool bothBots = false,
        int gapsPerDecision = AI.Planning.Planner.GapsPerDecision,
        string planName = "settler")
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
        var second = SettlementScenarios.NeighbourSite(world, first, apart);
        var recipe = SettlementScenarios.VillageRecipe.AsPlayed;
        siteA = first;
        siteB = second;

        // <b>Which faction founds first is a flag, because the first run left one settlement inert and the
        // symptom alone cannot say whether that follows the ORDER or the ID.</b> Trees belong to faction zero
        // by default — sixty-nine thousand of them — so "faction zero behaves differently" and "the first one
        // founded behaves differently" are both live explanations, and they want opposite fixes.
        var firstFaction = new FactionId(swapFactions ? 1 : 0);
        var secondFaction = new FactionId(swapFactions ? 0 : 1);
        var granaryA = SettlementScenarios.Populate(
            world, recipe.Farms, recipe.Woodcutters, recipe.Quarriers, recipe.Carts, recipe.Wagons,
            first, firstFaction);
        // <b>The control this probe was missing.</b> It differs from the passing year leg in three ways at
        // once — two settlements, the as-played recipe, and thirty-two metres of relief — so "the second
        // village breaks the first" was an assumption with two other candidates standing beside it. With
        // --onevillage the first settlement is alone and everything else is held.
        var granaryB = granaryA;
        if (!onlyOne)
        {
            // dressMap: false — the map is already dressed, and dressing it again re-forests the first
            // settlement's cleared ground. §130. --dresstwice restores the old behaviour so the fault stays
            // reachable: a fixed bug with no way left to reproduce it is a fix nobody can check.
            granaryB = SettlementScenarios.Populate(
                world, recipe.Farms, recipe.Woodcutters, recipe.Quarriers, recipe.Carts, recipe.Wagons,
                second, secondFaction, dressMap: dressTwice);
        }

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

        // <b>Who is assigned, and where the hands are, per faction.</b> §129 eliminated three explanations and
        // stopped short of this one. An inert settlement is either a settlement whose people were never given
        // work, or one whose people have work and are not doing it, and those want opposite fixes — the first
        // is a command that did not arrive, the second is a job that cannot be carried out.
        Console.WriteLine();
        ReportWork(world, "before the first tick");
        // <b>A full rebuild, as the control on §101's windowed raster.</b> That optimisation re-rasterises only
        // the ground a placement change touched, which is right for one settlement and is the only thing in
        // this sequence that could block cells at a site nothing was built on. If a whole-map rebuild restores
        // the ground, the window is the fault; if it does not, the fault is in what the window was given.
        world.RebuildTerrainNavigation();
        ReportWork(world, "after a full rebuild");
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        ReportWork(world, "after one tick");
        for (var warm = 0; warm < TicksPerSecond * 30; warm++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        }

        ReportWork(world, "after thirty seconds");
        Console.WriteLine();

        // <b>The bot's settlement starts with nobody employed.</b> Founding posts every hand at a producer,
        // which would leave a bot with nothing to do and an acceptance test that proved nothing. Stripping the
        // assignments makes the claim the sharp one: can a rule-bot take a settlement that is standing idle
        // and put it to work through the same commands a person has. §132.
        AI.Planning.Planner? driver = null;
        AI.Planning.Planner? other = null;
        if (bot || bothBots)
        {
            driver = Unemploy(world, secondFaction, gapsPerDecision, planName);
        }

        // <b>And the first settlement too, when the whole map is left to itself.</b> §140. This is the state
        // the design is actually heading for: nobody steering either side, so what a year produces is what
        // the rules produce. It also makes the two settlements comparable in a way §136 wanted and could not
        // have — same recipe, same starting cache, same driver, different ground.
        if (bothBots)
        {
            other = Unemploy(world, firstFaction, gapsPerDecision, planName);
        }

        // Recorded before the clock starts, because "did anything happen" is a comparison and needs both ends.
        var openingGrain = new[] { StockOf(world, 0, Resource.Grain), StockOf(world, 1, Resource.Grain) };
        var faults = new List<string>();
        var carried = new Dictionary<int, (Resource, int)>();
        var totalTicks = (int)(years * WorldCalendar.YearSeconds * TicksPerSecond);
        var reported = world.Date.Season;
        Console.WriteLine(
            "        date        | f0 grain | f0 wood | f0 people | f1 grain | f1 wood | f1 people | " +
            "f0 work | f1 work | drift");
        for (var tick = 1; tick <= totalTicks; tick++)
        {
            // Inside the loop and before the tick, on the RaidDirector's precedent: a driver stepped with the
            // fixed step decides at the same moment however fast the clock is running, so a watched run and a
            // headless one see the same game.
            driver?.Update(world);
            other?.Update(world);
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);

            // Conservation across BOTH, every tick, exactly as the one-village year leg does it. A unit of
            // grain moving from one faction's books to the other's would net to zero here and has to be
            // caught by the per-faction stores below instead.
            var drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero && faults.Count == 0)
            {
                Console.WriteLine(
                    $"  drift at tick {tick}: grain {drift.Grain}, wood {drift.Wood}, stone {drift.Stone}");
                // <b>Catch the body in the act.</b> §163: three call sites were guarded on a hypothesis and
                // the drift did not move, which is the same way five hypotheses about the uphill reaches went.
                // A body's cargo is one resource and one count, so a swap has to be a body whose Carrying
                // changed while it was holding something. This says which one, and what it was holding.
                foreach (ref readonly var body in world.Agents.All)
                {
                    if (!body.IsAlive) continue;
                    var was = carried.TryGetValue(body.Id.Value, out var seen) ? seen : (Resource.Grain, 0);
                    var now = (body.Jobs.Carrying, body.Jobs.CarriedUnits);
                    if (was == now) continue;
                    if (was.Item2 > 0 && was.Item1 != now.Carrying)
                    {
                        Console.WriteLine(
                            $"    RECLASSIFIED body {body.Id.Value}: was {was.Item2} {was.Item1}, " +
                            $"now {now.CarriedUnits} {now.Carrying}; " +
                            $"job {body.Jobs.Assignment.Kind} cargo {body.Jobs.Assignment.Cargo} " +
                            $"leg {body.Jobs.Leg} project {body.Jobs.Project.Value}");
                    }
                }
            }

            foreach (ref readonly var body in world.Agents.All)
            {
                if (body.IsAlive) carried[body.Id.Value] = (body.Jobs.Carrying, body.Jobs.CarriedUnits);
            }

            if (!drift.IsZero)
            {
                faults.Add($"conservation broke on tick {tick} ({world.Date})");
                break;
            }

            if (world.Date.Season == reported) continue;
            reported = world.Date.Season;
            Report(world, granaryA, granaryB);
            ReportFields(world, 0);
            ReportFields(world, 1);
        }

        Report(world, granaryA, granaryB);
        Console.WriteLine();

        // <b>The test names something that must CHANGE, and the first version did not.</b> It asked whether
        // each faction ended with people and with grain above zero, and reported "both settlements fed
        // themselves" over one that had not eaten a grain in ninety days — an economy doing nothing satisfies
        // both halves perfectly. §130. A settlement that is alive is one whose stores move, so that is what is
        // asserted, alongside nobody going hungry.
        foreach (var (label, faction) in new[] { ("faction 0", 0), ("faction 1", 1) })
        {
            var people = PeopleOf(world, faction);
            if (people == 0 && StockOf(world, faction, Resource.Grain) == 0)
            {
                // A faction that was never founded — the --onevillage control has one — is not a fault.
                continue;
            }

            var grain = StockOf(world, faction, Resource.Grain);
            var moved = Math.Abs(grain - openingGrain[faction]);
            if (people == 0) faults.Add($"{label} has nobody left");
            else if (grain <= 0) faults.Add($"{label} ended with no grain and {people} mouths");
            else if (moved == 0)
            {
                faults.Add(
                    $"{label} is inert: {people} people and {grain} grain, unchanged from the {openingGrain[faction]} " +
                    "it started with — nobody ate and nobody harvested");
            }
        }

        // <b>What it built, not how many orders it issued.</b> §140: "four sites laid" is a count of the
        // bot's own decisions and says nothing about whether anything is standing — the same distinction as
        // §135's stores-changed rule. A run where a bot lays four sites and finishes none looks identical in
        // the counters and is a total failure on the ground.
        foreach (var ran in new[] { other, driver })
        {
            if (ran is null) continue;
            var standing = Structures(world, ran.Faction);
            Console.WriteLine(
                $"  faction {ran.Faction.Value} built: {standing.Barracks} barracks, " +
                $"{standing.Palisades} palisade, {standing.StoneWalls} stone wall, " +
                $"{standing.Unfinished} still going up; {standing.Militia} militia standing");
            // Only where the clock allows it at all. I first set this at nine tenths of a year on the
            // arithmetic — a barracks is 1,800 labour-seconds and two hands cannot spend that in the 540 a
            // tenth of a year gives them — and the gate's own short leg then built one with four militia
            // standing. The arithmetic is not the measurement, so the threshold is the measured one: every
            // leg long enough to have been observed doing it asserts that it did.
            if (years < 0.09f)
            {
                continue;
            }

            if (standing.Barracks == 0)
            {
                faults.Add(
                    $"faction {ran.Faction.Value}'s bot never finished a barracks — " +
                    $"{standing.Unfinished} unfinished");
            }
            else if (standing.Militia == 0)
            {
                faults.Add(
                    $"faction {ran.Faction.Value}'s bot has a barracks and no militia");
            }
        }

        foreach (var ran in new[] { other, driver })
        {
            if (ran is null) continue;
            // <b>Orders and gaps, not sites laid.</b> §141: the planner has no tally of its own acts
            // because the world already holds one — see the structures line above, which counts what is
            // standing. What only the planner knows is how often a rule wanted something it could not have,
            // and that is the figure worth printing.
            Console.WriteLine(
                $"  faction {ran.Faction.Value}'s '{ran.PlanName}' plan: {ran.Decisions:N0} decisions, " +
                $"{ran.OrdersIssued:N0} orders, {ran.GapsLeftOpen:N0} gap(s) left open");
            // The plan as written and the last of what it did. §141's explainer: a plan that cannot say why
            // is untestable, and this scenario is where "why" gets asked.
            Console.WriteLine(ran.Describe());
        }

        foreach (var fault in faults) Console.WriteLine($"  FAULT: {fault}");
        if (faults.Count == 0)
        {
            Console.WriteLine("  both settlements fed themselves, and the books balanced every tick");
        }

        return faults.Count > 0 ? 1 : 0;
    }

    /// <summary>Assignments held and hands present, per faction, so an inert settlement can name its stage.</summary>
    private static Vector2 siteA;
    private static Vector2 siteB;

    private static void ReportWork(SimulationWorld world, string when)
    {
        Console.WriteLine($"  work, {when}: travel refusals so far {world.TravelRefusals}");
        // <b>Did the ground change?</b> Both directions of the failing pair are unpriced, so the two cells are
        // not connected — which is a claim about walkability, not about the cost field. This counts the ground
        // each settlement sits on, so a run with one village and a run with two can be compared cell for cell.
        foreach (var (label, at) in new[] { ("faction 0's site", siteA), ("faction 1's site", siteB) })
        {
            var blocked = 0;
            var solid = 0;
            var tight = 0;
            var walkable = 0;
            var radius = Simulation.Agents.AgentDefaults.RoutingRadius;
            for (var dz = -40; dz <= 40; dz++)
            for (var dx = -40; dx <= 40; dx++)
            {
                var probe = at + new Vector2(dx, dz) * world.Navigation.Transform.CellSize;
                if (!world.Navigation.TryWorldToCell(probe, out var cell)) continue;
                if (world.Navigation.IsWalkable(cell, radius)) { walkable++; continue; }
                blocked++;
                // <b>Solid, or merely tight?</b> IsWalkable refuses a cell that is marked blocked and a cell
                // whose clearance is under the body's radius, and those are different faults: the first is an
                // obstacle standing there, the second is an obstacle standing NEAR and the distance field
                // saying so. §116 spent a section on the difference.
                if (world.Navigation.IsBlocked(cell)) solid++;
                else tight++;
            }

            // <b>And how many trees are actually alive there.</b> If the count is the same in both runs then
            // the trees were never killed by the founding — only dropped from whatever index the raster reads —
            // and the run that looks healthy is the one with a stale index rather than cleared ground.
            var standing = 0;
            foreach (ref readonly var node in world.Nodes.All)
            {
                if (!node.IsAlive || !node.IsNaturalDeposit) continue;
                if (Vector2.Distance(node.Position, at) <= 20f) standing++;
            }

            Console.WriteLine(
                $"    {label}: {walkable} walkable, {blocked} refused within 20 m " +
                $"({solid} marked solid, {tight} clear ground with too little room), " +
                $"{standing} deposits alive inside 20 m");
        }
        // <b>Where each faction's things actually are.</b> A settlement 198 m away cannot block ground it is
        // not standing on, so either it is standing somewhere unexpected or something else is. A bounding box
        // per faction answers the first half in one line, and the placement grid answers the second: a cell
        // blocked by a building is a different fault from a cell blocked by a tree.
        foreach (var faction in new[] { 0, 1 })
        {
            var low = new Vector2(float.MaxValue);
            var high = new Vector2(float.MinValue);
            var settlement = 0;
            foreach (ref readonly var node in world.Nodes.All)
            {
                if (!node.IsAlive || node.Faction.Value != faction || node.IsNaturalDeposit) continue;
                settlement++;
                low = Vector2.Min(low, node.Position);
                high = Vector2.Max(high, node.Position);
            }

            if (settlement == 0) continue;
            Console.WriteLine(
                $"    faction {faction}'s {settlement} buildings span ({low.X:F0},{low.Y:F0}) to " +
                $"({high.X:F0},{high.Y:F0})");
        }

        foreach (var faction in new[] { 0, 1 })
        {
            var assigned = 0;
            var working = 0;
            var interrupted = 0;
            foreach (ref readonly var body in world.Agents.All)
            {
                if (!body.IsAlive || body.Faction.Value != faction) continue;
                if (body.Jobs.Assignment.Kind == AssignmentKind.None) continue;
                assigned++;
                if (body.Jobs.Activity == ActivityKind.Working) working++;
                if (body.Jobs.IsInterrupted) interrupted++;
            }

            var handed = 0;
            var sinks = 0;
            var supplied = 0;
            foreach (ref readonly var node in world.Nodes.All)
            {
                if (!node.IsAlive || node.Faction.Value != faction) continue;
                if (node.Hands > 0) handed++;
                if (!node.IsSink) continue;
                sinks++;
                if (node.Supply.IsValid) supplied++;
            }

            Console.WriteLine(
                $"    faction {faction}: {assigned} assigned ({working} active, {interrupted} interrupted), " +
                $"{handed} nodes with hands, {supplied}/{sinks} sinks bound to a store");

            // <b>And for an unbound sink, what the binder was told.</b> BindCatchments accepts a store only if
            // travel seconds price and land inside the catchment budget, so an unbound house is either a pair
            // the router will not price at all or a price over the line — and §125 measured that same threshold
            // being displaced by the hierarchy's 17% overestimate. Which of the two it is decides everything.
            if (supplied >= sinks) continue;
            foreach (ref readonly var sink in world.Nodes.All)
            {
                if (!sink.IsAlive || sink.Faction.Value != faction || !sink.IsSink) continue;
                if (sink.Supply.IsValid) continue;
                foreach (ref readonly var store in world.Nodes.All)
                {
                    if (!store.IsAlive || !store.OwnsCatchment || store.Faction != sink.Faction) continue;
                    // <b>Both directions, because the field is built at the GOAL.</b> A pair that prices one
                    // way and not the other says the failure is the field rather than the ground: the same two
                    // cells are connected, and only the end the Dijkstra started from has changed.
                    var radius = Simulation.Agents.AgentDefaults.RoutingRadius;
                    var forward = world.TryTravelSeconds(
                        sink.Position, store.Position, radius, out var toStore);
                    var back = world.TryTravelSeconds(
                        store.Position, sink.Position, radius, out var toSink);
                    Console.WriteLine(
                        $"      unbound {sink.Kind} at ({sink.Position.X:F0},{sink.Position.Y:F0}) <-> " +
                        $"{store.Kind} at ({store.Position.X:F0},{store.Position.Y:F0}), " +
                        $"{Vector2.Distance(sink.Position, store.Position):F0} m apart, budget " +
                        $"{store.CatchmentSeconds:F0} s");
                    Console.WriteLine(
                        $"        house -> store (field at the store): " +
                        (forward ? $"{toStore:F1} s" : "unpriced") +
                        $" | store -> house (field at the house): " +
                        (back ? $"{toSink:F1} s" : "unpriced"));
                }

                break;
            }
        }
    }

    private static void Report(SimulationWorld world, NodeId granaryA, NodeId granaryB)
    {
        Console.WriteLine(
            $"  {world.Date,-18} | {StockOf(world, 0, Resource.Grain),8:N0} | " +
            $"{StockOf(world, 0, Resource.Wood),7:N0} | {PeopleOf(world, 0),9} | " +
            $"{StockOf(world, 1, Resource.Grain),8:N0} | {StockOf(world, 1, Resource.Wood),7:N0} | " +
            $"{PeopleOf(world, 1),9} | " +
            $"{Work(world, 0)} {Work(world, 1)} | " +
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

    /// <summary>
    /// Every field of a faction, with the hands on it, the legs they have finished and its crop progress.
    /// </summary>
    /// <remarks>
    /// <b>Because §136 measured seven hands producing less than five and no policy can be tuned against
    /// that.</b> A field yielding less with more labour is three different mechanisms wearing one symptom, and
    /// they want opposite fixes:
    /// <list type="bullet">
    /// <item>the extra hand is <b>walking rather than working</b> — it would show few legs finished of its
    /// own while the others are unchanged;</item>
    /// <item>two hands on one field <b>halve each other's shifts</b> — the field's total legs would be flat in
    /// the hands posted to it;</item>
    /// <item>the yield is <b>capped by something other than labour</b> — reap progress would be complete
    /// either way and the extra hands are simply eating.</item>
    /// </list>
    /// Legs completed is the jobs layer's own counter and the figure §? built the jobs trace around, so this
    /// is a report of an existing number rather than a new one.
    /// </remarks>
    private static void ReportFields(SimulationWorld world, int faction)
    {
        var hands = new Dictionary<int, (int Hands, int Legs)>();
        foreach (ref readonly var body in world.Agents.All)
        {
            if (!body.IsAlive || body.Faction.Value != faction || body.HasCart) continue;
            if (body.Jobs.Assignment.Kind != AssignmentKind.Work) continue;
            if (body.Jobs.Assignment.Cargo != Resource.Grain) continue;
            var site = body.Jobs.Assignment.Source.Value;
            var seen = hands.GetValueOrDefault(site);
            hands[site] = (seen.Hands + 1, seen.Legs + body.Jobs.LegsCompleted);
        }

        var line = new List<string>();
        foreach (var id in world.Nodes.SettlementNodes)
        {
            ref readonly var node = ref world.Nodes.Get(id);
            if (!node.IsAlive || node.Faction.Value != faction || node.Kind != NodeKind.Farm) continue;
            var on = hands.GetValueOrDefault(id.Value);
            var reap = CropCycle.ReapTargetOf(in node);
            var progress = reap <= 0f ? 0f : node.ReapWork / reap * 100f;
            line.Add($"[{on.Hands}h {on.Legs}L {progress:F0}%]");
        }

        Console.WriteLine($"    f{faction} fields: {string.Join(" ", line)}");
    }

    /// <summary>
    /// Where a faction's hands are, so two settlements' output can be compared by their labour.
    /// </summary>
    /// <remarks>
    /// <b>Because "the bot underproduces" is not a finding.</b> §135 had it feeding itself and never growing
    /// against a founding that grew from thirteen to nineteen, and the candidate explanations — too few on the
    /// fields, too much churn, hands idle — are distinguishable only by counting them. Carts excluded: they are
    /// the hauling board's and belong to neither policy.
    /// </remarks>
    private static string Work(SimulationWorld world, int faction)
    {
        var fields = 0;
        var wood = 0;
        var other = 0;
        var idle = 0;
        foreach (ref readonly var body in world.Agents.All)
        {
            if (!body.IsAlive || body.Faction.Value != faction || body.HasCart) continue;
            if (!body.Jobs.HasAssignment) { idle++; continue; }
            if (body.Jobs.Assignment.Kind != AssignmentKind.Work) { other++; continue; }
            if (body.Jobs.Assignment.Cargo == Resource.Grain) fields++;
            else if (body.Jobs.Assignment.Cargo == Resource.Wood) wood++;
            else other++;
        }

        return $"f{fields}w{wood}o{other}i{idle}";
    }

    /// <summary>Houses standing, so "growth is capped by housing" can be seen rather than assumed.</summary>
    private static int HousesOf(SimulationWorld world, int faction)
    {
        var houses = 0;
        foreach (var id in world.Nodes.SettlementNodes)
        {
            ref readonly var node = ref world.Nodes.Get(id);
            if (node.IsAlive && node.Faction.Value == faction && node.Kind == NodeKind.House) houses++;
        }

        return houses;
    }

    /// <summary>
    /// Takes a faction's whole workforce off work and hands it to a bot.
    /// </summary>
    /// <remarks>
    /// <b>The bot's settlement starts with nobody employed.</b> Founding posts every hand at a producer,
    /// which would leave a bot with nothing to do and an acceptance test that proved nothing. Stripping the
    /// assignments makes the claim the sharp one: can a rule-bot take a settlement standing idle and put it
    /// to work through the same commands a person has. §132.
    /// </remarks>
    private static AI.Planning.Planner Unemploy(
        SimulationWorld world,
        FactionId faction,
        int gapsPerDecision,
        string planName)
    {
        var theirs = new List<Simulation.Agents.AgentId>();
        foreach (ref readonly var body in world.Agents.All)
        {
            if (body.IsAlive && body.Faction == faction) theirs.Add(body.Id);
        }

        world.QueueAssign(theirs, Assignment.None);
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        Console.WriteLine(
            $"  faction {faction.Value} is driven by a bot, and starts with {theirs.Count} people and no work");
        return new AI.Planning.Planner(faction, AI.Planning.Plan.Named(planName), gapsPerDecision);
    }

    /// <summary>What one faction has standing, counted from the world rather than from the bot's tally.</summary>
    private static (int Barracks, int Palisades, int StoneWalls, int Unfinished, int Militia) Structures(
        SimulationWorld world,
        FactionId faction)
    {
        var barracks = 0;
        var palisades = 0;
        var stone = 0;
        var unfinished = 0;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || node.Faction != faction) continue;
            if (!node.IsBuilt)
            {
                if (node.Kind is NodeKind.Barracks or NodeKind.PalisadeWall) unfinished++;
                continue;
            }

            if (node.Kind == NodeKind.Barracks) barracks++;
            else if (node.Kind == NodeKind.PalisadeWall) palisades++;
            else if (node.Kind == NodeKind.StoneWall) stone++;
        }

        var militia = 0;
        foreach (ref readonly var body in world.Agents.All)
        {
            if (body.IsAlive && body.Faction == faction &&
                body.Role == Simulation.Agents.AgentRole.Militia)
            {
                militia++;
            }
        }

        return (barracks, palisades, stone, unfinished, militia);
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

}
