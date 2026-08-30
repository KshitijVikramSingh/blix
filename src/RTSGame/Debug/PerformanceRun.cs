namespace RTSGame.Debug;

/// <summary>
/// What one sealed <c>--perf-run</c> spent, reported once at the end instead of scrolled past.
/// </summary>
/// <remarks>
/// <b>Written because the camera matrix is two dozen runs and the only instrument was a scrolling line.</b>
/// The periodic FRAME/BUILD/LOAD report is the right thing for sitting in the chair — it says what the frame
/// is doing right now, next to what can be seen. It is the wrong thing for comparing four camera motions
/// against two lighting states at three standoffs, because the comparison is then somebody reading dozens of
/// lines per case and remembering a number. Every wrong measurement this stage has produced so far came from
/// exactly that: a figure recalled rather than recorded.
/// <para>
/// So this keeps the raw per-frame samples and prints percentiles once. Three properties matter:
/// </para>
/// <list type="bullet">
/// <item><b>Percentiles, not an average.</b> The smoothed FRAME figure is an exponential average, which is
/// readable and hides exactly what pacing means. A run averaging 14 ms with a 40 ms ninety-fifth percentile
/// is choppy, and the average cannot say so.</item>
/// <item><b>Warm-up separated rather than dropped.</b> First presentation, terrain meshing and the first
/// cover resolve are a different question from steady state, and §75 has already been misled once by a
/// startup frame counted as a regression. Both windows are reported; neither is discarded.</item>
/// <item><b>One machine-readable line per case.</b> <c>PERFCASE key=value</c> is what the matrix script
/// tabulates, so the table in the plan is generated from the runs rather than typed from memory.</item>
/// </list>
/// </remarks>
internal sealed class PerformanceRun
{
    /// <summary>One rendered frame, as the numbers the frame budget is argued in.</summary>
    internal readonly record struct Sample(
        double FrameMilliseconds,
        double RenderDeltaMilliseconds,
        double Update,
        double Fog,
        double Render,
        double Ground,
        double Nodes,
        double Agents,
        double Scatter,
        double Overlay,
        double Stage,
        double Record,
        int Instances,
        long SceneTriangles,
        long CasterTriangles,
        int CasterInstancesNear,
        int CasterInstancesMid,
        int CasterInstancesFar,
        long CasterTrianglesNear,
        long CasterTrianglesMid,
        long CasterTrianglesFar,
        int TreesDrawn,
        int ChunksDrawn,
        float CameraDistance);

    private readonly List<Sample> warmUp = new();
    private readonly List<Sample> steady = new();
    private readonly int warmUpFrames;

    /// <summary>
    /// Whether the run presented through FIFO.
    /// </summary>
    /// <remarks>
    /// Reported rather than assumed, because the first matrix run was taken with it on and every frame figure
    /// in it was a multiple of a refresh interval. A cost and a cadence are both worth measuring and quoting
    /// one as the other is how a GPU budget gets set from a monitor.
    /// </remarks>
    private readonly bool vsync;
    private double[] scratch = new double[256];

    public PerformanceRun(string label, int warmUpFrames, bool vsync)
    {
        Label = label;
        this.warmUpFrames = Math.Max(0, warmUpFrames);
        this.vsync = vsync;
    }

    /// <summary>What the run was: camera motion, light, standoff, fog. Printed and keyed on.</summary>
    public string Label { get; }

    public bool Reported { get; private set; }

    public int FramesSeen => warmUp.Count + steady.Count;

    public void Observe(in Sample sample)
    {
        if (Reported) return;
        (FramesSeen < warmUpFrames ? warmUp : steady).Add(sample);
    }

