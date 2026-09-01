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

    /// <summary>
    /// What the guided search costs in route quality, against the search it replaces.
    /// </summary>
    /// <remarks>
    /// <b>The other half of the claim.</b> Steering by the corner graph's cost-to-goal is not an admissible
    /// lower bound, so the guided search may return a route costing more than the optimum — and a search that
    /// is twenty times cheaper while walking people the long way round is not an improvement, it is a
    /// different bug. Same world, same pairs, both arms, in one process: whichever way the trade falls, it
    /// falls on a number here rather than on an argument.
    /// <para>
    /// One warm-up per arm and the pairs run in the same order, because both arms fill tiles and build fields
    /// as they go and a comparison that let one arm inherit the other's caches would be measuring the order
    /// the questions arrived in.
    /// </para></remarks>
    private static int ReportRouteQuality(
        float extentMeters,
        float reliefAmplitudeMetres,
        int warmupSeconds,
        Region region,
        Archetype archetype,
        uint mapSeed)
    {
        var wasEnabled = PathService.GuidedSearch;
        Console.WriteLine("  route quality, guided against flat — same pairs, same world, one process");
        // <b>Anchored on the village and aimed at compass points, because the map's corners are water.</b> The
        // first version of this used the four corners and four of its five pairs came back unroutable from
        // both arms — a comparison of two nothings. The village site is walkable by construction (it was
        // chosen to be), and a ring around it at a third of the map crosses the ridges and the wood without
        // starting in a lake.
        var reach = extentMeters * 0.33f;
        // <b>Taken from a body, not from a coordinate.</b> Three tries at writing this anchor by hand put it
        // under a building, then in a lake: the founding point is covered by the settlement it founded, and a
        // start cell that does not admit a body is resolved outward by only 2.25 cells where a goal is
        // resolved much further — so pairs leaving the village came back unroutable while their reverses
        // routed fine. A villager is standing somewhere walkable by definition.
        var site = Vector2.Zero;
        var pairs = Array.Empty<(string Name, Vector2 From, Vector2 To)>();

        var results = new List<(string Name, float Flat, float Guided)>();
        foreach (var guided in new[] { false, true })
        {
            // Both halves, because the restart is what makes a field exist at all: without it the "guided"
            // arm finds nothing to steer by and the comparison is two identical searches reporting a perfect
            // 1.000x. That is exactly what the first version of this leg did, five times over.
            PathService.GuidedSearch = guided;
            PathService.GuidedRestart = guided;
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

            foreach (ref readonly var body in world.Agents.All)
            {
                if (!body.IsAlive) continue;
                site = body.Position;
                break;
            }

            pairs = new (string Name, Vector2 From, Vector2 To)[]
            {
                ("out, south-west", site, site + new Vector2(-reach, -reach)),
                ("out, west", site, site + new Vector2(-reach, 0f)),
                ("out, south", site, site + new Vector2(0f, -reach)),
                ("out, north-west", site, site + new Vector2(-reach, reach * 0.4f)),
                ("back, south-west", site + new Vector2(-reach, -reach), site),
                ("back, west", site + new Vector2(-reach, 0f), site),
            };
            if (results.Count == 0)
            {
                Console.WriteLine($"    anchored on a villager at ({site.X:F0}, {site.Y:F0})");
            }

            var estimatesBefore = world.GuideEstimates;
            for (var i = 0; i < pairs.Length; i++)
            {
                var measured = world.MeasureRoute(
                    pairs[i].From, pairs[i].To, Simulation.Agents.AgentDefaults.RoutingRadius);
                var metres = measured?.Metres ?? float.NaN;
                if (!guided) results.Add((pairs[i].Name, metres, float.NaN));
                else results[i] = (results[i].Name, results[i].Flat, metres);
            }

            if (!guided) continue;
            // <b>The estimate's own accuracy, on THIS map.</b> §100's harness reports mean 1.0014 / worst
            // 1.187 — on a tuned world with a single rectangle in it, where the corner graph is trivial. The
            // village decomposes into thousands, and an estimate is only as good as the graph it comes from.
            // If this comes back near one, a 5x route is not the estimate's fault and the fault is mine.
            // Two goals, because one is an anecdote. The harness costs a whole-map flat field per goal, which
            // is why it is two rather than twenty.
            // <b>The same ground priced four ways, because an error in a sum is attributed by subtraction.</b>
            // §122 found the estimate 28% out here against 1.0014 on the tuned world, and the tuned world has
            // one rectangle in it — so every term that could be wrong was zero there. These say which.
            foreach (var (label, at) in new[]
                     {
                         ("from a villager", pairs[0].From),
                         ("from the west", pairs[5].From),
                     })
            {
                var radius = Simulation.Agents.AgentDefaults.RoutingRadius;
                var arms = new[]
                {
                    ("as shipped   ", world.MeasureRectangleFidelity(at, radius)),
                    ("no bend      ", world.MeasureRectangleFidelity(at, radius, bendOverride: 0f)),
                    ("no climb     ", world.MeasureRectangleFidelity(at, radius, chargeClimb: false)),
                    ("neither      ", world.MeasureRectangleFidelity(at, radius, 0f, chargeClimb: false)),
                };
                if (arms[0].Item2.ReachableCells == 0)
                {
                    Console.WriteLine($"    estimate fidelity {label}: nothing reachable, skipped");
                    continue;
                }

                Console.WriteLine(
                    $"    estimate fidelity {label} — {arms[0].Item2.ReachableCells:N0} cells, " +
                    $"{arms[0].Item2.RefinedRegions:N0} rectangles");
                foreach (var (arm, fidelity) in arms)
                {
                    Console.WriteLine(
                        $"      {arm} mean {fidelity.MeanRatio:F4} | p99 {fidelity.NinetyNinthRatio:F4} | " +
                        $"worst {fidelity.WorstRatio:F3}");
                }
            }
            var estimates = world.GuideEstimates;
            var priced = estimates.Priced - estimatesBefore.Priced;
            var fell = estimates.FellBack - estimatesBefore.FellBack;
            Console.WriteLine(
                $"    guide lookups: {priced:N0} priced by the corner graph, {fell:N0} fell back to the flat " +
                $"estimate ({(priced + fell > 0 ? fell * 100.0 / (priced + fell) : 0.0):F1}%)");
        }

        PathService.GuidedSearch = wasEnabled;
        PathService.GuidedRestart = wasEnabled;
        var worst = 1f;
        foreach (var (name, flat, guidedMetres) in results)
        {
            var ratio = flat > 0f && float.IsFinite(flat) && float.IsFinite(guidedMetres)
                ? guidedMetres / flat
                : float.NaN;
            if (float.IsFinite(ratio)) worst = MathF.Max(worst, ratio);
            Console.WriteLine(
                $"    {name,-20} flat {flat,7:F0} m | guided {guidedMetres,7:F0} m | " +
                $"{(float.IsFinite(ratio) ? $"{ratio:F3}x" : "one arm found nothing")}");
        }

        // <b>Five per cent, and the number is a judgement rather than a discovery.</b> A body walking five per
        // cent further is invisible from the chair; a search costing twenty times more is not. Anything past
        // this and the trade has stopped being worth taking — which is what happened: §121 measured 5.02x and
        // turned the lever off.
        const float WorstRatioCeiling = 1.05f;
        var verdict = worst > WorstRatioCeiling
            ? $"worst {worst:F3}x, past the {WorstRatioCeiling:F2}x ceiling"
            : $"worst {worst:F3}x, inside the {WorstRatioCeiling:F2}x ceiling";

        // <b>A fault only when somebody has turned the lever on.</b> With guided search off — which is the
        // shipped state — this leg is a standing measurement of what the rejected idea would cost, and a
        // fixture that fails on every run for a thing nobody enabled is the wolf-crying that §120 had to fix
        // in the jobs trace. Enable it and the same number becomes a fault, which is the acceptance test for
        // the next attempt.
        if (!wasEnabled)
        {
            Console.WriteLine($"    {verdict} — measured with the lever off, so a report and not a fault");
            return 0;
        }

        if (worst > WorstRatioCeiling)
        {
            Console.WriteLine($"    FAULT: {verdict} — the guided search is buying its speed with detours");
            return 1;
        }

        Console.WriteLine($"    {verdict}");
        return 0;
    }

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

            // <b>And then called home, one body at a time.</b> The corner order is a cohort and takes the
            // shared field, so the searches it provokes are all crowd-side — dropped bodies and congestion
            // recovery — which are exactly the ones §121 must not steer. The event a player actually reported
            // was the other kind: a lone villager, spread out and unjammed, asked for a route back across the
            // map. A single-body order is that path exactly, and it is what BeginSoloMove serves.
            var homeRoutesBefore = world.Routes.Snapshot();
            var homeLogFrom = world.Routes.Recorded;
            var worstHomeTickMs = 0.0;
            foreach (var id in movers)
            {
                world.QueueMove(new[] { id }, startPositions[0]);
                for (var tick = 0; tick < 3; tick++)
                {
                    var tickStart = Stopwatch.GetTimestamp();
                    world.Tick((float)SimulationWorld.FixedDeltaSeconds);
                    worstHomeTickMs = Math.Max(
                        worstHomeTickMs, Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds);
                }
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

            Console.WriteLine(
                $"    called home one at a time — worst tick {worstHomeTickMs,7:F1} ms");
            foreach (var line in world.Routes.Describe(homeRoutesBefore, homeLogFrom, verbatimLimit: 4))
            {
                Console.WriteLine($"      {line}");
            }

            if (worstHomeTickMs > 50.0)
            {
                Console.WriteLine(
                    $"    FAULT: calling one villager home cost {worstHomeTickMs:F1} ms in a tick");
                faults++;
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

        faults += ReportRouteQuality(
            extentMeters, reliefAmplitudeMetres, warmupSeconds, region, archetype, mapSeed);

        Console.WriteLine(faults == 0
            ? "  every corner: somebody walked, nothing was searched for and discarded, and no tick hitched"
            : $"  {faults} fault(s)");
        return faults > 0 ? 1 : 0;
    }
}
