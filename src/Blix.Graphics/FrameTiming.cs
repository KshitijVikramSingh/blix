namespace Blix.Graphics;

/// <summary>
/// What the host measured and submitted, as facts: how long the last frame's CPU phases took, what
/// it handed the GPU, and what the GPU spent per pass.
/// </summary>
/// <remarks>
/// <para>
/// <b>Facts, not a measurement.</b> Nothing here chooses a window, a percentile or a warm-up, and
/// nothing is drained by reading it. Which frames count, and what a number is compared with, is
/// the reader's. A reader that wants the GPU time of a window copies the <see cref="GpuPassTotal"/>
/// entries it cares about at the window's start and subtracts them from the same entries later,
/// which is exact and needs no ownership. Copy the entries: the dictionary itself is live, so keeping
/// a reference to it keeps nothing.
/// </para>
/// <para>
/// <b>Cheap to read and always on.</b> The backend keeps these as it records and submits, whether
/// or not diagnostics exist, so a program measuring itself does not have to keep a diagnostics
/// producer alive to learn what its host already knows.
/// </para>
/// <para>
/// <b>What was submitted, which is not what was meant.</b> <see cref="SubmittedWork"/> counts what
/// reached the backend. A program comparing it with its own idea of what it staged can see a draw it
/// meant to issue and did not, or one it did not know it was issuing. What an indirect draw draws is
/// decided on the GPU, so its triangles are not counted.
/// </para>
/// </remarks>
public interface IFrameTiming
{
    /// <summary>The last frame the host submitted, or null when it has submitted none.</summary>
    FrameTiming? LastFrame { get; }

    /// <summary>What each pass of <see cref="LastFrame"/> submitted, by pass name.</summary>
    IReadOnlyDictionary<string, SubmittedWork> LastFramePasses { get; }

    /// <summary>Whether GPU pass timings are resolved at all. False means <see cref="GpuPassTotals"/> stays empty.</summary>
    bool GpuTimestampsSupported { get; }

    /// <summary>Cumulative GPU time per pass since the host started, and how many resolutions it is over.</summary>
    /// <remarks>
    /// <para>
    /// <b>Live, not a snapshot.</b> This is the host's own table, updated as timings resolve, and
    /// reading it allocates nothing. To measure a window, copy the entries at its start
    /// (<see cref="GpuPassTotal"/> is a value) and subtract them from the entries at its end.
    /// </para>
    /// <para>GPU timings resolve a frame or more after submission, so these lag <see cref="LastFrame"/>.</para>
    /// </remarks>
    IReadOnlyDictionary<string, GpuPassTotal> GpuPassTotals { get; }

    /// <summary>
    /// Whether each pass is submitted and timed on its own, so its GPU time cannot overlap another's.
    /// </summary>
    /// <remarks>
    /// A measurement mode, off by default: it waits for the GPU at every pass boundary, so a frame
    /// runs far slower, and the numbers ATTRIBUTE rather than decompose. They sum to more than the
    /// frame, and a pass that normally hides behind another reads as more than its marginal cost.
    /// Marginal cost is what an A/B is for. A host that cannot isolate passes ignores it.
    /// </remarks>
    bool IsolatePasses { get; set; }

    /// <summary>Isolated GPU time per pass, cumulative while <see cref="IsolatePasses"/> was on.</summary>
    IReadOnlyDictionary<string, GpuPassTotal> IsolatedPassTotals { get; }

    /// <summary>Frames recorded while isolation was on: the denominator for a pass that skips frames.</summary>
    long IsolatedFrames { get; }

    /// <summary>Discards what isolation has measured, starting a new window (say, once a scene has loaded).</summary>
    void ResetIsolatedTotals();

    /// <summary>
    /// The smallest pass cost isolation can resolve here, in milliseconds: an empty submission timed the
    /// same way. A pass under it cannot be measured by isolation at all; 0 on a host that cannot isolate.
    /// </summary>
    /// <remarks>Measured when called, so call it outside a timed window.</remarks>
    double MeasureIsolationFloorMs();
}

