using System.Diagnostics;

namespace RTSGame.Simulation.Navigation;

/// <summary>What the search did. Whether the caller got a route is a separate question — see RouteRow.</summary>
/// <remarks>
/// <b>Two axes, because the first version of this collapsed them and immediately mislabelled a search.</b>
/// The ending and the answer are not the same fact: a search can close its goal and have the route thrown
/// away by smoothing, and a search stopped by the budget can hand back a usable partial. The first reading
/// of the placement click filed a budget-stopped search as <see cref="Unresolvable"/> — because it tested
/// nullness before the budget — which reads as "the goal could not be placed" and would have sent the next
/// session to look at goal resolution.
/// <para>
/// So these describe the search, ordered narrowest cause first, and <c>Answered</c> carries what the body
/// actually received. The distinction that matters most is between <see cref="Reached"/> and the bounded
/// endings: a search that found its goal after 150,000 expansions is a heuristic that has stopped steering,
/// and a search that spent the same 150,000 and was stopped is a body about to ask again from wherever it
/// got to. The first is a cost; the second is a cost that recurs.
/// </para></remarks>
internal enum RouteOutcome
{
    /// <summary>Answered by the straight segment. No search ran.</summary>
    Direct,
    /// <summary>The search closed the goal cell.</summary>
    Reached,
    /// <summary>The expansion budget stopped it and it handed back the part it had solved.</summary>
    Partial,
    /// <summary>The budget stopped it before it reached anything nearer the goal than the start. No route.</summary>
    Truncated,
    /// <summary>The open set emptied without reaching the goal: nothing walkable connects the two.</summary>
    Exhausted,
    /// <summary>The order's pooled allowance was already spent, so no search was run at all.</summary>
    Refused,
    /// <summary>No search ran and no route came back: start or goal could not be placed at all.</summary>
    Unresolvable,
}

/// <summary>One route request, as it happened.</summary>
internal readonly record struct RouteLogEntry(
    long Sequence,
    RouteReason Reason,
    RouteOutcome Outcome,
    bool Answered,
    long Expansions,
    double Milliseconds,
    int CellSpan);

/// <summary>One reason's share of the routing, over some window.</summary>
/// <summary>
/// One reason's share of the routing over some window, and how much of it the bodies could use.
/// </summary>
/// <param name="Answered">
/// Requests that came back with a route. <b>Queries minus this is time bought and thrown away</b>, which is
/// the figure the placement click turned on: both of its searches cost a second between them and one of the
/// two bodies got nothing at all.
/// </param>
internal readonly record struct RouteRow(
    RouteReason Reason,
    long Queries,
    long Answered,
    long Expansions,
    double Milliseconds,
    long[] Outcomes);

/// <summary>
/// What routing was asked for, by whom, and how it ended.
/// </summary>
/// <remarks>
/// <b>Two instruments, because the events being measured are two different sizes.</b> A quiet tick issues
/// hundreds of route queries and wants totals; the click after a placement change issues two and wants them
/// individually. §115 measured the second with the first and got "2 route queries for 20 bodies", which was
/// enough to kill a wrong hypothesis (a repath storm) and not enough to name what was left.
/// <para>
/// So: per-reason totals, and a ring of the last <see cref="LogCapacity"/> requests kept verbatim. The ring
/// is what answers a two-search event, and it says so when a window overflowed it rather than quietly
/// reporting the tail as the whole.
/// </para>
/// <para>
/// Wall clock lives in here, which is why this is not fingerprinted and why nothing in the simulation may
/// read it back. It is a report.
/// </para></remarks>
internal sealed class RouteAttribution
{
    /// <summary>
    /// Requests kept verbatim. Sixty-four covers a placement click (two) and a cohort order (twenty) whole.
    /// </summary>
    private const int LogCapacity = 64;

    internal static readonly RouteReason[] Reasons = Enum.GetValues<RouteReason>();
    internal static readonly RouteOutcome[] Outcomes = Enum.GetValues<RouteOutcome>();

    private readonly long[] queries = new long[Reasons.Length];
    private readonly long[] answered = new long[Reasons.Length];
    private readonly long[] expansions = new long[Reasons.Length];
    private readonly long[] ticks = new long[Reasons.Length];
    private readonly long[,] outcomes = new long[Reasons.Length, Outcomes.Length];
    private readonly RouteLogEntry[] log = new RouteLogEntry[LogCapacity];

    /// <summary>Requests recorded since construction. The ring's sequence numbers are positions in this.</summary>
    public long Recorded { get; private set; }

