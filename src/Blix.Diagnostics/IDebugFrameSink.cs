namespace Blix.Diagnostics;

// A consumer of finished frames. Sinks are registered with DebugSystem
// and invoked once per frame after the snapshot has been pushed into
// the history ring, before Current is cleared. This means a sink can
// safely re-read History during Consume (e.g. compute a moving average)
// and can hand the DebugFrame off to background work — the frame is
// immutable once produced.
//
// Sinks must not mutate the DebugFrame (it's exposed as IReadOnlyList
// everywhere — but the contract is also documented here). Sinks should
// be cheap; expensive work (JSON serialization, network I/O) should be
// offloaded by the sink onto its own thread, since Consume runs on the
// GL thread.
public interface IDebugFrameSink
{
    void Consume(DebugFrame frame);
}
