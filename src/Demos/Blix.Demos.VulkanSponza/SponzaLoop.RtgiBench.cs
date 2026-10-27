using System.Globalization;
using Blix.Core;

namespace Blix.Demos.VulkanSponza;

// --rtgi-bench N [--rtgi-bench-at F] [--rtgi-bench-pass train|rays|trace]: one of the cache's per-point passes measured
// alone, on a frozen cache (the training by default). From post-load frame F
// (2400) the cache stops moving -- its frame number, so stamps and the dirty list stand still; no placement, no trace,
// no resolve, blend or eviction, so the training re-reads the same points, rays and hits -- and the training is
// dispatched N times a frame. Its GPU time (the pass's timestamps) over windows of RtgiBenchWindow frames, divided by
// the dispatches, gives a dispatch's cost; after RtgiBenchSamples windows the median, least and most are printed and the
// demo closes. Live boots measured the same training 3.3-4.6 ms from run to run, the cache's state drifting with every
// variant (2026-10-27), and the same training 2.9 ms in one boot and 4.1 in the next with nothing changed: so variants
// are compared within one boot. --rtgi-bench-sweep B0,B1,...: the training's measurement bits (rtgi_train.comp's uDouble,
// as BLIX_RTGI_TRAIN_DOUBLE) taken in turn a window each, round after round, each value's median printed. Its
// accumulators keep growing (nothing resolves them): fine for the ~30 s it runs.
internal sealed partial class SponzaLoop
{
    private const int RtgiBenchWindow = 60, RtgiBenchSamples = 12, RtgiBenchWarmup = 30, RtgiBenchSettle = 8;
    private int rtgiBenchReps;
    private int rtgiBenchAt = 2400;
    private bool rtgiBenching;
    private int rtgiBenchFrame;
    private double rtgiBenchMsAt;
    private readonly List<double> rtgiBenchMs = new();
    private int[] rtgiBenchSweep = Array.Empty<int>();
    // --rtgi-bench-pass train (default), rays (placement, rtgi_rays.comp) or trace (the Metal tracer's closest hits and
    // the cache's sun rays). The trace by the frame's wall time (GPU-bound, no vsync): the tracer's own times are command
    // buffers' spans, overlapping (43 ms of closest hits a frame in 17.5 ms frames); its sweep is the traces a frame
    // (--rtgi-bench-sweep 1,5: the difference over 4 is one's cost).
    private readonly System.Diagnostics.Stopwatch rtgiBenchClock = System.Diagnostics.Stopwatch.StartNew();
    private string rtgiBenchPass = "train";
    private readonly Dictionary<int, List<double>> rtgiBenchSweepMs = new();

    private void ParseRtgiBench(AppArgs args)
    {
        if (args.Int("rtgi-bench") is { } n) rtgiBenchReps = Math.Max(1, n);
        if (args.Int("rtgi-bench-at") is { } at) rtgiBenchAt = Math.Max(1, at);
        if (args.String("rtgi-bench-pass") is "train" or "rays" or "trace") rtgiBenchPass = args.String("rtgi-bench-pass")!;
        if (args.String("rtgi-bench-sweep") is { Length: > 0 } sweep)
            rtgiBenchSweep = sweep.Split(',').Select(b => int.Parse(b, CultureInfo.InvariantCulture)).ToArray();
    }

