using Blix.Graphics;
using Blix.Graphics.MetalHWX;

namespace Blix.Demos.VulkanSponza;

// A frame's Metal work as one trip (2026-10-15). Each in-frame batch was its own external pass: the frame submitted,
// Metal waited for it, traced, signalled, and the frame went on -- three or four times a frame on Sponza, with the GPU
// idle at every turn (a Metal System Trace: ~29 ms of a 103 ms frame idle, ~24 ms more in waits). Batches whose answers
// are read on a LATER frame (RTGI's, SponzaLoop.Rtgi.cs) are deferred here and encoded
// in one group (MetalRayTracer.EncodeGroup) with the traced screen shadows -- or, on a frame without them, in a group
// pass of their own. The frames' chain (IGraphicsDevice.ChainFrames) puts the next frame's readers after it.
internal sealed partial class SponzaLoop
{
    private PassHandle tracerGroupPassHandle;
    private readonly List<Action<ExternalWorkSync, MetalRayTracer>> tracerDeferred = new();
    private bool tracerGroupRecorded;

    private bool TracerGroupsOn => FrameRaysOnHardware;

    private void DeferTracerBatch(Action<ExternalWorkSync, MetalRayTracer> batch) => tracerDeferred.Add(batch);

    // In a group's callback (as the frame executes, after every batch of it was recorded): the deferred batches, taken.
    private void EncodeDeferredBatches(ExternalWorkSync sync, MetalRayTracer tracer)
    {
        var pending = tracerDeferred.ToArray();
        tracerDeferred.Clear();
        foreach (var batch in pending) batch(sync, tracer);
    }

    // After the frame's tracer users have recorded: the deferred batches in a group of their own when no screen-shadow
    // group took them.
    private void FlushTracerGroup()
    {
        var grouped = tracerGroupRecorded;
        tracerGroupRecorded = false;
        if (grouped || tracerDeferred.Count == 0) return;
        if (frameTracer is not { } tracer)
        {
            tracerDeferred.Clear();
            return;
        }
        graph.External(tracerGroupPassHandle, sync => tracer.EncodeGroup(sync, () => EncodeDeferredBatches(sync, tracer)));
    }
}
