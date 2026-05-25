namespace Blix.Graphics;

// Backend-agnostic surface for occlusion-query infrastructure. The GL
// backend implements this; render-layer occluders (Blix.Render) consume
// it through this interface so they don't reach into a concrete device.
//
// Lifecycle: AllocateOcclusionQuery returns a handle; the caller wraps
// drawing in pass.BeginOcclusionQuery(id) / pass.EndOcclusionQuery();
// next frame, TryGetOcclusionResult polls for the boolean "any sample
// passed" result. When the caller no longer needs the handle (e.g. the
// tracked entity has gone away), ReleaseOcclusionQuery returns it to
// the pool.
//
// OcclusionQuerySupported gates the whole subsystem — on a backend
// without the relevant extension, allocate returns 0 (a sentinel
// meaning "skip query"), Try* returns false forever, and Release is
// a no-op. Callers can use the supported flag to bypass query setup
// entirely on those platforms.
public interface IOcclusionQueryProvider
{
    bool OcclusionQuerySupported { get; }
    int AllocateOcclusionQuery();
    void ReleaseOcclusionQuery(int queryId);
    bool TryGetOcclusionResult(int queryId, out bool anySamplesPassed);
}
