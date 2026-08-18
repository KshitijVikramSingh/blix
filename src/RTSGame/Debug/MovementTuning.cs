using Blix.Diagnostics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Movement;
using RTSGame.Simulation.Navigation;

namespace RTSGame.Debug;

/// <summary>
/// Live-tunable body feel, surfaced as sliders by the diagnostics overlay.
/// </summary>
/// <remarks>
/// These are the numbers that decide whether a unit reads as a person or a go-kart, and
/// they are the ones no amount of headless measurement can settle — a crowd that scores
/// well on route length and stall time can still look wrong, and the only instrument for
/// that is somebody watching it. So they are sliders rather than constants under test.
/// <para>
/// Applied to every live body each frame rather than only at spawn, so moving a slider
/// changes what is already on the map instead of only what is created next.
/// </para>
/// </remarks>
/// <summary>
/// How fast the world runs, as a multiplier on wall-clock time entering the fixed step.
/// </summary>
/// <remarks>
/// Compression is a <em>tick-rate</em> multiplier and never a speed multiplier, and the two
/// are not interchangeable however alike they feel from the chair. Raising speed grows a
/// player's reach in metres, which grows the contested fraction of the map and hands back
/// exactly the tenure a large map was chosen to buy — a 2x speed bump on an 800 m map gives
/// the contest geometry of a 400 m one. Raising the tick rate preserves every in-game ratio
/// and only shortens the waiting.
/// <para>
/// <c>FixedDeltaSeconds</c> stays at 1/30 whatever this says, so not one constant the
/// locomotion layer was tuned against is touched. The spiral guard scales with it, because a
/// guard fixed in wall-clock seconds becomes a different guard the moment time is
/// compressed.
/// </remarks>
internal sealed class ClockSettings
{
    /// <summary>Sim seconds per wall-clock second.</summary>
    /// <remarks>
    /// 1.5x is the proposed default: an hour of wall clock is a year of ten-minute seasons.
    /// Left on a slider because it is the one dial that changes how much of the game a
    /// player sees per sitting without changing the game.
    /// </remarks>
    [Tune(0.25, 6.0, Label = "time compression (x)")]
    public float Compression = 1f;
}

internal sealed class BodyFeelSettings
{
    [Tune(0.5, 12.0, Label = "top speed (m/s)")]
    public float MaximumSpeed = AgentDefaults.MaximumSpeed;

    [Tune(0.5, 24.0, Label = "acceleration (m/s2)")]
    public float Acceleration = AgentDefaults.Acceleration;

    [Tune(0.5, 32.0, Label = "deceleration (m/s2)")]
    public float Deceleration = AgentDefaults.Deceleration;

    [Tune(0.5, 12.0, Label = "turn rate (rad/s)")]
    public float MaximumTurnSpeed = AgentDefaults.MaximumTurnSpeed;

    [Tune(0.0, 2.5, Label = "free-turn speed (m/s)")]
    public float FreeTurnSpeed = AgentDefaults.FreeTurnSpeed;

    /// <summary>Pushes the current values onto the defaults and every live body.</summary>
    public void Apply(SimulationWorld world)
    {
        AgentDefaults.Acceleration = Acceleration;
        AgentDefaults.Deceleration = Deceleration;
        AgentDefaults.MaximumTurnSpeed = MaximumTurnSpeed;
        AgentDefaults.FreeTurnSpeed = FreeTurnSpeed;

        var agents = world.Agents.MutableSpan();
        for (var i = 0; i < agents.Length; i++)
        {
            ref var agent = ref agents[i];
            if (!agent.IsAlive) continue;
            // A body deliberately given no speed budget is holding ground on purpose —
            // a wall unit — and must not be handed one by a slider.
            if (agent.MaximumSpeed > 0f) agent.MaximumSpeed = MaximumSpeed;
            agent.Acceleration = Acceleration;
            agent.Deceleration = Deceleration;
            agent.MaximumTurnSpeed = MaximumTurnSpeed;
        }
    }
}

/// <summary>
/// Per-frame readout of how the crowd is actually doing, to sit beside the sliders.
/// </summary>
/// <remarks>
/// The same quantities the headless benchmark reports, computed live, so a slider can be
/// judged against them rather than against an impression. `red` is
/// <c>StuckSeconds &gt; 0.35</c>, exactly as the renderer draws it, and `pile` is the
/// largest cluster of red bodies within 1.2 m — thirty units spread over four exits and
/// ten wedged in one corner are the same headcount and very different pictures.
/// </remarks>
internal sealed class CrowdMetrics
{
    private const float RedStuckSeconds = 0.35f;
    private const float PileConnectRadius = 1.2f;

    private readonly List<System.Numerics.Vector2> red = new();
    private readonly List<int> frontier = new();

