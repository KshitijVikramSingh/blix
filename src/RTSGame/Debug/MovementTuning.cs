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

    /// <summary>Pushes the current values onto the defaults and every live body.</summary>
    /// <remarks>
    /// Free-turn speed used to be a slider beside these and is now an eighth of top speed,
    /// because that is what it always was — written as 0.55 m/s against a body doing 4.5, it
    /// would have become a third of a walk if left alone. The routing layer's turn rate
    /// follows this one for the same reason: two numbers that are required to agree are not
    /// two decisions.
    /// </remarks>
    public void Apply(SimulationWorld world)
    {
        AgentDefaults.Acceleration = Acceleration;
        AgentDefaults.Deceleration = Deceleration;
        AgentDefaults.MaximumTurnSpeed = MaximumTurnSpeed;
        AgentDefaults.FreeTurnSpeed = MaximumSpeed * 0.1222f;
        PathService.ReferenceTurnSpeed = MaximumTurnSpeed;

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


    [Tune(0.0, 3.0, Label = "climb (s/m)", Group = "Routing")]
    public float ClimbSecondsPerMetre
    {
        get => PathService.ClimbSecondsPerMetre;
        set => PathService.ClimbSecondsPerMetre = value;
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

}

