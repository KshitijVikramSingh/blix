using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// Where a frame's time goes (2026-10-19: "fps randomly dips too low, moving kills fps" -- the lighting right, the frame
// next). On unless BLIX_PROFILE=0. Every frame: its interval, the host's CPU phases (wait, encode, submit/present), the
// demo's own CPU sections (Prof scopes: what it records and what it runs outside the frame -- the gather's passes, a
// compose, a read-back), whether the camera moved, and the GI's stage. Every ProfileWindowSeconds: the window's frame
// times, moving against still, the sections' cost a frame, and the GPU's passes' a frame (the host's cumulative pass
// totals, differenced). A slow frame (over SpikeMs, or SpikeOverMedian x the window's median) at once, with its
// breakdown. At exit, the session's.
internal sealed partial class SponzaLoop
{
    private static readonly bool profileOn = Environment.GetEnvironmentVariable("BLIX_PROFILE") != "0";
    private const double ProfileWindowSeconds = 5.0;
    private const double SpikeMs = 50.0, SpikeOverMedian = 2.5;

    private readonly struct ProfFrame
    {
        public ProfFrame(double interval, double wait, double encode, double present, bool moving, string state, Dictionary<string, double> sections, double gcPause, int gen2)
        { Interval = interval; Wait = wait; Encode = encode; Present = present; Moving = moving; State = state; Sections = sections; GcPause = gcPause; Gen2 = gen2; }
        public double GcPause { get; }
        public int Gen2 { get; }
        public double Interval { get; }
        public double Wait { get; }
        public double Encode { get; }
        public double Present { get; }
        public bool Moving { get; }
        public string State { get; }
        public Dictionary<string, double> Sections { get; }
    }

    private readonly Stopwatch profFrameClock = new(), profWindowClock = new();
    private Dictionary<string, double> profSections = new(StringComparer.Ordinal);
    private readonly List<ProfFrame> profWindow = new(), profSession = new();
    private Dictionary<string, GpuPassTotal>? profGpuAtWindow;
    private Vector3 profLastCameraPosition;
    private float profLastYaw, profLastPitch;
    private int profSpikesThisWindow;
    private TimeSpan profGcPause;
    private int profGen2;
    // The Metal tracer's cumulative GPU time at the window's start (its work is outside the Vulkan pass timings).
    private double profMetalTraceMs, profMetalShadowMs;
    // The frame's size, for the TAA readout's share of pixels (set where the frame is recorded).
    private (int Width, int Height) profFrameSize;
    private long profMetalTraces, profMetalShadows;
    private long profFrames;

    // A section of the demo's CPU work this frame (nested sections count in each: read them as "of which").
    private readonly struct ProfScope : IDisposable
    {
        private readonly SponzaLoop? owner;
        private readonly string name;
        private readonly long start;

        public ProfScope(SponzaLoop owner, string name)
        {
            this.owner = profileOn ? owner : null;
            this.name = name;
            start = profileOn ? Stopwatch.GetTimestamp() : 0;
        }

        public void Dispose()
        {
            if (owner is null) return;
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            owner.profSections[name] = owner.profSections.TryGetValue(name, out var had) ? had + ms : ms;
        }
    }

    private ProfScope Prof(string name) => new(this, name);