    public int Moving { get; private set; }
    public int Red { get; private set; }
    public int DeepestPile { get; private set; }
    public float MeanSpeed { get; private set; }
    public float WorstStuckSeconds { get; private set; }

    public void Sample(SimulationWorld world)
    {
        red.Clear();
        Moving = 0;
        var speedSum = 0f;
        var worstStuck = 0f;
        foreach (ref readonly var agent in world.Agents.All)
        {
            if (!agent.IsAlive || !agent.HasDestination) continue;
            Moving++;
            speedSum += agent.Velocity.Length();
            worstStuck = MathF.Max(worstStuck, agent.StuckSeconds);
            if (agent.StuckSeconds > RedStuckSeconds) red.Add(agent.Position);
        }
        Red = red.Count;
        MeanSpeed = Moving == 0 ? 0f : speedSum / Moving;
        WorstStuckSeconds = worstStuck;
        DeepestPile = LargestCluster();
    }

    public void Report(DebugContext debug)
    {
        debug.Values.Value("moving", Moving);
        debug.Values.Value("red", Red);
        debug.Values.Value("deepest-pile", DeepestPile);
        debug.Values.Value("mean-speed", MathF.Round(MeanSpeed, 2));
        debug.Values.Value("worst-stuck", MathF.Round(WorstStuckSeconds, 2));
        debug.Stats.Gauge("crowd/red", Red);
        debug.Stats.Gauge("crowd/deepest-pile", DeepestPile);
        debug.Stats.Gauge("crowd/mean-speed", MeanSpeed);
    }

    private int LargestCluster()
    {
        if (red.Count == 0) return 0;
        var visited = new bool[red.Count];
        var radiusSquared = PileConnectRadius * PileConnectRadius;
        var largest = 0;
        for (var seed = 0; seed < red.Count; seed++)
        {
            if (visited[seed]) continue;
            visited[seed] = true;
            frontier.Clear();
            frontier.Add(seed);
            var size = 0;
            while (frontier.Count > 0)
            {
                var current = frontier[^1];
                frontier.RemoveAt(frontier.Count - 1);
                size++;
                for (var other = 0; other < red.Count; other++)
                {
                    if (visited[other]) continue;
                    if (System.Numerics.Vector2.DistanceSquared(red[current], red[other]) >
                        radiusSquared)
                    {
                        continue;
                    }
                    visited[other] = true;
                    frontier.Add(other);
                }
            }
            largest = Math.Max(largest, size);
        }
        return largest;
    }
}

/// <summary>
/// Reciprocal-velocity avoidance, live. See <see cref="ReciprocalVelocitySolver"/> for what
/// each value is for and what it cost to arrive at — the documentation stays with the value
/// rather than moving here, and these are proxies onto it.
/// </summary>
internal sealed class AvoidanceSettings
{
    [Tune(0.5, 8.0, Label = "neighbour range (m)", Group = "Avoidance")]
    public float NeighborDistance
    {
        get => ReciprocalVelocitySolver.NeighborDistance;
        set => ReciprocalVelocitySolver.NeighborDistance = value;
    }

    [Tune(0.0, 0.9, Label = "velocity commitment", Group = "Avoidance")]
    public float VelocityCommitment
    {
        get => ReciprocalVelocitySolver.VelocityCommitment;
        set => ReciprocalVelocitySolver.VelocityCommitment = value;
    }

    [Tune(0.1, 3.0, Label = "horizon: movers (s)", Group = "Avoidance")]
    public float TimeHorizon
    {
        get => ReciprocalVelocitySolver.TimeHorizon;
        set => ReciprocalVelocitySolver.TimeHorizon = value;
    }

    [Tune(0.05, 2.0, Label = "horizon: standing (s)", Group = "Avoidance")]
    public float StationaryTimeHorizon
    {
        get => ReciprocalVelocitySolver.StationaryTimeHorizon;
        set => ReciprocalVelocitySolver.StationaryTimeHorizon = value;
    }

    [Tune(0.05, 1.5, Label = "horizon: walls (s)", Group = "Avoidance")]
    public float StaticTimeHorizon
    {
        get => ReciprocalVelocitySolver.StaticTimeHorizon;
        set => ReciprocalVelocitySolver.StaticTimeHorizon = value;
    }

    [Tune(-0.10, 0.20, Label = "wall clearance (m)", Group = "Avoidance")]
    public float StaticSeparationMargin
    {
        get => ReciprocalVelocitySolver.StaticSeparationMargin;
        set => ReciprocalVelocitySolver.StaticSeparationMargin = value;
    }

    [Tune(0.0, 4.5, Label = "idle yield speed (m/s)", Group = "Avoidance")]
    public float IdleYieldSpeed
    {
        get => ReciprocalVelocitySolver.IdleYieldSpeed;
        set => ReciprocalVelocitySolver.IdleYieldSpeed = value;
    }

