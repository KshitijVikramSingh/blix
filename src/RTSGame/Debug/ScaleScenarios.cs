using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;

namespace RTSGame.Debug;

/// <summary>
/// What the movement layer costs on a world the size the game actually wants,
/// rather than the 30 m square every constant in it was tuned on.
/// </summary>
/// <remarks>
/// Two costs in the tick scale with the <em>area of the map</em> and not with the
/// number of bodies on it: the congestion field decays every cell every tick, and
/// the agent broad phase clears every bucket every rebuild. On a 3,600-cell world
/// both are free and invisible. The design calls for 800–1200 m, which is 2.5
/// million to 5.8 million cells, and the question is whether that is a tuning
/// problem or a rewrite.
/// <para>
/// The scenario is deliberately split in two. The <em>idle</em> pass gives no body
/// a destination, so nothing routes and nothing deposits pressure: what is left is
/// exactly the area-scaled floor, measured without a pathfinding number sitting on
/// top of it. The <em>moving</em> pass then puts the same crowd under a group move,
/// which is the honest tick — and on a large map most of what it reports is the
/// routing layer, which is Session 3's problem rather than this one's.
/// </para>
/// </remarks>
internal static class ScaleScenarios
{
    private static readonly float[] Extents = { 800f, 1000f, 1200f };
    private static readonly int[] AgentCounts = { 500, 1000, 2000 };

    private const int IdleTicks = 60;
    private const int MovingTicks = 120;

    public static int Run(float[]? extents = null, int[]? agentCounts = null)
    {
        extents ??= Extents;
        agentCounts ??= AgentCounts;
        // The numbers in this run get pasted into a plan document and compared against
        // numbers taken on another machine, so the grouping and the decimal point are
        // not allowed to depend on where the machine thinks it is.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        Console.WriteLine("RTSGame world-scale measurement");
        Console.WriteLine(
            $"  baseline: the tuned world is {SimulationWorld.DefaultExtentMeters:F0} m " +
            "and is not moved by this scenario");
        Console.WriteLine();
        Console.WriteLine("  predictions written down in advance, at 1200 m:");
        Console.WriteLine("    congestion  ~69 MB of traffic per tick -> >=3.5 ms, agent-count independent");
        Console.WriteLine("    index       640k List.Clear() per rebuild -> 1.3-3.2 ms, agent-count independent");
        Console.WriteLine("    together    5-7 ms/tick, comparable to the entire current movement cost");
        Console.WriteLine();
        Console.WriteLine("  'index' is a subset of steering + collision, not a column beside them.");
        Console.WriteLine();

        // The first world built pays for JIT and for the first big GC arena, which
        // would otherwise be charged to whichever case happened to run first.
        var warmup = new SimulationWorld();
        Populate(warmup, 200, issueGroupMove: true);
        for (var i = 0; i < 30; i++) warmup.Tick((float)SimulationWorld.FixedDeltaSeconds);

        RunCase(SimulationWorld.DefaultExtentMeters, 500);
        foreach (var extent in extents)
        {
            foreach (var count in agentCounts) RunCase(extent, count);
        }

        return 0;
    }

