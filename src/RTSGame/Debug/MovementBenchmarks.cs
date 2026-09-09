using System.Diagnostics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;

namespace RTSGame.Debug;

internal static class MovementBenchmarks
{
    /// <summary>
    /// Multiplier on every tick budget here, from the body coming down from a run to a walk.
    /// </summary>
    /// <remarks>
    /// Same re-basing as the self-tests, and needed for the same reason: these windows were
    /// sized so a scenario finishes inside them at 4.5 m/s. Left alone at 1.5 they measure a
    /// half-finished walk, which is not a milder version of the right answer — it corrupts
    /// the metrics outright. <c>walked/optimal</c> reported 0.40x, a body apparently walking
    /// less far than the shortest route exists, because the numerator was distance travelled
    /// so far and the denominator the whole journey.
    /// </remarks>
    private const int WalkingPace = 3;

    public static int Run()
    {
        var warmup = new SimulationWorld();
        MovementStressScenarios.Populate(warmup, 30, issueGroupMove: true);
        Tick(warmup, 30 * WalkingPace);

        Console.WriteLine(
            $"RTSGame movement timing benchmark ({120 * WalkingPace} fixed ticks per scenario)");
        var terrainCommandWorld = new SimulationWorld();
        var terrainCommandAgents = TerrainStressScenarios.Populate(
            terrainCommandWorld,
            issueGroupMove: false);
        terrainCommandWorld.QueueMove(terrainCommandAgents, new System.Numerics.Vector2(10f, 6f));
        var commandStart = Stopwatch.GetTimestamp();
        terrainCommandWorld.Tick((float)SimulationWorld.FixedDeltaSeconds);
        Console.WriteLine(
            $"  30-agent terrain command frame | " +
            $"{Stopwatch.GetElapsedTime(commandStart).TotalMilliseconds:F2} ms");
        foreach (var count in new[] { 50, 200, 500 })
        {
            var world = new SimulationWorld();
            MovementStressScenarios.Populate(world, count, issueGroupMove: true);
            world.Timings.Reset();
            var start = Stopwatch.GetTimestamp();
            Tick(world, 120 * WalkingPace);
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Console.WriteLine($"  {world.Timings.Format(count, world.TickNumber)} | wall {elapsedMilliseconds:F1} ms | flowFields {world.FlowFieldBuilds} | astar {world.PathQueries} | contestedArrivals {world.CrowdedArrivalBlockCount}");
            Console.WriteLine(
                $"    avoidance | solves {world.AvoidanceSolves} | " +
                $"infeasible {world.AvoidanceInfeasible * 100.0 / Math.Max(1, world.AvoidanceSolves):F1}% | " +
                $"terrain-fallback {world.AvoidanceTerrainFallbacks * 100.0 / Math.Max(1, world.AvoidanceSolves):F1}% | " +
                $"dead-stops {world.AvoidanceTerrainDeadStops} | " +
                $"detour-grants {world.CongestionRepathCount} | " +
                $"congestion-reroutes {world.CongestionRerouteCount} " +
                $"in {120 * WalkingPace / 30}s");
        }
        // Constricted geometry is where avoidance is actually hard; the open-field
        // scenarios above never exercise a wall.
        foreach (var (label, build) in new (string, Func<SimulationWorld, int>)[]
                 {
                     ("pen escape (30, walls + 4 gaps)", w =>
                     {
                         MovementStressScenarios.PopulatePenEscape(w, seed: 17);
                         return 30;
                     }),
                     ("one-cell gate (50)", w =>
                     {
                         for (var x = 0; x < w.Placement.Transform.Width; x++)
                         {
                             if (x == 9) continue;
                             w.QueueToggleObstacle(w.Placement.Transform.CellCenter(new RTSGame.Simulation.Spatial.GridCell(x, 10)));
                         }
                         w.Tick((float)SimulationWorld.FixedDeltaSeconds);
                         var ids = new List<RTSGame.Simulation.Agents.AgentId>();
                         for (var i = 0; i < 50; i++)
                         {
                             ids.Add(w.SpawnAgent(new System.Numerics.Vector2(
                                 -6f + i % 10 * 0.9f, -4f - i / 10 * 0.9f)));
                         }
                         w.QueueMove(ids, new System.Numerics.Vector2(0f, 6f));
                         return 50;
                     }),
                 })
        {
            var world = new SimulationWorld();
            var count = build(world);
            world.Timings.Reset();
            // Direction reversals are the headless proxy for the spinning seen in
            // play: a body whose intended heading flips by more than a right angle
            // from one tick to the next is not steering, it is changing its mind.
            // How far each body walks against how far it needed to, and how much
            // wall it hugs on the way. Beelining into corners shows up as both:
            // distance well above the straight line, spent in low-clearance cells.
            var startPosition = new System.Numerics.Vector2[world.Agents.Count];
            var travelled = new float[world.Agents.Count];
            var clearanceSum = 0.0;
            var clearanceSamples = 0L;
            // Move orders are queued and applied on the first tick, so the optimal
            // route has to be sampled after one step — before that every agent's
            // requested destination is still its own position.
            world.Tick((float)SimulationWorld.FixedDeltaSeconds);
            var optimalDistance = new float[world.Agents.Count];
            foreach (ref readonly var agent in world.Agents.All)
            {
                startPosition[agent.Id.Value] = agent.Position;
                optimalDistance[agent.Id.Value] =
                    world.TryOptimalTravelTime(agent.Id, agent.RequestedDestination, out var best)
                        ? best * AgentDefaults.MaximumSpeed
                        : 0f;
            }
            var previousHeading = new System.Numerics.Vector2[world.Agents.Count];
            var reversals = 0;
            var headingSamples = 0;
            var totalTurnDegrees = 0.0;
            // What a player actually complains about is a knot of red units leaning on a
            // gap. Red is StuckSeconds > 0.35, exactly as the renderer draws it, so these
            // numbers describe the thing on screen rather than a proxy for it: how much
            // time is spent failing, the worst single episode, and how deep the pile gets.
            var redTicks = 0L;
            var redRunTicks = new int[world.Agents.Count];
            var worstRedRun = 0;
            var deepestPile = 0;
            var pileTickSum = 0L;
            var pileTicks = 0;
            var redPositions = new List<System.Numerics.Vector2>();
            // How square bodies are to the gap they are going through. Zero is dead-on;
            // ninety is sideways. A body that reaches a one-body gap oblique has to reorient
            // in the one place there is no room to, and that is what the shuffling at a
            // chokepoint looks like from outside.
            var approachSum = 0.0;
            var approachSamples = 0L;
            var obliqueSamples = 0L;
            for (var tick = 0; tick < 300 * WalkingPace; tick++)
            {
                world.Tick((float)SimulationWorld.FixedDeltaSeconds);
                redPositions.Clear();
                foreach (ref readonly var agent in world.Agents.All)
                {
                    if (!agent.IsAlive || !agent.HasDestination) continue;
                    if (agent.StuckSeconds > StallReporting.StalledSeconds)
                    {
                        redTicks++;
                        redRunTicks[agent.Id.Value]++;
                        worstRedRun = Math.Max(worstRedRun, redRunTicks[agent.Id.Value]);
                        redPositions.Add(agent.Position);
                    }
                    else
                    {
                        redRunTicks[agent.Id.Value] = 0;
                    }
                    // The resolved velocity, not the intent: intent is what the unit
                    // means to do, resolved is what the body actually does, and it
                    // is the second one the player watches.
                    travelled[agent.Id.Value] +=
                        agent.Velocity.Length() * (float)SimulationWorld.FixedDeltaSeconds;
                    if (world.Navigation.TryWorldToCell(agent.Position, out var here))
                    {
                        clearanceSum += world.Navigation.Clearance(here);
                        clearanceSamples++;
                    }
                    var approach = world.ApertureApproachDegrees(agent.Id);
                    if (approach >= 0f)
                    {
                        approachSum += approach;
                        approachSamples++;
                        if (approach > 30f) obliqueSamples++;
                    }
                    if (agent.Velocity.LengthSquared() < 0.25f) continue;
                    var heading = System.Numerics.Vector2.Normalize(agent.Velocity);
                    var previous = previousHeading[agent.Id.Value];
                    if (previous != System.Numerics.Vector2.Zero)
                    {
                        headingSamples++;
                        var turn = System.Numerics.Vector2.Dot(previous, heading);
                        if (turn < 0.5f) reversals++;
                        totalTurnDegrees += MathF.Acos(Math.Clamp(turn, -1f, 1f)) * 180f / MathF.PI;
                    }
                    previousHeading[agent.Id.Value] = heading;
                }

                var pile = LargestCluster(redPositions, radius: 1.2f);
                deepestPile = Math.Max(deepestPile, pile);
                if (pile > 1)
                {
                    pileTickSum += pile;
                    pileTicks++;
                }
            }
            var efficiency = 0.0;
            var efficiencyCount = 0;
            foreach (ref readonly var agent in world.Agents.All)
            {
                var best = optimalDistance[agent.Id.Value];
                if (best < 1f) continue;
                efficiency += travelled[agent.Id.Value] / best;
                efficiencyCount++;
            }
            Console.WriteLine(
                $"  {label} | {count} agents | " +
                $"walked/optimal {efficiency / Math.Max(1, efficiencyCount):F2}x | " +
                $"mean-clearance {clearanceSum / Math.Max(1, clearanceSamples):F2}m | " +
                $"turns>60deg {reversals * 100.0 / Math.Max(1, headingSamples):F2}% | " +
                $"mean-turn {totalTurnDegrees / Math.Max(1, headingSamples):F1}deg/tick | " +
                $"infeasible {world.AvoidanceInfeasible * 100.0 / Math.Max(1, world.AvoidanceSolves):F1}% | " +
                $"terrain-fallback {world.AvoidanceTerrainFallbacks * 100.0 / Math.Max(1, world.AvoidanceSolves):F1}% | " +
                $"dead-stops {world.AvoidanceTerrainDeadStops} | " +
                $"detour-grants {world.CongestionRepathCount} | " +
                $"congestion-reroutes {world.CongestionRerouteCount} " +
                $"in {300 * WalkingPace / 30}s");
            Console.WriteLine(
                $"    gaps | approach {approachSum / Math.Max(1, approachSamples):F1}deg mean | " +
                $"oblique>30deg {obliqueSamples * 100.0 / Math.Max(1, approachSamples):F1}% | " +
                $"samples {approachSamples}");
            Console.WriteLine(
                $"    stuck | red {redTicks * SimulationWorld.FixedDeltaSeconds:F1} agent-s | " +
                $"worst-red-run {worstRedRun * SimulationWorld.FixedDeltaSeconds:F1}s | " +
                $"deepest-pile {deepestPile} | " +
                $"mean-pile {(pileTicks == 0 ? 0.0 : pileTickSum / (double)pileTicks):F1} " +
                $"over {pileTicks * SimulationWorld.FixedDeltaSeconds:F1}s");
        }
        return 0;
    }