    /// <summary>
    /// Prints the block and the one keyed line, once.
    /// </summary>
    /// <param name="tickMilliseconds">
    /// The simulation's own average total tick, read at the end rather than sampled per frame: it is already
    /// an exponential average inside <see cref="SimulationTimings"/>, and taking percentiles of a smoothed
    /// figure would be a number about the smoothing.
    /// </param>
    /// <param name="agents">Bodies alive, because every per-tick figure is per this many.</param>
    public void Report(double tickMilliseconds, int agents)
    {
        if (Reported) return;
        Reported = true;

        // A run shorter than its own warm-up would otherwise report an empty steady state and a full warm-up,
        // which reads as "the frame costs nothing" rather than "you asked for sixty frames".
        var body = steady.Count > 0 ? steady : warmUp;
        var window = steady.Count > 0 ? "steady" : "warm-up only";

        Console.WriteLine();
        Console.WriteLine($"=== perf case: {Label} (vsync {(vsync ? "on — cadence" : "off — cost")}) ===");
        if (warmUp.Count > 0)
        {
            Console.WriteLine(
                $"  startup: first frame {warmUp[0].FrameMilliseconds:F1} ms, " +
                $"warm-up p50 {Percentile(warmUp, s => s.FrameMilliseconds, 0.50):F1} ms, " +
                $"max {Percentile(warmUp, s => s.FrameMilliseconds, 1.0):F1} ms " +
                $"over {warmUp.Count} frames (excluded from everything below)");
        }

        if (body.Count == 0)
        {
            Console.WriteLine("  no frames rendered — nothing to report");
            return;
        }

        Console.WriteLine(
            $"  frame ({window}, {body.Count} frames): " +
            $"p50 {Percentile(body, s => s.FrameMilliseconds, 0.50):F1} ms " +
            $"({1000.0 / Math.Max(0.001, Percentile(body, s => s.FrameMilliseconds, 0.50)):F0} fps) · " +
            $"p95 {Percentile(body, s => s.FrameMilliseconds, 0.95):F1} ms · " +
            $"max {Percentile(body, s => s.FrameMilliseconds, 1.0):F1} ms");
        // <b>The frame closed against its own halves.</b> Outside is what neither half claimed: acquiring a
        // swapchain image, submitting, and waiting on the GPU. It is the term that decides whether the next
        // slice belongs in C# or in a shader, and the first matrix had no name for it at all.
        // The two clocks, side by side. Equal figures mean the host pairs update and render one to one and
        // either may be quoted; a gap means the loop is doing something the frame budget has to know about.
        Console.WriteLine(
            $"  clocks p50: update-delta {Percentile(body, s => s.FrameMilliseconds, 0.50):F2} ms vs " +
            $"render-delta {Percentile(body, s => s.RenderDeltaMilliseconds, 0.50):F2} ms " +
            $"({1000.0 / Math.Max(0.001, Percentile(body, s => s.RenderDeltaMilliseconds, 0.50)):F0} fps by " +
            $"the render clock)");
        Console.WriteLine(
            $"  split p50/p95 ms: update {Pair(body, s => s.Update)} (fog {Pair(body, s => s.Fog)}) · " +
            $"render {Pair(body, s => s.Render)} · " +
            $"outside {Pair(body, Outside)}");
        Console.WriteLine(
            $"  build p50/p95 ms: ground {Pair(body, s => s.Ground)} · nodes {Pair(body, s => s.Nodes)} · " +
            $"agents {Pair(body, s => s.Agents)} · scatter {Pair(body, s => s.Scatter)} · " +
            $"overlay {Pair(body, s => s.Overlay)} · stage {Pair(body, s => s.Stage)} · " +
            $"record {Pair(body, s => s.Record)}");
        Console.WriteLine(
            $"  tick {tickMilliseconds:F3} ms over {agents} agents · " +
            $"zoom {Percentile(body, s => s.CameraDistance, 0.50):F0} m " +
            $"({Percentile(body, s => s.CameraDistance, 0.0):F0}-{Percentile(body, s => s.CameraDistance, 1.0):F0} m)");
        Console.WriteLine(
            $"  staged p50: {Percentile(body, s => s.Instances, 0.50):F0} instances = " +
            $"{Percentile(body, s => s.SceneTriangles, 0.50) / 1000.0:F0}k scene triangles, " +
            $"{Percentile(body, s => s.CasterTriangles, 0.50) / 1000.0:F0}k cast · " +
            $"{Percentile(body, s => s.TreesDrawn, 0.50):F0} trees, " +
            $"{Percentile(body, s => s.ChunksDrawn, 0.50):F0} ground chunks");
        // <b>Per cascade, because the partition can only be judged per cascade.</b> Three boxes holding the
        // same 2.9M triangles and three holding 0.8M/2.6M/2.9M sum to numbers a total cannot tell apart.
        Console.WriteLine(
            $"  cascades p50 (near/mid/far): " +
            $"{Percentile(body, s => s.CasterInstancesNear, 0.50):F0}/" +
            $"{Percentile(body, s => s.CasterInstancesMid, 0.50):F0}/" +
            $"{Percentile(body, s => s.CasterInstancesFar, 0.50):F0} casters = " +
            $"{Percentile(body, s => s.CasterTrianglesNear, 0.50) / 1000.0:F0}k/" +
            $"{Percentile(body, s => s.CasterTrianglesMid, 0.50) / 1000.0:F0}k/" +
            $"{Percentile(body, s => s.CasterTrianglesFar, 0.50) / 1000.0:F0}k triangles");

        // The line the matrix script reads. Deliberately flat, single-space separated and free of the units
        // and punctuation above: a table generated from prose is a table with a parser bug in it.
        Console.WriteLine(
            $"PERFCASE case={Label} vsync={(vsync ? "on" : "off")} window={window.Replace(' ', '-')} " +
            $"frames={body.Count} warmup={warmUp.Count} " +
            $"frame_p50={Percentile(body, s => s.FrameMilliseconds, 0.50):F2} " +
            $"render_delta_p50={Percentile(body, s => s.RenderDeltaMilliseconds, 0.50):F2} " +
            $"frame_p95={Percentile(body, s => s.FrameMilliseconds, 0.95):F2} " +
            $"frame_max={Percentile(body, s => s.FrameMilliseconds, 1.0):F2} " +
            $"startup_max={(warmUp.Count > 0 ? Percentile(warmUp, s => s.FrameMilliseconds, 1.0) : 0.0):F2} " +
            $"update_p50={Percentile(body, s => s.Update, 0.50):F2} " +
            $"fog_p50={Percentile(body, s => s.Fog, 0.50):F2} " +
            $"render_p50={Percentile(body, s => s.Render, 0.50):F2} " +
            $"outside_p50={Percentile(body, Outside, 0.50):F2} " +
            $"ground_p50={Percentile(body, s => s.Ground, 0.50):F2} " +
            $"nodes_p50={Percentile(body, s => s.Nodes, 0.50):F2} " +
            $"agents_p50={Percentile(body, s => s.Agents, 0.50):F2} " +
            $"scatter_p50={Percentile(body, s => s.Scatter, 0.50):F2} " +
            $"overlay_p50={Percentile(body, s => s.Overlay, 0.50):F2} " +
            $"stage_p50={Percentile(body, s => s.Stage, 0.50):F2} " +
            $"record_p50={Percentile(body, s => s.Record, 0.50):F2} " +
            $"tick_ms={tickMilliseconds:F3} agents={agents} " +
            $"zoom_p50={Percentile(body, s => s.CameraDistance, 0.50):F1} " +
            $"instances={Percentile(body, s => s.Instances, 0.50):F0} " +
            $"scene_tris={Percentile(body, s => s.SceneTriangles, 0.50):F0} " +
            $"cast_tris={Percentile(body, s => s.CasterTriangles, 0.50):F0} " +
            $"cast_tris_near={Percentile(body, s => s.CasterTrianglesNear, 0.50):F0} " +
            $"cast_tris_mid={Percentile(body, s => s.CasterTrianglesMid, 0.50):F0} " +
            $"cast_tris_far={Percentile(body, s => s.CasterTrianglesFar, 0.50):F0} " +
            $"trees={Percentile(body, s => s.TreesDrawn, 0.50):F0} " +
            $"chunks={Percentile(body, s => s.ChunksDrawn, 0.50):F0}");
        Console.Out.Flush();
    }