    private static void RunCase(float extentMeters, int agentCount)
    {
        var constructionStart = Stopwatch.GetTimestamp();
        var world = new SimulationWorld(extentMeters);
        var constructionMilliseconds =
            Stopwatch.GetElapsedTime(constructionStart).TotalMilliseconds;

        var cells = world.Congestion.CellCount;
        var buckets = world.AgentIndexBuckets;
        // What a dense field would read and write every tick. Kept as the figure the sparse
        // one is measured against: the field allocates and sweeps by jam now, not by area, so
        // this is the bill that is no longer being paid rather than one that is.
        var congestionMegabytes = cells * 3L * sizeof(float) * 2 / (1024.0 * 1024.0);

        Console.WriteLine(
            $"  {world.ExtentMeters:F1} m | {agentCount} agents | " +
            $"nav {cells:N0} cells | index {buckets:N0} buckets | " +
            $"dense-equivalent traffic {congestionMegabytes:F1} MB/tick | " +
            $"build {constructionMilliseconds:F0} ms | " +
            $"raster {world.NavigationBytes / 1024.0 / 1024.0:F2} MB " +
            $"({world.ChunkedRegions}/{world.RegionCount} regions chunked) | " +
            $"managed {GC.GetTotalMemory(false) / (1024.0 * 1024.0):F0} MB");
        var meshStart = Stopwatch.GetTimestamp();
        var mesh = world.DecomposeWalkable(AgentDefaults.Radius);
        Console.WriteLine(
            $"    ground | {mesh.Count:N0} rectangles | {mesh.Crossings.Count:N0} crossings | " +
            $"decomposed in {Stopwatch.GetElapsedTime(meshStart).TotalMilliseconds:F0} ms");

        // Idle: no destinations, so no routing and no deposits. Only the area floor.
        var idle = Populate(world, agentCount, issueGroupMove: false);
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        world.Timings.Reset();
        var idleStart = Stopwatch.GetTimestamp();
        for (var tick = 0; tick < IdleTicks; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        }
        var idleWall = Stopwatch.GetElapsedTime(idleStart).TotalMilliseconds / IdleTicks;
        Console.WriteLine(
            $"    idle   | {Breakdown(world)} | wall {idleWall:F2} ms/tick | " +
            $"rebuilds {world.AgentIndexRebuildsPerTick}/tick | " +
            $"live cells {world.Congestion.LiveCellCount:N0}");
        var idleSearches = world.RegionSearches;
        var idleTiles = world.TileRefinements;

        // Moving: the same crowd, under one group move, which is the honest tick.
        world.QueueMove(idle, MoveTarget(world));
        // The command applies on the next tick and the group's field is built on the
        // one after, so the pair of them is what a player pays for a single order.
        // Timed apart from the steady state because on a large map it is the single
        // largest number in the run and it would otherwise be smeared over 120 ticks.
        var routeStart = Stopwatch.GetTimestamp();
        var routeSearches = world.RegionSearches;
        var routeTiles = world.TileRefinements;
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var firstRouteMilliseconds = Stopwatch.GetElapsedTime(routeStart).TotalMilliseconds;
        routeSearches = world.RegionSearches - routeSearches;
        routeTiles = world.TileRefinements - routeTiles;
        world.Timings.Reset();
        var movingStart = Stopwatch.GetTimestamp();
        for (var tick = 0; tick < MovingTicks; tick++)
        {
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        }
        var movingWall = Stopwatch.GetElapsedTime(movingStart).TotalMilliseconds / MovingTicks;
        Console.WriteLine(
            $"    moving | {Breakdown(world)} | wall {movingWall:F2} ms/tick | " +
            $"first-route tick {firstRouteMilliseconds:F0} ms " +
            $"({routeSearches:N0} searches, {routeTiles:N0} tiles) | " +
            $"flowFields {world.FlowFieldBuilds} | astar {world.PathQueries} | " +
            $"live cells {world.Congestion.LiveCellCount:N0} | " +
            $"congestion {world.Congestion.ResidentBytes / 1024.0:N0} KB | " +
            $"region-searches {world.RegionSearches - idleSearches:N0} | " +
            $"tiles {world.TileRefinements - idleTiles:N0}");
        Console.WriteLine();
    }

    private static string Breakdown(SimulationWorld world) => world.Timings.Format(
        world.Agents.Count,
        world.TickNumber);

    /// <summary>
    /// A crowd at the centre of whatever world it is given, at the density the
    /// dense stress scenarios use, so the only thing changing between cases is the
    /// amount of empty map around it.
    /// </summary>
    private static AgentId[] Populate(SimulationWorld world, int count, bool issueGroupMove)
    {
        const float spacing = 0.72f;
        var columns = (int)MathF.Ceiling(MathF.Sqrt(count * 1.15f));
        var rows = (int)MathF.Ceiling(count / (float)columns);
        var ids = new AgentId[count];
        var spawned = 0;

        for (var row = 0; row < rows && spawned < count; row++)
        for (var column = 0; column < columns && spawned < count; column++)
        {
            var position = new Vector2(
                (column - (columns - 1) * 0.5f) * spacing,
                (row - (rows - 1) * 0.5f) * spacing);
            ids[spawned++] = world.SpawnAgent(position, radius: AgentDefaults.CrowdRadius);
        }

        if (issueGroupMove) world.QueueMove(ids, MoveTarget(world));
        return ids;
    }

    /// <summary>
    /// Far enough that the crowd is in transit for the whole window, near enough
    /// that the case is measuring movement rather than a map-crossing march.
    /// </summary>
    private static Vector2 MoveTarget(SimulationWorld world)
    {
        var reach = MathF.Min(world.ExtentMeters * 0.25f, 60f);
        return new Vector2(reach, 0f);
    }

