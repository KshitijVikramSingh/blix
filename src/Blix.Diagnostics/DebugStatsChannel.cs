namespace Blix.Diagnostics;

// Typed counters and gauges. Producers push values; the channel aggregates
// eagerly by path so a snapshot of Entries at any moment reflects the
// running total (Count) or most recent value (Gauge). Insertion order is
// preserved via a parallel list of paths — Dictionary<string, T> order is
// implementation-defined on a freshly added entry under heavy churn, and
// stable UI rows matter more than the microsecond saved by collapsing.
//
// Aggregation rules:
//   Count(path, delta)  : entries[path].Value += delta
//   Gauge(path, value)  : entries[path].Value  = value
//
// A path may not switch kinds within a frame — mixing Count and Gauge for
// the same name is a producer bug (would silently misreport summed vs
// last-write semantics). We throw on the second call rather than degrade.
public sealed class DebugStatsChannel
{
    private readonly DebugContext context;
    private readonly Dictionary<string, int> indexByPath = new(StringComparer.Ordinal);
    private readonly List<DebugStatEntry> entries = new();

    internal DebugStatsChannel(DebugContext context)
    {
        this.context = context;
    }

    public IReadOnlyList<DebugStatEntry> Entries => entries;

    // Accumulate a count delta on the current scope's path/name. Repeated
    // calls sum. delta may be negative (e.g. "draws after culling" reduces).
    public void Count(string name, long delta)
    {
        Accumulate(name, DebugStatKind.Count, delta);
    }

    // Shorthand for the very hot path-callsites (Phase 3 push hook) where
    // every recorded draw bumps "draws" by one. Avoids the call-site
    // boilerplate of `Count(name, 1)`.
    public void Increment(string name)
    {
        Accumulate(name, DebugStatKind.Count, 1.0);
    }

    // Snapshot of an instantaneous value. Last write wins.
    public void Gauge(string name, double value)
    {
        Assign(name, DebugStatKind.Gauge, value);
    }

    private void Accumulate(string name, DebugStatKind kind, double delta)
    {
        var path = context.BuildPath(name);
        if (indexByPath.TryGetValue(path, out var existingIndex))
        {
            var existing = entries[existingIndex];
            EnsureKindMatches(existing, kind);
            entries[existingIndex] = existing with { Value = existing.Value + delta };
            return;
        }

        indexByPath[path] = entries.Count;
        entries.Add(new DebugStatEntry(path, context.CurrentScope, name, kind, delta));
    }

    private void Assign(string name, DebugStatKind kind, double value)
    {
        var path = context.BuildPath(name);
        if (indexByPath.TryGetValue(path, out var existingIndex))
        {
            var existing = entries[existingIndex];
            EnsureKindMatches(existing, kind);
            entries[existingIndex] = existing with { Value = value };
            return;
        }

        indexByPath[path] = entries.Count;
        entries.Add(new DebugStatEntry(path, context.CurrentScope, name, kind, value));
    }

    private static void EnsureKindMatches(DebugStatEntry existing, DebugStatKind requested)
    {
        if (existing.Kind != requested)
        {
            throw new InvalidOperationException(
                $"Stat '{existing.Path}' was first recorded as {existing.Kind} but a {requested} call followed. " +
                "A path may not mix kinds within a frame.");
        }
    }
}
