using System.Globalization;
using Blix.Core;

namespace Blix.Demos.VulkanSponza;

// --rtgi-bench N [--rtgi-bench-at F]: the cache's training measured alone, on a frozen cache. From post-load frame F
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
    private readonly Dictionary<int, List<double>> rtgiBenchSweepMs = new();

    private void ParseRtgiBench(AppArgs args)
    {
        if (args.Int("rtgi-bench") is { } n) rtgiBenchReps = Math.Max(1, n);
        if (args.Int("rtgi-bench-at") is { } at) rtgiBenchAt = Math.Max(1, at);
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
                $"[VulkanSponza] rtgi bench: the cache frozen at frame {postLoadFrames} ({RtgiPoints:N0} points), the training {rtgiBenchReps} times a frame."));
        }
        return rtgiBenching ? rtgiBenchReps : 1;
    }

    // Each frame: the training's GPU time over the window, a dispatch.
    private void RtgiBenchTick()
    {
        if (!rtgiBenching) return;
        rtgiBenchFrame++;
        if (rtgiBenchFrame < RtgiBenchWarmup) return;
        var totalMs = host.Timing.GpuPassTotals.TryGetValue("rtgi-train", out var t) ? t.TotalMs : 0.0;
        // The sweep's value for the window starting now (the timings lag the recording by a few frames: a window's first
        // RtgiBenchSettle frames are the last value's, and skipped).
        int window = (rtgiBenchFrame - RtgiBenchWarmup) / RtgiBenchWindow;
        int inWindow = (rtgiBenchFrame - RtgiBenchWarmup) % RtgiBenchWindow;
        if (rtgiBenchSweep.Length > 0) rtgiTrainDouble = rtgiBenchSweep[window % rtgiBenchSweep.Length];
        if (inWindow == RtgiBenchSettle) { rtgiBenchMsAt = totalMs; return; }
        if (inWindow != 0 || window == 0) return;
        var ms = (totalMs - rtgiBenchMsAt) / ((RtgiBenchWindow - RtgiBenchSettle) * (double)rtgiBenchReps);
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
                    $"[VulkanSponza] rtgi bench: training doubled {b,3}: {v[v.Length / 2]:0.000} ms a dispatch (median of {v.Length}; least {v[0]:0.000}, most {v[^1]:0.000})."));
            }
            host.RequestClose();
            return;
        }
        rtgiBenchMs.Add(ms);
        if (rtgiBenchMs.Count < RtgiBenchSamples) return;
        var sorted = rtgiBenchMs.OrderBy(v => v).ToArray();
        var label = Environment.GetEnvironmentVariable("BLIX_RTGI_TRAIN_DOUBLE") is { Length: > 0 } d ? $", doubled {d}" : "";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] rtgi bench: training {sorted[sorted.Length / 2]:0.000} ms a dispatch (median of {sorted.Length} windows of {RtgiBenchWindow} frames x {rtgiBenchReps}; least {sorted[0]:0.000}, most {sorted[^1]:0.000}){label}."));
        host.RequestClose();
    }
}
