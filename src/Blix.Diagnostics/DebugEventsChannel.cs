using System.Diagnostics;

namespace Blix.Diagnostics;

// Episodic event channel. Producers push timestamped messages with an
// optional structured payload; the channel preserves order and does not
// aggregate. A frame with no events has an empty Entries list.
//
// Path resolution is identical to other channels: each event captures
// the current scope at emit time. Sinks filter or route by path (e.g.
// ConsoleEventSink prints "assets/..." events to stdout).
//
// The Stopwatch reference comes from DebugSystem so events across
// frames share a single monotonic time axis — useful for sinks that
// build timeline visualizations or for correlating two events that
// straddle a frame boundary.
public sealed class DebugEventsChannel
{
    private readonly DebugContext context;
    private readonly Stopwatch clock;
    private readonly List<DebugEventEntry> entries = new();

    internal DebugEventsChannel(DebugContext context, Stopwatch clock)
    {
        this.context = context;
        this.clock = clock;
    }

    public IReadOnlyList<DebugEventEntry> Entries => entries;

    public void Info(string message, object? payload = null)
    {
        Emit(DebugEventSeverity.Info, message, payload);
    }

    public void Warn(string message, object? payload = null)
    {
        Emit(DebugEventSeverity.Warn, message, payload);
    }

    public void Error(string message, object? payload = null)
    {
        Emit(DebugEventSeverity.Error, message, payload);
    }

    private void Emit(DebugEventSeverity severity, string message, object? payload)
    {
        ArgumentNullException.ThrowIfNull(message);
        entries.Add(new DebugEventEntry(
            Path: context.CurrentScope,
            Scope: context.CurrentScope,
            Severity: severity,
            Message: message,
            Payload: payload,
            TimestampMs: clock.Elapsed.TotalMilliseconds));
    }
}
