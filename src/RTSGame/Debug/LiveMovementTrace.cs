using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;

namespace RTSGame.Debug;

/// <summary>
/// Low-volume, incident-oriented telemetry for interactive movement debugging.
/// It deliberately measures physical progress independently of the locomotion
/// state so an agent marked as queued can still be identified as stalled.
/// </summary>
internal sealed class LiveMovementTrace
{
    private const float StallBeginSeconds = 0.50f;
    private const float MeaningfulProgressPerSecond = 0.055f;
    /// <summary>Travelled-to-net ratio above which a body counts as going in circles.</summary>
    private const float OrbitRatio = 1.7f;
    /// <summary>Metres a body must have walked in the window before the ratio means anything.</summary>
    private const float OrbitTravelFloor = 0.35f;
    private readonly Dictionary<int, AgentSample> samples = new();
    private SimulationWorld? observedWorld;
    private long nextSummaryTick;
    private int previousRepathCount;
    private int previousImmediateRepairCount;
    private int previousRerouteCount;

    private sealed class AgentSample
    {
        public Vector2 Position;
        public Vector2 FirstWaypoint;
        public float NoProgressSeconds;
        public float StallDuration;
        public int WaypointCount;
        public bool Stalled;
        public Vector2 WindowStartPosition;
        public float DistanceTravelledInWindow;
    }

