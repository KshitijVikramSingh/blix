using System.Numerics;

namespace Blix.Diagnostics;

/// <summary>
/// Where a thing has been, remembered across frames.
/// </summary>
/// <remarks>
/// <b>The second singular.</b> Everything a producer emitted used to die with its frame: channels clear on
/// BeginFrame, and the only memory diagnostics had was a ring of whole finished frames that the overlay
/// scans to draw sparklines. Scalar history therefore already worked — a trail did not, and could not be
/// built on top, because a draw command cannot outlive the frame that made it.
/// <para>
/// That constraint is why the one real instrument this codebase has for a question about time —
/// The external RTSGame consumer's StallCensus lives in the game rather than the engine, hand-rolling per-body arrays of
/// accumulators. A path through space is the commonest temporal question there is for anything that moves,
/// and Spear's whole domain is things that move.
/// </para>
/// <para>
/// <b>Storage only, no reductions.</b> Remembering points removes a constraint; deciding what a path
/// MEANS — smoothed, resampled, summarised — is policy, and policy waits for a second consumer to
/// disagree with the first.
/// </para>
/// </remarks>
public sealed class DebugTrails
{
    /// <summary>
    /// Hard cap on remembered points per trail, whatever duration was asked for.
    /// </summary>
    /// <remarks>
    /// A safety valve on memory, not a tuning knob: a producer sampling every frame for a minute would
    /// otherwise hold several thousand points per trail and grow without bound if it never stopped. When it
    /// bites, the trail is shorter than the duration requested — so it says so, once per path, rather than
    /// quietly returning a truncated answer that looks like a complete one.
    /// </remarks>
    public const int MaxPointsPerTrail = 4096;

    private readonly Dictionary<string, Trail> trails = new(StringComparer.Ordinal);
    private readonly HashSet<string> warnedCapped = new(StringComparer.Ordinal);

    /// <summary>How many trails are being remembered.</summary>
    public int Count => trails.Count;

    /// <summary>
    /// Records where this thing is now, and returns everywhere it has been within <paramref name="seconds"/>.
    /// </summary>
    /// <remarks>
    /// The returned list is the store's own and is rewritten on the next call, so a caller that keeps it
    /// past this frame must copy — which is exactly what the draw channel does, because a DebugFrame
    /// snapshot must never be mutable after the fact.
    /// </remarks>
    public IReadOnlyList<Vector3> Append(
        string path, Vector3 point, double nowMs, float seconds, int frameNumber = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!trails.TryGetValue(path, out var trail))
        {
            trail = new Trail();
            trails[path] = trail;
        }

        // <b>One sample per path per frame, however many times it is asked for.</b> Found by the test that
        // draws one trail into two views: each call appended, so the same body sampled twice a frame and
        // the two views disagreed about its history by one point. A trail is where something HAS BEEN,
        // which is a fact about the frame — not about how many times somebody asked to see it.
        var repeat = trail.HasSampled && trail.LastFrame == frameNumber;
        if (!repeat)
        {
            trail.Points.Add(point);
            trail.Times.Add(nowMs);
            trail.LastFrame = frameNumber;
            trail.HasSampled = true;
        }

        // Age out by time first, which is what the caller actually asked for.
        trail.LastTouchedMs = nowMs;

        var oldest = nowMs - seconds * 1000.0;
        var drop = 0;
        while (drop < trail.Times.Count && trail.Times[drop] < oldest) drop++;
        if (drop > 0)
        {
            trail.Points.RemoveRange(0, drop);
            trail.Times.RemoveRange(0, drop);
        }

        // Then the memory cap, which can only bite if the duration alone was not enough to bound it.
        var excess = trail.Points.Count - MaxPointsPerTrail;
        if (excess > 0)
        {
            trail.Points.RemoveRange(0, excess);
            trail.Times.RemoveRange(0, excess);
            if (warnedCapped.Add(path))
            {
                Console.Error.WriteLine(
                    $"[diagnostics] trail '{path}' hit the {MaxPointsPerTrail}-point cap and is now shorter " +
                    $"than the {seconds:F1}s requested. Sample less often, or ask for less history.");
            }
        }

        return trail.Points;
    }

    /// <summary>Forgets one trail — for a thing that no longer exists.</summary>
    public bool Forget(string path) => trails.Remove(path);

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        trails.Clear();
        warnedCapped.Clear();
    }

    /// <summary>
    /// Drops trails nothing has touched for a while, so a churn of short-lived paths cannot leak.
    /// </summary>
    /// <remarks>
    /// Called by the system once a frame. Without it, every entity that ever had a trail would keep its
    /// points forever after it died — the points would age to empty, but the dictionary entry would not.
    /// </remarks>
    public void Expire(double nowMs, float staleSeconds)
    {
        if (trails.Count == 0) return;

        var cutoff = nowMs - staleSeconds * 1000.0;
        List<string>? dead = null;
        foreach (var (path, trail) in trails)
        {
            if (trail.Times.Count == 0 || trail.LastTouchedMs < cutoff)
            {
                (dead ??= new List<string>()).Add(path);
            }
        }

        if (dead is null) return;
        foreach (var path in dead)
        {
            trails.Remove(path);
            warnedCapped.Remove(path);
        }
    }

    private sealed class Trail
    {
        public List<Vector3> Points { get; } = new();

        public List<double> Times { get; } = new();

        public int LastFrame { get; set; }

        public bool HasSampled { get; set; }

        public double LastTouchedMs { get; set; }
    }
}