    /// <summary>
    /// What a move order costs as a function of how far it is, which is the measurement
    /// the rest of this file quietly avoided.
    /// </summary>
    /// <remarks>
    /// Every case above orders a sixty-metre move, because that is what keeps a crowd in
    /// transit for a hundred and twenty ticks. It is also, on a kilometre map, a short walk
    /// — and a player who clicks the far edge is asking a different question of the router
    /// entirely: the abstract search has to reach across the whole map, and every crossing
    /// it settles on the way costs a region-local search. Reported here rather than assumed
    /// to be the same.
    /// </remarks>
    /// <summary>
    /// What one cross-map order costs on the village the game actually builds.
    /// </summary>
    /// <remarks>
    /// <b>§93 measured a synthetic sculpted world and under-reported the freeze.</b> That world decomposes into
    /// 562 rectangles; a real village carries twenty-five thousand trees and outcrops, every one of which
    /// blocks cells and cuts the walkable area into more rectangles — and both the mesh build and the field's
    /// corner Dijkstra scale with rectangle count. The report from the chair was an order across the whole map
    /// on the standard 600 m village, so that is what this measures.
    /// </remarks>
    public static int RunPathProfile(float extentMeters, float reliefAmplitudeMetres, int orders)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        var world = SettlementScenarios.BuildVillage(
            extentMeters, out _, reliefAmplitudeMetres <= 0f ? 32f : reliefAmplitudeMetres);
        for (var warm = 0; warm < 30; warm++) world.Tick((float)SimulationWorld.FixedDeltaSeconds);

        var movers = new List<AgentId>();
        foreach (ref readonly var agent in world.Agents.All)
        {
            if (agent.IsAlive) movers.Add(agent.Id);
        }

        Console.WriteLine(
            $"RTSGame path profile — the village as built | {world.ExtentMeters:F0} m | " +
            $"{world.Nodes.LiveCount:N0} nodes | {movers.Count} people");
        Console.WriteLine("  one order each, across the map, as a player would click");
        Console.WriteLine();

