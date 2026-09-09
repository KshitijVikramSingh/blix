using System.Diagnostics;

namespace RTSGame.Debug;

/// <summary>
/// The stages of a tick, plus two that are not stages at all.
/// </summary>
/// <remarks>
/// <b><see cref="Pathfinding"/> and <see cref="AgentIndex"/> cut across the rest and must not be summed with
/// them.</b> Both are accumulators — pathfinding adds up whatever routing happened wherever it happened, and
/// most of it happens inside <see cref="Commands"/> — so a report that adds every counter it can see gets
/// more than the tick it is describing. `--pathprofile` did exactly that and printed "named 4,259 of 2,662
/// ms" without anybody noticing, which is a total that is its own refutation. See <see cref="IsCrossCutting"/>.
/// </remarks>
internal enum SimulationPhase
{
    Congestion,
    AgentIndex,
    Commands,
    Economy,
    Knowledge,
    Threat,
    Jobs,
    Behaviors,
    NavigationRefresh,
    Pathfinding,
    PreferredVelocity,
    LocalSteering,
    Integration,
    ColliderSync,
    CollisionResolution,
    CongestionRecovery,
    TotalTick,
}

internal sealed class SimulationTimings
{
    private readonly TimingCounter[] counters =
        new TimingCounter[Enum.GetValues<SimulationPhase>().Length];

    public void Record(SimulationPhase phase, long elapsedTicks)
    {
        var milliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        counters[(int)phase].Add(milliseconds);
    }

    public void Reset() => Array.Clear(counters);

    /// <summary>Whether a phase is an accumulator laid over the others rather than a stage of the tick.</summary>
    public static bool IsCrossCutting(SimulationPhase phase) =>
        phase is SimulationPhase.Pathfinding or SimulationPhase.AgentIndex or SimulationPhase.TotalTick;

    /// <summary>One phase's running average, for a report that wants a number rather than the line.</summary>
    public double AverageOf(SimulationPhase phase) => counters[(int)phase].AverageMilliseconds;

    public string Format(int agentCount, long tickNumber)
    {
        var values = Enum.GetValues<SimulationPhase>()
            .Select(phase => $"{Label(phase)} {counters[(int)phase].AverageMilliseconds:F3} ms")
            .ToArray();
        // <b>Slots, not people, and the label now says so.</b> §187: this read "30 agents" unchanged
        // through a war that took faction 1 from seventeen people to six, because the caller passes
        // Agents.Count — the allocated slots, dead ones included — and "agents" reads as population. The
        // number is left alone so every perf figure in the plan stays comparable; only the word is fixed.
        return $"timings | {agentCount} slots | tick {tickNumber} | {string.Join(" | ", values)}";
    }

    private static string Label(SimulationPhase phase) => phase switch
    {
        SimulationPhase.Threat => "threat",
        SimulationPhase.Congestion => "congestion",
        SimulationPhase.AgentIndex => "index",
        SimulationPhase.NavigationRefresh => "nav",
        SimulationPhase.Pathfinding => "paths",
        SimulationPhase.PreferredVelocity => "preferred",
        SimulationPhase.LocalSteering => "steering",
        SimulationPhase.ColliderSync => "colliders",
        SimulationPhase.CollisionResolution => "collision",
        SimulationPhase.CongestionRecovery => "recovery",
        SimulationPhase.TotalTick => "total",
        _ => phase.ToString().ToLowerInvariant(),
    };

    private struct TimingCounter
    {
        public double AverageMilliseconds { get; private set; }
        private long samples;

        public void Add(double milliseconds)
        {
            samples++;
            var weight = samples <= 20 ? 1.0 / samples : 0.05;
            AverageMilliseconds += (milliseconds - AverageMilliseconds) * weight;
        }
    }
}