    [Tune(0.033, 1.0, Label = "overlap recovery (s)", Group = "Avoidance")]
    public float OverlapRecoverySeconds
    {
        get => ReciprocalVelocitySolver.OverlapRecoverySeconds;
        set => ReciprocalVelocitySolver.OverlapRecoverySeconds = value;
    }

    [Tune(0.0, 0.15, Label = "contact margin (m)", Group = "Avoidance")]
    public float ContactSeparationMargin
    {
        get => ReciprocalVelocitySolver.ContactSeparationMargin;
        set => ReciprocalVelocitySolver.ContactSeparationMargin = value;
    }
}

/// <summary>Positional depenetration, live. See <see cref="CollisionSystem"/>.</summary>
internal sealed class ContactSettings
{
    [Tune(1, 10, Label = "relaxation passes", Group = "Contact")]
    public int RelaxationPasses
    {
        get => CollisionSystem.RelaxationPasses;
        set => CollisionSystem.RelaxationPasses = value;
    }

    [Tune(0.2, 1.0, Label = "relaxation factor", Group = "Contact")]
    public float RelaxationFactor
    {
        get => CollisionSystem.RelaxationFactor;
        set => CollisionSystem.RelaxationFactor = value;
    }

    [Tune(0.5, 1.0, Label = "yield share", Group = "Contact")]
    public float YieldShare
    {
        get => CollisionSystem.YieldShare;
        set => CollisionSystem.YieldShare = value;
    }

    [Tune(0.0, 1.0, Label = "sidestep bias", Group = "Contact")]
    public float YieldLateralBias
    {
        get => CollisionSystem.YieldLateralBias;
        set => CollisionSystem.YieldLateralBias = value;
    }

    [Tune(0.001, 0.10, Label = "sidestep cutoff (m)", Group = "Contact")]
    public float YieldLateralOverlapLimit
    {
        get => CollisionSystem.YieldLateralOverlapLimit;
        set => CollisionSystem.YieldLateralOverlapLimit = value;
    }
}

/// <summary>Backpressure field, live. See <see cref="CongestionField"/>.</summary>
internal sealed class CongestionSettings
{
    [Tune(0.3, 10.0, Label = "fade (s)", Group = "Congestion")]
    public float DecaySeconds
    {
        get => CongestionField.DecaySeconds;
        set => CongestionField.DecaySeconds = value;
    }

    [Tune(0.1, 5.0, Label = "deposit gain", Group = "Congestion")]
    public float DepositGain
    {
        get => CongestionField.DepositGain;
        set => CongestionField.DepositGain = value;
    }

    [Tune(0.0, 1.0, Label = "open-ground share", Group = "Congestion")]
    public float OpenGroundShare
    {
        get => CongestionField.OpenGroundShare;
        set => CongestionField.OpenGroundShare = value;
    }

    [Tune(0.1, 1.5, Label = "with-flow factor", Group = "Congestion")]
    public float FollowingFactor
    {
        get => CongestionField.FollowingFactor;
        set => CongestionField.FollowingFactor = value;
    }

    [Tune(1.0, 4.0, Label = "against-flow factor", Group = "Congestion")]
    public float OpposingFactor
    {
        get => CongestionField.OpposingFactor;
        set => CongestionField.OpposingFactor = value;
    }
}

/// <summary>Route cost, live. All in seconds unless named otherwise. See PathService.</summary>
internal sealed class RoutingSettings
{
    // In cells rather than seconds now: what a queue costs is how many bodies are ahead
    // times how long one takes to clear a cell, and the second half of that is a property of
    // how fast the body walks. A slider in seconds meant this quietly re-tuned itself every
    // time anybody touched the speed slider above it.
    [Tune(0.0, 12.0, Label = "congestion cells/pressure", Group = "Routing")]
    public float CongestionCellsPerPressure
    {
        get => PathService.CongestionCellsPerPressure;
        set => PathService.CongestionCellsPerPressure = value;
    }

    [Tune(0.0, 12.0, Label = "detour bubble (s)", Group = "Routing")]
    public float DetourAvoidanceSeconds
    {
        get => PathService.DetourAvoidanceSeconds;
        set => PathService.DetourAvoidanceSeconds = value;
    }

    [Tune(0.0, 4.0, Label = "bottleneck reservation (s)", Group = "Routing")]
    public float BottleneckReservationSeconds
    {
        get => PathService.BottleneckReservationSeconds;
        set => PathService.BottleneckReservationSeconds = value;
    }

    [Tune(0.0, 3.0, Label = "climb (s/m)", Group = "Routing")]
    public float ClimbSecondsPerMetre
    {
        get => PathService.ClimbSecondsPerMetre;
        set => PathService.ClimbSecondsPerMetre = value;
    }