    public void Observe(SimulationWorld world, float deltaSeconds)
    {
        if (!ReferenceEquals(world, observedWorld)) Reset(world);

        var active = 0;
        var stalled = 0;
        var invalidGeometry = 0;
        var blockedImmediateSteps = 0;
        var visiblyYielding = 0;
        var orbiting = 0;
        var inTransit = 0;
        var atSlot = 0;
        var maximumCrowdedArrivalAttempts = 0;

        foreach (ref readonly var agent in world.Agents.All)
        {
            if (!samples.TryGetValue(agent.Id.Value, out var sample))
            {
                sample = new AgentSample { Position = agent.Position };
                samples.Add(agent.Id.Value, sample);
            }

            var path = world.GetRemainingPath(agent.Id);
            var firstWaypoint = path.Length > 0 ? path[0] : agent.Destination;
            var physicallyMoved = Vector2.Distance(agent.Position, sample.Position);
            var wantsProgress = agent.HasDestination &&
                                Vector2.Distance(agent.Position, agent.Destination) > 0.12f;
            var minimumProgress = MeaningfulProgressPerSecond * deltaSeconds;

            if (wantsProgress && physicallyMoved < minimumProgress)
            {
                sample.NoProgressSeconds += deltaSeconds;
                if (sample.Stalled) sample.StallDuration += deltaSeconds;
            }
            else
            {
                sample.NoProgressSeconds = 0f;
                if (sample.Stalled)
                {
                    Console.WriteLine(
                        $"TRACE STALL-END tick={world.TickNumber} id={agent.Id.Value} duration={sample.StallDuration:F2}s " +
                        $"moved={physicallyMoved:F3} pos={Format(agent.Position)} " +
                        $"repaths={world.CongestionRepathCount}");
                    sample.Stalled = false;
                    sample.StallDuration = 0f;
                }
            }

            if (!sample.Stalled && sample.NoProgressSeconds >= StallBeginSeconds)
            {
                sample.Stalled = true;
                sample.StallDuration = sample.NoProgressSeconds;
                PrintStallBegin(world, agent, path, firstWaypoint);
            }

            var pathChanged = wantsProgress &&
                              (path.Length != sample.WaypointCount ||
                               Vector2.DistanceSquared(firstWaypoint, sample.FirstWaypoint) > 0.25f * 0.25f);
            if (sample.Stalled && pathChanged && sample.WaypointCount > 0)
            {
                Console.WriteLine(
                    $"TRACE PATH-CHANGE tick={world.TickNumber} id={agent.Id.Value} " +
                    $"waypoints={sample.WaypointCount}->{path.Length} next={Format(firstWaypoint)} " +
                    $"repathRequested={agent.RepathRequested} cooldown={agent.RepathCooldown:F2}");
            }

            // A body going in circles moves plenty, so the stall detector above
            // cannot see it. Compare distance walked against distance actually
            // covered: a unit that has travelled several times its own net
            // displacement is orbiting, not travelling.
            sample.DistanceTravelledInWindow += physicallyMoved;
            sample.Position = agent.Position;
            sample.FirstWaypoint = firstWaypoint;
            sample.WaypointCount = path.Length;

            if (agent.HasDestination) active++;
            if (sample.Stalled) stalled++;
            if (!world.IsAgentGeometryValid(agent.Id)) invalidGeometry++;
            if (wantsProgress && path.Length > 0 && !ImmediateStepIsGeometryValid(world, agent, firstWaypoint))
            {
                blockedImmediateSteps++;
            }
            if (agent.IsVisiblyYielding) visiblyYielding++;
                var netMoved = Vector2.Distance(agent.Position, sample.WindowStartPosition);
            if (wantsProgress &&
                sample.DistanceTravelledInWindow > OrbitTravelFloor &&
                sample.DistanceTravelledInWindow > netMoved * OrbitRatio)
            {
                orbiting++;
            }
            if (agent.MoveGroupId != 0)
            {
                if (agent.ApproachingSlot) atSlot++;
                else inTransit++;
            }
            maximumCrowdedArrivalAttempts = Math.Max(
                maximumCrowdedArrivalAttempts,
                agent.CrowdedArrivalAttempts);
        }

        if (world.TickNumber < nextSummaryTick) return;
        nextSummaryTick = world.TickNumber + 30;
        if (active == 0 && stalled == 0) return;

        Console.WriteLine(
            $"TRACE SUMMARY tick={world.TickNumber} active={active} stalled={stalled} " +
            $"yielding={visiblyYielding} orbiting={orbiting} " +
            $"cohort={inTransit}/slot={atSlot} " +
            $"contacts={world.LastContactCount} overlap={WorstOverlap(world):F3} " +
            $"pressure={world.Congestion.Peak:F2}/rev={world.Congestion.Revision} " +
            $"invalidGeometry={invalidGeometry} blockedSteps={blockedImmediateSteps} " +
            $"maxArrivalAttempts={maximumCrowdedArrivalAttempts} " +
            $"routeRepairs=+{world.ImmediateRouteRepairCount - previousImmediateRepairCount} " +
            $"replans=+{world.CongestionRepathCount - previousRepathCount} " +
            $"reroutes=+{world.CongestionRerouteCount - previousRerouteCount}");
        previousRepathCount = world.CongestionRepathCount;
        previousImmediateRepairCount = world.ImmediateRouteRepairCount;
        previousRerouteCount = world.CongestionRerouteCount;
        foreach (ref readonly var agent in world.Agents.All)
        {
            if (!samples.TryGetValue(agent.Id.Value, out var sample)) continue;
            sample.WindowStartPosition = agent.Position;
            sample.DistanceTravelledInWindow = 0f;
        }
    }

    /// <summary>
    /// Deepest body-on-body interpenetration anywhere in the world, in metres.
    /// Depenetration should hold this at zero; a persistent non-zero value means
    /// corrections are being refused, almost always by static geometry.
    /// </summary>
    private static float WorstOverlap(SimulationWorld world)
    {
        var agents = world.Agents.All;
        var worst = 0f;
        for (var first = 0; first < agents.Length; first++)
        for (var second = first + 1; second < agents.Length; second++)
        {
            var minimum = agents[first].Radius + agents[second].Radius;
            var distance = Vector2.Distance(agents[first].Position, agents[second].Position);
            worst = MathF.Max(worst, minimum - distance);
        }
        return worst;
    }

