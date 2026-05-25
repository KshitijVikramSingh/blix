namespace Blix.Graphics;

// Side-channel hook that lets external code observe command recording
// without coupling the graphics layer to any particular diagnostics or
// profiling system. Implementations receive begin/end notifications for
// each named render pass and one OnDraw per draw command.
//
// Recording-time (not GPU-time) is the intentional design point: counting
// draws + triangles per pass is a CPU-side concern, and tagging it at the
// point of recording is cheaper and simpler than instrumenting the
// graphics device. GPU timing is a separate concern (Phase 7) and lives
// behind the same seam — a GPU-aware recorder can be plugged in later
// without changing this interface or its call sites.
//
// Lifecycle contract:
//   - OnPassBegin(name) precedes any OnDraw calls attributable to that
//     pass; OnPassEnd must follow.
//   - OnDraw between OnPassEnd and a subsequent OnPassBegin is a bug in
//     the dispatcher and a recorder may treat it as unscoped.
//   - Recorders run on the GL thread, synchronous with command recording.
public interface IFrameRecorder
{
    void OnPassBegin(string passName);

    void OnPassEnd();

    void OnDraw(in DrawIndexedCommand command);
}
