using OpenTK.Graphics.OpenGL4;

namespace Blix.Graphics.OpenGL;

public sealed partial class OpenGLGraphicsDevice
{
    // ARB_timer_query was promoted to core in OpenGL 3.3, so any context
    // we run against (Apple GL 4.1, modern desktops) supports it. We
    // still extension-check for completeness — non-conformant drivers
    // exist and the cost of the check is one extension lookup at init.
    private const string TimerQueryExtension = "GL_ARB_timer_query";

    private readonly bool gpuTimingSupported;

    // FIFO of timestamp-query pairs awaiting GPU completion. Each entry
    // captures the pass name + the issue-frame number so cross-frame
    // attribution is possible for sinks that care. The queue is drained
    // head-first each frame: once we hit one that's not yet available,
    // we stop (later pairs can't be ready before earlier ones in
    // practice, because they were issued later into the same GL stream).
    private readonly Queue<PendingGpuQuery> pendingGpuQueries = new();

    // Buffer of completed timings since the last ConsumeAvailableGpuTimings.
    // The runtime drains this once per frame after Execute.
    private readonly List<GpuPassTiming> availableGpuTimings = new();

    private int currentGpuFrameNumber;

    public bool GpuTimingSupported => gpuTimingSupported;

    // Set false to skip query issuance entirely — no GL calls, no
    // overhead. Default ON when supported so the HUD shows GPU times
    // out of the box; demos / headless tools can flip it off if they
    // want to compare timings with/without the queries themselves.
    public bool GpuTimingEnabled { get; set; }

    // Drains everything that became available since the last call.
    // Returns a fresh list each call; ownership transfers to the caller.
    public IReadOnlyList<GpuPassTiming> ConsumeAvailableGpuTimings()
    {
        if (availableGpuTimings.Count == 0)
        {
            return Array.Empty<GpuPassTiming>();
        }
        var snapshot = availableGpuTimings.ToArray();
        availableGpuTimings.Clear();
        return snapshot;
    }

    // Called from the device ctor's extension-detect block. Pulled into a
    // method so the ctor body stays readable.
    private bool DetectGpuTimingSupport()
    {
        return DetectExtension(TimerQueryExtension);
    }

    // Issued at the very start of each pass body in ExecutePass. Returns
    // the begin-query id; 0 means timing was skipped for this pass.
    private int BeginPassGpuTiming(string passName)
    {
        if (!gpuTimingSupported || !GpuTimingEnabled)
        {
            return 0;
        }

        var begin = GL.GenQuery();
        GL.QueryCounter(begin, QueryCounterTarget.Timestamp);
        return begin;
    }

    // Paired with BeginPassGpuTiming. Records the end timestamp and
    // enqueues the pair for later result harvesting. A zero beginQueryId
    // means BeginPassGpuTiming was a no-op; we skip end-side work too.
    private void EndPassGpuTiming(string passName, int beginQueryId)
    {
        if (beginQueryId == 0)
        {
            return;
        }

        var end = GL.GenQuery();
        GL.QueryCounter(end, QueryCounterTarget.Timestamp);
        pendingGpuQueries.Enqueue(new PendingGpuQuery(
            PassName: passName,
            BeginQueryId: beginQueryId,
            EndQueryId: end,
            FrameIssued: currentGpuFrameNumber));
    }

    // Walks the queue head until it hits a pair that isn't yet available
    // (or the queue empties). Each ready pair becomes a GpuPassTiming;
    // the underlying GL queries are deleted to reclaim their handles.
    private void HarvestGpuTimings()
    {
        if (!gpuTimingSupported)
        {
            return;
        }

        while (pendingGpuQueries.Count > 0)
        {
            var head = pendingGpuQueries.Peek();
            GL.GetQueryObject(head.EndQueryId, GetQueryObjectParam.QueryResultAvailable, out int endReady);
            if (endReady == 0)
            {
                // End not ready -> begin definitely not, since end was
                // issued later into the GL stream. Preserve FIFO and stop.
                break;
            }
            GL.GetQueryObject(head.BeginQueryId, GetQueryObjectParam.QueryResultAvailable, out int beginReady);
            if (beginReady == 0)
            {
                break;
            }

            pendingGpuQueries.Dequeue();

            // QueryResult on a timestamp query returns nanoseconds.
            GL.GetQueryObject(head.BeginQueryId, GetQueryObjectParam.QueryResult, out long beginNs);
            GL.GetQueryObject(head.EndQueryId, GetQueryObjectParam.QueryResult, out long endNs);
            GL.DeleteQuery(head.BeginQueryId);
            GL.DeleteQuery(head.EndQueryId);

            var elapsedNs = endNs - beginNs;
            // Negative deltas should be impossible per spec; clamp just
            // in case a flaky driver produces one. (Some old vendors did.)
            if (elapsedNs < 0)
            {
                elapsedNs = 0;
            }
            var elapsedMs = elapsedNs / 1_000_000.0;
            availableGpuTimings.Add(new GpuPassTiming(head.PassName, elapsedMs, head.FrameIssued));
        }
    }

    // Called by Dispose. Reclaims any GL query objects we still hold so
    // they don't leak past the device's lifetime.
    private void DisposeGpuTiming()
    {
        while (pendingGpuQueries.Count > 0)
        {
            var head = pendingGpuQueries.Dequeue();
            GL.DeleteQuery(head.BeginQueryId);
            GL.DeleteQuery(head.EndQueryId);
        }
    }

    private readonly record struct PendingGpuQuery(
        string PassName,
        int BeginQueryId,
        int EndQueryId,
        int FrameIssued);
}
