namespace Blix.Graphics.Vulkan;

// The frame facts the device keeps as it records and submits, whether or not anything reads them.
// See IFrameTiming for what they are and are not; this file is only how they are kept.
//
// Counting costs a few integer adds per draw and one dictionary entry per pass per frame, done
// where the draw is already being translated, so a program never pays for a diagnostics producer
// to learn what its host submitted.
public sealed partial class VulkanGraphicsDevice : IFrameTiming
{
    // <b>A non-draining accumulator beside the draining queue, because two consumers cannot share one
    // drain.</b> The Silk runtime already calls ConsumeAvailableGpuTimings every frame to feed the overlay's
    // gpu/passes scope, so anything else asking for them gets an empty list — which is how a game measuring
    // itself ends up reporting that the GPU costs nothing. Totals are cumulative and never cleared; a caller
    // wanting a window takes a snapshot and subtracts, which is exact and needs no ownership.
    private readonly Dictionary<string, GpuPassTotal> gpuPassTotals = new(StringComparer.Ordinal);

    // Filled while a frame records, swapped into lastFramePasses when it is submitted, so a reader
    // between frames sees one whole frame and never a half-recorded one.
    private Dictionary<string, SubmittedWork> recordingPasses = new(StringComparer.Ordinal);
    private Dictionary<string, SubmittedWork> lastFramePasses = new(StringComparer.Ordinal);
    private SubmittedWork recordingWork;
    private FrameTiming? lastFrame;

    /// <inheritdoc />
    public FrameTiming? LastFrame => lastFrame;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, SubmittedWork> LastFramePasses => lastFramePasses;

    /// <inheritdoc />
    public bool GpuTimestampsSupported => timestampsSupported;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, GpuPassTotal> GpuPassTotals => gpuPassTotals;

    // Isolation is how this device measures when asked to; see GpuPassIsolation for why MoltenVK needs it.
    bool IFrameTiming.IsolatePasses { get => GpuPassIsolation; set => GpuPassIsolation = value; }

    IReadOnlyDictionary<string, GpuPassTotal> IFrameTiming.IsolatedPassTotals => GpuPassIsolatedTotals;

    long IFrameTiming.IsolatedFrames => GpuIsolationFrames;

    void IFrameTiming.ResetIsolatedTotals() => ResetGpuIsolation();

    double IFrameTiming.MeasureIsolationFloorMs() => MeasureSubmitFloorMs();

    private void AccumulateGpuPassTotal(string pass, double elapsedMs)
    {
        var current = gpuPassTotals.TryGetValue(pass, out var found) ? found : default;
        gpuPassTotals[pass] = current + new GpuPassTotal(elapsedMs, 1);
    }

    private void BeginFrameCount()
    {
        recordingPasses.Clear();
        recordingWork = default;
    }

    private void CountPass(string pass) => Count(pass, new SubmittedWork(1, 0, 0, 0, 0, 0, 0));

    // Counted exactly as submitted. A zero-instance draw is legal and draws nothing, so it counts one
    // draw, zero instances and zero triangles; this used to clamp to one instance and report the
    // triangles of a draw the GPU never made, in the one API whose job is to say what was submitted.
    // Negative counts are refused before this is reached (TranslateDrawIndexed).
    private void CountDraw(string pass, VkPipelineEntry pipeline, int indexCount, int instanceCount)
    {
        var triangles = pipeline.Topology == PrimitiveTopology.Triangles ? (long)(indexCount / 3) * instanceCount : 0;
        Count(pass, new SubmittedWork(0, 1, instanceCount, triangles, 0, 0, 0));
    }

    private void CountIndirect(string pass, int drawCount) =>
        Count(pass, new SubmittedWork(0, 0, 0, 0, 1, drawCount, 0));

    private void CountDispatch(string pass) => Count(pass, new SubmittedWork(0, 0, 0, 0, 0, 0, 1));

    private void Count(string pass, SubmittedWork work)
    {
        recordingWork += work;
        recordingPasses[pass] = (recordingPasses.TryGetValue(pass, out var found) ? found : default) + work;
    }

    private void EndFrameCount(double waitMs, double encodeMs, double submitPresentMs)
    {
        lastFrame = new FrameTiming(currentGpuFrameNumber, waitMs, encodeMs, submitPresentMs, recordingWork);
        (lastFramePasses, recordingPasses) = (recordingPasses, lastFramePasses);
    }
}