    public void Record(
        RouteReason reason,
        RouteOutcome outcome,
        bool wasAnswered,
        long expansionCount,
        long elapsedTicks,
        int cellSpan)
    {
        var index = (int)reason;
        queries[index]++;
        if (wasAnswered) answered[index]++;
        expansions[index] += expansionCount;
        ticks[index] += elapsedTicks;
        outcomes[index, (int)outcome]++;
        log[(int)(Recorded % LogCapacity)] = new RouteLogEntry(
            Recorded,
            reason,
            outcome,
            wasAnswered,
            expansionCount,
            Stopwatch.GetElapsedTime(0, elapsedTicks).TotalMilliseconds,
            cellSpan);
        Recorded++;
    }

    /// <summary>A copy, for a caller that wants to measure a window by difference.</summary>
    public RouteAttribution Snapshot()
    {
        var copy = new RouteAttribution();
        Array.Copy(queries, copy.queries, queries.Length);
        Array.Copy(answered, copy.answered, answered.Length);
        Array.Copy(expansions, copy.expansions, expansions.Length);
        Array.Copy(ticks, copy.ticks, ticks.Length);
        Array.Copy(outcomes, copy.outcomes, outcomes.Length);
        copy.Recorded = Recorded;
        return copy;
    }

    /// <summary>Every reason that asked for anything since <paramref name="before"/>, costliest first.</summary>
    public List<RouteRow> Since(RouteAttribution before)
    {
        var rows = new List<RouteRow>();
        foreach (var reason in Reasons)
        {
            var index = (int)reason;
            var count = queries[index] - before.queries[index];
            if (count == 0) continue;
            var byOutcome = new long[Outcomes.Length];
            foreach (var outcome in Outcomes)
            {
                byOutcome[(int)outcome] = outcomes[index, (int)outcome] - before.outcomes[index, (int)outcome];
            }

            rows.Add(new RouteRow(
                reason,
                count,
                answered[index] - before.answered[index],
                expansions[index] - before.expansions[index],
                Stopwatch.GetElapsedTime(0, ticks[index] - before.ticks[index]).TotalMilliseconds,
                byOutcome));
        }

        rows.Sort((left, right) => right.Milliseconds.CompareTo(left.Milliseconds));
        return rows;
    }

    /// <summary>
    /// This window's routing as lines, ready to print. One formatter, for one reason.
    /// </summary>
    /// <remarks>
    /// <b>Shared between the fixture and the chair on purpose.</b> §119: the stall a player felt was
    /// click-caused and the fixture that ranked the debt could not produce it, so the two have to be able to
    /// print the same thing — a live report that formats routing its own way is a second opinion nobody will
    /// reconcile. Returns nothing at all when nothing asked, because this is printed once a second in the
    /// live loop and a quiet second should be silent.
    /// </remarks>
    public List<string> Describe(RouteAttribution before, long logFrom, int verbatimLimit = 12)
    {
        var lines = new List<string>();
        var rows = Since(before);
        if (rows.Count == 0) return lines;

        lines.Add($"routing, by who asked ({rows.Sum(row => row.Queries):N0} queries)");
        foreach (var row in rows)
        {
            var endings = string.Join(", ", Outcomes
                .Where(outcome => row.Outcomes[(int)outcome] > 0)
                .Select(outcome => $"{row.Outcomes[(int)outcome]} {outcome.ToString().ToLowerInvariant()}"));
            lines.Add(
                $"  {row.Reason,-18} {row.Queries,5:N0} queries {row.Milliseconds,9:F1} ms " +
                $"{row.Expansions,9:N0} cells — {row.Answered} answered, {endings}");
        }

        var (entries, lost) = LogSince(logFrom);
        if (entries.Count == 0 || entries.Count > verbatimLimit) return lines;
        lines.Add("  each request, in order" +
                  (lost > 0 ? $" ({lost} older ones fell out of the ring)" : string.Empty));
        foreach (var entry in entries)
        {
            lines.Add(
                $"    #{entry.Sequence,-6} {entry.Reason,-18} {entry.Outcome,-12} " +
                $"{(entry.Answered ? "routed  " : "NOTHING ")} " +
                $"{entry.Expansions,9:N0} cells {entry.Milliseconds,8:F1} ms " +
                $"over {entry.CellSpan:N0} cells of map");
        }

        return lines;
    }

    /// <summary>
    /// Requests recorded at or after <paramref name="sequence"/>, oldest first, and how many were lost.
    /// </summary>
    /// <remarks>
    /// The lost count is the whole reason this returns a pair. A ring reporting its tail as though it were
    /// the window is the same failure this class exists to correct one level up.
    /// </remarks>
    public (List<RouteLogEntry> Entries, long Lost) LogSince(long sequence)
    {
        var oldest = Math.Max(sequence, Recorded - LogCapacity);
        var entries = new List<RouteLogEntry>();
        for (var at = oldest; at < Recorded; at++)
        {
            entries.Add(log[(int)(at % LogCapacity)]);
        }

        return (entries, oldest - sequence);
    }
}