    /// <summary>
    /// Size of the largest group of positions connected within <paramref name="radius"/>.
    /// </summary>
    /// <remarks>
    /// A pile is not a count of failing units, it is a count of failing units leaning on
    /// each other — thirty spread over four exits and ten wedged in one corner are very
    /// different pictures and the same total. Connectivity, not headcount.
    /// </remarks>
    private static int LargestCluster(List<System.Numerics.Vector2> positions, float radius)
    {
        if (positions.Count == 0) return 0;
        var visited = new bool[positions.Count];
        var frontier = new Stack<int>();
        var largest = 0;
        var radiusSquared = radius * radius;
        for (var seed = 0; seed < positions.Count; seed++)
        {
            if (visited[seed]) continue;
            visited[seed] = true;
            frontier.Push(seed);
            var size = 0;
            while (frontier.TryPop(out var current))
            {
                size++;
                for (var other = 0; other < positions.Count; other++)
                {
                    if (visited[other]) continue;
                    if (System.Numerics.Vector2.DistanceSquared(
                            positions[current], positions[other]) > radiusSquared)
                    {
                        continue;
                    }
                    visited[other] = true;
                    frontier.Push(other);
                }
            }
            largest = Math.Max(largest, size);
        }
        return largest;
    }

    private static void Tick(SimulationWorld world, int count)
    {
        for (var i = 0; i < count; i++) world.Tick((float)SimulationWorld.FixedDeltaSeconds);
    }
}
