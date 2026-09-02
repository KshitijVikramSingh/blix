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
        var world = BuildVillage(extentMeters, out var granary, reliefAmplitudeMetres, region, archetype, mapSeed);
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
            // <b>Asked per resource, not resource by resource.</b> `drift.Grain != 0 || drift.Wood != 0`
            // was a check that named the two resources that existed, so it would have gone on passing while a
            // third drifted — and the fault message would have gone on reporting two columns of zeros.
            var drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero)
            {
                var missing = string.Join(
                    ", ",
                    Resources.All.Select(r => $"{drift[r]:+#;-#;0} {r.ToString().ToLowerInvariant()}"));
                faults.Add($"conservation broke on tick {tick} ({world.Date}): {missing} unaccounted for");
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
        var world = BuildVillage(extentMeters, out var granary);
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

            // Per resource, for the reason given on the same check in the settlement run above.
            var drift = world.Economy.Discrepancy(world.Nodes, world.Agents);
            if (!drift.IsZero)
            {
                var missing = string.Join(
                    ", ",
                    Resources.All.Select(r => $"{drift[r]:+#;-#;0} {r.ToString().ToLowerInvariant()}"));
                faults.Add($"conservation broke on tick {tick} ({world.Date}): {missing} unaccounted for");
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

        // Walk in, loot, walk out, with a wide allowance for a crowded lane — and now for the woods as well.
        //
        // <b>The allowance was calibrated against crowds and had to learn about barriers.</b> Its own comment
        // says "a crowded lane": three legs' worth of walking for a two-leg trip, which is fifty per cent of
        // slack for bodies getting in each other's way. That was ample while the woodland was rings with cleared
        // rides through them. It is not ample now: closed woodland on the flat map went from seven per cent to
        // sixteen when stands tightened, the rides are gone, and a raider walks <em>around</em> a wood rather
        // than down a lane cut for it. Measured, a raider lived 191 s against an allowance of 182 — a five per
        // cent overrun reported as "something scripted to walk has stopped walking", which was not what had
        // happened.
        //
        // Scaled by the barriers actually present rather than by a bigger constant, so the check keeps meaning
        // the thing it was written to mean on any map: a body that detours is walking, and a body that has
        // stopped is still caught. Path length through randomly-placed obstacles inflates roughly linearly in
        // their area share at these fractions, which is where the coefficient comes from — and on a map with no
        // woodland on it nothing changes at all.
        var closed = ClosedShare(world);
        var round = (3f * settings.ArrivesAt / UnitType.Raider.MaximumSpeed + settings.LootSeconds)
            * (1f + 1.4f * closed);
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
        var world = BuildVillage(extentMeters, out _);
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
        var granary = Populate(world, Farms, Woodcutters, Quarriers, Carts, Wagons);

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
            if (!node.IsAlive || node.IsNaturalDeposit || node.IsPile) continue;
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
            var store = Populate(probe, Farms, Woodcutters, Quarriers, Carts, Wagons);
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
    /// <summary>
    /// The village as the game builds it — terrain, layout, settlement, and every tree and outcrop on it.
    /// </summary>
    /// <remarks>
    /// Internal rather than private since §94, because the routing profile has to run against THIS world and
    /// not against a synthetic one. The difference is the whole finding: a sculpted test world decomposes into
    /// 562 rectangles and a real village into far more, and the flow field's cost follows the rectangles.
    /// </remarks>
    /// <summary>
    /// How many of each trade a village is founded with, and what time of day it starts.
    /// </summary>
    /// <remarks>
    /// <b>Because "shared, deliberately" was true of the method and false of its arguments.</b>
    /// <see cref="Populate"/> carries a comment saying the headless gate and the live game use one settlement
    /// definition so the two cannot drift — and they had drifted anyway, in the call: the gate founded twelve
    /// farms, seven cutters and seven carts at dawn, and the game founded eight, four and five at
    /// <c>StartAtSeconds(3100)</c>. Nineteen people against thirteen, at different hours.
    /// <para>
    /// That is not a cosmetic difference. §120 went looking for a stall a player had reported on a click into
    /// the fog and could not reproduce it, because the fixture was ordering a different village about at a
    /// different time of day. A shared method with unshared arguments is two definitions wearing one name.
    /// </para></remarks>
    internal readonly record struct VillageRecipe(
        int Farms,
        int Woodcutters,
        int Quarriers,
        int Carts,
        int Wagons,
        float StartSeconds)
    {
        /// <summary>What the year-long gate legs assert about.</summary>
        public static VillageRecipe Gate { get; } = new(12, 7, 1, 7, 0, 0f);

        /// <summary>What `--village` actually founds, which is what anybody judging from the chair sees.</summary>
        public static VillageRecipe AsPlayed { get; } = new(8, 4, 1, 5, 0, 3100f);
    }

    internal static SimulationWorld BuildVillage(
        float extentMeters,
        out NodeId granary,
        float reliefAmplitudeMetres = 0f,
        Region region = Region.Downland,
        Archetype archetype = Archetype.SplitValley,
        uint mapSeed = 0x5EED1234u,
        VillageRecipe? recipe = null)
    {
        var built = recipe ?? VillageRecipe.Gate;
        var world = new SimulationWorld(extentMeters);
        if (built.StartSeconds > 0f) world.StartAtSeconds(built.StartSeconds);
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
            built.Farms,
            built.Woodcutters,
            built.Quarriers,
            built.Carts,
            built.Wagons,
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
    /// <param name="dressMap">
    /// Whether to paint the biomes and scatter the woodland and outcrops, which is whole-map work.
    /// <b>The second settlement on a map must pass false</b>, or it replants the forest over the first one's
    /// cleared ground — see the note at the call site. Default true so every existing caller is unchanged.
    /// <para>
    /// Known consequence, recorded rather than hidden: the woodland scatter takes the settlement centre and
    /// keeps a near band of trees around it, which §22 established is an economic fact about a site rather
    /// than scenery. A second settlement founded with <c>dressMap: false</c> therefore gets the map's ambient
    /// woodland and not its own ring, so its wood line is whatever the ground happened to give it. Splitting
    /// the band from the scatter is the proper fix and is a separate piece of work.
    /// </para>
    /// </param>
    /// <param name="faction">
    /// Who this settlement belongs to. <b>Added because a second player needs a settlement of its own.</b>
    /// Everything underneath already carries a faction — nodes have one, bodies have one, and hauling,
    /// catchment and housing all filter on it — but the recipe that lays a village down never passed one, so
    /// every settlement this project has ever built belonged to faction zero. That is the single assumption
    /// standing between the world and two players in it.
    /// </param>
    public static NodeId Populate(
        SimulationWorld world,
        int farms,
        int woodcutters,
        int quarriers,
        int carts,
        int wagons,
        Vector2 centre = default,
        Simulation.Collision.FactionId? faction = null,
        bool dressMap = true)
    {
        var granary = world.AddNode(NodeKind.Granary, centre, capacity: 9000, faction: faction);

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
        var people = farms + woodcutters + quarriers + carts + wagons;
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
                occupancy: Occupancy, faction: faction);
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
            producers.Add((world.AddNode(NodeKind.Farm, at, YardCapacity, Resource.Grain, faction: faction), at));
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
        //
        // <b>Whole-map work, and therefore not a settlement's to do twice.</b> §130: these three paint the
        // biomes, scatter woodland and scatter outcrops across the entire map, so a second call to Populate
        // re-forests the ground the first settlement had already cleared to build on. Measured: founding a
        // second village 198 m away put 144 trees back inside twenty metres of the first one's centre, walled
        // its houses off from a granary eleven metres away, and its economy went inert without a single
        // conservation fault — the books balance perfectly when nothing happens.
        //
        // With one settlement "found a settlement" and "dress the map" are the same act and nothing
        // distinguishes them, which is why this stood for as long as there was only ever one.
        if (dressMap)
        {
            PaintBiomes(world);
            ScatterWoodland(world, centre);
            ScatterOutcrops(world);
        }
        // After the scatter, because both of these are readings of ground that has to exist first: the fields
        // have their fertility from the soil field and the wood line is a distance to actual trunks.
        ReportFarmland(world);

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
                UnitType.Villager, faction: faction);
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
                at + new Vector2(0f, extent + UnitType.Villager.Radius + 0.6f), UnitType.Villager, faction: faction);
            world.QueueAssign(
                new[] { hand },
                Assignment.Work(
                    tree, at, extent, Resource.Wood,
                    Deposits.LoadSeconds(Resource.Wood, UnitType.Villager.CarryCapacity),
                    EconomySystem.HandoverSeconds));
        }

        // <b>Quarriers, posted the same way and for the same reason.</b> One rather than four: a settlement
        // founding itself has no use for stone yet — nothing is built of it — so this is the smallest crew that
        // makes the mechanic observable rather than a crew sized against a demand that does not exist. What it
        // proves is that stone moves at all: out of the rock, into a pair of hands, into a store, with
        // conservation holding across a resource that has no production term.
        //
        // It also puts the map's answer on the report. On a map whose nearest outcrop is inside a quarrier's
        // reach the stone comes in; on one where it is not, this hand stands idle and the settlement is being
        // told the same thing the wood line tells it — that the answer is a depot out at the rock.
        for (var i = 0; i < quarriers; i++)
        {
            var rock = NearestUnworkedOutcrop(world, granary, claimed);
            if (!rock.IsValid) break;
            ref readonly var face = ref world.Nodes.Get(rock);
            var at = face.Position;
            var extent = face.FootprintRadius;
            var hand = world.SpawnAgent(
                at + new Vector2(0f, extent + UnitType.Villager.Radius + 0.6f), UnitType.Villager, faction: faction);
            world.QueueAssign(
                new[] { hand },
                Assignment.Work(
                    rock, at, extent, Resource.Stone,
                    Deposits.LoadSeconds(Resource.Stone, UnitType.Villager.CarryCapacity),
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
                centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 7f, UnitType.Villager, faction: faction);
        }

        return granary;
    }

    /// <summary>The nearest tree to the store that no cutter has been sent to yet.</summary>
    /// <summary>The nearest unworked outcrop within a quarrier's reach of a store.</summary>
    /// <remarks>
    /// Deliberately separate from <see cref="NextUnclaimedTree"/> rather than one function with a resource
    /// argument, because the two differ in what they have to check: a tree has to be <em>reachable</em>, since
    /// a wood's interior is impassable and a cutter posted inside a stand stands beside it forever. An outcrop
    /// sits on open crag, so it has no such trap — and adding a reachability query for it would be paying for
    /// a problem stone does not have.
    /// </remarks>
    private static NodeId NearestUnworkedOutcrop(SimulationWorld world, NodeId store, HashSet<int> claimed)
    {
        var from = world.Nodes.Get(store).Position;
        var best = NodeId.None;
        var bestDistance = Quarrying.ReachMetres * Quarrying.ReachMetres;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || node.Kind != NodeKind.Outcrop) continue;
            if (node.Stock.Stone <= 0 || claimed.Contains(node.Id.Value)) continue;
            var distance = Vector2.DistanceSquared(node.Position, from);
            if (distance > bestDistance) continue;
            bestDistance = distance;
            best = node.Id;
        }

        if (best.IsValid) claimed.Add(best.Value);
        return best;
    }

    private static NodeId NextUnclaimedTree(SimulationWorld world, NodeId store, HashSet<int> claimed)
    {
        var from = world.Nodes.Get(store).Position;
        var best = NodeId.None;
        var bestDistance = Woodland.ReachMetres * Woodland.ReachMetres;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || node.Kind != NodeKind.Tree || claimed.Contains(node.Id.Value)) continue;
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

    /// <summary>
    /// What share of the map is closed woodland: ground a body has to walk round.
    /// </summary>
    /// <remarks>
    /// Sampled on a stride rather than every cell, because this feeds a tolerance rather than a decision and a
    /// tolerance does not need four decimal places from thirteen million reads.
    /// </remarks>
    private static float ClosedShare(SimulationWorld world)
    {
        var transform = world.Terrain.Transform;
        var closed = 0;
        var seen = 0;
        for (var z = 0; z < transform.Height; z += 8)
        for (var x = 0; x < transform.Width; x += 8)
        {
            seen++;
            if (world.Terrain.Surface(new GridCell(x, z)) == TerrainSurface.Forest) closed++;
        }

        return closed / (float)MathF.Max(1, seen);
    }

    /// <summary>The interior's lowest ground and how much relief it has, which every biome question needs.</summary>
    /// <remarks>
    /// One measurement with three callers rather than three copies of a sampling loop — §58 recorded the
    /// duplication as something that would drift, and the third caller arriving is when to fix it.
    /// </remarks>
    /// <summary>
    /// What the fields are standing on, which is the only thing that says whether the map reached the economy.
    /// </summary>
    /// <remarks>
    /// <b>Printed because the ordering that makes fertility work is not enforced anywhere.</b> A field reads
    /// the soil when it is placed, so the country has to be painted first — and it is, on both paths that
    /// generate relief, by two separate call sites neither of which mentions the other. Nothing stops a third
    /// path from placing fields on a generated map before the soil exists, and the symptom would be every
    /// field reporting exactly one: not a crash, not a wrong-looking number, just a map that quietly stopped
    /// mattering. One line naming the spread turns that into something a run says out loud.
    /// <para>
    /// A flat map genuinely has no soil field and every field there genuinely is neutral, so it says so in
    /// those words rather than printing a row of ones that would read like the bug.
    /// </para>
    /// </remarks>
    public static void ReportFarmland(SimulationWorld world)
    {
        var fertility = new List<float>();
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (node.IsAlive && node.Kind == NodeKind.Farm) fertility.Add(node.Fertility);
        }

        // <b>What the woodland field actually offers, as a distribution.</b> "No stretches of forest, only small
        // groups" is a claim about the shape of this curve: forest needs a sustained high tail, and clumps
        // scattered everywhere is what a curve with no tail and a raised floor produces.
        if (world.Terrain.Woodland is { } cover)
        {
            var samples = new List<float>();
            var reach = world.ExtentMeters * 0.46f;
            for (var z = -reach; z <= reach; z += 6f)
            for (var x = -reach; x <= reach; x += 6f)
            {
                if (world.Terrain.Contains(new Vector2(x, z))) samples.Add(cover.At(new Vector2(x, z)));
            }

            if (samples.Count > 0)
            {
                samples.Sort();
                float Q(float q) => samples[Math.Clamp((int)(samples.Count * q), 0, samples.Count - 1)];
                var wooded = 0;
                foreach (var one in samples)
                {
                    if (one >= 0.85f) wooded++;
                }

                Console.WriteLine(
                    $"    woodland cover: median {Q(0.5f):F2}, p75 {Q(0.75f):F2}, p90 {Q(0.90f):F2}, " +
                    $"p99 {Q(0.99f):F2}, max {samples[^1]:F2} — {wooded * 100f / samples.Count:F0}% of the " +
                    "map is at closed-canopy pressure");
                ReportWoodPatches(world);
            }
        }

        if (fertility.Count == 0) return;
        // <b>How far the wood actually is, which is the question the pressure field cannot answer.</b>
        // Woodland pressure says a wood belongs here; a cutter needs a trunk to be standing at a distance he
        // can walk. Those are different claims, and siting the village on the first one gave a settlement with
        // 0.38 pressure in reach and not one tree in it.
        {
            var store = Vector2.Zero;
            foreach (ref readonly var node in world.Nodes.All)
            {
                if (node.IsAlive && node.Stores) { store = node.Position; break; }
            }

            var nearest = float.MaxValue;
            var bands = new int[6];
            var edges = new[] { 30f, 60f, 90f, 120f, 180f, 260f };
            foreach (ref readonly var tree in world.Nodes.All)
            {
                if (!tree.IsAlive || !tree.IsStanding) continue;
                var d = Vector2.Distance(tree.Position, store);
                nearest = MathF.Min(nearest, d);
                for (var i = 0; i < edges.Length; i++)
                {
                    if (d <= edges[i]) bands[i]++;
                }
            }

            // <b>And the stone line beside it, because stone's whole claim is that it is further.</b> Printed
            // in the same shape as the wood line so the two are comparable at a glance: if a map's rock is not
            // measurably further off than its trees, the third resource is not earning its bookkeeping.
            var rock = float.MaxValue;
            var outcrops = 0;
            var stone = 0;
            foreach (ref readonly var node in world.Nodes.All)
            {
                if (!node.IsAlive || node.Kind != NodeKind.Outcrop) continue;
                outcrops++;
                stone += node.Stock.Stone;
                rock = MathF.Min(rock, Vector2.Distance(node.Position, store));
            }

            // <b>Quarries and blocks are different counts and the line said only one of them.</b> Once a
            // deposit became a cluster, "72 outcrops" was true of the rocks and useless about the map — the
            // number that decides whether stone is a place you go to is how many <em>workings</em> there are.
            Console.WriteLine(
                outcrops == 0
                    ? "    the stone line: no outcrop anywhere on this map — nothing here has bare enough ground"
                    : $"    the stone line: {SeededOutcrops} quarries of {outcrops} blocks holding " +
                      $"{stone:N0} stone, nearest {rock:F0} m from the store — a quarrier reaches " +
                      $"{Quarrying.ReachMetres:F0} m");

            Console.WriteLine(
                $"    the wood line: nearest tree {nearest:F0} m from the store, and within " +
                $"30/60/90/120/180/260 m there are {bands[0]}/{bands[1]}/{bands[2]}/{bands[3]}/" +
                $"{bands[4]}/{bands[5]} trees — a cutter reaches {Woodland.ReachMetres:F0} m");
        }

        if (world.Terrain.Soil is null)
        {
            Console.WriteLine($"    farmland: {fertility.Count} fields on level neutral ground");
            return;
        }

        fertility.Sort();
        var median = fertility[fertility.Count / 2];
        var total = 0f;
        foreach (var one in fertility) total += one;
        // In grain as well as in multiples, because a multiple is not a quantity anybody can be hungry
        // against. The ration is the other half of the comparison and it is what decides the gate.
        var grain = total * EconomyRates.GrainPerFarmPerYear;
        var mouths = grain / EconomyRates.GrainPerVillagerPerYear;
        Console.WriteLine(
            $"    farmland: {fertility.Count} fields at {fertility[0]:F2}–{fertility[^1]:F2} " +
            $"fertility, median {median:F2} — {grain:N0} grain a year at full potential, " +
            $"which feeds {mouths:F0}");
    }

    /// <summary>
    /// How big this map's woods are, as connected patches rather than as a share.
    /// </summary>
    /// <remarks>
    /// <b>Because a share is not a shape, and the share was the only thing being measured.</b> Reported from the
    /// chair: "if 54% of the map is at closed canopy but that 54% is spread out across the entire map in bunches
    /// of 3-4 tiles it won't actually read as a forest, only as an area with a lot of trees around." Exactly
    /// right, and the previous instrument could not tell those two apart — both give 54%.
    /// <para>
    /// So this floods connected regions of closed-canopy ground and reports how large they are. A forest is a
    /// patch you can walk into and lose sight of the edge of, which on a 600 m map means hectares, not tiles.
    /// Four-connected on purpose: diagonal linking would thread separate copses into one "patch" through a
    /// single touching corner, which is the measurement flattering itself.
    /// </para>
    /// </remarks>
    private static void ReportWoodPatches(SimulationWorld world)
    {
        if (world.Terrain.Woodland is not { } cover) return;
        const float cellMetres = 8f;
        var reach = world.ExtentMeters * 0.46f;
        var across = Math.Max(2, (int)MathF.Round(reach * 2f / cellMetres));
        var closed = new bool[across * across];
        for (var z = 0; z < across; z++)
        for (var x = 0; x < across; x++)
        {
            var at = new Vector2(-reach + x * cellMetres, -reach + z * cellMetres);
            closed[z * across + x] = world.Terrain.Contains(at) && cover.At(at) >= 0.85f;
        }

        var seen = new bool[closed.Length];
        var patches = new List<int>();
        var frontier = new Stack<int>();
        for (var start = 0; start < closed.Length; start++)
        {
            if (!closed[start] || seen[start]) continue;
            var size = 0;
            frontier.Push(start);
            seen[start] = true;
            while (frontier.Count > 0)
            {
                var here = frontier.Pop();
                size++;
                var hx = here % across;
                var hz = here / across;
                void Visit(int nx, int nz)
                {
                    if (nx < 0 || nz < 0 || nx >= across || nz >= across) return;
                    var next = nz * across + nx;
                    if (!closed[next] || seen[next]) return;
                    seen[next] = true;
                    frontier.Push(next);
                }

                Visit(hx - 1, hz);
                Visit(hx + 1, hz);
                Visit(hx, hz - 1);
                Visit(hx, hz + 1);
            }

            patches.Add(size);
        }

        if (patches.Count == 0)
        {
            Console.WriteLine("    woods: no closed-canopy patch anywhere");
            return;
        }

        patches.Sort();
        var perCell = cellMetres * cellMetres / 10_000f;
        var biggest = patches[^1] * perCell;
        var median = patches[patches.Count / 2] * perCell;
        var tiny = 0;
        foreach (var one in patches)
        {
            // Under a third of a hectare is a copse at best — the "bunches of 3-4 tiles" case, counted so it
            // cannot hide inside an average.
            if (one * perCell < 0.35f) tiny++;
        }

        Console.WriteLine(
            $"    woods: {patches.Count} patches, biggest {biggest:F1} ha, median {median:F2} ha, " +
            $"{tiny * 100f / patches.Count:F0}% of them under a third of a hectare");
    }

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

        // <b>Built before the flat-map early return, not after it.</b> This is where the interior's floor and
        // span are already measured, so it is the right place — but a flat map leaves this method immediately,
        // and building the field below that line meant flat ground got no field at all and the scatter fell back
        // to a default. Which happened to behave correctly, and is exactly the kind of accident that stops
        // behaving correctly the moment the default changes.
        world.Terrain.SetSoil(Soil.For(world.Terrain, floor, span));
        world.Terrain.SetWoodland(WoodlandCover.For(world.Terrain, world.Terrain.Layout, floor, span));
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

    /// <summary>How many anchors the map-wide scatter tries, per square kilometre of map.</summary>
    /// <remarks>
    /// Chosen so the accepted count lands near what the three rings it replaced produced — the flat map still
    /// comes out at the density §22's constants were measured against, because on flat ground every term in the
    /// density is one. What changes on a map with relief is <em>where</em> they go.
    /// </remarks>
    /// <summary>
    /// Candidate wood anchors per square kilometre, before the pressure field accepts or refuses them.
    /// </summary>
    /// <remarks>
    /// <b>Cut from 62,000 because the thing it feeds changed underneath it.</b> An anchor is accepted with
    /// probability equal to the local pressure, and that pressure used to sit at 0.02–0.03 across most of a
    /// map — so sixty-two thousand candidates yielded a few thousand trees, and the budget was really a
    /// division by the acceptance rate. Renormalising the field (see <c>WoodlandCover.Concentrate</c>) raised
    /// acceptance by more than an order of magnitude and the same budget produced 31,000 to 57,000 trees.
    /// <para>
    /// <b>Then measured back up, because matching the old total was the wrong target.</b> Cutting to 6,000 held
    /// the count where it used to be — but the old count was spread thinly over the whole map, and the same
    /// count concentrated into two thirds of it is one tree per twenty square metres. That is woodland pasture.
    /// Reported from the chair: the densest area on the map, and the older maps were two or three times denser.
    /// <para>
    /// At 25,000 a deeply wooded map carries 36,569 trees, which inside its 20 ha wood is one tree per 6.6 m² —
    /// a canopy you cannot see through, which is what a forest is. The spacing test caps how tightly they pack,
    /// so raising this fills woods rather than peppering open ground: acceptance out there is still low.
    /// </para>
    /// <para>
    /// <b>A budget that only means something in combination with an acceptance rate has to be re-measured
    /// whenever the rate moves</b> — twice now, in both directions, and the lesson is that the number to aim at
    /// is density inside a wood rather than a total across a map.
    /// </para>
    /// </remarks>
    private const int WoodAnchorsPerSquareKilometre = 25_000;

    /// <summary>
    /// Stone, placed where the rock is and nowhere else.
    /// </summary>
    /// <remarks>
    /// <b>The settlement does not appear in this function, and that is the whole design.</b> §70 deleted a tree
    /// ring that put fuel wherever the village landed; this is written so the equivalent mistake is not
    /// available. An outcrop goes where <see cref="Biomes"/> says crag or scree — steep, high, thin-soiled
    /// ground, classified from grade and height by the generator — so a map's stone is decided before anybody
    /// chooses where to live, and choosing well cannot bring it closer.
    /// <para>
    /// <b>Sparse on purpose, and clustered.</b> A quarry is a place rather than a scatter: a few deposits worth
    /// walking to beats stone everywhere, which would make the distance mechanic vanish exactly the way the
    /// tree ring made it vanish. So the spacing is wide, and what governs the count is how much broken ground
    /// the map actually has — a downland map with two per cent crag gets a handful, and a highland map gets
    /// real quarries. That is the map deciding how rich in stone a country is, which is the same thing
    /// fertility does for grain.
    /// </para>
    /// <para>
    /// No spatial hash and no clump machinery, unlike the woodland: at these counts a linear spacing test is a
    /// few thousand comparisons, and the borrowed complexity would be the only thing to go wrong.
    /// </para>
    /// </remarks>
    private static void ScatterOutcrops(SimulationWorld world)
    {
        var (floor, span) = InteriorRelief(world);
        if (span < 1f) return;

        // <b>Rockier country spreads its quarries further apart and makes each one bigger, and those two
        // cancel.</b> A region's rockiness used to scale only the cluster size, so upland heath simply had
        // more stone than downland — the map type changed the <em>quantity</em>. What it should change is the
        // <em>shape</em>: heath has a few large workings, downland has more small ones, and both come out with
        // roughly the same amount of rock on the map.
        //
        // The arithmetic is deliberate rather than tuned. One anchor claims an area of spacing squared, so
        // scaling the spacing by the square root of rockiness makes the number of anchors fall as 1/rockiness,
        // while the cluster grows as rockiness — and blocks, being anchors times cluster, stays put.
        var rocky = MathF.Max(0.25f, RegionProfile.For(world.Terrain.Region).Rockiness);
        var spacingMetres = StoneSpacingMetres * MathF.Sqrt(rocky);
        // Coarse, because what is being looked for is a region of broken ground rather than a cell of it.
        const float stepMetres = 12f;
        var reach = InteriorExtent(world.ExtentMeters);
        var across = Math.Max(1, (int)MathF.Round(reach / stepMetres));
        var placed = new List<Vector2>();
        var seed = 0x85EBCA6Bu;

        float Next()
        {
            seed += 0x9E3779B9u;
            var z = seed;
            z = (z ^ (z >> 16)) * 0x21F0AAADu;
            z = (z ^ (z >> 15)) * 0x735A2D97u;
            z ^= z >> 15;
            return (z & 0xFFFFFFu) / (float)0x1000000u;
        }

        for (var z = 0; z <= across; z++)
        for (var x = 0; x <= across; x++)
        {
            // Jittered inside its own cell, so a grid sweep does not produce a grid of rocks.
            var at = new Vector2(
                (x + Next() - 0.5f) / across - 0.5f,
                (z + Next() - 0.5f) / across - 0.5f) * reach;
            if (!world.Terrain.Contains(at)) continue;
            // <b>Rock shows where the soil is thin, which is a wider and truer rule than "crag or scree".</b>
            // Keyed to those two biomes, stone existed on 3% of a downland map and nowhere else — clumped on
            // the highest tops, so whole maps had none within reach of anywhere a village could stand: 282 m
            // on the village's own default map, against 34 m on BrokenRidge. A resource that half the maps
            // simply do not have is not a hard choice, it is a coin toss before the game starts.
            //
            // Soil depth is the honest criterion and the terrain already computes it: bare rock and scree read
            // near zero by their bed values, and so does any ground steep enough to have lost its soil, which
            // is where you actually find stone showing through. It also composes with everything else the soil
            // field knows — a dry region's thin ground carries more visible rock than a fen's, without a word
            // about regions here.
            //
            // Still never in the fertile valley the village wants, because that ground is deep by definition.
            // The map decides how much stone a country has; it just no longer decides on 3% of the evidence.
            var biome = Biomes.At(world.Terrain, at, floor, span);
            if (biome is Biome.Water) continue;
            var depth = world.Terrain.Soil is { } soil ? soil.DepthAt(at) : 1f;
            if (depth > ThinSoilForStone) continue;

            var clear = true;
            foreach (var other in placed)
            {
                if (Vector2.DistanceSquared(other, at) >= spacingMetres * spacingMetres) continue;
                clear = false;
                break;
            }

            if (!clear) continue;
            placed.Add(at);

            // <b>How rich this deposit is, from how bare the ground is and what country it is in.</b> Reported
            // from the chair: a deposit was always one lone block, wherever it was, so a map's stone read as
            // scattered pebbles and no place was a quarry. Two terms, and both are already known — how far
            // under the threshold the soil is here, which says how completely the rock has broken through, and
            // the region's own rockiness, which says whether this is heath or fen.
            //
            // <b>What falls out is the thing worth having.</b> Rock is abundant on thin, steep, high ground;
            // thin ground is poor farmland; the site scorer wants fertile ground — so the biggest quarries are
            // furthest from anywhere a village wants to be, and the country you settle in decides whether stone
            // is a short cart ride or an expedition. Marginal ground still carries the odd single block, which
            // is what "less concentrated, smaller, more spread out" means in numbers.
            var bareness = 1f - Math.Clamp(depth / ThinSoilForStone, 0f, 1f);
            var richness = bareness * rocky;
            var blocks = 1 + (int)MathF.Floor(Math.Clamp(richness, 0f, 1.6f) * StoneBlocksPerQuarry);

            for (var block = 0; block < blocks; block++)
            {
                // The first block is the anchor itself; the rest ring it close enough to read as one working.
                var where = block == 0
                    ? at
                    : at + Polar(Next(), StoneBlockSpacing * 0.7f, StoneBlockSpacing * 2.2f, Next());
                if (!world.Terrain.Contains(where)) continue;
                // Not in the water, and not on ground that has since turned out to be deep: a cluster spreads
                // off its anchor and may cross the line the anchor passed.
                if (Biomes.At(world.Terrain, where, floor, span) is Biome.Water) continue;
                var node = world.AddNode(NodeKind.Outcrop, where, capacity: (int)Quarrying.StonePerOutcrop);
                world.SeedStock(node, Resource.Stone, (int)Quarrying.StonePerOutcrop);
            }
        }

        SeededOutcrops = placed.Count;
    }

    /// <summary>
    /// Soil depth at or below which bare rock shows, and an outcrop can stand.
    /// </summary>
    /// <remarks>
    /// Chosen from the measured distribution rather than picked, and it took three passes to land. Keyed to
    /// crag-or-scree it was 3% of a downland map and the nearest rock ran to 282 m; at a 0.16 depth it went
    /// the other way, 79 outcrops on SplitValley with the nearest 22 m from the granary, which is abundance
    /// where scarcity was the entire point.
    /// <para>
    /// <b>The threshold decides where stone can be and the spacing decides how much of it there is</b>, and
    /// separating those two is what made this tunable. This one stays low so stone stays a property of thin
    /// ground; <see cref="StoneSpacingMetres"/> does the thinning.
    /// </remarks>
    private const float ThinSoilForStone = 0.10f;

    /// <summary>How far apart two outcrops must stand, which is what decides how much stone a map has.</summary>
    /// <remarks>
    /// <b>A quarry is a place, not a scatter</b> — the point of stone is that you go somewhere for it, and a
    /// field of rocks is not somewhere. Measured across four archetypes: 44 m gives 25–45 outcrops, which reads
    /// as broken ground everywhere; 95 m gives <b>10–16, and they read as quarries.</b>
    /// <para>
    /// The distance spread survives the thinning, which is the property worth keeping: nearest rock 36 m on
    /// SplitValley and 46 m on BrokenRidge against a quarrier's 60 m reach, 80 m on DiagonalRiver and 88 m on
    /// YValley beyond it. So some maps are worked from home and some want a depot at the rock, and which kind
    /// you are on is the map's answer rather than a dial's.
    /// </para>
    /// <para>
    /// Each deposit is unchanged at 300 — about fifteen months of one pair of hands — so a map now carries
    /// 3,000 to 4,800 stone against a forest's 400,000 wood. Whether that is the right quantity is not
    /// answerable until something is built of it, which is the next arc.
    /// </para>
    /// </remarks>
    private const float StoneSpacingMetres = 118f;

    /// <summary>How many extra blocks the richest ground adds to a quarry, beyond the one it always has.</summary>
    /// <remarks>
    /// So the poorest qualifying ground carries a single boulder and the best carries a working face of several.
    /// The <em>count</em> of quarries is still <see cref="StoneSpacingMetres"/>'s business — this decides how
    /// much of a place each one is, which is the axis that was missing when every deposit was one block.
    /// </remarks>
    private const int StoneBlocksPerQuarry = 5;

    /// <summary>How close together the blocks of one quarry stand.</summary>
    /// <remarks>
    /// Small against the 95 m between quarries, which is the whole point: near enough that a cluster is one
    /// place a quarrier works, far enough apart that the rocks are distinct objects rather than one lump.
    /// </remarks>
    private const float StoneBlockSpacing = 4.5f;

    /// <summary>How many outcrops the map had, so working them out can be reported against it.</summary>
    private static int SeededOutcrops;

    private static void ScatterWoodland(SimulationWorld world, Vector2 centre)
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
        // <b>Explicitly typed, not <c>var</c>, and that is what silences the nullable warning honestly.</b>
        // <c>var</c> infers the <em>nullable</em> annotation for a reference type and relies on flow analysis
        // to narrow it — and flow state does not cross into a local function, so inside TooClose below the
        // compiler falls back to the declared type and reads this dictionary as possibly null. Stating the
        // type fixes the cause; a <c>!</c> at the use site would only have hidden it.
        Dictionary<(int, int), List<Vector2>> buckets = new();

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

            // <b>No rides.</b> Four lanes were carved out from the settlement so a raid had somewhere to come
            // down, which was the right answer while the woodland was rings around the settlement and had
            // therefore sealed it in. It is the wrong answer twice over now: the woodland is placed by the land
            // and already leaves about a third of the map open, and a settlement in the finished game is
            // something a player builds rather than something this scenario lays out — so lanes radiating from
            // a position nobody chose are scenery pretending to be planning.
            //
            // What depended on them is the raid arrival, which used a ride's bearing. See RaidDirector: it now
            // looks for open ground instead, which is what the lanes were standing in for.
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

        // <b>How much woodland the ground itself carries.</b> Slope is the human part of it: a slope is hard
        // to plough, so forest survives on it and the flat gets cleared. Height adds a little — higher is
        // cooler and poorer and keeps its trees.
        //
        // <b>Scaled by how much relief the map actually has, which is the migration guarantee.</b> Written as a
        // modulation around one and faded out as the height range goes to nothing, flat ground comes out at
        // exactly the density it always had and nothing calibrated moves until somebody generates relief on
        // purpose.
        // <b>Four shaping functions removed with the ring they belonged to.</b> Relief, Cover, Aspect and
        // Shelter were this scenario's own opinions about where woodland goes — slope, height, which way a
        // hillside faces, how enclosed it is — written when the scatter was rings around a settlement and the
        // terrain was only allowed to reject candidates. WoodlandCover owns all four judgements now, on a
        // baked field, and has owned them since §69; these were left behind as dead code the compiler had been
        // naming in every build.
        //
        // Worth deleting rather than leaving to rot for the same reason Band was: a dead implementation of a
        // superseded idea is not inert, it is a working shortcut one call away from the next person who wants
        // woodland to do something the field will not do.
        var (reliefFloor, span) = InteriorRelief(world);
        var strength = Math.Clamp(span / 8f, 0f, 1f);




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
        // <b>The ring-planting machinery is gone with the ring.</b> Band placed trees in an annulus around a
        // position, which is the whole shape of "the map is arranged around the settlement" — kept as dead
        // code it would be a working implementation of the thing that was just deleted, sitting one call away
        // from whoever next wants a quick fix for a village with no fuel.

        /// <summary>
        /// Anchors over the whole map, accepted against how much woodland the ground there carries.
        /// </summary>
        /// <remarks>
        /// Uniform in <em>position</em> and shaped entirely by density, which is the inversion this is for: the
        /// rings were uniform in density and shaped by position. A wood now appears where the ground suits a
        /// wood, at whatever distance from anybody that happens to be.
        /// <para>
        /// Clumped like the rings were, because that part was right — a canopy reads as a canopy rather than as
        /// evenly spread noise. Clump size follows the local density, so thin country gets scattered singles
        /// and good country gets stands, which is a second thing the rings could not express: they had one
        /// clump size per radius.
        /// </para>
        /// </remarks>
        void Woods()
        {
            var pressure = world.Terrain.Woodland
                ?? WoodlandCover.For(world.Terrain, world.Terrain.Layout, 0f, 0f);
            var half = world.ExtentMeters * 0.5f;
            var area = world.ExtentMeters * world.ExtentMeters / 1_000_000f;
            var anchors = (int)MathF.Round(WoodAnchorsPerSquareKilometre * area);
            for (var i = 0; i < anchors; i++)
            {
                var anchor = new Vector2(Next() * 2f - 1f, Next() * 2f - 1f) * half;
                if (!world.Terrain.Contains(anchor)) continue;
                // Never inside the settlement's own ring: the near band owns that ground and doubling up there
                // would move §22's in-reach count as a side effect.
                if (Vector2.Distance(anchor, centre) < Woodland.ReachMetres + 8f) continue;

                // <b>One field, asked once.</b> Slope, shelter, aspect, biome, climate, the noise and whatever
                // the layout authored all live in WoodlandCover now — including the veto that used to be
                // CanRoot, since "nothing grows in a river" is a term in the same product rather than a
                // separate rule that could disagree with it.
                var density = pressure.At(anchor);
                // <b>Squared, so raising the budget fills woods instead of peppering the open.</b> Linear
                // acceptance means every extra candidate lands in proportion to the local pressure, so
                // quadrupling the budget quadruples the <em>scattered</em> trees too — and a scattered tree is
                // the expensive kind: level of detail here is by crowding, so a tree standing alone is drawn at
                // full detail. Measured, 531 near-tier trees at 5,940 triangles each was 3.1M of a 6.4M frame.
                //
                // Squaring leaves a closed wood untouched — pressure there is at or above one — and cuts open
                // ground hard: a fifth becomes a twenty-fifth. Which is also the honest shape of the thing.
                // Trees in the open are survivors, and survivors are rare.
                if (Next() > density * density) continue;

                // Denser ground carries bigger stands. Eleven was the old forest clump and three the old
                // fringe; the same range, now decided by the place rather than by the radius.
                var clump = 3 + (int)MathF.Round(Math.Clamp(density, 0f, 1.6f) * 7f);
                // <b>Spacing is what caps a forest, so spacing is what had to move.</b> Trees per unit area go
                // as the inverse square of spacing, so doubling the density of a stand means dividing its
                // spacing by root two — and no amount of extra anchors does it, because <see cref="TryPlant"/>
                // refuses anything closer than this. Measured: tripling the anchor budget moved the total by
                // 1.8x and the dense share not at all.
                //
                // 2.2 m in closed wood becomes 1.55, which is the trunk spacing of a plantation rather than a
                // park. Open ground keeps its wide spacing, so the contrast widens at both ends.
                var spacing = 1.1f + 0.45f / MathF.Max(0.3f, density);
                for (var k = 0; k < clump; k++)
                {
                    var at = anchor + new Vector2(Next() * 2f - 1f, Next() * 2f - 1f) * spacing * 2.2f;
                    for (var attempt = 0; attempt < 6; attempt++)
                    {
                        if (TryPlant(world.Terrain.ClampPosition(at), spacing)) break;
                        at = anchor + new Vector2(Next() * 2f - 1f, Next() * 2f - 1f) * spacing * 2.6f;
                    }
                }
            }
        }

        // <b>The near band is gone, and its own last sentence is the argument against it.</b> It planted 46
        // trees in a ring around the settlement and defended itself as an economic constant — §22's starting
        // fuel, which the year gate was calibrated against — while ending on "it is also true of settlements:
        // you found the place because there was wood round it." That is the correct causation stated in the
        // comment and inverted in the code. The band did not put the settlement where the wood was; it put
        // wood where the settlement was.
        //
        // Reported from the chair, and it is the same objection: <em>place a settlement around existing woods
        // rather than change the map to fit the player.</em> Which is also the last thing holding §51's first
        // finding in place — hauling has never once been seen in a session, no cart ever built and no stranded
        // stock ever boarded, because a guaranteed ring of fuel inside every cutter's reach means nothing is
        // ever far from anything. Distance cannot bite while the map is edited to remove it.
        //
        // What replaces it is ChooseSite asking the woodland field where the trees actually are. The
        // settlement moves to the wood.
        // <b>And everything past reach is placed by the land, not by the settlement.</b> It used to be three
        // more rings — out to ringRadius × 8, which is 240 m — so woodland was a function of distance from the
        // player with geography allowed only to <em>reject</em> candidates. Two things followed. Trees thinned
        // outward from the village whatever the ground was doing, and on a 600 m map <b>everything beyond 240 m
        // of the settlement had no trees at all</b>: the outer half of the world was bare because no band
        // reached it.
        //
        // Sampled over the whole map instead, accepted against a density that is entirely about the ground —
        // what country it is, how steep, how high, how sheltered. The settlement no longer appears in the
        // expression. What it does appear in is the near band above, which stays a ring on purpose: that one is
        // §22's economic constant and is a fact about the site rather than about the scenery.
        Woods();

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
    /// <summary>
    /// How much standing water a site may have on it. Essentially none.
    /// </summary>
    /// <remarks>
    /// Five centimetres, which is a damp hollow rather than water. Not zero, because the water level is sampled
    /// from a four-metre lattice and a hair of interpolated depth on ground that is really dry would otherwise
    /// refuse half the valley floors on the map — and a valley floor beside a river is the single best place to
    /// found, which is the whole reason floodplain exists as a biome.
    /// </remarks>
    private const float WadeableSiteDepth = 0.05f;

    public static Vector2 ChooseSite(SimulationWorld world, float extentMeters)
    {
        var span = ReliefSpan(world);
        if (span < 1f) return CornerSite(extentMeters);

        var terrain = world.Terrain;
        var inset = FieldKeepOut + Woodland.ReachMetres + 40f;
        var limit = extentMeters * 0.5f - inset;
        var best = CornerSite(extentMeters);
        var bestScore = float.NegativeInfinity;
        // What made this the best site, so "why here" is answerable rather than a single opaque score.
        var bestWhy = (Farm: 1f, Wood: 0f, Back: 0f, Grade: 0f);
        var candidates = 0;
        var woodSeen = 0f;
        const float step = 15f;
        for (var z = -limit; z <= limit; z += step)
        for (var x = -limit; x <= limit; x += step)
        {
            var at = new Vector2(x, z);
            var here = terrain.SampleHeight(at);

            // A bench to build on. Sampled over the ground the village and its fields actually occupy
            // rather than at a point, because a flat spot in a steep place is not a site.
            //
            // <b>On the average of that ground, with a separate guard against the worst of it — and it was on
            // the worst alone.</b> A max-of-five gate at 0.11 was a fair reading of "level enough to build on"
            // when relief was a handful of smooth mounds, because then the max and the mean agreed. Erosion
            // dissects, so they stopped agreeing: measured across the candidate grid, SplitValley's median
            // max-of-five is 0.243 against a mean-of-five of 0.160, and the gate was throwing out
            // <b>814 sites of 841</b>. Eighteen survivors is not a choice of where to found, it is one place
            // with rounding, and none of the eighteen had a tree near it.
            //
            // The same cross-layer shape as every bug in §51's list: a threshold correct in the layer that
            // owns it — a buildable grade really is about 11% — and wrong where it meets another, because one
            // erosion gully clipping a sixteen-metre ring now disqualifies an entire hillside that a village
            // would sit on quite happily. The mean says whether this is level ground; the cliff guard says
            // whether it straddles something no village straddles.
            var core = terrain.SampleGrade(at);
            var meanCore = core;
            for (var i = 0; i < 4; i++)
            {
                var angle = i / 4f * MathF.Tau;
                var g = terrain.SampleGrade(at + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * FieldKeepOut);
                core = MathF.Max(core, g);
                meanCore += g;
            }

            meanCore /= 5f;
            if (meanCore > LevelEnoughGrade || core > CliffGrade) continue;

            // <b>And not in the water, which the flatness test actively steers it into.</b> The score rewards
            // level ground, and the most level ground on a map with a river through it is the river — so the
            // village was founded in the channel and the villagers stood in it. Reported from the chair as the
            // village drowning along with the men.
            //
            // A radius rather than a point, because a settlement is thirty-six metres across and its fields
            // reach further: standing dry at the granary is no use if the ground the fields want is a floodplain
            // under half a metre of water. Checked out to the field keep-out, which is exactly the ground the
            // village occupies.
            if (terrain.Drainage is { } water)
            {
                var wet = water.LevelAt(at) - here;
                for (var i = 0; i < 8 && wet <= WadeableSiteDepth; i++)
                {
                    var angle = i / 8f * MathF.Tau;
                    var about = at + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * FieldKeepOut;
                    wet = MathF.Max(wet, water.LevelAt(about) - terrain.SampleHeight(about));
                }

                if (wet > WadeableSiteDepth) continue;
            }

            // <b>Where the wood actually is, and what the fields will actually grow.</b> Both of these were
            // proxies, and both proxies were reasonable right up until the thing they stood for existed.
            //
            // Wood was <em>slope within a cutter's reach</em> — steep ground being where woodland survives the
            // plough. That was a fair guess while trees were planted in a ring regardless of the land, because
            // then nothing could contradict it. Now <see cref="WoodlandCover"/> is the actual answer to "how
            // much wood is here", computed from geography, ground, shelter, aspect and soil, and a proxy for a
            // quantity that is sitting in a field one call away is just a worse copy of it. Same shape as slope
            // standing in for path cost, and width standing in for depth: <b>a proxy agrees with its target in
            // every tested case until something separates them, and what separates these two is a fertile
            // valley floor thick with trees — high wood, no slope at all.</b>
            //
            // Farmland was not a term at all, which is why founding was never a decision. It is the first
            // consumer of Soil.FertilityAt, and it is sampled over the ground the fields will occupy rather
            // than at the granary, because a settlement eats off its fields and not off its yard.
            var wood = 0f;
            var farm = 0f;
            var back = 0f;
            for (var i = 0; i < 12; i++)
            {
                var angle = i / 12f * MathF.Tau;
                var bearing = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                wood += world.Terrain.Woodland?.At(at + bearing * (Woodland.ReachMetres * 0.85f)) ?? 0f;
                farm += world.Terrain.Soil?.FertilityAt(at + bearing * (FieldKeepOut * 0.8f)) ?? 1f;
                // The tallest thing within a couple of hundred metres, relative to here.
                back = MathF.Max(back, terrain.SampleHeight(at + bearing * 170f) - here);
            }

            wood /= 12f;
            farm /= 12f;
            candidates++;
            woodSeen = MathF.Max(woodSeen, wood);

            // <b>The two economic terms lead, and the backdrop is demoted to what it always was.</b> Shelter
            // behind the village is a thing you notice about a place; grain and fuel are the two things that
            // decide whether anybody is still living there in a year. Wood saturates because a cutter cannot
            // use more than reach-full of trees, and fertility does not, because better ground is better
            // ground all the way up.
            var score =
                1.65f * farm +
                1.30f * MathF.Min(1f, wood / 0.62f) +
                0.55f * MathF.Min(1f, back / (span * 0.35f)) -
                2.20f * (meanCore / LevelEnoughGrade);
            if (score <= bestScore) continue;
            bestScore = score;
            best = at;
            bestWhy = (farm, wood, back / MathF.Max(0.01f, span * 0.35f), meanCore);
        }

        // <b>Named terms, not one number.</b> A score of 1.69 says a site won and nothing about what it won
        // on, so a settlement founded on beautiful sheltered gravel reads identically to one founded on a
        // river flat. The columns are the answer to "why here", and they are also the only way to catch the
        // scorer preferring the wrong thing — which is how slope-as-wood survived as long as it did.
        Console.WriteLine(
            $"  founded at ({best.X:F0}, {best.Y:F0}): standing at {terrain.SampleHeight(best):F1} m on a " +
            $"grade of {terrain.SampleGrade(best):F3}, score {bestScore:F2} over a {span:F0} m height range");
        Console.WriteLine(
            $"    why here: farmland {bestWhy.Farm:F2}, wood {bestWhy.Wood:F2} within reach, " +
            $"backdrop {bestWhy.Back:F2}, worst grade {bestWhy.Grade:F3}");
        // How much of the map was even eligible, because a site chosen from eighteen candidates and a site
        // chosen from four hundred are different claims about the map, and the score alone cannot tell them
        // apart. Also the strongest wood any candidate had, which is the honest ceiling on "found near woods":
        // if the best in the whole map is nothing, the settlement is not being sited badly, it cannot be
        // sited well.
        Console.WriteLine(
            $"    chosen from {candidates} eligible sites, the woodiest of which had {woodSeen:F2} " +
            $"within a cutter's {Woodland.ReachMetres:F0} m reach");
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

    /// <summary>How many hands start on the rock.</summary>
    /// <remarks>
    /// One, against four cutters and twelve fields, because nothing is built of stone yet — see the note at
    /// the posting loop. It is sized to make the mechanic observable, not to meet a demand.
    /// </remarks>
    private const int Quarriers = 1;

    private const float FieldKeepOut = 16f;

    /// <summary>Average grade over a village's own ground that still counts as level enough to build on.</summary>
    /// <remarks>
    /// Chosen from the measured distribution rather than picked: across the candidate grid this admits about
    /// three fifths of CornerHighlands, two fifths of SplitValley and half of Escarpment, so every archetype
    /// offers a real choice of where to found and none offers the whole map.
    /// </remarks>
    private const float LevelEnoughGrade = 0.14f;

    /// <summary>The worst grade anywhere under the village, past which it is straddling something.</summary>
    private const float CliffGrade = 0.45f;

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

        // <b>Stone gets its own line rather than a third column, because it is a different kind of number.</b>
        // Grain and wood are flows against an appetite — produced, eaten, short — and stone is a stock being
        // moved off the map into the settlement, with nothing yet drawing on it. Reported as what came out of
        // the rock and what is left in it, which is the only question worth asking until something is built of
        // it: <b>did any stone move at all.</b> Zero with outcrops on the map means the quarry is out of reach,
        // which is the map talking and not a bug.
        var quarried = 0;
        var inTheRock = 0;
        var outcrops = 0;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || node.Kind != NodeKind.Outcrop) continue;
            outcrops++;
            inTheRock += node.Stock.Stone;
        }

        var held = world.Nodes.TotalHeld();
        var carried = EconomySystem.CarriedTotal(world.Agents).Stone;
        quarried = held.Stone + carried;
        // <b>The line accounts for itself, because the first version of it did not and I nearly believed it.</b>
        // It reported 20 held against 62 gone from the rock and no conservation fault, which is two claims that
        // cannot both be true — and the resolution was that the missing units were in a place the report was
        // not looking rather than a place the ledger was not counting. Printing every term of the identity is
        // what turns "these numbers look odd" into "this term is the one".
        var seeded = world.Economy.Seeded.Stone;
        var accounted = inTheRock + held.Stone + carried + (int)world.Economy.Consumed.Stone;
        Console.WriteLine(
            outcrops == 0 && quarried == 0
                ? "  stone: none on this map"
                : $"  stone: {quarried:N0} quarried and held ({held.Stone:N0} stored, {carried:N0} on backs), " +
                  $"{inTheRock:N0} still in {outcrops} outcrops, {world.Economy.Consumed.Stone:N0} consumed — " +
                  $"{accounted:N0}/{seeded:N0} accounted for");
        var carts = 0;
        foreach (ref readonly var body in world.Agents.All)
        {
            if (body.IsAlive && body.HasCart) carts++;
        }

        Console.WriteLine(
            $"  hauling: {carts} carts built, {economy.HaulsAssigned:N0} board jobs given out, " +
            $"{economy.HaulsAbandoned:N0} dropped when the source emptied or the sink filled, " +
            $"{economy.RoutesFinished:N0} standing routes lost an endpoint");
        // <b>Why no wood was cut, said out loud, because the number alone reads as a bug.</b> A year that
        // produces nothing and goes short seventeen hundred looks like a broken economy, and this one is a
        // working economy in a state it has never been in: no store has a tree inside a cutter's reach, so
        // there is no cutting to do and no arrangement in place to fix it.
        // <para>
        // Kept as a report rather than solved, deliberately. <see cref="Woodland.ReachMetres"/> already
        // prescribes the remedy — a forward depot at the wood line, after which wood piles up somewhere
        // nobody eats and carts follow on their own — and in the finished game the thing that puts a depot
        // there is a player. Until there is an interface to commit to that, an unfuelled settlement is the
        // honest reading of a village that has not solved its wood problem, and quietly planting fuel next to
        // it to make the column look healthy is how the ring got there in the first place.
        // </para>
        var nearest = float.MaxValue;
        foreach (ref readonly var store in world.Nodes.All)
        {
            if (!store.IsAlive || !store.Stores) continue;
            foreach (ref readonly var tree in world.Nodes.All)
            {
                if (!tree.IsAlive || !tree.IsStanding || tree.Stock.Wood <= 0) continue;
                nearest = MathF.Min(nearest, Vector2.Distance(tree.Position, store.Position));
            }
        }

        if (nearest > Woodland.ReachMetres && nearest < float.MaxValue)
        {
            Console.WriteLine(
                $"  the wood problem: the nearest standing tree is {nearest:F0} m from any store and a " +
                $"cutter reaches {Woodland.ReachMetres:F0} m, so no wood can be cut at all — the answer is " +
                "a forward depot at the wood line, and nothing in this scenario builds one");
        }

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
