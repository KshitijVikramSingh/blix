using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Navigation;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Debug;

/// <summary>
/// What a click into unscouted ground costs, and whether anybody walks.
/// </summary>
/// <remarks>
/// <b>Written because no fixture in this project could produce the worst event anybody had seen.</b> §119
/// ranked the routing debt off `--pathprofile`, which issues one kind of order on a bare map, while a player
/// clicking a far corner of the fog put the settlement into a permanent retry loop: eleven searches a second,
/// 250,000 cells each, none of them answering, for as long as the game was left running. From the chair it
/// read as "it hitched and then nobody moved".
/// <para>
/// So this is that click, headless and on the same map: the real generated village, warmed up until its
/// people are at work, then ordered to each corner in turn — one fresh village per corner, because a click
/// that strands people changes what the next one measures. What it reports is not milliseconds first but
/// <b>whether the bodies moved</b>, because that is the thing the player said.
/// </para>
/// <para>
/// The corners are deliberately the whole set rather than the one that failed. A click into the fog is any
/// direction the player fancies, and a fixture that tests the one direction somebody happened to complain
/// about is how §119 happened.
/// </para></remarks>
internal static class FogClickScenarios
{
    private const int TicksPerSecond = (int)(1.0 / SimulationWorld.FixedDeltaSeconds);

    public static int Run(
        float extentMeters,
        float reliefAmplitudeMetres,
        int warmupSeconds,
        int watchSeconds,
        Region region,
        Archetype archetype,
        uint mapSeed,
        int expansionBudget)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        if (expansionBudget > 0)
        {
            PathService.ExpansionBudgetOverride = expansionBudget;
            Console.WriteLine(
                $"  per-search expansion ceiling lowered to {expansionBudget:N0} cells — this is a fixture " +
                "knob, and it exists because the truncated-route path is otherwise only reachable on a real " +
                "map with a body eight hundred cells from its goal");
        }

        Console.WriteLine(
            $"RTSGame click into the fog — {extentMeters:F0} m {region}/{archetype} seed {mapSeed}, " +
            $"relief {reliefAmplitudeMetres:F0} m");
        Console.WriteLine(
            $"  {warmupSeconds} s of warm-up so the people are at work, then one order per corner, " +
            $"{watchSeconds} s watched");
        Console.WriteLine();

        var half = extentMeters * 0.5f;
        // Just inside the edge, because the click itself is clamped and the interesting case is ground the
        // player cannot see rather than a coordinate off the map.
        var inset = half - 10f;
        var corners = new (string Name, Vector2 Target)[]
        {
            ("south-west", new Vector2(-inset, -inset)),
            ("north-west", new Vector2(-inset, inset)),
            ("south-east", new Vector2(inset, -inset)),
            ("north-east", new Vector2(inset, inset)),
        };

