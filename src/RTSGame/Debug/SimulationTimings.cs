using System.Diagnostics;

namespace RTSGame.Debug;

internal enum SimulationPhase
{
    Congestion,
    AgentIndex,
    Commands,
    Economy,
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

    /// <summary>One phase's running average, for a report that wants a number rather than the line.</summary>
    public double AverageOf(SimulationPhase phase) => counters[(int)phase].AverageMilliseconds;

    public string Format(int agentCount, long tickNumber)
    {
        var values = Enum.GetValues<SimulationPhase>()
            .Select(phase => $"{Label(phase)} {counters[(int)phase].AverageMilliseconds:F3} ms")
            .ToArray();
        return $"timings | {agentCount} agents | tick {tickNumber} | {string.Join(" | ", values)}";
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