    [Tune(0.5, 12.0, Label = "routed turn rate (rad/s)", Group = "Routing")]
    public float ReferenceTurnSpeed
    {
        get => PathService.ReferenceTurnSpeed;
        set => PathService.ReferenceTurnSpeed = value;
    }

    [Tune(4.0, 200.0, Label = "blocked-cell barrier (cells)", Group = "Routing")]
    public float BlockedFlowCells
    {
        get => PathService.BlockedFlowCells;
        set => PathService.BlockedFlowCells = value;
    }

    [Tune(0.0, 4.0, Label = "gap line-up standoff (m)", Group = "Routing")]
    public float ApertureApproachStandoff
    {
        get => PathService.ApertureApproachStandoff;
        set => PathService.ApertureApproachStandoff = value;
    }

    [Tune(1.0, 12.0, Label = "aperture search (m)", Group = "Routing")]
    public float ApertureSearchDistance
    {
        get => PathService.ApertureSearchDistance;
        set => PathService.ApertureSearchDistance = value;
    }
}

/// <summary>Group transit and recovery, live. See <see cref="SimulationWorld"/>.</summary>
internal sealed class GroupSettings
{
    [Tune(0.0, 6.0, Label = "station-keeping gain", Group = "Group")]
    public float FormationKeepingGain
    {
        get => SimulationWorld.FormationKeepingGain;
        set => SimulationWorld.FormationKeepingGain = value;
    }

    [Tune(0.0, 1.0, Label = "station-keeping speed cap", Group = "Group")]
    public float FormationLateralSpeedFraction
    {
        get => SimulationWorld.FormationLateralSpeedFraction;
        set => SimulationWorld.FormationLateralSpeedFraction = value;
    }

    [Tune(0.02, 1.0, Label = "flow smoothing", Group = "Group")]
    public float FlowSmoothing
    {
        get => SimulationWorld.FlowSmoothing;
        set => SimulationWorld.FlowSmoothing = value;
    }

    [Tune(0.0, 4.0, Label = "corner blend range (m)", Group = "Group")]
    public float CornerBlendDistance
    {
        get => SimulationWorld.CornerBlendDistance;
        set => SimulationWorld.CornerBlendDistance = value;
    }

    [Tune(0.0, 1.0, Label = "corner blend strength", Group = "Group")]
    public float CornerBlendStrength
    {
        get => SimulationWorld.CornerBlendStrength;
        set => SimulationWorld.CornerBlendStrength = value;
    }

    [Tune(0.0, 3.0, Label = "route adoption stagger (s)", Group = "Group")]
    public float MaximumRouteAdoptionDelay
    {
        get => SimulationWorld.MaximumRouteAdoptionDelay;
        set => SimulationWorld.MaximumRouteAdoptionDelay = value;
    }

    [Tune(0.0, 10.0, Label = "route commitment (s)", Group = "Group")]
    public float RouteCommitmentSeconds
    {
        get => SimulationWorld.RouteCommitmentSeconds;
        set => SimulationWorld.RouteCommitmentSeconds = value;
    }

    [Tune(0.1, 5.0, Label = "commitment release stall (s)", Group = "Group")]
    public float RouteReconsiderStallSeconds
    {
        get => SimulationWorld.RouteReconsiderStallSeconds;
        set => SimulationWorld.RouteReconsiderStallSeconds = value;
    }
}

/// <summary>Stuck-body recovery, live. See <see cref="SimulationWorld"/>.</summary>
internal sealed class RecoverySettings
{
    [Tune(0.1, 6.0, Label = "detour stall threshold (s)", Group = "Recovery")]
    public float CongestionRecoveryStallSeconds
    {
        get => SimulationWorld.CongestionRecoveryStallSeconds;
        set => SimulationWorld.CongestionRecoveryStallSeconds = value;
    }

    [Tune(0.033, 3.0, Label = "detour interval (s)", Group = "Recovery")]
    public float CongestionRecoveryInterval
    {
        get => SimulationWorld.CongestionRecoveryInterval;
        set => SimulationWorld.CongestionRecoveryInterval = value;
    }

    [Tune(0.2, 8.0, Label = "abandon gap after (s)", Group = "Recovery")]
    public float ApertureAbandonSeconds
    {
        get => SimulationWorld.ApertureAbandonSeconds;
        set => SimulationWorld.ApertureAbandonSeconds = value;
    }

    [Tune(0.5, 15.0, Label = "abandon held for (s)", Group = "Recovery")]
    public float ApertureAbandonHoldSeconds
    {
        get => SimulationWorld.ApertureAbandonHoldSeconds;
        set => SimulationWorld.ApertureAbandonHoldSeconds = value;
    }
}
