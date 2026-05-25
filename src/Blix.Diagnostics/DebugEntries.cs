namespace Blix.Diagnostics;

public enum DebugControlKind
{
    Boolean,
    Float,
    Enum,
    Button
}

public sealed record DebugValueEntry(
    string Path,
    string Scope,
    string Name,
    object? Value);

public sealed record DebugControlEntry(
    string Path,
    string Scope,
    string Name,
    DebugControlKind Kind,
    object Value,
    float Min = 0.0f,
    float Max = 1.0f,
    IReadOnlyList<string>? Options = null);

public enum DebugStatKind
{
    // Summable across producers / push calls within a single frame.
    // Multiple Count(path, delta) calls accumulate.
    Count,
    // Snapshot of an instantaneous value. Repeated Gauge(path, ...) calls
    // overwrite — last write wins.
    Gauge
}

// Stats are stored as double regardless of count vs gauge — a double's
// 53-bit mantissa losslessly represents counts well beyond any realistic
// per-frame draw/triangle total, and a single numeric type simplifies
// sparkline rendering and history aggregation.
public sealed record DebugStatEntry(
    string Path,
    string Scope,
    string Name,
    DebugStatKind Kind,
    double Value);

// One row per (path) per frame. TotalMs sums every Measure call that
// shared this path; CallCount records how many measures contributed.
// Reading TotalMs alone is "time spent in X this frame," reading
// TotalMs/CallCount is "average per call."
public sealed record DebugTimerEntry(
    string Path,
    string Scope,
    string Name,
    double TotalMs,
    int CallCount);

public enum DebugEventSeverity
{
    Info,
    Warn,
    Error
}

// Episodic event. Unlike Stats/Timers, events are NOT aggregated by path —
// the channel keeps them in chronological order so sinks can replay or
// filter by time. The Path string is the scope at emit time; sinks may
// match against it as a prefix.
//
// Payload is intentionally object? for forward compatibility. Payloads
// MUST be treated as immutable by producers — snapshots copy the entry
// list but do not deep-clone payload graphs. Pass records or value types.
public sealed record DebugEventEntry(
    string Path,
    string Scope,
    DebugEventSeverity Severity,
    string Message,
    object? Payload,
    double TimestampMs);
