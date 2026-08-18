using System.Diagnostics;
using RTSGame.Simulation;

namespace RTSGame.Debug;

internal static class MovementBenchmarks
{
    public static int Run()
    {
        var warmup = new SimulationWorld();
        MovementStressScenarios.Populate(warmup, 30, issueGroupMove: true);
        Tick(warmup, 30);

        Console.WriteLine("RTSGame movement timing benchmark (120 fixed ticks per scenario)");
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
            Tick(world, 120);
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Console.WriteLine($"  {world.Timings.Format(count, world.TickNumber)} | wall {elapsedMilliseconds:F1} ms | flowFields {world.FlowFieldBuilds} | astar {world.PathQueries} | contestedArrivals {world.CrowdedArrivalBlockCount}");
            Console.WriteLine(
                $"    avoidance | solves {world.AvoidanceSolves} | " +
                $"infeasible {world.AvoidanceInfeasible * 100.0 / Math.Max(1, world.AvoidanceSolves):F1}% | " +
                $"terrain-fallback {world.AvoidanceTerrainFallbacks * 100.0 / Math.Max(1, world.AvoidanceSolves):F1}% | " +
                $"dead-stops {world.AvoidanceTerrainDeadStops} | " +
                $"detour-grants {world.CongestionRepathCount} | " +
                $"congestion-reroutes {world.CongestionRerouteCount} in 10s");
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
                        ? best * 4.5f
                        : 0f;
            }
            var previousHeading = new System.Numerics.Vector2[world.Agents.Count];
            var reversals = 0;
            var headingSamples = 0;
            var totalTurnDegrees = 0.0;
            for (var tick = 0; tick < 300; tick++)
            {
                world.Tick((float)SimulationWorld.FixedDeltaSeconds);
                foreach (ref readonly var agent in world.Agents.All)
                {
                    if (!agent.IsAlive || !agent.HasDestination) continue;
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
                $"congestion-reroutes {world.CongestionRerouteCount} in 10s");
        }
        return 0;
    }

    private static void Tick(SimulationWorld world, int count)
    {
        for (var i = 0; i < count; i++) world.Tick((float)SimulationWorld.FixedDeltaSeconds);
    }
}