    private static void PrintStallBegin(
        SimulationWorld world,
        in AgentState agent,
        ReadOnlySpan<Vector2> path,
        Vector2 firstWaypoint)
    {
        var navDescription = "outside";
        var nextStepDescription = "none";
        if (world.Navigation.TryWorldToCell(agent.Position, out var cell))
        {
            navDescription = $"{cell.X},{cell.Z}/blocked={world.Navigation.IsBlocked(cell)}" +
                             $"/clear={world.Navigation.Clearance(cell):F2}" +
                             $"/walk={world.Navigation.IsWalkable(cell, agent.Radius)}";
            if (path.Length > 0)
            {
                var direction = firstWaypoint - agent.Position;
                var step = direction.LengthSquared() > 0.0001f
                    ? agent.Position + Vector2.Normalize(direction) * MathF.Min(0.25f, direction.Length())
                    : agent.Position;
                var terrainStep = world.Terrain.CanTraverse(agent.Position, step);
                var navStep = world.Navigation.TryWorldToCell(step, out var nextCell) &&
                              world.Navigation.CanTraverse(cell, nextCell, agent.Radius);
                var geometryStep = world.IsAgentStepGeometryValid(agent.Id, step);
                nextStepDescription =
                    $"terrain={terrainStep}/nav={navStep}/geometry={geometryStep}/sample={Format(step)}";
            }
        }

        Console.WriteLine(
            $"TRACE STALL-BEGIN tick={world.TickNumber} id={agent.Id.Value} " +
            $"state={agent.LocomotionState} pos={Format(agent.Position)} dest={Format(agent.Destination)} " +
            $"requested={Format(agent.RequestedDestination)} next={Format(firstWaypoint)} waypoints={path.Length} " +
            $"speed={agent.Velocity.Length():F2} preferred={agent.PreferredVelocity.Length():F2} " +
            $"stuckFor={agent.StuckSeconds:F2} pressure={agent.CrowdPressureSeconds:F2} " +
            $"group={agent.MoveGroupId}/slot={agent.ApproachingSlot}/flow={agent.UsesFlowTransit}" +
            $"/flowRejects={agent.FlowStepRejections}/noIntent={agent.NoIntentSeconds:F2} " +
            $"arrivalAttempts={agent.CrowdedArrivalAttempts} " +
            $"arrivalContactFrames={agent.CrowdedArrivalContactFrames} " +
            $"yield={agent.CongestionYieldSeconds:F2} " +
            $"surface={world.Terrain.SampleSurface(agent.Position)} height={world.Terrain.SampleHeight(agent.Position):F2} " +
            $"grade={world.Terrain.SampleGrade(agent.Position):F2} nav={navDescription} nextStep={nextStepDescription} " +
            $"geometryValid={world.IsAgentGeometryValid(agent.Id)} contacts={world.LastContactCount} " +
            $"repathRequested={agent.RepathRequested} cooldown={agent.RepathCooldown:F2}");
    }

    private void Reset(SimulationWorld world)
    {
        observedWorld = world;
        samples.Clear();
        nextSummaryTick = world.TickNumber;
        previousRepathCount = world.CongestionRepathCount;
        previousImmediateRepairCount = world.ImmediateRouteRepairCount;
        Console.WriteLine($"TRACE WORLD agents={world.Agents.Count} navRevision={world.Navigation.Revision}");
    }

    private static bool ImmediateStepIsGeometryValid(
        SimulationWorld world,
        in AgentState agent,
        Vector2 firstWaypoint)
    {
        var direction = firstWaypoint - agent.Position;
        if (direction.LengthSquared() <= 0.0001f) return true;
        var step = agent.Position + Vector2.Normalize(direction) * MathF.Min(0.25f, direction.Length());
        return world.IsAgentStepGeometryValid(agent.Id, step);
    }

    private static string Format(Vector2 value) => $"({value.X:F2},{value.Y:F2})";
}
