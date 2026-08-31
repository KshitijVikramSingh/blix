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
    /// <summary>
    /// Replays the reported sequence — far, then halfway, then far again — and names what each order did.
    /// </summary>
    /// <remarks>
    /// <b>From the chair:</b> "I gave my first command, they stood in place (it was across the map, in the
    /// fog), then I clicked another spot in the fog relatively closer halfway across the map, they walked and
    /// reached, then I clicked somewhere around the initial spot again, and the game froze."
    /// <para>
    /// Three orders, three different outcomes, and the third one is not the same failure as the first. So this
    /// runs exactly that and reports, per order: how many bodies went onto the shared field, how many got their
    /// own route, how many were refused one and stand there — and, when a route is refused, which of the four
    /// refusals it was. Then it lets the bodies run and reports how far they actually got, because "accepted an
    /// order" and "went somewhere" are different claims.
    /// </para>
    /// <para>
    /// Fog is not in this: the order path does not consult it, so a target in unexplored ground is only
    /// incidentally special — it is unexplored because nobody has been there, which correlates with it being
    /// woodland the router finds awkward, and that correlation is the whole of the connection.
    /// </para>
    /// </remarks>
    public static int RunOrderProbe(float extentMeters, float reliefAmplitudeMetres)
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

        var half = extentMeters * 0.5f - 12f;
        var origin = Centroid(world, movers);
        Console.WriteLine(
            $"RTSGame order probe — {world.ExtentMeters:F0} m village, {world.Nodes.LiveCount:N0} nodes, " +
            $"{movers.Count} people at ({origin.X:F0}, {origin.Y:F0})");
        Console.WriteLine();

        // Far, halfway, then far again — the reported sequence, in the reported order.
        var far = new Vector2(-origin.X * 0.9f, -origin.Y * 0.9f);
        var midway = origin + (far - origin) * 0.5f;
        // <b>The catalogue, not just the reported sequence.</b> An order to a random spot can fail in more ways
        // than one, and the point of this probe is to name them all before anything is designed around them. So
        // after the reported far-mid-far it walks a set of deliberately awkward targets: ground nothing can
        // stand on, ground across water, ground the body is already on, and ground off the map.
        var awkward = FindAwkwardTargets(world, movers, extentMeters);

        // Forty simulated seconds an order, not three. The first version ran ninety ticks and reported that
        // the bodies had moved three metres, which at a villager's pace is exactly right for three seconds and
        // says nothing at all about whether they were walking or wedged.
        Order(world, movers, far, "far, across the map", 1200);
        Order(world, movers, midway, "halfway back", 1200);
        Order(world, movers, far, "far again", 1200);

        foreach (var (label, target) in awkward)
        {
            Order(world, movers, target, label, 600);
        }

        return 0;
    }

    /// <summary>
    /// Targets chosen to break an order in a different way each time.
    /// </summary>
    /// <remarks>
    /// Found by looking at the map rather than by hard-coded coordinates, because the interesting ones are
    /// properties of the ground — the middle of a lake, the middle of a wood — and a coordinate that is a lake
    /// on one seed is a meadow on the next.
    /// </remarks>
    private static List<(string Label, Vector2 Target)> FindAwkwardTargets(
        SimulationWorld world,
        List<AgentId> movers,
        float extentMeters)
    {
        var found = new List<(string, Vector2)>();
        Vector2? deepWater = null;
        Vector2? deepWood = null;
        var transform = world.Navigation.Transform;
        for (var z = 4; z < world.Navigation.Height - 4 && (deepWater is null || deepWood is null); z += 3)
        for (var x = 4; x < world.Navigation.Width - 4; x += 3)
        {
            var cell = new Simulation.Spatial.GridCell(x, z);
            var surface = world.Terrain.Surface(cell);
            // Wanted well inside, not on the shore or the treeline: a target one cell into an obstacle is
            // resolved outward to walkable ground immediately and tests nothing.
            if (deepWater is null && surface == Simulation.Terrain.TerrainSurface.Impassable &&
                Surrounded(world, cell, Simulation.Terrain.TerrainSurface.Impassable, 6))
            {
                deepWater = transform.CellCenter(cell);
            }

            if (deepWood is null && surface == Simulation.Terrain.TerrainSurface.Forest &&
                Surrounded(world, cell, Simulation.Terrain.TerrainSurface.Forest, 6))
            {
                deepWood = transform.CellCenter(cell);
            }
        }

        if (deepWater is { } water) found.Add(("deep inside impassable ground", water));
        if (deepWood is { } wood) found.Add(("deep inside a wood", wood));
        found.Add(("where they already stand", Centroid(world, movers)));
        found.Add(("off the map entirely", new Vector2(extentMeters, extentMeters)));
        return found;
    }

    private static bool Surrounded(
        SimulationWorld world,
        Simulation.Spatial.GridCell centre,
        Simulation.Terrain.TerrainSurface surface,
        int radius)
    {
        for (var dz = -radius; dz <= radius; dz += radius)
        for (var dx = -radius; dx <= radius; dx += radius)
        {
            var cell = new Simulation.Spatial.GridCell(centre.X + dx, centre.Z + dz);
            if (world.Terrain.Surface(cell) != surface) return false;
        }

        return true;
    }

    private static Vector2 Centroid(SimulationWorld world, List<AgentId> movers)
    {
        var centre = Vector2.Zero;
        foreach (var id in movers) centre += world.Agents.Get(id).Position;
        return movers.Count == 0 ? Vector2.Zero : centre / movers.Count;
    }

    private static void Order(
        SimulationWorld world,
        List<AgentId> movers,
        Vector2 target,
        string label,
        int ticks)
    {
        var before = Centroid(world, movers);
        var outcomesBefore = world.OrderOutcomes;
        var refusalsBefore = world.RouteRefusals;
        var routingBefore = world.RoutingCost;
        var searchBefore = world.PathSearch;
        var dropsBefore = world.FlowTransitDrops;

        var orderStart = Stopwatch.GetTimestamp();
        world.QueueMove(movers, target);
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var orderMs = Stopwatch.GetElapsedTime(orderStart).TotalMilliseconds;

        var worstTick = 0.0;
        var worstTickAt = 0;
        var runStart = Stopwatch.GetTimestamp();
        var trace = new List<(int Tick, float Remaining, int Moving, float WorstStuck)>();
        for (var tick = 0; tick < ticks; tick++)
        {
            var tickStart = Stopwatch.GetTimestamp();
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var tickMs = Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds;
            if (tickMs > worstTick)
            {
                worstTick = tickMs;
                worstTickAt = tick;
            }

            // Sampled rather than summarised: an order that closes half the distance and then stops looks
            // identical at the end to one that never started, and the difference is the whole question.
            if ((tick + 1) % (ticks / 4) != 0) continue;
            var moving = 0;
            var worstStuck = 0f;
            foreach (var id in movers)
            {
                ref readonly var body = ref world.Agents.Get(id);
                if (body.Velocity.LengthSquared() > 0.04f) moving++;
                worstStuck = MathF.Max(worstStuck, body.StuckSeconds);
            }

            trace.Add((tick + 1, Vector2.Distance(Centroid(world, movers), target), moving, worstStuck));
        }

        var runMs = Stopwatch.GetElapsedTime(runStart).TotalMilliseconds;
        var outcomes = world.OrderOutcomes;
        var refusals = world.RouteRefusals;
        var routing = world.RoutingCost;
        var search = world.PathSearch;
        var drops = world.FlowTransitDrops;
        var after = Centroid(world, movers);
        var travelled = Vector2.Distance(before, after);
        var remaining = Vector2.Distance(after, target);

        Console.WriteLine($"  {label} — target ({target.X:F0}, {target.Y:F0})");
        Console.WriteLine(
            $"    outcomes: {outcomes.Transit - outcomesBefore.Transit} on the shared field, " +
            $"{outcomes.SlotPath - outcomesBefore.SlotPath} on own route, " +
            $"{outcomes.Refused - outcomesBefore.Refused} REFUSED a route");
        var noStart = refusals.NoStartCell - refusalsBefore.NoStartCell;
        var noGoal = refusals.NoGoalCell - refusalsBefore.NoGoalCell;
        var badStart = refusals.StartUnresolvable - refusalsBefore.StartUnresolvable;
        var badGoal = refusals.GoalUnresolvable - refusalsBefore.GoalUnresolvable;
        var nothing = refusals.SearchFoundNothing - refusalsBefore.SearchFoundNothing;
        var truncated = refusals.TruncatedToStart - refusalsBefore.TruncatedToStart;
        if (noStart + noGoal + badStart + badGoal + nothing + truncated > 0)
        {
            Console.WriteLine(
                $"    refusals: goal off grid {noGoal}, start off grid {noStart}, " +
                $"goal unresolvable {badGoal}, start unresolvable {badStart}, " +
                $"search found nothing {nothing}, truncated to start {truncated}");
        }

        Console.WriteLine(
            $"    cost: order tick {orderMs,7:F1} ms | {ticks} ticks {runMs / ticks,6:F2} ms each, " +
            $"worst {worstTick,7:F1} ms | mesh {routing.MeshMs - routingBefore.MeshMs,6:F1} " +
            $"tiles {routing.TileMs - routingBefore.TileMs,6:F1} " +
            $"field {routing.FieldMs - routingBefore.FieldMs,6:F1} ms");
        Console.WriteLine(
            $"    search: {search.Expansions - searchBefore.Expansions:N0} cells expanded, " +
            $"worst single {search.Worst:N0} | transit drops " +
            $"{drops.Rejected - dropsBefore.Rejected} rejected, " +
            $"{drops.NoGradient - dropsBefore.NoGradient} no gradient");
        Console.WriteLine(
            $"    motion: moved {travelled,6:F1} m over {ticks / 30f:F0} s, still {remaining,6:F1} m out" +
            (travelled < 2f ? "  <-- STOOD STILL" : string.Empty));
        foreach (var (tick, left, moving, stuck) in trace)
        {
            Console.WriteLine(
                $"      t+{tick / 30f,4:F0}s | {left,6:F1} m to go | {moving,2} of {movers.Count} moving | " +
                $"worst stall {stuck,5:F1} s");
        }
        Console.WriteLine();
    }

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
            Console.WriteLine(
                $"           | tile split: seed {after.TileSeedMs - before.TileSeedMs,7:F1} ms " +
                $"({after.TileSeedCells - before.TileSeedCells:N0} perimeter cells priced) | " +
                $"search {after.TileSearchMs - before.TileSearchMs,7:F1} ms " +
                $"({after.RegionRelaxations - before.RegionRelaxations:N0} visits, " +
                $"{after.RegionSteps - before.RegionSteps:N0} priced, " +
                $"{(after.RegionRelaxations > before.RegionRelaxations ? (after.TileSearchMs - before.TileSearchMs) * 1e6 / (after.RegionRelaxations - before.RegionRelaxations) : 0.0):F0} ns each)");
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

        // <b>What a finished building costs.</b> Construction changes the placement grid, which re-rasterises
        // navigation, which bumps the revision every mesh and field is keyed by — so the click after a
        // building completes pays for the whole map twice over. Measured here rather than argued about: force
        // one rebuild, then order again.
        Console.WriteLine();
        Console.WriteLine("  what a placement change costs the next click");
        var rasterBefore = world.NavRasterCost;
        // <b>A placement change, then the refresh the game actually runs.</b> The first version of this called
        // RebuildTerrainNavigation directly, which measures the full path whatever the placement path does —
        // so it went on reporting 780 ms after the local pass had been written.
        if (world.Placement.Transform.TryWorldToCell(new Vector2(6f, 6f), out var wall))
        {
            for (var dx = 0; dx < 4; dx++)
            {
                world.Placement.SetOccupied(new Simulation.Spatial.GridCell(wall.X + dx, wall.Z), true);
            }
        }

        var rasterStart = Stopwatch.GetTimestamp();
        world.RefreshNavigationForTest();
        var rasterMs = Stopwatch.GetElapsedTime(rasterStart).TotalMilliseconds;
        var after0 = world.NavRasterCost;
        Console.WriteLine(
            $"  raster {rasterMs,8:F1} ms ({after0.Rebuilds - rasterBefore.Rebuilds} rebuild, " +
            $"{world.Navigation.Width * world.Navigation.Height:N0} cells) — " +
            $"terrain pass {after0.TerrainMs - rasterBefore.TerrainMs:F1} ms, " +
            $"clearance {after0.RestMs - rasterBefore.RestMs:F1} ms over " +
            $"{Simulation.Navigation.NavigationRasterizer.ClearanceCells:N0} cells " +
            $"(window {Simulation.Navigation.NavigationRasterizer.ClearanceWindowCells:N0}, " +
            $"terrain window {Simulation.Navigation.NavigationRasterizer.TerrainCells:N0}) with " +
            $"{Simulation.Navigation.NavigationRasterizer.ObstacleBoxes:N0} boxes, " +
            $"apply {after0.ApplyMs - rasterBefore.ApplyMs:F1} ms");

        var coldBefore = world.RoutingCost;
        var coldStart = Stopwatch.GetTimestamp();
        world.QueueMove(movers, new Vector2(half * 0.5f, half * 0.5f));
        world.Tick((float)SimulationWorld.FixedDeltaSeconds);
        var coldMs = Stopwatch.GetElapsedTime(coldStart).TotalMilliseconds;
        var cold = world.RoutingCost;
        Console.WriteLine(
            $"  order  {coldMs,8:F1} ms | mesh {cold.MeshMs - coldBefore.MeshMs,7:F1} ms " +
            $"({cold.MeshBuilds - coldBefore.MeshBuilds} builds) | " +
            $"tiles {cold.TileMs - coldBefore.TileMs,7:F1} ms | " +
            $"field {cold.FieldMs - coldBefore.FieldMs,7:F1} ms");

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
