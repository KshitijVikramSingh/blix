using OpenTK.Graphics.OpenGL4;

namespace Blix.Graphics.OpenGL;

public sealed partial class OpenGLGraphicsDevice
{
    // GL_ARB_occlusion_query2 was core in 3.3 — GL_ANY_SAMPLES_PASSED
    // returns boolean-ish results (any sample contributed). We use the
    // ANY variant rather than SAMPLES_PASSED because the cull only
    // needs "visible or not", and ANY is meaningfully cheaper on
    // tile-deferred GPUs (Apple Silicon).
    private const string OcclusionQueryExtension = "GL_ARB_occlusion_query2";

    private readonly Queue<int> occlusionQueryPool = new();
    private readonly HashSet<int> occlusionQueryAllocated = new();
    private bool occlusionQuerySupported;

    public bool OcclusionQuerySupported => occlusionQuerySupported;

    // Allocate a fresh query handle from the pool. Returns 0 if the
    // extension is unsupported (caller should treat as "skip query").
    public int AllocateOcclusionQuery()
    {
        if (!occlusionQuerySupported)
        {
            return 0;
        }
        if (occlusionQueryPool.Count > 0)
        {
            var id = occlusionQueryPool.Dequeue();
            occlusionQueryAllocated.Add(id);
            return id;
        }
        var fresh = GL.GenQuery();
        occlusionQueryAllocated.Add(fresh);
        return fresh;
    }

    // Return a query to the pool. Safe to call on a query whose result
    // hasn't been read — the next allocation will reuse the handle, and
    // the driver will overwrite the contents on next glBeginQuery.
    public void ReleaseOcclusionQuery(int queryId)
    {
        if (queryId == 0 || !occlusionQueryAllocated.Remove(queryId))
        {
            return;
        }
        occlusionQueryPool.Enqueue(queryId);
    }

    // Non-blocking read of a query result. Returns false if the result
    // isn't ready yet (driver still has the work in flight) — caller
    // should poll again next frame. When true, anySamplesPassed
    // reflects whether ANY fragment contributed during the query's
    // begin/end scope.
    public bool TryGetOcclusionResult(int queryId, out bool anySamplesPassed)
    {
        anySamplesPassed = false;
        if (queryId == 0 || !occlusionQueryAllocated.Contains(queryId))
        {
            return false;
        }
        GL.GetQueryObject(queryId, GetQueryObjectParam.QueryResultAvailable, out int ready);
        if (ready == 0)
        {
            return false;
        }
        GL.GetQueryObject(queryId, GetQueryObjectParam.QueryResult, out int samplesPassed);
        anySamplesPassed = samplesPassed != 0;
        return true;
    }

    // Init hook called from the device ctor. Lives in this partial so
    // ctor wiring stays terse — the .cs ctor just calls this.
    private void DetectOcclusionQuerySupport()
    {
        occlusionQuerySupported = DetectExtension(OcclusionQueryExtension);
    }

    // Called from Dispose. Cleans up GL handles for all allocated +
    // pooled queries.
    private void DisposeOcclusionQueries()
    {
        foreach (var id in occlusionQueryAllocated)
        {
            GL.DeleteQuery(id);
        }
        foreach (var id in occlusionQueryPool)
        {
            GL.DeleteQuery(id);
        }
        occlusionQueryAllocated.Clear();
        occlusionQueryPool.Clear();
    }
}