    // At the top of RenderFrame: the last frame closed (its interval, the host's phases for it, its sections), the next opened.
    private void ProfileFrameBegin()
    {
        if (!profileOn) return;
        profFrames++;
        if (!profFrameClock.IsRunning)
        {
            profFrameClock.Start();
            profWindowClock.Start();
            profGpuAtWindow = new Dictionary<string, GpuPassTotal>(host.Timing.GpuPassTotals);
            profLastCameraPosition = cameraPosition;
            return;
        }
        var interval = profFrameClock.Elapsed.TotalMilliseconds;
        profFrameClock.Restart();
        var moving = Vector3.Distance(cameraPosition, profLastCameraPosition) > 1e-4f
            || MathF.Abs(camera.Yaw - profLastYaw) > 1e-3f || MathF.Abs(camera.Pitch - profLastPitch) > 1e-3f;
        profLastCameraPosition = cameraPosition;
        profLastYaw = camera.Yaw;
        profLastPitch = camera.Pitch;
        var last = host.Timing.LastFrame;
        var state = $"{(CookScreenNow ? "cook-screen" : "scene")} {sunNodeStage}{(liveRetargeting ? " re-aiming" : "")}{(fineGathering ? " fine" : "")}";
        // The garbage collector's pauses over the frame, and its full collections (a 6-7 GB heap).
        var gcPause = GC.GetTotalPauseDuration();
        var gen2 = GC.CollectionCount(2);
        var frame = new ProfFrame(interval, last?.WaitMs ?? 0, last?.EncodeMs ?? 0, last?.SubmitPresentMs ?? 0, moving, state, profSections,
            (gcPause - profGcPause).TotalMilliseconds, gen2 - profGen2);
        profGcPause = gcPause;
        profGen2 = gen2;
        profSections = new Dictionary<string, double>(StringComparer.Ordinal);
        profWindow.Add(frame);
        profSession.Add(frame);
        if (profSession.Count > 200_000) profSession.RemoveRange(0, 100_000);
        // A slow frame, at once: against a fixed bar and the window's median so far.
        var median = profWindow.Count >= 10 ? Percentile(profWindow.Select(f => f.Interval), 0.5) : 16.7;
        if ((interval > SpikeMs || interval > SpikeOverMedian * median) && interval > 20.0 && profSpikesThisWindow < 6)
        {
            profSpikesThisWindow++;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[profile] slow frame {profFrames}: {interval:0.0} ms (median {median:0.0}){(moving ? " moving" : "")}, {state}; host wait {frame.Wait:0.0} encode {frame.Encode:0.0} submit/present {frame.Present:0.0}; gc pause {frame.GcPause:0.0}{(frame.Gen2 > 0 ? " (a full collection)" : "")}; {Sections(frame.Sections, 6)}"));
        }
        if (profWindowClock.Elapsed.TotalSeconds >= ProfileWindowSeconds) ProfileWindowEnd();
    }