    // RecordRtgi: the training's dispatches this frame (1 but on the bench).
    private int RtgiTrainReps()
    {
        if (rtgiBenchReps <= 0) return 1;
        if (!rtgiBenching && postLoadFrames >= rtgiBenchAt)
        {
            rtgiBenching = true;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] rtgi bench: the cache frozen at frame {postLoadFrames} ({RtgiPoints:N0} points), {rtgiBenchPass} {rtgiBenchReps} times a frame."));
        }
        return !rtgiBenching ? 1 : rtgiBenchPass == "train" ? rtgiBenchReps : 0;
    }

    // The benched pass's GPU time so far: the pass's timestamps, or for the trace the wall clock.
    private double BenchTotalMs()
    {
        if (rtgiBenchPass == "trace") return rtgiBenchClock.Elapsed.TotalMilliseconds;
        var pass = rtgiBenchPass == "rays" ? "rtgi-rays" : "rtgi-train";
        return host.Timing.GpuPassTotals.TryGetValue(pass, out var t) ? t.TotalMs : 0.0;
    }

    // BLIX_RTGI_BENCH_DUMP=DIR: the frozen frame's training inputs written once -- points, rays, hits (as the training reads
    // them: this frame's parity) and meta.json (the cell parameters, camera, points) -- for tools/gi-model's layout
    // models (rtgi_layout.py: what a frame touches, cache lines and pages, under one slot layout or another).
    private void RtgiBenchDump()
    {
        if (Environment.GetEnvironmentVariable("BLIX_RTGI_BENCH_DUMP") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        device.WaitIdle();
        var prev = (int)(rtgiFrameCount & 1u);
        var n = RtgiPoints;
        File.WriteAllBytes(Path.Combine(dir, "points.bin"), device.ReadGpuBuffer(rtgiPointsBuf[prev], 0, n * 32));
        File.WriteAllBytes(Path.Combine(dir, "rays.bin"), device.ReadGpuBuffer(rtgiRaysBuf[prev], 0, n * 32));
        File.WriteAllBytes(Path.Combine(dir, "hits.bin"), device.ReadGpuBuffer(rtgiHitsBuf[prev], 0, n * 48));
        File.WriteAllText(Path.Combine(dir, "meta.json"), string.Create(CultureInfo.InvariantCulture,
            $"{{\"points\": {n}, \"visible\": {rtgiTiles}, \"cell\": {rtgiCell}, \"grow\": {rtgiCellGrow}, \"slots\": {RtgiSlots}, \"top\": {RtgiTopLevel}, \"near\": {rtgiNearField}, \"levels\": {rtgiTrainLevels}, \"camera\": [{cameraPosition.X}, {cameraPosition.Y}, {cameraPosition.Z}]}}"));
        Console.WriteLine($"[VulkanSponza] rtgi bench: the frozen frame's training inputs written to {dir}.");
    }

    // Each frame: the benched pass's GPU time over the window, a dispatch.
    private void RtgiBenchTick()
    {
        if (!rtgiBenching) return;
        rtgiBenchFrame++;
        if (rtgiBenchFrame == 2) RtgiBenchDump();
        if (rtgiBenchFrame < RtgiBenchWarmup) return;
        var totalMs = BenchTotalMs();
        // The sweep's value for the window starting now (the timings lag the recording by a few frames: a window's first
        // RtgiBenchSettle frames are the last value's, and skipped).
        int window = (rtgiBenchFrame - RtgiBenchWarmup) / RtgiBenchWindow;
        int inWindow = (rtgiBenchFrame - RtgiBenchWarmup) % RtgiBenchWindow;
        if (rtgiBenchSweep.Length > 0)
        {
            if (rtgiBenchPass == "trace") rtgiBenchReps = rtgiBenchSweep[window % rtgiBenchSweep.Length];
            else rtgiTrainDouble = rtgiBenchSweep[window % rtgiBenchSweep.Length];
        }
        if (inWindow == RtgiBenchSettle) { rtgiBenchMsAt = totalMs; return; }
        if (inWindow != 0 || window == 0) return;
        // A dispatch's time; the trace's, a frame's.
        var ms = (totalMs - rtgiBenchMsAt) / ((RtgiBenchWindow - RtgiBenchSettle) * (rtgiBenchPass == "trace" ? 1.0 : rtgiBenchReps));
        if (rtgiBenchSweep.Length > 0)
        {
            var value = rtgiBenchSweep[(window - 1) % rtgiBenchSweep.Length];
            if (!rtgiBenchSweepMs.TryGetValue(value, out var list)) rtgiBenchSweepMs[value] = list = new List<double>();
            list.Add(ms);
            if (window < RtgiBenchSamples * rtgiBenchSweep.Length) return;
            foreach (var b in rtgiBenchSweep.Distinct())
            {
                var v = rtgiBenchSweepMs[b].OrderBy(x => x).ToArray();
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[VulkanSponza] rtgi bench: {rtgiBenchPass} variant {b,3}: {v[v.Length / 2]:0.000} ms a dispatch (median of {v.Length}; least {v[0]:0.000}, most {v[^1]:0.000})."));
            }
            host.RequestClose();
            return;
        }
        rtgiBenchMs.Add(ms);
        if (rtgiBenchMs.Count < RtgiBenchSamples) return;
        var sorted = rtgiBenchMs.OrderBy(v => v).ToArray();
        var label = Environment.GetEnvironmentVariable("BLIX_RTGI_TRAIN_DOUBLE") is { Length: > 0 } d ? $", doubled {d}" : "";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] rtgi bench: {rtgiBenchPass} {sorted[sorted.Length / 2]:0.000} ms a dispatch (median of {sorted.Length} windows of {RtgiBenchWindow} frames x {rtgiBenchReps}; least {sorted[0]:0.000}, most {sorted[^1]:0.000}){label}."));
        host.RequestClose();
    }
}
