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
            $"managed {GC.GetTotalMemory(false) / (1024.0 * 1024.0):F0} MB");
        var portalStart = Stopwatch.GetTimestamp();
        var portals = world.PortalCount;
        Console.WriteLine(
            $"    graph  | {world.RegionCount:N0} regions | {portals:N0} portals | " +
            $"{portals * 2:N0} nodes | built in " +
            $"{Stopwatch.GetElapsedTime(portalStart).TotalMilliseconds:F0} ms");

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
        var idleInherited = world.InheritedTiles;

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
            $"tiles {world.TileRefinements - idleTiles:N0} built, " +
            $"{world.InheritedTiles - idleInherited:N0} inherited");
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
        foreach (var weight in new[] { 1f, 1.5f, 2f, 3f })
        {
        RTSGame.Simulation.Navigation.PathService.HeuristicWeight = weight;
        Console.WriteLine($"  heuristic weight {weight:F1}");
        foreach (var fraction in new[] { 0.05f, 0.25f, 0.5f, 0.95f })
        {
            var world = new SimulationWorld(extentMeters);
            var ids = Populate(world, agentCount, issueGroupMove: false);
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);

            var distance = (half * 2f - 12f) * fraction;
            var target = new Vector2(-half + 6f + distance, 0f);
            var searchesBefore = world.RegionSearches;
            world.QueueMove(ids, target);
            var orderStart = Stopwatch.GetTimestamp();
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var orderMilliseconds = Stopwatch.GetElapsedTime(orderStart).TotalMilliseconds;

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
        }

        Console.WriteLine();
        }

        RTSGame.Simulation.Navigation.PathService.HeuristicWeight = 1f;

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
        var crowd = Populate(repeated, agentCount, issueGroupMove: false);
        repeated.Tick((float)SimulationWorld.FixedDeltaSeconds);
        Console.WriteLine(
            $"  regions {repeated.RegionCount:N0} | portals {repeated.PortalCount:N0} | " +
            $"terrain {(sculpted ? "ridge, lake and road" : "empty")}");
        for (var order = 0; order < 8; order++)
        {
            // Eight points around the map, each well away from the last.
            var angle = order * MathF.Tau * 0.375f;
            var reach = (half - 20f) * (order % 3 == 0 ? 0.95f : 0.6f);
            var searchesBefore = repeated.RegionSearches;
            repeated.QueueMove(
                crowd,
                new Vector2(MathF.Cos(angle) * reach, MathF.Sin(angle) * reach));
            var start = Stopwatch.GetTimestamp();
            repeated.Tick((float)SimulationWorld.FixedDeltaSeconds);
            repeated.Tick((float)SimulationWorld.FixedDeltaSeconds);
            Console.WriteLine(
                $"  order {order + 1} | {Stopwatch.GetElapsedTime(start).TotalMilliseconds,8:F1} ms | " +
                $"searches {repeated.RegionSearches - searchesBefore,6:N0} | " +
                $"cached ingress {repeated.CachedIngressCount:N0}");
            for (var tick = 0; tick < 20; tick++)
            {
                repeated.Tick((float)SimulationWorld.FixedDeltaSeconds);
            }
        }

        return 0;
    }
}
