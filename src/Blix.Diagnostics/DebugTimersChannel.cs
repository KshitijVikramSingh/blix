using System.Diagnostics;

namespace Blix.Diagnostics;

// CPU span timing channel.
//
//   using (ctx.Timers.Measure("Opaque")) { BuildOpaquePass(); }
//
// The returned IDisposable captures Stopwatch.GetTimestamp on construction
// and records elapsed milliseconds on Dispose. Nesting works naturally
// because each token holds its own start tick — there is no implicit
// timer stack, so two siblings or a child-inside-parent both produce
// independent entries.
//
// Aggregation is by full path: a second Measure call resolving to the
// same path accumulates into the existing entry (TotalMs += elapsed,
// CallCount += 1) instead of producing a duplicate row. This is what
// the Phase 3 push hook will need when 100 sub-mesh draws each measure
// a "DrawIndexed" span — one summary row, not 100.
//
// AppendCompleted is the system-side back door: DebugSystem uses it to
// record the frame-level timer that it owns end-to-end and that no user
// code can wrap in a using-block. Not exposed publicly.
public sealed class DebugTimersChannel
{
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private readonly DebugContext context;
    private readonly Dictionary<string, int> indexByPath = new(StringComparer.Ordinal);
    private readonly List<DebugTimerEntry> entries = new();

    internal DebugTimersChannel(DebugContext context)
    {
        this.context = context;
    }

    public IReadOnlyList<DebugTimerEntry> Entries => entries;

    public IDisposable Measure(string name)
    {
        var path = context.BuildPath(name);
        var scope = context.CurrentScope;
        return new MeasureToken(this, path, scope, name, Stopwatch.GetTimestamp());
    }

    // Push a pre-measured duration from outside the using-block path.
    // Used by DebugSystem to inject the frame-level CPU timer (which spans
    // BeginFrame -> EndFrame and can't be wrapped in a using) and by the
    // runtime to push asynchronous results — e.g. Phase 7's GPU pass
    // timings, which become available 1–N frames after the work ran.
    //
    // Aggregation matches Measure(): same path -> TotalMs sums and
    // CallCount increments, so multiple late results for the same pass
    // collapse into one row instead of producing duplicates.
    public void AppendCompleted(string name, string scope, double durationMs)
    {
        var path = string.IsNullOrEmpty(scope) ? name : $"{scope}/{name}";
        Record(path, scope, name, durationMs);
    }

    private void Record(string path, string scope, string name, double durationMs)
    {
        if (indexByPath.TryGetValue(path, out var existingIndex))
        {
            var existing = entries[existingIndex];
            entries[existingIndex] = existing with
            {
                TotalMs = existing.TotalMs + durationMs,
                CallCount = existing.CallCount + 1
            };
            return;
        }

        indexByPath[path] = entries.Count;
        entries.Add(new DebugTimerEntry(path, scope, name, durationMs, CallCount: 1));
    }

    private sealed class MeasureToken : IDisposable
    {
        private readonly DebugTimersChannel channel;
        private readonly string path;
        private readonly string scope;
        private readonly string name;
        private readonly long startTicks;
        private bool disposed;

        public MeasureToken(DebugTimersChannel channel, string path, string scope, string name, long startTicks)
        {
            this.channel = channel;
            this.path = path;
            this.scope = scope;
            this.name = name;
            this.startTicks = startTicks;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            var elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
            var elapsedMs = elapsedTicks * TicksToMs;
            channel.Record(path, scope, name, elapsedMs);
        }
    }
}