    private static double Percentile(IEnumerable<double> values, double q)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
    }

    private static string Sections(Dictionary<string, double> sections, int top) =>
        sections.Count == 0 ? "no sections" : string.Join(", ", sections.OrderByDescending(s => s.Value).Take(top)
            .Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Key} {s.Value:0.0}")));

    private static string FrameStats(List<ProfFrame> frames, string label)
    {
        if (frames.Count == 0) return $"{label} none";
        var iv = frames.Select(f => f.Interval).ToArray();
        return string.Create(CultureInfo.InvariantCulture,
            $"{label} {frames.Count} frames p50 {Percentile(iv, 0.5):0.0} p95 {Percentile(iv, 0.95):0.0} max {iv.Max():0.0} ms ({1000.0 * frames.Count / Math.Max(1e-9, iv.Sum()):0.0} fps)");
    }

    // The sections' mean cost a frame over these frames.
    private static string SectionMeans(List<ProfFrame> frames, int top)
    {
        var sums = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var f in frames)
            foreach (var (k, v) in f.Sections) sums[k] = sums.TryGetValue(k, out var had) ? had + v : v;
        return sums.Count == 0 ? "no sections" : string.Join(", ", sums.OrderByDescending(s => s.Value).Take(top)
            .Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Key} {s.Value / frames.Count:0.00}")));
    }

    private void ProfileWindowEnd()
    {
        var frames = profWindow.ToList();
        profWindow.Clear();
        profSpikesThisWindow = 0;
        var seconds = profWindowClock.Elapsed.TotalSeconds;
        profWindowClock.Restart();
        var moving = frames.Where(f => f.Moving).ToList();
        var still = frames.Where(f => !f.Moving).ToList();
        // The GPU's passes over the window: the host's cumulative totals differenced, a frame.
        var gpuNow = new Dictionary<string, GpuPassTotal>(host.Timing.GpuPassTotals);
        var gpu = new List<(string Pass, double Ms)>();
        foreach (var (pass, total) in gpuNow)
        {
            var before = profGpuAtWindow is not null && profGpuAtWindow.TryGetValue(pass, out var b) ? b : default;
            var d = total - before;
            if (d.TotalMs > 0) gpu.Add((pass, d.TotalMs / Math.Max(1, frames.Count)));
        }
        profGpuAtWindow = gpuNow;
        var states = frames.GroupBy(f => f.State).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[profile] {seconds:0.0} s: {FrameStats(frames, "all")}; {FrameStats(moving, "moving")}; {FrameStats(still, "still")}; states {string.Join(", ", states)}"));
        if (frames.Count == 0) return;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[profile]   host a frame: wait {frames.Average(f => f.Wait):0.0}, encode {frames.Average(f => f.Encode):0.0}, submit/present {frames.Average(f => f.Present):0.0} ms; gc pause {frames.Sum(f => f.GcPause):0} ms in all ({frames.Sum(f => f.Gen2)} full); clipmap rested {clipmapRested} frames so far; sections a frame (ms): {SectionMeans(frames, 10)}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[profile]   gpu a frame (ms, {gpu.Sum(g => g.Ms):0.0} in all): {string.Join(", ", gpu.OrderByDescending(g => g.Ms).Take(12).Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Pass} {g.Ms:0.00}")))}"));
        if (frameTracer is { } tracer)
        {
            var trace = tracer.TraceStats;
            var shadow = tracer.ShadowStatsOf(-1);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[profile]   metal a frame: closest traces {(trace.TotalGpuMs - profMetalTraceMs) / frames.Count:0.00} ms ({(trace.Dispatches - profMetalTraces) / (double)frames.Count:0.0} batches), shadow traces {(shadow.TotalGpuMs - profMetalShadowMs) / frames.Count:0.00} ms ({(shadow.Dispatches - profMetalShadows) / (double)frames.Count:0.0} batches); allocated {tracer.Memory.AllocatedMb:N0} of {tracer.Memory.RecommendedMb:N0} MB recommended"));
            profMetalTraceMs = trace.TotalGpuMs; profMetalTraces = trace.Dispatches;
            profMetalShadowMs = shadow.TotalGpuMs; profMetalShadows = shadow.Dispatches;
        }
        // --taa-count-rejection: the share of pixels the resolve took history for, and of those the share it cut back.
        if (taaCountRejection && !taaStats.Equals(default(GpuBufferHandle)))
        {
            var c = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(taaStats, 0, 8).AsSpan()).ToArray();
            device.WriteGpuBuffer(taaStats, 0, new byte[8]);
            var pixels = (double)profFrameSize.Width * profFrameSize.Height * frames.Count;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[profile]   taa: history taken at {100.0 * c[1] / Math.Max(1.0, pixels):0.0}% of pixels, cut back at {100.0 * c[0] / Math.Max(1u, c[1]):0.0}% of those"));
        }
        if (moving.Count > 0)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[profile]   moving, sections a frame (ms): {SectionMeans(moving, 8)}"));
    }

    // At exit: the session's frames, moving against still, and its slowest.
    private void ProfileSessionEnd()
    {
        if (!profileOn || profSession.Count == 0) return;
        var moving = profSession.Where(f => f.Moving).ToList();
        var still = profSession.Where(f => !f.Moving).ToList();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[profile] session: {FrameStats(profSession, "all")}; {FrameStats(moving, "moving")}; {FrameStats(still, "still")}."));
        Console.WriteLine($"[profile] session, sections a frame (ms): {SectionMeans(profSession, 12)}");
        if (moving.Count > 0) Console.WriteLine($"[profile] session moving, sections a frame (ms): {SectionMeans(moving, 12)}");
        foreach (var f in profSession.OrderByDescending(f => f.Interval).Take(8))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[profile] session slowest: {f.Interval:0.0} ms{(f.Moving ? " moving" : "")}, {f.State}; wait {f.Wait:0.0} encode {f.Encode:0.0} present {f.Present:0.0}; gc pause {f.GcPause:0.0}{(f.Gen2 > 0 ? " (full)" : "")}; {Sections(f.Sections, 5)}"));
    }
}
