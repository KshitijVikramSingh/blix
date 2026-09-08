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
    /// <summary>
    /// Extra quarter turns applied to every rigged body, on a slider.
    /// </summary>
    /// <remarks>
    /// <b>A dial because it could not be derived, and I tried three times.</b> Which way a character faces
    /// in its own space is a fact about the asset: ankle-to-toe gives the answer to within a splay angle,
    /// and both signs of that reading were reported wrong from the chair — the last as bodies walking away
    /// from where they were going. A number nobody can settle by measurement should be visibly a question
    /// rather than quietly indistinguishable from an answer, which is the rule the look dials already
    /// follow. Turn it while a villager walks; the value that works goes in the asset's notes.
    /// <para>
    /// <b>It sits at zero, and that is the point of having had it.</b> The measurement was right all along —
    /// what was wrong was the draw turning bodies to face the steering heading rather than their velocity.
    /// Once that was fixed the dial settled back to nothing, which is the cleanest evidence available that
    /// the remaining derivation is sound. It stays for the next asset whose rest pose is not axis-aligned.
    /// </para>
    /// </remarks>
    [Tune(0.0, 3.0, Label = "body yaw (quarter turns)", Group = "bodies")]
    public float YawQuarters;

    [Tune(0.5, 12.0, Label = "top speed (m/s)")]
    public float MaximumSpeed = AgentDefaults.MaximumSpeed;

    [Tune(0.5, 24.0, Label = "acceleration (m/s2)")]
    public float Acceleration = AgentDefaults.Acceleration;

    [Tune(0.5, 32.0, Label = "deceleration (m/s2)")]
    public float Deceleration = AgentDefaults.Deceleration;

    [Tune(0.5, 12.0, Label = "turn rate (rad/s)")]
    public float MaximumTurnSpeed = AgentDefaults.MaximumTurnSpeed;

    /// <summary>How fast a body with a turning circle may come round while barely moving.</summary>
    /// <remarks>
    /// This decides what a wagon reversing looks like, and there is no headless measure that
    /// settles it. Low, and it must roll to turn, so it swings wide — and risks sitting pointed the
    /// wrong way. High, and it slows to a crawl and pivots, which is what it does at the shipped
    /// value: asked for a 3.4 m circle it traces 0.88 m, because by then it is doing 0.3 m/s. What
    /// the shipped value does buy is the part that matters at range — 6.4 s to come about against a
    /// villager's 1.3.
    /// </remarks>
    [Tune(0.02, 2.0, Label = "wagon pivot floor (rad/s)")]
    public float PivotTurnSpeed = LocalSteeringSystem.PivotTurnSpeed;

    /// <summary>Pushes the current values onto the defaults and every live body.</summary>
    /// <remarks>
    /// Free-turn speed used to be a slider beside these and is now a share of top speed,
    /// because that is what it always was — written as 0.55 m/s against a body doing 4.5, it
    /// would have become a third of a walk if left alone. The routing layer's turn rate
    /// follows this one for the same reason: two numbers that are required to agree are not
    /// two decisions.
    /// <para>
    /// These <em>scale</em> a body rather than replacing it. They used to assign, which was right
    /// while every unit was the same body and became wrong the moment a roster existed: dragging
    /// top speed would have flattened a cart, a scout and a wagon into one pace and there would
    /// have been no way back short of a restart. Held at the defaults every ratio is one, so a
    /// world nobody has touched the sliders in is bit-identical.
    /// </para>
    /// </remarks>
    public void Apply(SimulationWorld world)
    {
        AgentDefaults.Acceleration = Acceleration;
        AgentDefaults.Deceleration = Deceleration;
        AgentDefaults.MaximumTurnSpeed = MaximumTurnSpeed;
        LocalSteeringSystem.PivotTurnSpeed = PivotTurnSpeed;
        PathService.ReferenceTurnSpeed = MaximumTurnSpeed;

        var speedRatio = MaximumSpeed / AgentDefaults.MaximumSpeed;
        var turnRatio = MaximumTurnSpeed / defaultTurnSpeed;
        var agents = world.Agents.MutableSpan();
        for (var i = 0; i < agents.Length; i++)
        {
            ref var agent = ref agents[i];
            if (!agent.IsAlive) continue;
            // A body deliberately given no speed budget is holding ground on purpose —
            // a wall unit — and must not be handed one by a slider.
            if (agent.MaximumSpeed > 0f) agent.MaximumSpeed = baseSpeeds[agent.Id.Value] * speedRatio;
            agent.Acceleration = Acceleration;
            agent.Deceleration = Deceleration;
            agent.MaximumTurnSpeed = baseTurnSpeeds[agent.Id.Value] * turnRatio;
        }
    }

    /// <summary>What each body was spawned with, so the sliders scale rather than overwrite.</summary>
    /// <remarks>
    /// Indexed by agent id, which is never reused, so a despawned unit's entry is simply never
    /// read again. Captured on first sight rather than at spawn because this is a debug overlay and
    /// must not put a hook in the spawn path to exist.
    /// </remarks>
    private float[] baseSpeeds = Array.Empty<float>();
    private float[] baseTurnSpeeds = Array.Empty<float>();
    private int observed;
    private readonly float defaultTurnSpeed = AgentDefaults.MaximumTurnSpeed;

    /// <summary>Records any body seen for the first time at whatever it was built with.</summary>
    public void Observe(SimulationWorld world)
    {
        var agents = world.Agents.All;
        if (baseSpeeds.Length < agents.Length)
        {
            Array.Resize(ref baseSpeeds, Math.Max(agents.Length, 64));
            Array.Resize(ref baseTurnSpeeds, Math.Max(agents.Length, 64));
        }

        // Counted separately from the array's capacity: ids are slot indices, so a world that
        // grows to five bodies inside an array sized for sixty-four would otherwise never record
        // anything after the first frame, and every unit spawned later would scale from zero.
        for (; observed < agents.Length; observed++)
        {
            baseSpeeds[observed] = agents[observed].MaximumSpeed;
            baseTurnSpeeds[observed] = agents[observed].MaximumTurnSpeed;
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
/// <summary>
/// How bodies treat walls. Back on the panel after the audit took it off.
/// </summary>
/// <remarks>
/// The audit judged these settled, on the grounds that the locomotion work had measured them
/// and recorded why. The first session of real play on a real map disagreed: wall-hugging came
/// back, and the reason none of the dials touched it was that the dials that govern it were
/// no longer there. Settled by measurement on a 30 m fixture is not the same as settled, and
/// this is the correction.
/// </remarks>
internal sealed class WallSettings
{
    [Tune(0.05, 4.0, Label = "horizon: walls (s)", Group = "Walls")]
    public float StaticTimeHorizon
    {
        get => ReciprocalVelocitySolver.StaticTimeHorizon;
        set => ReciprocalVelocitySolver.StaticTimeHorizon = value;
    }

    [Tune(-0.10, 0.40, Label = "wall standoff (m)", Group = "Walls")]
    public float StaticSeparationMargin
    {
        get => ReciprocalVelocitySolver.StaticSeparationMargin;
        set => ReciprocalVelocitySolver.StaticSeparationMargin = value;
    }

    [Tune(0.0, 4.0, Label = "neighbour lookahead (m)", Group = "Walls")]
    public float NeighborLookahead
    {
        get => ReciprocalVelocitySolver.NeighborLookahead;
        set => ReciprocalVelocitySolver.NeighborLookahead = value;
    }
}

internal sealed class RoutingSettings
{
    /// <summary>How much a body's own speed changes what a queue is worth to it.</summary>
    /// <remarks>
    /// One means a scout values a twenty-second jam at twice what the reference body does, because
    /// it eats twice as much of its journey; zero means everybody prices a queue identically, which
    /// is what the router did while every unit walked at the same pace. See
    /// <c>PathService.CongestionSpeedScaling</c> for the sweep — the arithmetic is unambiguous and
    /// the clock is not.
    /// </remarks>
    /// <summary>How much a body's own width changes what a queue is worth to it.</summary>
    /// <remarks>
    /// Measured through one 3 m gap behind thirty villagers: a 0.55 m cart lost 0.2 s to it and a
    /// 0.90 m body 16.2 s. A wide body waits for a hole it fits through and most of the holes in a
    /// queue of narrow bodies are not it.
    /// </remarks>
    [Tune(0.0, 1.0, Label = "congestion by unit width", Group = "Routing")]
    public float CongestionSizeScaling
    {
        get => PathService.CongestionSizeScaling;
        set => PathService.CongestionSizeScaling = value;
    }

    [Tune(0.0, 1.0, Label = "congestion by unit speed", Group = "Routing")]
    public float CongestionSpeedScaling
    {
        get => PathService.CongestionSpeedScaling;
        set => PathService.CongestionSpeedScaling = value;
    }

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


    /// <summary>Standoff at which a body lines up on a gap's axis before entering it.</summary>
    /// <remarks>
    /// <b>The slider its own documentation said it shipped as, and did not.</b>
    /// <c>PathService.ApertureApproachStandoff</c> ends on "it ships as a slider rather than as a decision,
    /// because whoever is watching can weigh it and the benchmark cannot" — and no slider was ever wired, so
    /// the field sat at zero, its first line short-circuited on <c>&lt;= 0f</c>, and the whole feature was
    /// unreachable. The compiler had been saying so in every build: <em>never assigned to, and will always
    /// have its default value</em>.
    /// <para>
    /// Still zero by default, so nothing about how bodies move changes here. What changes is that the trade
    /// the remarks describe — a cleaner approach angle against stall time and steering stability — can now
    /// actually be weighed by somebody watching, which was the stated plan.
    /// </para>
    /// </remarks>
    [Tune(0.0, 3.0, Label = "aperture standoff (m)", Group = "Routing")]
    public float ApertureApproachStandoff
    {
        get => PathService.ApertureApproachStandoff;
        set => PathService.ApertureApproachStandoff = value;
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