        var half = extentMeters * 0.5f - 12f;
        for (var order = 0; order < orders; order++)
        {
            // Opposite corners, alternating, so no order re-uses the last one's corridor or its goal.
            var sign = order % 2 == 0 ? 1f : -1f;
            var target = new Vector2(sign * half * 0.9f, -sign * half * 0.9f);
            var before = world.RoutingCost;
            var climbBefore = world.ClimbCost;
            var start = Stopwatch.GetTimestamp();
            world.QueueMove(movers, target);
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var orderMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var after = world.RoutingCost;
            Console.WriteLine(
                $"  order {order + 1} | {orderMs,8:F1} ms | " +
                $"mesh {after.MeshMs - before.MeshMs,7:F1} ms " +
                $"({after.MeshBuilds - before.MeshBuilds} builds, {after.MeshRectangles:N0} rects) | " +
                $"tiles {after.TileMs - before.TileMs,7:F1} ms ({after.TileFills - before.TileFills} fills) | " +
                $"field {after.FieldMs - before.FieldMs,8:F1} ms ({after.Fields - before.Fields} built) | " +
                $"climb {world.ClimbCost.Calls - climbBefore.Calls:N0} calls, " +
                $"{world.ClimbCost.Samples - climbBefore.Samples:N0} samples");
            // Twenty quiet ticks after the order, which is where a stall would show up as the bodies
            // actually start moving and ask for what the order did not build.
            var quiet = Stopwatch.GetTimestamp();
            var quietBefore = world.RoutingCost;
            var queriesBefore = world.PathQueries;
            var expansionsBefore = world.PathSearch.Expansions;
            var dropsBefore = world.FlowTransitDrops;
            var failuresBefore = world.PathSearch.Failures;
            var searchesQuietBefore = world.RegionSearches;
            for (var tick = 0; tick < 20; tick++) world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var quietAfter = world.RoutingCost;
            Console.WriteLine(
                $"           | {Stopwatch.GetElapsedTime(quiet).TotalMilliseconds / 20.0,8:F2} ms/tick over the " +
                $"next 20 | mesh {quietAfter.MeshMs - quietBefore.MeshMs,6:F1} | " +
                $"tiles {quietAfter.TileMs - quietBefore.TileMs,6:F1} | " +
                $"field {quietAfter.FieldMs - quietBefore.FieldMs,7:F1} ms " +
                $"({quietAfter.Fields - quietBefore.Fields} built)");
            // <b>And the tick's own phases, because the first run of this profile found 317 ms a tick that the
            // routing counters could not see.</b> A split that only covers the structures you suspected is a
            // split that will always blame one of them.
            Console.WriteLine(
                $"           | phases: paths {world.Timings.AverageOf(SimulationPhase.Pathfinding),7:F2} " +
                $"behaviors {world.Timings.AverageOf(SimulationPhase.Behaviors),6:F2} " +
                $"steering {world.Timings.AverageOf(SimulationPhase.LocalSteering),6:F2} " +
                $"collision {world.Timings.AverageOf(SimulationPhase.CollisionResolution),6:F2} " +
                $"nav {world.Timings.AverageOf(SimulationPhase.NavigationRefresh),6:F2} " +
                $"recovery {world.Timings.AverageOf(SimulationPhase.CongestionRecovery),6:F2} " +
                $"jobs {world.Timings.AverageOf(SimulationPhase.Jobs),6:F2} " +
                $"commands {world.Timings.AverageOf(SimulationPhase.Commands),6:F2} ms");
            // <b>Calls, not just milliseconds.</b> 315 ms a tick is twenty bodies asking once or one body
            // asking twenty times, and those want opposite fixes — a per-tick cap, or a search that stops
            // being asked.
            var queries = world.PathQueries - queriesBefore;
            var search = world.PathSearch;
            Console.WriteLine(
                $"           | A* {queries:N0} queries over 20 ticks " +
                $"({queries / 20.0:F1} a tick, " +
                $"{(queries > 0 ? world.Timings.AverageOf(SimulationPhase.Pathfinding) * 20.0 / queries : 0.0):F2} " +
                $"ms each) | region searches {world.RegionSearches - searchesQuietBefore:N0}");
            if (queries > 0)
            {
                Console.WriteLine(
                    $"           | expansions {search.Expansions - expansionsBefore:N0} " +
                    $"({(search.Expansions - expansionsBefore) / (double)search.GridCells * 100.0:F0}% of the " +
                    $"grid's {search.GridCells:N0} cells), worst single {search.Worst:N0}, " +
                    $"{search.Failures - failuresBefore} exhausted the map");
                var drops = world.FlowTransitDrops;
                Console.WriteLine(
                    $"           | transit drops: {drops.Rejected - dropsBefore.Rejected} rejected steps, " +
                    $"{drops.NoGradient - dropsBefore.NoGradient} no gradient | " +
                    $"entries {world.FieldEntries.Found}/{world.FieldEntries.Found + world.FieldEntries.Missed} " +
                    $"found, {world.FieldRejoins} rejoins, " +
                    $"worst drop pressure {world.WorstDropPressure:F3}");
            }
        }