/// <summary>One submitted frame: which, how long its CPU phases took, and what it handed the GPU.</summary>
/// <param name="Frame">
/// The host's frame sequence number, starting at 1. Not a count of submitted frames: a frame whose
/// swapchain turned out to be out of date takes a number and submits nothing, so consecutive
/// submitted frames can skip one. It is the same number GPU pass timings are tagged with.
/// </param>
/// <param name="WaitMs">Waiting for the frame slot to come free: GPU throttle and present pacing, together.</param>
/// <param name="EncodeMs">Recording every command, which is the cost draw count drives.</param>
/// <param name="SubmitPresentMs">Submitting the frame and queueing it for presentation.</param>
/// <param name="Work">Everything the frame submitted, over all its passes.</param>
public readonly record struct FrameTiming(
    long Frame, double WaitMs, double EncodeMs, double SubmitPresentMs, SubmittedWork Work);

/// <summary>What a frame, or one pass of it, handed the GPU.</summary>
/// <param name="Passes">Graphics and compute passes.</param>
/// <param name="Draws">Directly issued draws.</param>
/// <param name="Instances">Instances over those draws.</param>
/// <param name="Triangles">Triangles over those draws, counted only for triangle pipelines.</param>
/// <param name="IndirectDraws">Indirect draw calls issued.</param>
/// <param name="IndirectCommands">Draw records those calls read, whose contents the GPU decides.</param>
/// <param name="Dispatches">Compute dispatches.</param>
public readonly record struct SubmittedWork(
    int Passes, int Draws, long Instances, long Triangles, int IndirectDraws, long IndirectCommands, int Dispatches)
{
    /// <summary>The two added together.</summary>
    public static SubmittedWork operator +(SubmittedWork a, SubmittedWork b) => new(
        a.Passes + b.Passes, a.Draws + b.Draws, a.Instances + b.Instances, a.Triangles + b.Triangles,
        a.IndirectDraws + b.IndirectDraws, a.IndirectCommands + b.IndirectCommands, a.Dispatches + b.Dispatches);
}

/// <summary>Cumulative GPU time for one pass.</summary>
/// <param name="TotalMs">Milliseconds over every resolution.</param>
/// <param name="Samples">How many resolutions.</param>
public readonly record struct GpuPassTotal(double TotalMs, long Samples)
{
    /// <summary>The two added together.</summary>
    public static GpuPassTotal operator +(GpuPassTotal a, GpuPassTotal b) => new(a.TotalMs + b.TotalMs, a.Samples + b.Samples);

    /// <summary>This total less an earlier one: the window between them.</summary>
    public static GpuPassTotal operator -(GpuPassTotal a, GpuPassTotal b) => new(a.TotalMs - b.TotalMs, a.Samples - b.Samples);

    /// <summary>Mean milliseconds per resolution, or 0 when there are none.</summary>
    public double MeanMs => Samples == 0 ? 0.0 : TotalMs / Samples;
}

/// <summary>Frame timing for a host that submits nothing.</summary>
public static class FrameTimings
{
    /// <summary>No frame submitted, no timestamps, no totals. What a host with no GPU reports.</summary>
    public static IFrameTiming None { get; } = new Nothing();

    private sealed class Nothing : IFrameTiming
    {
        private static readonly IReadOnlyDictionary<string, SubmittedWork> NoPasses = new Dictionary<string, SubmittedWork>();
        private static readonly IReadOnlyDictionary<string, GpuPassTotal> NoTotals = new Dictionary<string, GpuPassTotal>();

        public FrameTiming? LastFrame => null;
        public IReadOnlyDictionary<string, SubmittedWork> LastFramePasses => NoPasses;
        public bool GpuTimestampsSupported => false;
        public IReadOnlyDictionary<string, GpuPassTotal> GpuPassTotals => NoTotals;
        public bool IsolatePasses { get => false; set { } }
        public IReadOnlyDictionary<string, GpuPassTotal> IsolatedPassTotals => NoTotals;
        public long IsolatedFrames => 0;
        public void ResetIsolatedTotals() { }
        public double MeasureIsolationFloorMs() => 0;
    }
}