    /// <summary>
    /// What the frame spent in neither half.
    /// </summary>
    /// <remarks>
    /// Floored at zero rather than allowed negative: the update and render halves are timed inside their own
    /// callbacks while the frame delta is measured by the host between them, so a frame where the host did
    /// something cheap can round to a hair below the sum. A small floor is honest; a negative residual in a
    /// table is an invitation to explain a clock.
    /// </remarks>
    private static double Outside(Sample s) => Math.Max(0.0, s.FrameMilliseconds - s.Update - s.Render);

    private string Pair(List<Sample> body, Func<Sample, double> of) =>
        $"{Percentile(body, of, 0.50):F1}/{Percentile(body, of, 0.95):F1}";

    /// <summary>
    /// Nearest-rank percentile over one field.
    /// </summary>
    /// <remarks>
    /// Nearest-rank rather than interpolated: these are frame times and instance counts, and reporting a
    /// 14.37 ms frame that no frame took invites arguing about a frame that never happened.
    /// </remarks>
    private double Percentile(List<Sample> body, Func<Sample, double> of, double at)
    {
        if (body.Count == 0) return 0.0;
        if (scratch.Length < body.Count) scratch = new double[Math.Max(body.Count, scratch.Length * 2)];
        for (var i = 0; i < body.Count; i++) scratch[i] = of(body[i]);
        var span = scratch.AsSpan(0, body.Count);
        span.Sort();
        var index = (int)Math.Round(at * (body.Count - 1), MidpointRounding.AwayFromZero);
        return span[Math.Clamp(index, 0, body.Count - 1)];
    }
}