        return 0;
    }

    public static int RunOrderDistance(float extentMeters, int agentCount, bool sculpted = false)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        var warmup = new SimulationWorld();
        Populate(warmup, 200, issueGroupMove: true);
        for (var i = 0; i < 30; i++) warmup.Tick((float)SimulationWorld.FixedDeltaSeconds);

        Console.WriteLine(
            $"RTSGame move-order cost by distance | {extentMeters:F0} m map | {agentCount} agents");
        Console.WriteLine("  the order tick, and the ten ticks after it, as a player would feel them");
        Console.WriteLine();

        var half = extentMeters * 0.5f;
        foreach (var fraction in new[] { 0.05f, 0.25f, 0.5f, 0.95f })
        {
            var world = new SimulationWorld(extentMeters);
            var ids = Populate(world, agentCount, issueGroupMove: false);
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);

            var distance = (half * 2f - 12f) * fraction;
            var target = new Vector2(-half + 6f + distance, 0f);
            var searchesBefore = world.RegionSearches;
            var routingBefore = world.RoutingCost;
            world.QueueMove(ids, target);
            var orderStart = Stopwatch.GetTimestamp();
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var orderMilliseconds = Stopwatch.GetElapsedTime(orderStart).TotalMilliseconds;
            var routing = world.RoutingCost;

            var followStart = Stopwatch.GetTimestamp();
            for (var tick = 0; tick < 10; tick++)
            {
                world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            }

            var followMilliseconds = Stopwatch.GetElapsedTime(followStart).TotalMilliseconds / 10.0;
            Console.WriteLine(
                $"  {distance,7:F0} m | order {orderMilliseconds,8:F1} ms | " +
                $"next ticks {followMilliseconds,7:F2} ms | " +
                $"searches {world.RegionSearches - searchesBefore,6:N0} | " +
                $"tiles {world.TileRefinements:N0}");
            // <b>Where the order's milliseconds went, split three ways.</b> The total alone said only that a
            // real map costs twelve times a flat one; these three say which of the mesh, the tiles or the
            // field is doing it, which is the difference between a fix and a guess.
            Console.WriteLine(
                $"          | mesh {routing.MeshMs - routingBefore.MeshMs,7:F1} ms " +
                $"({routing.MeshBuilds - routingBefore.MeshBuilds} builds, " +
                $"{routing.MeshCacheHits - routingBefore.MeshCacheHits} hits, " +
                $"{routing.MeshRectangles:N0} rects) | " +
                $"tiles {routing.TileMs - routingBefore.TileMs,7:F1} ms " +
                $"({routing.TileFills - routingBefore.TileFills} fills) | " +
                $"field {routing.FieldMs - routingBefore.FieldMs,6:F1} ms " +
                $"({routing.Fields - routingBefore.Fields} built)");
        }


        // What a player actually does: click somewhere else, in the same world, again and
        // again. Crossing costs are cached against terrain and congestion, not against the
        // goal, so the question is how much of the first order's price the second one
        // inherits — and every case above answered a different question by building a fresh
        // world each time.
        // Scattered around the map rather than back and forth along one line. Alternating
        // between two ends flatters the cache badly: the second order re-uses the corridor
        // the first one paid for, and every order after that is nearly free — which is not
        // what a player does, and not what "every click hitches" meant.
        Console.WriteLine("  successive orders in one world, scattered targets");
        var repeated = new SimulationWorld(extentMeters);
        if (sculpted) WorldTerrainScenarios.Populate(repeated, issueGroupMove: false);
        Console.WriteLine(
            $"  raster {repeated.NavigationBytes / 1024.0 / 1024.0:F2} MB " +
            $"({repeated.ChunkedRegions}/{repeated.RegionCount} regions chunked)");
        var crowd = Populate(repeated, agentCount, issueGroupMove: false);
        repeated.Tick((float)SimulationWorld.FixedDeltaSeconds);
        for (var order = 0; order < 8; order++)
        {
            // Eight points around the map, each well away from the last.
            var angle = order * MathF.Tau * 0.375f;
            var reach = (half - 20f) * (order % 3 == 0 ? 0.95f : 0.6f);
            var searchesBefore = repeated.RegionSearches;
            repeated.QueueMove(
                crowd,
                new Vector2(MathF.Cos(angle) * reach, MathF.Sin(angle) * reach));
            var routingBefore = repeated.RoutingCost;
            var start = Stopwatch.GetTimestamp();
            repeated.Tick((float)SimulationWorld.FixedDeltaSeconds);
            repeated.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var routing = repeated.RoutingCost;
            // The split matters more here than in the table above: this is the case a player is in — the same
            // world, order after order — and "every click hitches" has to be attributable to one of three
            // things before it can be fixed.
            Console.WriteLine(
                $"  order {order + 1} | {Stopwatch.GetElapsedTime(start).TotalMilliseconds,8:F1} ms | " +
                $"searches {repeated.RegionSearches - searchesBefore,6:N0} | " +
                $"mesh {routing.MeshMs - routingBefore.MeshMs,7:F1} ms " +
                $"({routing.MeshBuilds - routingBefore.MeshBuilds} builds, " +
                $"{routing.MeshCacheHits - routingBefore.MeshCacheHits} hits, " +
                $"{routing.MeshRectangles:N0} rects) | " +
                $"tiles {routing.TileMs - routingBefore.TileMs,6:F1} ms " +
                $"({routing.TileFills - routingBefore.TileFills} fills) | " +
                $"field {routing.FieldMs - routingBefore.FieldMs,7:F1} ms " +
                $"({routing.Fields - routingBefore.Fields} built)");
            for (var tick = 0; tick < 20; tick++)
            {
                repeated.Tick((float)SimulationWorld.FixedDeltaSeconds);
            }
        }

        return 0;
    }
}
