using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

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

    public static int Run(
        float extentMeters,
        float years,
        float reliefAmplitudeMetres = 0f,
        Region region = Region.Downland,
        Archetype archetype = Archetype.SplitValley,
        uint mapSeed = 0x5EED1234u)
    {
        var world = Build(extentMeters, out var granary, reliefAmplitudeMetres, region, archetype, mapSeed);
        var totalTicks = (int)(years * WorldCalendar.YearSeconds * TicksPerSecond);
        var faults = new List<string>();

        Console.WriteLine(
            $"RTSGame settlement — {world.ExtentMeters:F0} m, {world.Agents.LiveCount} people, " +
            $"{world.Nodes.LiveCount} nodes, {years:F2} year(s)");
        Console.WriteLine(
            $"  {Farms} farms and {Woodcutters} cutters at one hand each, {Carts + Wagons} spare hands, " +
            $"one granary of {world.Nodes.Get(granary).Capacity:N0}, " +
            $"housing for {Occupancy} per household — a cart costs " +
            $"{SimulationWorld.CartTimber} wood and nobody has built one");
        Console.WriteLine(
            "        date        | grain | wood  | people      | hands | fields             | " +
            "forest         | hauls | carrying | grain-left | wood-left | short | unhoused | stalled | " +
            "ms/tick");

        var reported = Season.Winter;
        // Mouth-seconds, so a per-person figure means something in a settlement whose population moves.
        var mouthSeconds = 0.0;
        for (var tick = 1; tick <= totalTicks; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            mouthSeconds += world.Agents.LiveCount * SimulationWorld.FixedDeltaSeconds;

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
        Summarise(world, years, mouthSeconds);

        foreach (var fault in faults) Console.WriteLine($"  FAULT: {fault}");
        return faults.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// A settlement under raid, headless: does the defence decision behave, and does anything go missing?
    /// </summary>
    /// <remarks>
    /// <b>Stage E had no test, and everything it got wrong was a thing a test would have said out loud.</b>
    /// Watching a raid tells you whether it is interesting; it does not tell you that a hundred and forty
    /// route requests went out in a second, or that a villager is nine hundred metres from home, or which
    /// tick a unit of grain disappeared on. So the same three questions the defence asks get asked of it:
    /// <list type="bullet">
    /// <item><b>Does the ledger still balance?</b> Checked every tick. Killing a loaded raider drops its
    /// cargo on the ground, and a raider that gets away takes it out of the world — two paths that both
    /// move units between terms of the identity, which is exactly where conservation breaks quietly.</item>
    /// <item><b>Does anybody come home?</b> The distance of the furthest settler from the granary is the
    /// pursuit metric. A defence that chases a raider to the map edge is not a defence, and it shows up
    /// here as a number in the hundreds rather than as a thing you have to notice on screen.</item>
    /// <item><b>Does the raid end?</b> A raider alive far longer than the walk in and out again means
    /// something scripted has stopped walking, which is the failure that had fifty-six of them standing
    /// about at once.</item>
    /// </list>
    /// </remarks>
    public static int RunRaids(
        float extentMeters,
        float minutes,
        float secondsBetween,
        float raiderHealth = 0f,
        uint seed = 0x1B873593u,
        bool peers = false)
    {
        var world = Build(extentMeters, out var granary);
        var settings = new RaidSettings
        {
            // Explicit, because the shipped default is off — see RaidSettings.Enabled. This scenario exists
            // to raid, so it says so rather than relying on a default that has already changed once.
            Enabled = true,
            SecondsBetween = secondsBetween,
            CameraJumps = false,
            RaiderHealth = raiderHealth,
            PeerRaiders = peers,
        };
        var raids = new RaidDirector(settings, seed);
        var ledger = new RaidLedger(new Simulation.Collision.FactionId(0));
        var totalTicks = (int)(minutes * 60f * TicksPerSecond);
        var faults = new List<string>();
        var centre = world.Nodes.Get(granary).Position;

        Console.WriteLine(
            $"RTSGame raids — {world.ExtentMeters:F0} m, {world.Agents.LiveCount} people, " +
            $"a raid every {secondsBetween:F0} s of {settings.Party}, {minutes:F1} minute(s), " +
            $"raiders at {(raiderHealth > 0f ? raiderHealth : (peers ? UnitType.Villager : UnitType.Raider).Health):F0} " +
            $"health{(peers ? ", and villagers in every other respect too" : string.Empty)}");
        Console.WriteLine(
            "     min | people | grain | raiders | stood | fled | spare | stow | shut out | killed | " +
            "lost | stolen | piles | furthest | routes | ms/tick");

        var settlers = world.Agents.LiveCount;
        var furthestEver = 0f;
        var stoodEver = 0;
        var fledEver = 0;
        var spareEver = 0;
        var stowEver = 0;
        var crowdedEver = 0;
        var longestRaider = 0f;
        var raiderAge = new Dictionary<int, float>();
        var reported = 0;
        for (var tick = 1; tick <= totalTicks; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            raids.Update(world, (float)SimulationWorld.FixedDeltaSeconds);
            ledger.Observe(world, (float)SimulationWorld.FixedDeltaSeconds);

            var drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (drift.Grain != 0 || drift.Wood != 0)
            {
                faults.Add(
                    $"conservation broke on tick {tick} ({world.Date}): " +
                    $"{drift.Grain:+#;-#;0} grain, {drift.Wood:+#;-#;0} wood unaccounted for");
                break;
            }

            // How far the furthest settler is from its granary. This is the pursuit metric, and it is the
            // one that caught defenders following a raider off the edge of the settlement entirely.
            var furthest = 0f;
            var alive = 0;
            foreach (ref readonly var agent in world.Agents.All)
            {
                if (!agent.IsAlive) continue;
                if (agent.Faction.Value != 0)
                {
                    var age = raiderAge.TryGetValue(agent.Id.Value, out var had) ? had : 0f;
                    age += (float)SimulationWorld.FixedDeltaSeconds;
                    raiderAge[agent.Id.Value] = age;
                    longestRaider = MathF.Max(longestRaider, age);
                    continue;
                }

                alive++;
                furthest = MathF.Max(furthest, Vector2.Distance(agent.Position, centre));
            }

            furthestEver = MathF.Max(furthestEver, furthest);
            stoodEver = Math.Max(stoodEver, world.Threat.Standing);
            fledEver = Math.Max(fledEver, world.Threat.Fleeing);
            spareEver = Math.Max(spareEver, world.Threat.Surplus);
            stowEver = Math.Max(stowEver, world.PuttingDownCount);
            crowdedEver = Math.Max(crowdedEver, world.Threat.Crowded);

            var minute = tick / (60 * TicksPerSecond);
            if (minute == reported) continue;
            reported = minute;
            var piles = 0;
            foreach (ref readonly var node in world.Nodes.All)
            {
                if (node.IsAlive && node.IsPile) piles++;
            }

            Console.WriteLine(
                $"  {minute,6} | {alive,6} | {world.Nodes.Get(granary).Stock.Grain,5} | " +
                $"{raids.Alive,7} | {stoodEver,5} | {fledEver,4} | {spareEver,5} | {stowEver,4} | " +
                $"{crowdedEver,8} | {world.Threat.Killed,6} | " +
                $"{settlers - alive,4} | {raids.Stolen,6} | {piles,5} | {furthest,7:F0} m | " +
                $"{world.RoutePlansThisTick,6} | " +
                $"{world.Timings.Format(world.Agents.Count, world.TickNumber).Split("total ")[1].Split(" ms")[0],7}");
        }

        // The whole transaction, on both sides. It was "bodies killed" against "settlers lost", with the
        // difference to be done in your head — which is how twenty-four raiders came and went over eight
        // raids with not one killed and nobody noticing for two sessions. See RaidLedger.
        foreach (var line in ledger.Lines(raids.Raids, raids.Sent, raids.Escaped, raids.Stolen))
        {
            Console.WriteLine(line);
        }
        Console.WriteLine(
            $"  most standing at once {stoodEver}, most fleeing {fledEver}, " +
            $"most who left it to somebody closer {spareEver}, " +
            $"most putting a load down first {stowEver}, " +
            $"most shut out of a fight they had reached {crowdedEver}, " +
            $"furthest a settler went {furthestEver:F0} m, longest a raider lived {longestRaider:F0} s");

        // A settler two hundred metres from its granary is not defending anything. The settlement is
        // 36 m across and the raid arrives at 120 m, so anything past the arrival ring is a pursuit that
        // should have ended when the thing being protected stopped being in danger.
        if (furthestEver > settings.ArrivesAt)
        {
            faults.Add(
                $"a settler went {furthestEver:F0} m from home, past the {settings.ArrivesAt:F0} m a raid " +
                "even arrives at — defenders are chasing rather than defending");
        }

        // Walk in, loot, walk out, with a wide allowance for a crowded lane.
        var round = 3f * settings.ArrivesAt / UnitType.Raider.MaximumSpeed + settings.LootSeconds;
        if (longestRaider > round)
        {
            faults.Add(
                $"a raider lived {longestRaider:F0} s against a round trip of {round:F0} s — " +
                "something scripted to walk has stopped walking");
        }

        // Settlers, not bodies: LiveCount includes the raiders, so a settlement wiped out while nineteen
        // raiders stand about in it reads as a healthy population. That is how this check missed a wipe.
        var left = CountSettlers(world);
        if (left == 0) faults.Add($"the settlement was wiped out — {settlers} people, none left");

        foreach (var fault in faults) Console.WriteLine($"  FAULT: {fault}");
        return faults.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// Whether the village it lays down is a village anybody can actually work in.
    /// </summary>
    /// <remarks>
    /// <b>Two reports that are cheaper to measure than to watch for.</b> "One of our villagers pops in
    /// blocked by a tree every single run" is a spawn-placement claim, and "trees don't actually fell" is a
    /// claim about the wood economy — both of them are numbers, and both were being judged by eye against a
    /// scene where thirty-one trees come down a year out of nine and a half thousand.
    /// <para>
    /// So this reports the state of the village at tick zero and again after a few minutes: who is standing
    /// somewhere they cannot walk, who is overlapping something solid, how many cutters have a tree they can
    /// reach, and how many trees actually came down.
    /// </para>
    /// </remarks>
    public static int RunPlacementCheck(float extentMeters, float minutes)
    {
        var world = Build(extentMeters, out _);
        Console.WriteLine(
            $"RTSGame placement check — {world.ExtentMeters:F0} m, {world.Agents.LiveCount} people, " +
            $"{world.Nodes.LiveCount} nodes");

        var faults = new List<string>();
        Report(world, "at tick 0", faults);

        var treesBefore = StandingTrees(world);
        var ticks = (int)(minutes * 60f * TicksPerSecond);
        for (var tick = 1; tick <= ticks; tick++) world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        Report(world, $"after {minutes:F0} min", faults);

        var treesAfter = StandingTrees(world);
        var wood = 0;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (node.IsAlive && node.Stores) wood += node.Stock.Wood;
        }

        Console.WriteLine(
            $"  trees {treesBefore} -> {treesAfter} ({treesBefore - treesAfter} felled), " +
            $"{wood} wood in stores");
        if (treesBefore - treesAfter == 0)
        {
            faults.Add($"no tree came down in {minutes:F0} minutes of {Woodcutters} cutters working");
        }

        foreach (var fault in faults) Console.WriteLine($"  FAULT: {fault}");
        return faults.Count > 0 ? 1 : 0;
    }

    private static int StandingTrees(SimulationWorld world)
    {
        var trees = 0;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (node.IsAlive && node.IsStanding) trees++;
        }

        return trees;
    }

    private static void Report(SimulationWorld world, string when, List<string> faults)
    {
        var stuck = 0;
        var embedded = 0;
        var cutters = 0;
        var cuttersWithATree = 0;
        foreach (ref readonly var agent in world.Agents.All)
        {
            if (!agent.IsAlive || agent.Sheltered) continue;
            // Standing on ground the router says nobody may occupy.
            if (world.Placement.Transform.TryWorldToCell(agent.Position, out _) &&
                world.Navigation.Transform.TryWorldToCell(agent.Position, out var navCell) &&
                world.Navigation.Contains(navCell) &&
                !world.Navigation.IsWalkable(navCell, agent.NavigationRadius))
            {
                stuck++;
                // Named, not counted. "One body is stuck" is where this started and it is not enough to
                // find it with: which body, what it was told to do, what is on top of it, and how far the
                // nearest ground it could legally stand on is.
                var nearestTree = float.PositiveInfinity;
                foreach (ref readonly var node in world.Nodes.All)
                {
                    if (!node.IsAlive || !node.IsStanding) continue;
                    nearestTree = MathF.Min(
                        nearestTree, Vector2.Distance(agent.Position, node.Position));
                }

                var out_ = float.PositiveInfinity;
                for (var ring = 1; ring <= 40 && float.IsPositiveInfinity(out_); ring++)
                {
                    for (var step = 0; step < ring * 8; step++)
                    {
                        var angle = step / (float)(ring * 8) * MathF.Tau;
                        var probe = agent.Position +
                                    new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (ring * 0.5f);
                        if (!world.Navigation.Transform.TryWorldToCell(probe, out var cell)) continue;
                        if (!world.Navigation.Contains(cell)) continue;
                        if (!world.Navigation.IsWalkable(cell, agent.NavigationRadius)) continue;
                        out_ = ring * 0.5f;
                        break;
                    }
                }

                Console.WriteLine(
                    $"    stuck: body {agent.Id.Value} at ({agent.Position.X:F1}, {agent.Position.Y:F1}), " +
                    $"radius {agent.NavigationRadius:F2}, doing {agent.Jobs.Assignment.Kind}, " +
                    $"surface {world.Terrain.Surface(navCell)}, nearest trunk {nearestTree:F2} m, " +
                    $"nearest legal ground {(float.IsPositiveInfinity(out_) ? "none within 20 m" : $"{out_:F1} m")}");
            }

            // Overlapping something solid, which is a different failure: the raster may say the cell is
            // fine while a trunk or a wall is physically on top of the body.
            foreach (ref readonly var node in world.Nodes.All)
            {
                if (!node.IsAlive || !NodeFootprint.Blocks(node.Kind)) continue;
                var gap = node.FootprintRadius + agent.Radius;
                if (Vector2.DistanceSquared(agent.Position, node.Position) < gap * gap * 0.9f)
                {
                    embedded++;
                    break;
                }
            }

            if (agent.Jobs.Assignment.Kind != AssignmentKind.Work) continue;
            cutters++;
            if (world.CanReachTree(agent.Position)) cuttersWithATree++;
        }

        Console.WriteLine(
            $"  {when}: {stuck} standing on unwalkable ground, {embedded} overlapping something solid, " +
            $"{cuttersWithATree}/{cutters} workers with a tree in reach");
        if (stuck > 0) faults.Add($"{when}: {stuck} bodies on ground the router forbids");
        if (embedded > 0) faults.Add($"{when}: {embedded} bodies inside something solid");
    }

    private static int CountSettlers(SimulationWorld world)
    {
        var alive = 0;
        foreach (ref readonly var agent in world.Agents.All)
        {
            if (agent.IsAlive && agent.Faction.Value == 0) alive++;
        }

        return alive;
    }

    /// <summary>
    /// What forest cover costs: painting it, rasterising it, and re-opening it when a tree comes down.
    /// </summary>
    /// <remarks>
    /// The number that decides whether an impassable forest is affordable at all. Blocking the interior of
    /// every stand adds tens of thousands of impassable cells to a 1200x1200 raster, and the routing
    /// hierarchy has to decompose the free space around them — so the question is not whether the paint is
    /// cheap (it is) but what the <em>rebuild</em> costs, because a felled tree that re-opens ground pays it
    /// again. A settlement fells about twenty trees a year.
    /// </remarks>
    public static int RunForestCost(float extentMeters)
    {
        Console.WriteLine($"RTSGame forest cover cost — {extentMeters:F0} m map");
        SweepCover(extentMeters);
        var world = new SimulationWorld(extentMeters);
        var granary = Populate(world, Farms, Woodcutters, Carts, Wagons, ringRadius: 36f);

        var transform = world.Terrain.Transform;
        var cells = transform.Width * transform.Height;
        var forest = 0;
        for (var z = 0; z < transform.Height; z++)
        for (var x = 0; x < transform.Width; x++)
        {
            if (world.Terrain.Surface(new GridCell(x, z)) == TerrainSurface.Forest) forest++;
        }

        var (standing, trees) = world.Nodes.StandingTimber();
        Console.WriteLine(
            $"  {trees:N0} trees closed {forest:N0} of {cells:N0} navigation cells " +
            $"({forest / (float)cells * 100f:F1}% of the map, {forest * 0.25f:N0} m2)");
        Console.WriteLine($"  {ApproachReport(world, granary)}");
        Console.WriteLine($"  {BuildingReport(world)}");

        // The raster on its own, so the first tick's cost is attributed rather than guessed at.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        world.RebuildTerrainNavigation();
        var raster = clock.Elapsed.TotalMilliseconds;

        clock.Restart();
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var firstTick = clock.Elapsed.TotalMilliseconds;

        clock.Restart();
        for (var i = 0; i < 60; i++) world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var steady = clock.Elapsed.TotalMilliseconds / 60.0;

        // Repainting the whole map, which is what a scenario does once at setup.
        clock.Restart();
        world.RefreshForestCover();
        var repaint = clock.Elapsed.TotalMilliseconds;

        // And the cost a felling actually pays: re-decide the ground around one tree, then the rebuild the
        // next tick does if anything changed.
        var fringe = NodeId.None;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (node.IsAlive && node.IsStanding) fringe = node.Id;
        }

        var where = world.Nodes.Get(fringe).Position;
        clock.Restart();
        world.ReleaseForestCover(where);
        var release = clock.Elapsed.TotalMilliseconds;

        clock.Restart();
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var afterFelling = clock.Elapsed.TotalMilliseconds;

        // And the rebuild a change definitely triggers, because the tree above may have opened nothing —
        // a fringe tree whose neighbours still hold the ground closed changes no cell, and timing that
        // proves only that nothing happened.
        transform.TryWorldToCell(where, out var poke);
        world.Terrain.SetSurface(poke, TerrainSurface.Grass);
        clock.Restart();
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var forcedRebuild = clock.Elapsed.TotalMilliseconds;

        Console.WriteLine($"  rasterise on its own:           {raster:F0} ms");
        Console.WriteLine($"  first tick (raster + the rest):  {firstTick:F0} ms");
        Console.WriteLine($"  steady tick:                    {steady:F2} ms");
        Console.WriteLine($"  repaint the whole map:          {repaint:F0} ms");
        Console.WriteLine($"  re-open around one tree:        {release:F2} ms");
        Console.WriteLine($"  the tick that follows a felling:{afterFelling,7:F0} ms");
        Console.WriteLine($"  a tick with a forced rebuild:   {forcedRebuild:F0} ms");
        Console.WriteLine(
            "  a felling only costs the rebuild when it actually opens ground; a settlement fells " +
            "about twenty trees a year");
        return 0;
    }

    /// <summary>What buildings the settlement actually has, and where, since that is easy to doubt.</summary>
    private static string BuildingReport(SimulationWorld world)
    {
        var counts = new Dictionary<NodeKind, int>();
        var housing = 0;
        var nearest = float.PositiveInfinity;
        var furthest = 0f;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || node.IsStanding || node.IsPile) continue;
            counts[node.Kind] = counts.GetValueOrDefault(node.Kind) + 1;
            if (node.Kind != NodeKind.House) continue;
            housing += node.Occupancy;
            var distance = node.Position.Length();
            nearest = MathF.Min(nearest, distance);
            furthest = MathF.Max(furthest, distance);
        }

        var parts = counts.OrderBy(entry => entry.Key.ToString())
            .Select(entry => $"{entry.Value} {entry.Key.ToString().ToLowerInvariant()}");
        return $"buildings: {string.Join(", ", parts)} — housing for {housing}, " +
               $"houses {nearest:F0}–{furthest:F0} m from the granary";
    }

    /// <summary>
    /// Whether anything can actually get from the edge of the map to the granary.
    /// </summary>
    /// <remarks>
    /// <b>The check the forest most needs and the one it is easiest to forget.</b> An impassable ring around
    /// a settlement reads as a defensible position and is in fact the end of the game: no raid can reach the
    /// granary, so the whole of Stage E becomes untestable, and nothing about the economy would ever notice.
    /// Priced in route seconds from eight bearings, because "is there a path" and "is there a path a raid
    /// would take" are the same question asked with and without a number.
    /// </remarks>
    private static string ApproachReport(SimulationWorld world, NodeId granary)
    {
        var target = world.Nodes.Get(granary).Position;
        var edge = world.ExtentMeters * 0.48f;
        var open = 0;
        var quickest = float.PositiveInfinity;
        for (var bearing = 0; bearing < 8; bearing++)
        {
            var angle = bearing / 8f * MathF.Tau;
            var from = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * edge;
            if (!world.TryTravelSeconds(from, target, AgentDefaults.RoutingRadius, out var seconds))
            {
                continue;
            }

            open++;
            quickest = MathF.Min(quickest, seconds);
        }

        return open == 0
            ? "APPROACHES: none — the wood has sealed the settlement in and no raid can reach it"
            : $"approaches: {open} of 8 bearings reach the granary, quickest {quickest:F0} route-seconds " +
              $"({Woodland.Rides} rides {Woodland.RideWidth:F0} m wide)";
    }

    /// <summary>
    /// What each setting of the cover dials actually produces, so the default comes from a measurement.
    /// </summary>
    /// <remarks>
    /// Three numbers per setting and they are three different failure modes. <b>Closed</b> is how much of
    /// the map is wall, which is the thing being tuned. <b>In reach</b> is how many trees a cutter based at
    /// the granary can still get to — and if that hits zero the settlement cannot cut wood at all, because
    /// the near band it starts on has closed over its own stragglers. <b>Cuttable</b> is how many of the
    /// whole forest have open ground beside them, which is the supply the settlement can ever reach without
    /// building outward.
    /// </remarks>
    private static void SweepCover(float extentMeters)
    {
        var trees = Woodland.CoverTrees;
        var radius = Woodland.CoverRadius;
        Console.WriteLine("  trees  reach   closed   in reach   cuttable");
        foreach (var count in new[] { 2, 3, 4 })
        foreach (var reach in new[] { 2.2f, 2.6f, 3.0f, 3.4f })
        {
            Woodland.CoverTrees = count;
            Woodland.CoverRadius = reach;
            var probe = new SimulationWorld(extentMeters);
            var store = Populate(probe, Farms, Woodcutters, Carts, Wagons, ringRadius: 36f);
            var transform = probe.Terrain.Transform;
            var closed = 0;
            for (var z = 0; z < transform.Height; z++)
            for (var x = 0; x < transform.Width; x++)
            {
                if (probe.Terrain.Surface(new GridCell(x, z)) == TerrainSurface.Forest) closed++;
            }

            var from = probe.Nodes.Get(store).Position;
            var inReach = 0;
            var cuttable = 0;
            foreach (ref readonly var node in probe.Nodes.All)
            {
                if (!node.IsAlive || !node.IsStanding) continue;
                if (!probe.CanReachTree(node.Position)) continue;
                cuttable++;
                if (Vector2.Distance(node.Position, from) <= Woodland.ReachMetres) inReach++;
            }

            Console.WriteLine(
                $"  {count,5}  {reach,5:F1}   {closed / (float)(transform.Width * transform.Height) * 100f,5:F1}%" +
                $"   {inReach,8}   {cuttable,8}");
        }

        Woodland.CoverTrees = trees;
        Woodland.CoverRadius = radius;
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
    /// <summary>
    /// Builds the world the year is measured in, on flat ground or on generated terrain.
    /// </summary>
    /// <remarks>
    /// <b>§54's last milestone, and it only became worth doing now.</b> The plan has owed a migration of the
    /// calibrated scenarios off flat ground since relief existed; the reason to hold off was that the terrain
    /// kept changing underneath, and a year-long economy assertion recalibrated against ground that moves next
    /// week measures nothing. The generator has now stopped moving.
    /// <para>
    /// <b>Flat stays the default, deliberately.</b> Every economic constant in this file was measured on flat
    /// ground, so flat is the control — the run that says whether a change broke the economy. Relief is the
    /// second run, which says whether the economy survives the ground the game actually generates. Two
    /// measurements answering two questions, rather than one measurement answering neither.
    /// </para>
    /// </remarks>
    private static SimulationWorld Build(
        float extentMeters,
        out NodeId granary,
        float reliefAmplitudeMetres = 0f,
        Region region = Region.Downland,
        Archetype archetype = Archetype.SplitValley,
        uint mapSeed = 0x5EED1234u)
    {
        var world = new SimulationWorld(extentMeters);
        if (reliefAmplitudeMetres > 0f)
        {
            world.Terrain.SetRegion(region);
            var layout = MapLayout.Composed(archetype, extentMeters, mapSeed, reliefAmplitudeMetres);
            var plan = ReliefPlan.FromLayout(layout, extentMeters, mapSeed);
            plan.Apply(world.Terrain);
            PaintCountry(world);
            world.RebuildTerrainNavigation();
            Console.WriteLine($"  ground: {archetype} in {RegionProfile.For(region).Name}, seed {mapSeed}");
            Console.WriteLine($"    {layout.Sentence}");
        }

        granary = Populate(
            world,
            Farms,
            Woodcutters,
            Carts,
            Wagons,
            ringRadius: 36f,
            centre: ChooseSite(world, extentMeters));
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
        var granary = world.AddNode(NodeKind.Granary, centre, capacity: 9000);

        // There is one harvest a year, so a settlement founded in spring lives on its stores until the
        // fiftieth day of the harvest season — 3,000 of the year's 5,400 seconds, better than half of it.
        // At 270 grain a head that is about 3,900 for this population, and a founding cache has to cover
        // it or the settlement starves through a summer with twelve healthy fields standing in front of
        // it. Seeded rather than conjured: the ledger records it, so conservation still balances.
        world.SeedStock(granary, Resource.Grain, 4200);
        world.SeedStock(granary, Resource.Wood, 1000);

        // The village core: houses on an arc to one side of the granary, tucked as close to it as their
        // own walls allow. A household outside every catchment goes hungry however full the stores are,
        // so near is the safe direction, and the arc is sized to leave a cart's width between
        // neighbours — buildings are 4.5 m across and a ring that fitted them at 1.5 m puts them
        // shoulder to shoulder.
        var people = farms + woodcutters + carts + wagons;
        // Two households more than the people need, because population is capped by housing and a
        // settlement with no spare room does not grow at all. Which is correct and is also why a run with
        // exactly enough houses measured nothing: growth accrues in houses that have room, so a full
        // settlement reports a readiness figure and no births. You build a house before you need it.
        var households = (people + Occupancy - 1) / Occupancy + 2;
        var houseWidth = NodeFootprint.HalfExtentOf(NodeKind.House) * 2f;

        // <b>The houses have to have gaps between them or they are not houses.</b> They were spread over a
        // half-circle whose arc length was, measured, 52.8 m for 52.6 m of building — nine 4.5 m houses
        // shoulder to shoulder with two centimetres to spare. From above that reads as one continuous wall,
        // and the report of the day was "there just aren't any houses" about a settlement that had nine of
        // them with room for thirty-six.
        //
        // So the arc is sized from the buildings rather than the buildings crammed into the arc: each house
        // gets its own width plus two thirds of one as a gap, and the ring grows until they fit. And it
        // skips the sector the fields are in, which is what leaves two thirds of a turn to spread over
        // instead of a half.
        const float houseGapShare = 1.65f;
        var fieldSector = MathF.PI * 0.55f;
        var houseSweep = MathF.Tau - fieldSector;
        var houseArc = MathF.Max(
            NodeFootprint.HalfExtentOf(NodeKind.Granary) + houseWidth * 0.5f + 1.5f,
            households * houseWidth * houseGapShare / houseSweep);
        for (var i = 0; i < households; i++)
        {
            // Starting clear of the fields and sweeping the long way round, so the village wraps the
            // settlement on three sides and the fields have the fourth.
            var angle = fieldSector * 0.5f + (i + 0.5f) / households * houseSweep;
            world.AddNode(
                NodeKind.House,
                centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * houseArc,
                capacity: 0,
                occupancy: Occupancy);
        }

        var producers = new List<(NodeId Node, Vector2 At)>();

        // <b>The fields are one contiguous block abutting the granary</b>, marching away from the village
        // rather than ringed all round it. Three things settled that shape and none of them was taste.
        // The reaper carries its own crop in, so every metre of the walk is a metre not spent reaping —
        // measured, fields on a 36 m ring lost <em>half the crop</em> to commuting inside a harvest window
        // that only just holds the reaping, and packed against the store the same fields brought in 97%.
        // A field is not a wall any more (<c>NodeFootprint.Blocks</c>), so they can be laid edge to edge
        // with no gap and no maze: a patchwork, which is what fields are, and what a dozen 4.5 m plots
        // half a metre apart conspicuously was not. And putting them all on one side keeps the village
        // out of the middle of them, so the traffic between store and field never crosses the housing.
        var slot = NodeFootprint.HalfExtentOf(NodeKind.Farm) * 2f;
        var across = (int)MathF.Ceiling(MathF.Sqrt(farms));
        var firstColumn = NodeFootprint.HalfExtentOf(NodeKind.Granary) + slot * 0.5f;
        for (var i = 0; i < farms; i++)
        {
            var column = i / across;
            var row = i % across;
            var at = centre + new Vector2(
                firstColumn + column * slot,
                (row - (across - 1) * 0.5f) * slot);
            producers.Add((world.AddNode(NodeKind.Farm, at, YardCapacity, Resource.Grain), at));
        }

        // A settlement that starts partway through a year has already worked the windows that have passed.
        // Without this, any mid-year start lands on a harvest whose fields were never broken — twelve
        // failed crops and nothing to reap, which is correct arithmetic and a nonsense founding. It is a
        // fact about setting a scenario up, not a rule of the game: the ground was broken last spring by
        // people the simulation was not running yet.
        var phase = CropCycle.PhaseOf(world.Date.Season);
        foreach (var (node, _) in producers)
        {
            ref var field = ref world.Nodes.Get(node);
            if (field.Kind != NodeKind.Farm) continue;
            field.CycleYear = world.Date.Year;
            if (phase is CropPhase.Maintain or CropPhase.Reap) field.PrepareWork = CropCycle.PrepareLabour;
            if (phase is CropPhase.Reap) field.MaintainWork = CropCycle.MaintainLabour;
        }

        // The forest. Where it is, and how thin it has been cut, is the whole of the wood economy: there
        // is no woodcutter building any more, only trees and the people sent to them.
        PaintBiomes(world);
        ScatterWoodland(world, centre, ringRadius);

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
            // Mustered a body's width off the wall rather than off the circle round the building, which
            // put everybody 2.2 m out on the first frame and read as a settlement standing back from its
            // own work before it had even started.
            var hand = world.SpawnAgent(
                placed + outward * (world.Nodes.Get(node).HalfExtent + UnitType.Villager.Radius + 0.9f),
                UnitType.Villager);
            ref readonly var site = ref world.Nodes.Get(node);
            world.QueueAssign(
                new[] { hand },
                site.Kind == NodeKind.Farm
                    // A field is worked, not stood at: prepared in spring, kept in summer, reaped in
                    // harvest, and the crop carried in by whoever reaped it.
                    ? Assignment.Work(
                        node, placed, extent, Resource.Grain,
                        EconomySystem.WorkShiftSeconds, EconomySystem.HandoverSeconds)
                    : Assignment.Hold(placed, Stagger(20f, i, producers.Count), extent));
        }

        // Cutters. Posted at a tree rather than at a building, because there is no longer a building to
        // post them at: the settlement's wood comes out of the nearest trees to a store, and when those
        // are gone the cutters go looking for a store that can still reach one. Each is dropped on a
        // different tree so they do not all fell the same trunk.
        var claimed = new HashSet<int>();
        for (var i = 0; i < woodcutters; i++)
        {
            var tree = NextUnclaimedTree(world, granary, claimed);
            if (!tree.IsValid) break;
            ref readonly var trunk = ref world.Nodes.Get(tree);
            var at = trunk.Position;
            var extent = trunk.FootprintRadius;
            var hand = world.SpawnAgent(
                at + new Vector2(0f, extent + UnitType.Villager.Radius + 0.6f), UnitType.Villager);
            world.QueueAssign(
                new[] { hand },
                Assignment.Work(
                    tree, at, extent, Resource.Wood,
                    Woodland.LoadSeconds(UnitType.Villager.CarryCapacity),
                    EconomySystem.HandoverSeconds));
        }

        // <b>Spare hands, not carts.</b> There is no hauler unit any more: hauling is a job a villager
        // takes, which costs the settlement a sack of timber and gives them a handcart for as long as they
        // keep it. So a settlement that needs no hauling has no carts in it — not seven idle ones — and
        // the "zero hauling journeys in a year" figure now means something stronger than it did: nobody
        // even had to build a cart.
        //
        // What these are instead is the labour a growing settlement has spare, which is what Stage C is
        // about and what a receding wood line produces on its own.
        for (var i = 0; i < carts + wagons; i++)
        {
            var angle = i / (float)(carts + wagons) * MathF.Tau;
            world.SpawnAgent(
                centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 7f, UnitType.Villager);
        }

        return granary;
    }

    /// <summary>The nearest tree to the store that no cutter has been sent to yet.</summary>
    private static NodeId NextUnclaimedTree(SimulationWorld world, NodeId store, HashSet<int> claimed)
    {
        var from = world.Nodes.Get(store).Position;
        var best = NodeId.None;
        var bestDistance = Woodland.ReachMetres * Woodland.ReachMetres;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || !node.IsStanding || claimed.Contains(node.Id.Value)) continue;
            // Reachable, which since the interior of a wood is impassable is a real question: the nearest
            // tree to the granary is often one the near band closed over, and a cutter posted on it stands
            // beside it forever.
            if (!world.CanReachTree(node.Position)) continue;
            var distance = Vector2.DistanceSquared(node.Position, from);
            if (distance > bestDistance) continue;
            bestDistance = distance;
            best = node.Id;
        }

        if (best.IsValid) claimed.Add(best.Value);
        return best;
    }

    /// <summary>
    /// Trees, thinned near the settlement and continuous further out.
    /// </summary>
    /// <remarks>
    /// <b>The gradient is the point, and it is not decoration — it is the record of past logging.</b>
    /// Close in, where people have been cutting for years, there are stragglers: single trees with gaps
    /// between them. Further out there are canopies, clumps that have been nibbled at. Past that it is
    /// unbroken woodland nobody has reached yet. So the map already tells the player which direction the
    /// wood ran out in, before the simulation has run a tick, and "distance is the terrain" is a fact
    /// about the ground rather than a comment in a seed function.
    /// <para>
    /// It is also what makes the stage's two halves both true of one map. The stragglers inside
    /// <see cref="Woodland.ReachMetres"/> of the granary are a compact settlement's whole wood supply and
    /// they last about two years, so a year-long run needs no cart. Fell them and the nearest tree is
    /// forty metres out, no store can reach it, and the only answer is a depot at the tree line — at
    /// which point the wood is piling up somewhere nobody lives and the carts have work.
    /// </para>
    /// <para>
    /// Deterministic, from a counter rather than a clock: two runs of this world must be the same world,
    /// which the fingerprint checks and the save relies on. There is no <c>Random</c> anywhere in the
    /// simulation and this is not the place to introduce one.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Paints the country onto the map: moor high up, scree on the steep, marsh in the wet bottoms.
    /// </summary>
    /// <remarks>
    /// <b>Generation rather than dressing, because a surface is what path cost derives from.</b> Moor is a
    /// tenth slower than pasture, scree a fifth, marsh nearly half — so a route round a moor can be worth
    /// taking, a catchment reaching into one is smaller, and a marsh in the wrong place is a real cost to a
    /// settlement. That is the difference between geography and paint, and it is why this is fingerprinted
    /// and saved rather than derived per frame.
    /// <para>
    /// Painted before the woodland, so forest cover is written over the top of it — a wood on a moor is
    /// still a wood, and the cover's own surface value is what closes the ground inside a stand.
    /// </para>
    /// <para>
    /// On a map with no relief the classifier answers Meadow everywhere, so nothing is painted and every
    /// calibrated scenario keeps the ground it was measured on.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The map without its frontier, which is what "how high is this" has to be measured against.
    /// </summary>
    /// <remarks>
    /// <b>The rim would otherwise eat every threshold on the map.</b> Height enters the classifier
    /// normalised — <c>(here - floor) / span</c> — and the frontier stands at one and a half times the
    /// interior's own relief. Measured across the whole map the span more than doubles, every interior hill
    /// lands below a third of it, and moor disappears from a map that visibly has moors on it. The frontier
    /// is not the country being classified; it is the edge of it.
    /// </remarks>
    private static float InteriorExtent(float extentMetres) =>
        MathF.Max(extentMetres * 0.25f, extentMetres - 2f * ReliefPlan.RimWidthMetres);

    /// <summary>The interior's lowest ground and how much relief it has, which every biome question needs.</summary>
    /// <remarks>
    /// One measurement with three callers rather than three copies of a sampling loop — §58 recorded the
    /// duplication as something that would drift, and the third caller arriving is when to fix it.
    /// </remarks>
    /// <summary>The interior's floor and span, for callers outside the scenario. See InteriorRelief.</summary>
    public static (float Floor, float Span) InteriorReliefOf(SimulationWorld world) => InteriorRelief(world);

    private static (float Floor, float Span) InteriorRelief(SimulationWorld world)
    {
        var span = ReliefSpan(world);
        var floor = float.MaxValue;
        var reach = InteriorExtent(world.ExtentMeters);
        for (var z = 0; z <= 100; z++)
        for (var x = 0; x <= 100; x++)
        {
            var at = new Vector2(x / 100f - 0.5f, z / 100f - 0.5f) * reach;
            floor = MathF.Min(floor, world.Terrain.SampleHeight(at));
        }

        return (floor, span);
    }

    /// <summary>
    /// How much woodland a kind of country carries, as a multiple of what pasture carries.
    /// </summary>
    /// <remarks>
    /// <b>Trees standing in the middle of a floodplain was the thing that made this necessary.</b> Woodland
    /// density had been a function of slope and height, which is a good rule for a human reason — a slope is
    /// hard to plough, so forest survives on it — and it is blind to everything else about the ground. A
    /// floodplain is level, so the slope rule made it prime forest, and a level silted river-flat is in fact
    /// the very first ground anybody clears and grazes.
    /// <para>
    /// Every number here is a reason rather than a taste. <b>Floodplain</b> is cleared, grazed and seasonally
    /// wet, so it keeps almost nothing — a fringe of willows is what is left, which is what the species
    /// mapping gives it. <b>Marsh</b> drowns roots. <b>Moor</b> is exposed and thin-soiled, so what grows is
    /// stunted and scattered rather than absent. <b>Scree</b> has little to root in. And water and crag are
    /// zero because they are not soil at all.
    /// </para>
    /// <para>
    /// Meadow stays exactly one, which is the migration guarantee: a map with no relief classifies as all
    /// meadow, so nothing here can move a flat map's tree count. §22's economic constants are safe by
    /// construction rather than by being remembered.
    /// </para>
    /// </remarks>
    private static float WoodlandFor(Biome biome) => biome switch
    {
        Biome.Water => 0f,
        Biome.Crag => 0f,
        Biome.Floodplain => 0.14f,
        Biome.Marsh => 0.26f,
        Biome.Scree => 0.42f,
        Biome.Moor => 0.38f,
        _ => 1f,
    };

    /// <summary>Paints the country the ground implies. Public so the map lab can generate terrain alone.</summary>
    public static void PaintCountry(SimulationWorld world) => PaintBiomes(world);

    private static void PaintBiomes(SimulationWorld world)
    {
        var (floor, span) = InteriorRelief(world);
        if (span < 1f) return;

        var terrain = world.Terrain;
        var grid = terrain.Transform;

        // <b>Classified on a three-metre grid and stamped onto the navigation cells, which took this from
        // twenty seconds to under one.</b> The navigation grid is half a metre because that is the resolution
        // a body's clearance is decided at, and classifying at that resolution meant thirteen million calls to
        // a function that samples the drainage field, the grade and the height — to answer a question whose
        // answer changes over tens of metres. Nothing about a biome varies at half a metre.
        // <para>
        // Three metres is the honest cost, and it is smaller than what the water already costs: channel width
        // comes from the nearest four-metre lattice cell, so a shoreline was never finer than that. What the
        // stamp does add is a three-metre step in <em>path cost</em> at a boundary, which is well below the
        // scale any route is decided at.
        // </para>
        var stamp = MathF.Max(grid.CellSize, 3f);
        var step = Math.Max(1, (int)MathF.Round(stamp / grid.CellSize));
        var painted = 0;
        var tally = new int[Enum.GetValues<Biome>().Length];
        for (var z = 0; z < grid.Height; z += step)
        for (var x = 0; x < grid.Width; x += step)
        {
            var centre = grid.CellCenter(new GridCell(
                Math.Min(x + step / 2, grid.Width - 1),
                Math.Min(z + step / 2, grid.Height - 1)));
            var biome = Biomes.At(terrain, centre, floor, span);
            // The width is what decides whether water is a barrier or a wade, so it has to come from the
            // same sample the classification did rather than be looked up again somewhere else.
            var width = terrain.Drainage?.WidthAt(centre) ?? 0f;
            var depth = terrain.Drainage is { } flow
                ? flow.LevelAt(centre) - terrain.SampleHeight(centre)
                : 0f;
            var surface = Biomes.SurfaceOf(biome, width, depth);
            var toX = Math.Min(x + step, grid.Width);
            var toZ = Math.Min(z + step, grid.Height);
            tally[(int)biome] += (toX - x) * (toZ - z);
            if (surface == TerrainSurface.Grass) continue;
            for (var sz = z; sz < toZ; sz++)
            for (var sx = x; sx < toX; sx++)
            {
                terrain.SetSurface(new GridCell(sx, sz), surface);
                painted++;
            }
        }

        var cellCount = (float)(grid.Width * grid.Height);
        var breakdown = string.Join(
            ", ",
            Enum.GetValues<Biome>()
                .Where(biome => tally[(int)biome] > 0)
                .Select(biome => $"{tally[(int)biome] / cellCount * 100f:F0}% {biome.ToString().ToLowerInvariant()}"));
        Console.WriteLine(
            $"  country: {breakdown} over a {span:F0} m height range " +
            $"({painted * 100f / cellCount:F0}% of the map is something other than pasture)");
    }

    private static void ScatterWoodland(SimulationWorld world, Vector2 centre, float ringRadius)
    {
        var seed = 0x9E3779B9u;

        float Next()
        {
            // splitmix32: one multiply-xor-shift chain, deterministic, and enough for a scatter.
            seed += 0x9E3779B9u;
            var z = seed;
            z = (z ^ (z >> 16)) * 0x21F0AAADu;
            z = (z ^ (z >> 15)) * 0x735A2D97u;
            z ^= z >> 15;
            return (z & 0xFFFFFFu) / (float)0x1000000u;
        }

        // A coarse hash grid over the candidate positions, so the spacing test looks at a handful of
        // neighbours instead of every tree placed so far. Ten thousand trees against a linear scan is
        // fifty million distance tests and several seconds of startup; against this it is a few hundred
        // thousand. The cell is sized to the widest spacing any band asks for, so a tree's neighbours are
        // always in its own cell or one adjacent.
        const float cellSize = 4f;
        var buckets = new Dictionary<(int, int), List<Vector2>>();

        bool TooClose(Vector2 at, float spacing)
        {
            var cx = (int)MathF.Floor(at.X / cellSize);
            var cz = (int)MathF.Floor(at.Y / cellSize);
            for (var dz = -1; dz <= 1; dz++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!buckets.TryGetValue((cx + dx, cz + dz), out var bucket)) continue;
                foreach (var other in bucket)
                {
                    if (Vector2.DistanceSquared(other, at) < spacing * spacing) return true;
                }
            }

            return false;
        }

        bool TryPlant(Vector2 at, float spacing)
        {
            // Never in the fields or under a building. A tree standing in a wheat field is not a
            // collision — trees do not block — it is a lie about what that ground is being used for.
            if (MathF.Abs(at.X - centre.X) < FieldKeepOut && MathF.Abs(at.Y - centre.Y) < FieldKeepOut)
            {
                return false;
            }

            // And not in a ride. Four lanes out from the settlement, kept clear, because a wood that seals
            // the settlement in is a wood no raid can come out of — and the one thing this stage exists to
            // test could then never happen.
            if (Woodland.OnARide(at - centre)) return false;
            if (!CanRoot(at)) return false;
            if (TooClose(at, spacing)) return false;
            var node = world.AddNode(NodeKind.Tree, at, capacity: (int)Woodland.WoodPerTree);
            world.SeedStock(node, Resource.Wood, (int)Woodland.WoodPerTree);
            var settled = world.Nodes.Get(node).Position;
            var key = ((int)MathF.Floor(settled.X / cellSize), (int)MathF.Floor(settled.Y / cellSize));
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<Vector2>();
            list.Add(settled);
            return true;
        }

        // <b>Which way the map is, from here.</b> The settlement sits off toward a corner, so there is a
        // direction with a country in it and a direction with a border in it — and that is enough to shape a
        // woodland without inventing anything: the deep forest goes where the land is, and the open side is
        // the one that runs out.
        var inland = centre.LengthSquared() > 1f
            ? MathF.Atan2(-centre.Y, -centre.X)
            : 0f;

        // How much woodland belongs on this bearing, from bare to solid.
        float Shaped(Vector2 offset)
        {
            if (offset.LengthSquared() < 1f) return 1f;
            var bearing = MathF.Atan2(offset.Y, offset.X);

            float Lobe(float towards, float halfWidth)
            {
                var delta = MathF.Abs(MathF.IEEERemainder(bearing - towards, MathF.Tau));
                return delta >= halfWidth
                    ? 0f
                    : MathF.Cos(delta / halfWidth * MathF.PI * 0.5f);
            }

            // Two deep masses either side of inland, an open run toward the corner, and a floor of
            // stragglers everywhere so no side is a bald patch with a straight edge.
            var forest = MathF.Max(Lobe(inland - 0.55f, 1.05f), Lobe(inland + 0.6f, 0.95f));
            var open = Lobe(inland + MathF.PI, 1.15f);
            return Math.Clamp(0.14f + forest - open * 0.55f, 0.02f, 1f);
        }

        // <b>Where the wood is, given what the ground is doing.</b> The bearing shaping above gives a map a
        // near side and a far side; this gives it a reason. Until now the woodland and the relief were two
        // systems generated into the same space with no knowledge of each other, which is why the map read
        // as two things stacked rather than as one place.
        //
        // Slope is what decides it, and the mechanism is human rather than botanical: a slope is hard to
        // plough, so forest survives on it and the flat ground gets cleared. That single rule is worth more
        // than any amount of density tuning, because of what it does to the economy — <b>wood ends up
        // uphill and farmland on the level</b>, so a site is a trade between the two rather than a place
        // with the same resources in every direction. Height adds a little on top: higher is cooler and
        // poorer, and keeps its trees.
        //
        // Multiplicative with the bearing, and never zero, so it thins and thickens rather than deciding.
        //
        // <b>And scaled by how much relief the map actually has, which is the migration guarantee applied
        // here.</b> The first version returned 0.42 on level ground, so it halved the woodland on <em>every</em>
        // map — 11,177 trees became 5,294 on the flat, which is §22's economic constant cut in half by a
        // decision about terrain on a map with no terrain in it. Written as a modulation around one and
        // faded out as the height range goes to nothing, flat ground comes out at exactly the density it
        // always had and nothing calibrated moves until somebody generates relief on purpose.
        var (reliefFloor, span) = InteriorRelief(world);
        var strength = Math.Clamp(span / 8f, 0f, 1f);
        float Relief(Vector2 at)
        {
            if (strength <= 0f) return 1f;
            var slope = Smoothstep(0.04f, 0.26f, world.Terrain.SampleGrade(at));
            var above = Math.Clamp(world.Terrain.SampleHeight(at) / MathF.Max(1f, span), 0f, 1f);
            // Around one: the level ground loses some, the slopes and the tops gain more, and the total is
            // roughly redistributed rather than reduced.
            var shaped = Math.Clamp(1f + strength * (0.85f * slope + 0.30f * above - 0.35f), 0.15f, 1.7f);
            // <b>And what kind of country it is, which slope alone cannot say.</b> Faded by the same
            // strength, so it too vanishes on a flat map. See WoodlandFor.
            // <b>Times the region's own density, which is what a region mostly is.</b> Boreal forest and dry
            // scrub differ by a factor of seven here, and that single number does more for telling two maps
            // apart than any amount of shape does — a wood is most of what a person sees.
            var country = WoodlandFor(Biomes.At(world.Terrain, at, reliefFloor, span))
                * RegionProfile.For(world.Terrain.Region).TreeDensity;
            return shaped * (1f - strength + strength * country);
        }

        // <b>Nothing grows in a river or on bare rock, and that is not a density preference.</b> Kept apart
        // from Relief because it has to apply to the near band as well, which is exempt from every shaping
        // rule for §22's reason — the near band is the year's starting fuel and thinning it because of the
        // terrain would cut an economic constant by a side effect. A tree standing in the water is not
        // thinning, it is a lie about what that ground is.
        bool CanRoot(Vector2 at)
        {
            if (strength <= 0f) return true;
            var biome = Biomes.At(world.Terrain, at, reliefFloor, span);
            return biome is not (Biome.Water or Biome.Crag);
        }

        void Band(float inner, float outer, int trees, float spacing, int clump, bool shape = true)
        {
            for (var i = 0; i < trees; i++)
            {
                // A clump is one draw for the centre and the rest scattered around it, which is what
                // makes a canopy read as a canopy rather than as evenly spread noise.
                var anchor = centre + Polar(Next(), inner, outer, Next());
                // Shaped by bearing, and rejected rather than moved: nudging a refused anchor somewhere
                // acceptable would pile the rejects along the edge of the open sector and draw a wall
                // exactly where the gap is supposed to be.
                // Shaped by bearing and by what the ground is doing, and rejected rather than moved: nudging
                // a refused anchor somewhere acceptable would pile the rejects along the edge of the open
                // sector and draw a wall exactly where the gap is supposed to be.
                //
                // The near band is exempt from both, for §22's reason: it is an economic constant rather
                // than scenery, and thinning it because the settlement happens to have been founded on the
                // flat would cut the year's starting fuel as a side effect of a decision about terrain.
                if (shape && Next() > Shaped(anchor - centre) * Relief(anchor)) continue;
                // Off the map is a refusal too. Clamping instead would stack every out-of-bounds tree onto
                // the border as a hedge, which is the artefact a corner-ish settlement invites.
                if (!world.Terrain.Contains(anchor)) continue;
                for (var k = 0; k < clump; k++)
                {
                    var at = clump == 1
                        ? anchor
                        : anchor + new Vector2(Next() * 2f - 1f, Next() * 2f - 1f) * spacing * 2.2f;
                    for (var attempt = 0; attempt < 6; attempt++)
                    {
                        if (TryPlant(world.Terrain.ClampPosition(at), spacing)) break;
                        at = anchor + new Vector2(Next() * 2f - 1f, Next() * 2f - 1f) * spacing * 2.6f;
                    }
                }
            }
        }

        // <b>The first band is load-bearing and the rest are scenery.</b> Everything the economy gate
        // measures depends on how much wood stands within a cutter's reach of the granary — about two
        // years of this settlement's burning, so a one-year run never runs out and a two-year one only
        // just does. Change 46 and the numbers in §22 change with it. Everything past reach is the map
        // the player expands into, and its density is free to be whatever reads best.
        // <b>Unshaped, and that is not an oversight.</b> §22: everything the economy gate measures depends
        // on how much wood stands within a cutter's reach, so this band is an economic constant rather than
        // scenery — thinning it by bearing would cut the settlement's starting fuel roughly in half as a
        // side effect of a decision about how the map looks. It is also true of settlements: you found the
        // place because there was wood round it.
        Band(FieldKeepOut + 3f, Woodland.ReachMetres, trees: 46, spacing: 3.4f, clump: 1, shape: false);
        // Canopies: clumps just beyond reach, which is where the tree line currently sits. Started clear
        // of the reach radius rather than at it, because a clump scatters its members several metres
        // around its anchor and the ones that landed inward pushed the in-reach count from 46 to 66 —
        // half a settlement's annual fuel, arriving as a side effect of a density change.
        Band(Woodland.ReachMetres + 8f, ringRadius * 1.5f, trees: 260, spacing: 2.2f, clump: 6);
        // Closing up: the transition from a thinned edge to woodland proper.
        Band(ringRadius * 1.5f, ringRadius * 3f, trees: 620, spacing: 1.9f, clump: 9);
        // Continuous forest, and the reason a settlement expands rather than starves. Out to a bit under
        // half the map, because a 600 m world whose outer half is bare plain does not read as a world with
        // a forest in it — it reads as a diorama with a hedge round it.
        // <b>Denser, by request, and the density is why a forest reads as one.</b> Spacing 2.2 to 1.6 is
        // roughly twice the trunks per hectare, and the anchor count is up because bearing shaping refuses
        // most of what it is offered — the same number of anchors over a third of the compass would have
        // thinned the forest rather than concentrated it.
        Band(ringRadius * 3f, ringRadius * 8f, trees: 2600, spacing: 1.6f, clump: 11);

        (SeededTimber, SeededTrees) = world.Nodes.StandingTimber();

        // And close the inside of every stand. Painted once, after the whole woodland is down, because the
        // navigation raster rebuilds from a changed terrain revision on the next tick — so one refresh over
        // ten thousand trees costs one rebuild, and doing it per tree would cost ten thousand.
        world.RefreshForestCover();
        // <b>And rasterise it now, before anybody is posted.</b> Painting only bumps the terrain revision;
        // the rebuild happens on the next tick, which is exactly right for the running game and wrong for a
        // scenario that is about to ask the raster questions. Without this, everything that follows —
        // choosing which trees a cutter can reach, nudging a spawn off unwalkable ground — consults a
        // raster from before the forest existed, gets told the whole map is open, and posts a woodcutter
        // inside a wood it cannot leave. Measured: one cutter, two hundred and thirty-six route requests,
        // no wood.
        world.RebuildTerrainNavigation();
    }

    /// <summary>
    /// How much height this map has in it, for turning an elevation into a share of the whole.
    /// </summary>
    /// <remarks>
    /// Sampled coarsely — a hundred and one points a side is ten thousand height reads against a scatter
    /// that is about to do a hundred thousand — and floored, so a flat map divides by one rather than by
    /// nothing and every position comes out at the bottom of the range.
    /// </remarks>
    private static float ReliefSpan(SimulationWorld world)
    {
        var terrain = world.Terrain;
        var lowest = float.MaxValue;
        var highest = float.MinValue;
        var extent = world.ExtentMeters;
        for (var z = 0; z <= 100; z++)
        for (var x = 0; x <= 100; x++)
        {
            var at = new Vector2(x / 100f - 0.5f, z / 100f - 0.5f) * extent * 0.98f;
            var height = terrain.SampleHeight(at);
            lowest = MathF.Min(lowest, height);
            highest = MathF.Max(highest, height);
        }

        return highest - lowest;
    }

    private static float Smoothstep(float from, float to, float at)
    {
        var t = Math.Clamp((at - from) / MathF.Max(0.0001f, to - from), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Where to found, given the land: level ground to build on, wood within reach, and something at its back.
    /// </summary>
    /// <remarks>
    /// <b>The terrain is generated first and the settlement is placed against it</b>, which is the right way
    /// round and was not the case until now: the site was a fixed corner of the map and the land was
    /// generated around it, so whether the settlement had any geography near it was luck. It is also why
    /// landform coverage had to be raised as a stopgap — with a chosen site, that stops being a dial.
    /// <para>
    /// Three things are scored, and they pull against each other, which is what makes this a decision rather
    /// than an arithmetic maximum:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Level ground</b>, and it is a veto rather than a preference. Fields and buildings need a
    /// bench; a settlement on a hillside is one that cannot lay a field.</item>
    /// <item><b>Slope nearby but not underfoot</b>, because since the woodland learned about relief that is
    /// where the wood is. This is the trade the whole coupling was for: flat to farm, slope to cut.</item>
    /// <item><b>Something at its back</b> — high ground within a couple of hundred metres, which is the
    /// crude form of §54's approach count. Honest about what it is: a proxy. The real measure asks the
    /// router which bearings can reach the site and costs nothing extra to compute, and this is the
    /// placeholder until that exists.</item>
    /// </list>
    /// <para>
    /// <b>On a map with no relief it returns the corner it always did</b>, to the metre. Every calibrated
    /// scenario founds where it founded, and §22's economic constant is measured against the same ground it
    /// was measured against — the same guarantee the woodland and the ground cover make, for the same reason.
    /// </para>
    /// </remarks>
    public static Vector2 ChooseSite(SimulationWorld world, float extentMeters)
    {
        var span = ReliefSpan(world);
        if (span < 1f) return CornerSite(extentMeters);

        var terrain = world.Terrain;
        var inset = FieldKeepOut + Woodland.ReachMetres + 40f;
        var limit = extentMeters * 0.5f - inset;
        var best = CornerSite(extentMeters);
        var bestScore = float.NegativeInfinity;
        const float step = 15f;
        for (var z = -limit; z <= limit; z += step)
        for (var x = -limit; x <= limit; x += step)
        {
            var at = new Vector2(x, z);
            var here = terrain.SampleHeight(at);

            // A bench to build on. Sampled over the ground the village and its fields actually occupy
            // rather than at a point, because a flat spot in a steep place is not a site.
            var core = terrain.SampleGrade(at);
            for (var i = 0; i < 4; i++)
            {
                var angle = i / 4f * MathF.Tau;
                core = MathF.Max(
                    core,
                    terrain.SampleGrade(at + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * FieldKeepOut));
            }

            if (core > 0.11f) continue;

            // Where the wood will be: slope within a cutter's reach, since that is where the woodland
            // survives the plough.
            var wood = 0f;
            var back = 0f;
            for (var i = 0; i < 12; i++)
            {
                var angle = i / 12f * MathF.Tau;
                var bearing = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                wood += terrain.SampleGrade(at + bearing * (Woodland.ReachMetres * 0.85f));
                // The tallest thing within a couple of hundred metres, relative to here.
                back = MathF.Max(back, terrain.SampleHeight(at + bearing * 170f) - here);
            }

            wood /= 12f;

            var score =
                1.35f * MathF.Min(1f, back / (span * 0.35f)) +
                1.10f * MathF.Min(1f, wood / 0.16f) -
                2.20f * (core / 0.11f);
            if (score <= bestScore) continue;
            bestScore = score;
            best = at;
        }

        Console.WriteLine(
            $"  founded at ({best.X:F0}, {best.Y:F0}): standing at {terrain.SampleHeight(best):F1} m on a " +
            $"grade of {terrain.SampleGrade(best):F3}, score {bestScore:F2} over a {span:F0} m height range");
        return best;
    }

    /// <summary>Half-width of the ground the fields and the village occupy, which stays clear.</summary>
    /// <summary>
    /// Where a settlement starts: off toward a corner, not in the middle of everything.
    /// </summary>
    /// <remarks>
    /// <b>A settlement in the exact centre of a square map has no geography.</b> Every direction is the
    /// same direction — the same distance to the edge, the same amount of forest, the same everything — so
    /// nothing about where you are can matter, and "which way do I expand" has no answer. Corner-ish gives
    /// the map a near side and a far side for free, and that is the cheapest geography there is.
    /// <para>
    /// A quarter of the extent out on both axes, which on a 600 m map is 150 m: far enough that the corner
    /// is close and the interior is open, near enough that a settlement is not pressed against the border
    /// with half its catchment off the map.
    /// </para>
    /// </remarks>
    public static Vector2 CornerSite(float extentMeters) =>
        new(-extentMeters * 0.25f, -extentMeters * 0.22f);

    private const float FieldKeepOut = 16f;

    /// <summary>What the woodland held when it was seeded, so felling can be reported against it.</summary>
    private static int SeededTimber;

    private static int SeededTrees;

    private static Vector2 Polar(float turn, float inner, float outer, float radial)
    {
        var angle = turn * MathF.Tau;
        // Square-rooted so trees are spread evenly over the annulus rather than crowded at its inside.
        var radius = MathF.Sqrt(inner * inner + radial * (outer * outer - inner * inner));
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
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

    /// <summary>
    /// How far each working body actually ends up from the wall it is working at.
    /// </summary>
    /// <remarks>
    /// Reported every season because it is the number that goes wrong silently. Three separate figures
    /// had to agree before it came right — the walk target, the arrival tolerance and the crowd fallback —
    /// and while they disagreed, every body in the settlement failed to arrive, waited out a retry, and
    /// settled short of its own work. Nothing failed; it just looked like hesitation. <b>"Settled short"
    /// being anything other than zero is the warning.</b>
    /// </remarks>
    private static void ReportGaps(SimulationWorld world)
    {
        var hands = new List<float>();
        var settled = 0;
        foreach (ref readonly var agent in world.Agents.All)
        {
            if (!agent.IsAlive || agent.Jobs.PlaceExtent <= 0f) continue;
            if (agent.Jobs.Activity == ActivityKind.None) continue;
            var half = new Vector2(agent.Jobs.PlaceHalfWidth);
            var nearest = Vector2.Clamp(
                agent.Position, agent.Jobs.Place - half, agent.Jobs.Place + half);
            hands.Add(Vector2.Distance(agent.Position, nearest));
            if (agent.Jobs.SettledNearby) settled++;
        }

        if (hands.Count == 0) return;
        hands.Sort();
        Console.WriteLine(
            $"      at the wall: median {hands[hands.Count / 2]:F2} m over {hands.Count} bodies, " +
            $"{settled} settled short of it");
    }

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
            // One failed walk is the crowd at the granary door — nineteen producers deliver to one
            // building and occasionally somebody is shouldered out of the spot it was aiming at, counts a
            // retry, and walks back. Three is not the crowd. The column still shows every body that has
            // fallen short so the number is visible; the fault is reserved for one that is not getting
            // there.
            if (agent.Jobs.Retries < 3) continue;
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

        ReportGaps(world);
        Console.WriteLine(
            $"  {world.Date,-18} | {grain.Stored,5:N0} | {wood.Stored,5:N0} | {People(world),-11} | " +
            $"{hands,5} | {Fields(world),-18} | {Forest(world),-14} | " +
            $"{world.Economy.HaulsAssigned,5:N0} | {carried.Total,8:N0} | " +
            $"{Seasons(grain.Seasons),10} | {Seasons(wood.Seasons),9} | " +
            $"{world.Economy.Unmet.Grain + world.Economy.Unmet.Wood,5:N0} | " +
            $"{world.UnhousedCount,8} | {stalled,7} | " +
            $"{world.Timings.Format(world.Agents.Count, world.TickNumber).Split("total ")[1].Split(" ms")[0]}");
    }

    /// <summary>
    /// Who lives here, how much room is left, and whether the settlement can afford another mouth.
    /// </summary>
    /// <remarks>
    /// Readiness is the number worth watching. It is the brake on growth and it is continuous, so a
    /// settlement at 40% is growing at 40% of its housing's pace — and the answer to a low figure is more
    /// farms rather than more houses, which is the thing a bare population count cannot tell you.
    /// </remarks>
    private static string People(SimulationWorld world)
    {
        var room = 0;
        var privation = 0;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || !node.IsSink) continue;
            room += node.Housing;
            if (node.Privation > 0.5f) privation++;
        }

        var readiness = world.Economy.Readiness;
        return $"{world.Agents.LiveCount,3} +{room,2} {readiness * 100f,3:F0}%" +
               (privation > 0 ? $" {privation}!" : string.Empty);
    }

    /// <summary>
    /// The wood line: how much timber is left, and whether any of it is still in reach of a store.
    /// </summary>
    /// <remarks>
    /// <b>Trees in reach is the number Stage B is about.</b> Standing timber falling is the settlement
    /// eating its forest, which is expected and is the whole clock of the game. Trees <em>in reach</em>
    /// falling to zero is the settlement having outgrown its arrangement, and it is the moment a forward
    /// depot at the tree line stops being optional — after which the wood piles up somewhere nobody lives
    /// and the carts have work for the first time.
    /// </remarks>
    private static string Forest(SimulationWorld world)
    {
        var (standing, trees) = world.Nodes.StandingTimber();
        var reachable = 0;
        foreach (ref readonly var store in world.Nodes.All)
        {
            if (!store.IsAlive || !store.Stores) continue;
            foreach (ref readonly var tree in world.Nodes.All)
            {
                if (!tree.IsAlive || !tree.IsStanding || tree.Stock.Wood <= 0) continue;
                if (Vector2.Distance(tree.Position, store.Position) <= Woodland.ReachMetres) reachable++;
            }
        }

        // Axes actually swinging, rather than people who call themselves woodcutters. A cutter whose
        // trees run out loses its assignment altogether — it becomes spare labour, which is correct —
        // so counting cutters would count nobody at exactly the moment the number mattered. This one
        // goes to zero the season the wood line passes out of reach, which is the signal.
        return $"{trees,3} tr {reachable,3} nr {EconomySystem.CuttersAtWork(world.Agents),2} cut";
    }

    /// <summary>
    /// What the fields say about themselves, which is the only place the crop cycle is visible.
    /// </summary>
    /// <remarks>
    /// A field's trouble is always in the past — a ceiling not set in spring cannot be diagnosed at
    /// harvest from anything a body is doing — so the field has to say so at the time. Reported as the
    /// worst thing any field is saying plus how many agree with it, because twelve identical strings tell
    /// you less than one string and a count.
    /// </remarks>
    private static string Fields(SimulationWorld world)
    {
        var says = new Dictionary<string, int>();
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || node.Kind != NodeKind.Farm) continue;
            var state = CropCycle.StateOf(in node, world.Date.Season);
            says[state] = says.GetValueOrDefault(state) + 1;
        }

        if (says.Count == 0) return "none";
        var worst = says.OrderBy(entry => Rank(entry.Key)).First();
        return says.Count == 1
            ? $"{worst.Value} {worst.Key}"
            : $"{worst.Value} {worst.Key} +{says.Count - 1} more";
    }

    /// <summary>Worst first: a failure outranks a warning outranks everything being fine.</summary>
    private static int Rank(string state) => state switch
    {
        "failed" => 0,
        "unbroken" => 1,
        "neglected" => 2,
        "standing" => 3,
        _ when state.StartsWith("preparing", StringComparison.Ordinal) => 4,
        _ when state.StartsWith("reaping", StringComparison.Ordinal) => 5,
        _ when state.StartsWith("tending", StringComparison.Ordinal) => 6,
        _ => 7,
    };

    /// <summary>§8's autonomy time, rendered as the one thing the HUD says: how long.</summary>
    private static string Seasons(float seasons) => float.IsPositiveInfinity(seasons)
        ? "growing"
        : $"{seasons:F1} seas";

    private static void Summarise(SimulationWorld world, float years, double mouthSeconds)
    {
        var economy = world.Economy;
        Console.WriteLine(
            $"  over {years:F2} year(s): produced {economy.Produced.Grain:N0} grain and " +
            $"{economy.Produced.Wood:N0} wood, ate {economy.Consumed.Grain:N0} and " +
            $"{economy.Consumed.Wood:N0}, went short {economy.Unmet.Grain:N0} and {economy.Unmet.Wood:N0}");
        var carts = 0;
        foreach (ref readonly var body in world.Agents.All)
        {
            if (body.IsAlive && body.HasCart) carts++;
        }

        Console.WriteLine(
            $"  hauling: {carts} carts built, {economy.HaulsAssigned:N0} board jobs given out, " +
            $"{economy.HaulsAbandoned:N0} dropped when the source emptied or the sink filled, " +
            $"{economy.RoutesFinished:N0} standing routes run dry");
        var (standing, trees) = world.Nodes.StandingTimber();
        // Against what the woodland started with, not against everything ever seeded: the granary's
        // founding stock is also seeded wood, and subtracting standing timber from the whole ledger
        // credited the settlement with felling a thousand units of granary.
        Console.WriteLine(
            $"  forest: {trees:N0} of {SeededTrees:N0} trees left, holding {standing:N0} of " +
            $"{SeededTimber:N0} wood — wood is never produced, only taken out of trees");

        Console.WriteLine(
            $"  population: {economy.Born:N0} born, {economy.Emigrated:N0} left because their household " +
            $"went hungry, {world.Agents.LiveCount:N0} alive; readiness to feed one more is " +
            $"{economy.Readiness * 100f:F0}%");
        Console.WriteLine(
            $"  construction: {economy.Raised:N0} buildings finished — a house is " +
            $"{Construction.TimberFor(NodeKind.House)} timber carried out and " +
            $"{Construction.LabourFor(NodeKind.House):F0} labour-seconds, a depot " +
            $"{Construction.TimberFor(NodeKind.ForwardDepot)} and " +
            $"{Construction.LabourFor(NodeKind.ForwardDepot):F0}");

        // What the year cost per person, against what the rates say it should have. A settlement that ate
        // less than its appetite went short somewhere, and the shortfall column says where. Measured in
        // mouth-years rather than against the final headcount, so growth and emigration do not distort it
        // — a settlement that halved otherwise appears to have eaten double its ration.
        var mouthYears = MathF.Max(1f, (float)(mouthSeconds / WorldCalendar.YearSeconds));
        Console.WriteLine(
            $"  per person per year, over {mouthYears:F1} mouth-years: " +
            $"{economy.Consumed.Grain / mouthYears:N0} grain against a nominal " +
            $"{EconomyRates.GrainPerVillagerPerYear:N0}, {economy.Consumed.Wood / mouthYears:N0} wood " +
            $"against {EconomyRates.WoodPerVillagerPerYear:N0}");
    }
}
