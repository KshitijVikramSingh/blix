namespace Blix.Graphics;

/// <summary>GPU time per pass over the last N frames, from a host's cumulative totals.</summary>
/// <remarks>
/// <b>A window, exactly, rather than a smoothed guess.</b> <see cref="IFrameTiming.GpuPassTotals"/> only ever
/// grows, so the time a pass took over any span is the later total less the earlier one. Keep the last N
/// snapshots and each pass's mean is that difference over its resolutions: a figure that can be stated
/// ("mean of the last 60 frames") and compared across runs, which a smoothed average cannot. The overlay's
/// Perf tab reads one; so can an application that shows a pass's cost beside its own controls.
/// <para>
/// It is display, not measurement. These are attribution: on a tile-based GPU (Apple, through MoltenVK) a
/// pass's timestamps bracket its encoding rather than its tiled execution, so the passes need not sum to the
/// frame, and a pass hidden behind another reads as more than it costs. Paired A/B runs answer that.
/// </para>
/// </remarks>
public sealed class GpuPassWindow
{
    private readonly IFrameTiming timing;
    private readonly Dictionary<string, GpuPassTotal>[] ring;
    private int next;
    private int filled;

    /// <param name="timing">The host's timing, usually <c>host.Timing</c>.</param>
    /// <param name="frames">How many frames the window spans.</param>
    public GpuPassWindow(IFrameTiming timing, int frames = 60)
    {
        ArgumentNullException.ThrowIfNull(timing);
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 2);
        this.timing = timing;
        ring = new Dictionary<string, GpuPassTotal>[frames];
    }

    /// <summary>How many frames the window spans once full.</summary>
    public int Frames => ring.Length;

    /// <summary>How many frames it spans now: fewer until it has been sampled <see cref="Frames"/> times.</summary>
    public int Spanned => Math.Max(0, filled - 1);

    /// <summary>Takes this frame's totals. Call once a frame, after the frame is submitted.</summary>
    public void Sample()
    {
        if (!timing.GpuTimestampsSupported) return;
        var slot = ring[next] ??= new Dictionary<string, GpuPassTotal>(StringComparer.Ordinal);
        slot.Clear();
        foreach (var (pass, total) in timing.GpuPassTotals) slot[pass] = total;
        next = (next + 1) % ring.Length;
        if (filled < ring.Length) filled++;
    }

    /// <summary>Mean GPU milliseconds per run of <paramref name="pass"/> over the window, or 0.</summary>
    public double MeanMs(string pass) => Window(pass).MeanMs;

    /// <summary>Every pass with time in the window, heaviest first.</summary>
    public IReadOnlyList<(string Pass, double MeanMs)> ByCost()
    {
        if (filled < 2) return Array.Empty<(string, double)>();
        var list = new List<(string Pass, double MeanMs)>();
        foreach (var pass in Latest.Keys)
        {
            var mean = MeanMs(pass);
            if (mean > 0.0) list.Add((pass, mean));
        }

        list.Sort((a, b) => b.MeanMs.CompareTo(a.MeanMs));
        return list;
    }

    private Dictionary<string, GpuPassTotal> Latest => ring[(next - 1 + ring.Length) % ring.Length];

    private Dictionary<string, GpuPassTotal> Oldest => ring[filled < ring.Length ? 0 : next];

    private GpuPassTotal Window(string pass)
    {
        if (filled < 2 || !Latest.TryGetValue(pass, out var latest)) return default;
        // A pass that first ran inside the window started from nothing.
        return Oldest.TryGetValue(pass, out var oldest) ? latest - oldest : latest;
    }
}
