namespace Blix.Diagnostics;

// Prints Events at or above MinSeverity to stderr, one line per event.
// Errors and warnings stop being silent; Info events are off by default
// because they tend to be high-volume (per-frame upload reports, etc.).
//
// stderr (Console.Error) is chosen so the log stream is separable from
// any stdout pipeline output a demo or CLI tool produces. The format is
// intentionally machine-grep-friendly:
//
//   [WARN ] uploader: drain budget exceeded by 5.2ms
//
// Sinks should be cheap; this one is synchronous on the GL thread. If
// the console is redirected to a slow target the per-frame cost adds
// up — that's acceptable for a development tool, and bracket-quoting
// keeps multi-line messages parseable.
public sealed class ConsoleEventSink : IDebugFrameSink
{
    private readonly DebugEventSeverity minSeverity;
    private readonly TextWriter writer;

    public ConsoleEventSink()
        : this(DebugEventSeverity.Warn, Console.Error)
    {
    }

    public ConsoleEventSink(DebugEventSeverity minSeverity)
        : this(minSeverity, Console.Error)
    {
    }

    public ConsoleEventSink(DebugEventSeverity minSeverity, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        this.minSeverity = minSeverity;
        this.writer = writer;
    }

    public void Consume(DebugFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        for (var i = 0; i < frame.Events.Count; i++)
        {
            var entry = frame.Events[i];
            if (entry.Severity < minSeverity)
            {
                continue;
            }

            var path = string.IsNullOrEmpty(entry.Path) ? "<root>" : entry.Path;
            writer.WriteLine($"[{FormatSeverity(entry.Severity)}] {path}: {entry.Message}");
        }
    }

    private static string FormatSeverity(DebugEventSeverity severity) => severity switch
    {
        DebugEventSeverity.Info => "INFO ",
        DebugEventSeverity.Warn => "WARN ",
        DebugEventSeverity.Error => "ERROR",
        _ => severity.ToString()
    };
}