        var faults = 0;
        foreach (var (name, target) in corners)
        {
            // <b>The village as played, not as gated.</b> The first version of this used the gate's recipe
            // and every corner passed — nineteen people at dawn instead of thirteen at mid-morning, which is
            // a different settlement doing different work. See SettlementScenarios.VillageRecipe.
            var world = SettlementScenarios.BuildVillage(
                extentMeters,
                out _,
                reliefAmplitudeMetres,
                region,
                archetype,
                mapSeed,
                SettlementScenarios.VillageRecipe.AsPlayed);
            for (var tick = 0; tick < warmupSeconds * TicksPerSecond; tick++)
            {
                world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            }

            // Every live body, which is what a marquee over the village gives you.
            var movers = new List<Simulation.Agents.AgentId>();
            foreach (ref readonly var body in world.Agents.All)
            {
                if (body.IsAlive) movers.Add(body.Id);
            }
            var startPositions = movers.Select(id => world.Agents.Get(id).Position).ToArray();
            var routesBefore = world.Routes.Snapshot();
            var refusalsBefore = world.RouteRefusals;
            var logFrom = world.Routes.Recorded;
            var pathfindingBefore = world.Timings.AverageOf(SimulationPhase.Pathfinding);

            var orderStart = Stopwatch.GetTimestamp();
            world.QueueMove(movers, target);
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var orderMs = Stopwatch.GetElapsedTime(orderStart).TotalMilliseconds;

            var watchStart = Stopwatch.GetTimestamp();
            var worstTickMs = 0.0;
            for (var tick = 0; tick < watchSeconds * TicksPerSecond; tick++)
            {
                var tickStart = Stopwatch.GetTimestamp();
                world.Tick((float)SimulationWorld.FixedDeltaSeconds);
                worstTickMs = Math.Max(worstTickMs, Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds);
            }

            var watchMs = Stopwatch.GetElapsedTime(watchStart).TotalMilliseconds;
            // <b>Distance travelled, per body, because "did they move" is the reported symptom.</b> Not
            // distance to the target: a body that walks as far as the ground allows and stops has done
            // exactly what was asked of it, and one that stands still has not, and the two are the same
            // number of metres short.
            var moved = 0;
            var travelled = 0f;
            for (var i = 0; i < movers.Count; i++)
            {
                var distance = Vector2.Distance(world.Agents.Get(movers[i]).Position, startPositions[i]);
                travelled += distance;
                if (distance > 2f) moved++;
            }

            var resolution = world.LastOrderFoundNothing
                ? "NOTHING REACHABLE near it"
                : world.LastOrderWasBestEffort
                    ? $"best effort, {world.LastOrderShortfall:F0} m short of the ask"
                    : "taken as asked";
            Console.WriteLine(
                $"  {name,-11} ({target.X,5:F0},{target.Y,5:F0}) — {resolution}");
            Console.WriteLine(
                $"    order tick {orderMs,7:F1} ms | {watchSeconds} s watched in " +
                $"{watchMs / 1000.0,5:F1} s of wall clock, worst tick {worstTickMs,7:F1} ms");
            Console.WriteLine(
                $"    MOVED {moved} of {movers.Count} bodies, " +
                $"{travelled / Math.Max(1, movers.Count):F1} m each on average");
            foreach (var line in world.Routes.Describe(routesBefore, logFrom, verbatimLimit: 6))
            {
                Console.WriteLine($"    {line}");
            }

            // <b>Where the work went in the bin.</b> A route the search found and the smoothing discarded is
            // the worst refusal there is: it costs everything a successful search costs and the caller cannot
            // tell it from "no route exists", so it asks again next tick. §120.
            var refused = world.RouteRefusals;
            var discarded = (refused.SmoothedToNothing - refusalsBefore.SmoothedToNothing) +
                            (refused.FirstStepBlocked - refusalsBefore.FirstStepBlocked);
            if (discarded > 0)
            {
                Console.WriteLine(
                    $"    FAULT: {discarded} routes the search had already found were discarded — " +
                    $"{refused.SmoothedToNothing - refusalsBefore.SmoothedToNothing} smoothed to nothing, " +
                    $"{refused.FirstStepBlocked - refusalsBefore.FirstStepBlocked} first step blocked");
                faults++;
            }

            // <b>The fault is a body that was told to walk and did not, and the searching that bought it.</b>
            // Not the shortfall: a click into ground nobody can reach SHOULD end short, and the player said
            // so — they do not expect a villager to know what is behind the fog. What they expect is that it
            // walks as far as it can.
            if (moved == 0)
            {
                Console.WriteLine($"    FAULT: nobody moved");
                faults++;
            }

            // <b>A per-tick ceiling, because the reported symptom was a hitch and not a total.</b> Fifty
            // milliseconds is three frames at sixty — the point where a stutter stops being deniable. §118
            // argued the real headroom is nearer five, and this is deliberately the looser number: it is the
            // line below which nobody would have complained, not the line the frame budget wants.
            const double WorstTickCeilingMs = 50.0;
            if (worstTickMs > WorstTickCeilingMs)
            {
                Console.WriteLine(
                    $"    FAULT: worst tick {worstTickMs:F1} ms against a {WorstTickCeilingMs:F0} ms ceiling — " +
                    "the routes are answered now, but a search that reaches its expansion ceiling still costs " +
                    "a visible hitch");
                faults++;
            }

            var pathfinding = world.Timings.AverageOf(SimulationPhase.Pathfinding) - pathfindingBefore;
            if (watchMs > watchSeconds * 1000.0)
            {
                Console.WriteLine(
                    $"    FAULT: {watchSeconds} s of simulation took {watchMs / 1000.0:F1} s of wall clock — " +
                    $"the settlement cannot keep up with itself (pathfinding {pathfinding:F1} ms a tick)");
                faults++;
            }

            Console.WriteLine();
        }

        Console.WriteLine(faults == 0
            ? "  every corner: somebody walked, nothing was searched for and discarded, and no tick hitched"
            : $"  {faults} fault(s)");
        return faults > 0 ? 1 : 0;
    }
}
