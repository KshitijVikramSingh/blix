using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Jobs;

namespace RTSGame.Debug;

/// <summary>
/// Counts, over a whole run, how long bodies spend unable to move — and whether they recover.
/// </summary>
/// <remarks>
/// <b>Built because the population under discussion had never been measured, only watched.</b> §183. Red
/// cylinders were reported from the chair four times across §170–182 and the fix was attempted three times,
/// twice from the wrong end. The pen self-tests carry a rich census of exactly this — <c>ever-red</c>,
/// <c>longest-red</c>, <c>peak-internal-stuck</c>, a per-body line naming position, speed, waypoints and
/// repaths — and the hands-off two-village leg, which is the run whose situation matches what was actually
/// reported, prints none of it. Grepping that leg's output for stall figures returns zero lines.
/// <para>
/// <b>The question it exists to answer.</b> §182 named two thresholds and then had to admit the longer one
/// is not a stranded line: the pen legs report <c>arrived=30/30</c> with <c>longest-red=4.37s</c>, so bodies
/// stall for three and a half times 1.25 s and still get where they were going. Before anything can be
/// ratcheted, and long before §180's throw, somebody has to find where — or whether — the line between
/// <em>recovers</em> and <em>never arrives</em> actually falls. That is a distribution, not a constant.
/// </para>
/// <para>
/// <b>Recovery is measured by work done, not by the stall ending.</b> A stall ending only says the body
/// started moving; it does not say it got anywhere. <see cref="AgentJobs.LegsCompleted"/> does: it rises
/// when a body reaches a place and finishes what it went there for. So each episode is followed for a grace
/// window afterwards and asked whether that counter moved. An episode that ends and is followed by a
/// completed leg was traffic. One that ends and is followed by nothing, repeatedly, is a body going nowhere
/// however often it twitches — which is a state the old "did it stop being red" test would have scored as a
/// success.
/// </para>
/// <para>
/// It reads the world and never writes it, and it uses <see cref="StallReporting.StalledSeconds"/> rather
/// than the simulation's own figure, deliberately: an instrument that reads its threshold from the thing it
/// measures cannot be used to compare two versions of that thing. §182.
/// </para>
/// </remarks>
internal sealed class StallCensus
{
    /// <summary>One continuous spell of a body not getting anywhere.</summary>
    /// <param name="Body">Which body, so a bad one can be followed into the other logs.</param>
    /// <param name="Seconds">How long the spell lasted.</param>
    /// <param name="PeakStuckSeconds">
    /// The highest stall time reached. Larger than <paramref name="Seconds"/> is normal and not a bug: stall
    /// time decays at twice real time when a body moves, so a spell can end with time still on the clock.
    /// </param>
    /// <param name="ContactAtPeak">
    /// Whether anything was touching the body at its worst moment. <b>The distinction that decides the
    /// fix</b> and the one I kept conflating: contact means bodies cannot get past each other and wants a
    /// separation fix, no contact means the body cannot reach where it is going and wants a routing fix.
    /// </param>
    /// <param name="Assignment">What it had been told to do.</param>
    /// <param name="WentOnToWork">
    /// Whether it completed a leg within the grace window after the spell ended — the difference between a
    /// body held up and a body going nowhere.
    /// </param>
    public readonly record struct Episode(
        int Body,
        float Seconds,
        float PeakStuckSeconds,
        bool ContactAtPeak,
        AssignmentKind Assignment,
        bool WentOnToWork);

    /// <summary>
    /// How near the end of a run a spell has to be before "it never worked again" means nothing.
    /// </summary>
    /// <remarks>
    /// <b>There is no grace window in the verdict, and that is the second thing this instrument got wrong.</b>
    /// The first cut gave each spell twenty seconds to be followed by a completed leg. But
    /// <c>EconomySystem.WorkShiftSeconds</c> is <b>forty-five</b> — a single reaping shift is more than twice
    /// the window — so every body that stalled at the start of a shift scored unproductive by construction.
    /// The instrument reported <c>Work x41</c> and <c>Build x64</c> unproductive, and those numbers were
    /// measuring the window rather than the settlement. Fifth instrument in this arc to be wrong; the first
    /// one caught before its number was believed.
    /// <para>
    /// So productivity is asked without a deadline: did this body complete a leg at <em>any</em> point after
    /// the spell, before the run ended. The only residue is a spell so late in the run that nothing had time
    /// to follow it, and this figure exists solely to report how many of those there were rather than to
    /// decide anything. Set past the longest leg plus a walk across a village.
    /// </para>
    /// </remarks>
    public const float TailSeconds = 120f;

    private readonly List<Episode> episodes = new();
    private readonly HashSet<int> everStalled = new();
    private readonly HashSet<int> everWedged = new();

    private float[] startedAt = Array.Empty<float>();
    private float[] peak = Array.Empty<float>();
    private bool[] contactAtPeak = Array.Empty<bool>();
    private AssignmentKind[] doing = Array.Empty<AssignmentKind>();

    // <b>LegsCompleted resets to zero on every reassignment</b>, because JobSystem.Assign replaces the whole
    // AgentJobs struct. So the raw counter cannot be compared across a spell: a body that finished its leg
    // and was then given a new job reads as having done nothing. These two accumulate a monotonic total the
    // census owns — the last raw reading, and the sum of every rise in it — which survives reassignment.
    private int[] legsSeen = Array.Empty<int>();
    private int[] legsTotal = Array.Empty<int>();

    // <b>A second question the first one structurally cannot see.</b> §186: three villagers spent a
    // thirty-four-minute run holding a reaping job while standing in the enemy's village, and the stall
    // census reported 519 of 519 spells healthy — correctly, because StuckSeconds only accrues while a body
    // WANTS to move and is not moving. A body shuffling about under an order wants to move and does move.
    // So "not getting anywhere" and "not doing its job" are different questions, and only the second one
    // finds a body that is content to be somewhere useless.
    //
    // They live in one class because they are one sweep over one definition of working. They are reported
    // separately because a run can be perfect on one and terrible on the other, which is what happened.
    private float[] keptFromWorkSince = Array.Empty<float>();
    private float[] worstKeptFromWork = Array.Empty<float>();
    private AssignmentKind[] keptFrom = Array.Empty<AssignmentKind>();
    private InterruptKind[] keptBy = Array.Empty<InterruptKind>();
    private float[] keptAtDistance = Array.Empty<float>();

    // Spells whose grace window has not run out yet, so productivity is still undecided.
    private readonly List<int> pendingBody = new();
    private readonly List<Episode> pendingEpisode = new();
    private readonly List<float> pendingEndedAt = new();
    private readonly List<int> pendingLegs = new();

    private float elapsedSeconds;

    /// <summary>Bodies that were stalled at least once.</summary>
    public int BodiesEverStalled => everStalled.Count;

    /// <summary>
    /// Bodies that ever crossed <see cref="AgentDefaults.WedgedSeconds"/> — the ones drawn red.
    /// </summary>
    /// <remarks>
    /// <b>The acceptance test for §190, and the ratchet §180 asked for.</b> The paint threshold is now set
    /// past the longest stall that was ever followed by work in any measured run, so a body drawn red is a
    /// body that did not recover. That makes this a count of real faults rather than of traffic — which is
    /// what a ratchet needs and what 0.35 s could never provide, because at 0.35 s the figure was every
    /// body in the settlement several times a minute.
    /// </remarks>
    public int BodiesEverWedged => everWedged.Count;

    /// <summary>Every completed spell, productivity resolved.</summary>
    public IReadOnlyList<Episode> Episodes => episodes;

    /// <summary>The worst stall time any body reached.</summary>
    public float PeakStuckSeconds { get; private set; }

    /// <summary>Bodies still stalled when the run ended, which is the only unambiguous stranding.</summary>
    public int StalledAtTheEnd { get; private set; }

    /// <summary>Unproductive spells that ended too near the end of the run to be judged.</summary>
    public int UnjudgedInTheTail { get; private set; }

    /// <summary>
    /// Activities finished by everybody over the run. <b>A churn rate, not a measure of production.</b>
    /// </summary>
    /// <remarks>
    /// <b>An answer of exactly zero is a question about the instrument, not a finding.</b> The raid leg came
    /// back with 0 of 600 spells followed by work, which either means every stalled body in a raid is going
    /// nowhere or means nothing in that scenario completes legs at all. Those need telling apart before
    /// either is believed, and the total is what tells them apart: work happening elsewhere while no stalled
    /// body joins in is a real finding, and no work anywhere is a fact about the scenario.
    /// <para>
    /// <b>Updated every sample, not only at the close</b> — which it was, for one run. The live game prints
    /// this figure every thirty seconds and never closes, so it read a flat zero beside "41 of 41 spells
    /// were followed by work": a self-contradiction, since productivity IS this counter rising. Caught by
    /// the contradiction rather than by the zero, and worth the note because the fix for the raid leg's zero
    /// was the thing that introduced it.
    /// </para>
    /// <para>
    /// <b>And then it had to be renamed, because it is not work.</b> Third correction to one small counter.
    /// It reached 8,599 over nine minutes across 28 bodies — one activity per body every 1.8 seconds, which
    /// is flatly impossible against a 45-second work shift. The reason is that an activity is *any* leg:
    /// a guard's half-second dwell finishes one, and a settlement full of posted militia churns them by the
    /// thousand while producing nothing. So this counts <em>activities entered and left</em> and must never
    /// be compared between runs with different assignment mixes. For production, read what the scenario
    /// itself reports — the raid leg prints "labour-seconds withheld" and grain, which are the honest units.
    /// </para>
    /// </remarks>
    public int ActivitiesFinished { get; private set; }

    /// <summary>Call once per tick, after the world has stepped.</summary>
    public void Sample(SimulationWorld world, float deltaSeconds)
    {
        elapsedSeconds += deltaSeconds;
        Grow(world.Agents.Count);

        foreach (ref readonly var agent in world.Agents.All)
        {
            var index = agent.Id.Value;
            if (index < 0 || index >= startedAt.Length) continue;
            if (!agent.IsAlive)
            {
                startedAt[index] = 0f;
                // So a reused id starts counting from scratch rather than from the dead body's tally.
                legsSeen[index] = 0;
                continue;
            }

            // Before anything else, so a leg finished on this very tick is visible to the resolution below.
            var raw = agent.Jobs.LegsCompleted;
            if (raw > legsSeen[index]) legsTotal[index] += raw - legsSeen[index];
            legsSeen[index] = raw;

            // <b>How long it has held a job without doing any of it.</b> An unassigned body is not being
            // kept from anything, and a body at its work resets the clock.
            //
            // <b>And a body fighting is not being kept from anything either.</b> First live run of this
            // reported "#6 122s on Attack at 1 m from its place" three times over — bodies locked in combat
            // beside their target, which is exactly what an Attack assignment is for. They score as kept
            // from work because a fight runs as an interrupt, so IsWorking is false throughout. Counting
            // that is crying wolf, which is the one thing an instrument in this codebase may not do.
            //
            // So this measures LABOUR withheld, and the military verbs are excluded because for them the
            // interrupt IS the job. A guard away from its post is still counted: standing somewhere else is
            // not what a post is, and §143 spent a section on exactly that.
            if (!agent.Jobs.HasAssignment ||
                agent.Jobs.Assignment.Kind == AssignmentKind.Attack ||
                JobSystem.IsWorking(in agent))
            {
                keptFromWorkSince[index] = elapsedSeconds;
            }
            else
            {
                var away = elapsedSeconds - keptFromWorkSince[index];
                if (away > worstKeptFromWork[index])
                {
                    worstKeptFromWork[index] = away;
                    keptFrom[index] = agent.Jobs.Assignment.Kind;
                    keptBy[index] = agent.Jobs.Interrupt;
                    keptAtDistance[index] = JobSystem.DistanceToPlace(in agent);
                }
            }

            PeakStuckSeconds = MathF.Max(PeakStuckSeconds, agent.StuckSeconds);
            var stalled = agent.StuckSeconds > StallReporting.StalledSeconds;

            if (stalled)
            {
                if (startedAt[index] <= 0f)
                {
                    startedAt[index] = elapsedSeconds;
                    peak[index] = 0f;
                }

                everStalled.Add(index);
                if (agent.StuckSeconds > AgentDefaults.WedgedSeconds) everWedged.Add(index);
                if (agent.StuckSeconds >= peak[index])
                {
                    peak[index] = agent.StuckSeconds;
                    contactAtPeak[index] = agent.HadAgentContactThisTick;
                    doing[index] = agent.Jobs.Assignment.Kind;
                }

                continue;
            }

            if (startedAt[index] <= 0f) continue;

            // The spell is over. Productivity is not yet known, so it waits out its grace window.
            pendingBody.Add(index);
            pendingEpisode.Add(new Episode(
                index,
                elapsedSeconds - startedAt[index],
                peak[index],
                contactAtPeak[index],
                doing[index],
                WentOnToWork: false));
            pendingEndedAt.Add(elapsedSeconds);
            pendingLegs.Add(legsTotal[index]);
            startedAt[index] = 0f;
        }

        ResolvePending(world, force: false);
        CountLegs(world);
    }

    /// <summary>Call once when the run ends, to close out whatever was still open.</summary>
    public void Close(SimulationWorld world)
    {
        Grow(world.Agents.Count);

        // <b>A body still stalled when the clock stops is the only stranding this can be sure of.</b>
        // Everything else recovered by definition, because its spell ended.
        foreach (ref readonly var agent in world.Agents.All)
        {
            var index = agent.Id.Value;
            if (!agent.IsAlive || index < 0 || index >= startedAt.Length) continue;
            if (startedAt[index] <= 0f) continue;

            StalledAtTheEnd++;
            episodes.Add(new Episode(
                index,
                elapsedSeconds - startedAt[index],
                peak[index],
                contactAtPeak[index],
                doing[index],
                WentOnToWork: false));
            startedAt[index] = 0f;
        }

        ResolvePending(world, force: true);
        CountLegs(world);
    }

    /// <summary>
    /// Legs finished by everybody now alive, which only ever rises while nobody dies.
    /// </summary>
    /// <remarks>
    /// Summed from the census's own monotonic per-body totals rather than from the live counters, for two
    /// reasons that both bit: <c>LegsCompleted</c> resets on reassignment, and a body that dies takes its
    /// counter with it. Read off the live values this figure went <em>down</em>, which would read as work
    /// being undone.
    /// </remarks>
    private void CountLegs(SimulationWorld world)
    {
        var legs = 0;
        for (var i = 0; i < legsTotal.Length; i++) legs += legsTotal[i];
        ActivitiesFinished = legs;
    }

    private void ResolvePending(SimulationWorld world, bool force)
    {
        for (var i = pendingBody.Count - 1; i >= 0; i--)
        {
            var index = pendingBody[i];
            var id = new AgentId(index);
            var gone = !world.Agents.Contains(id) || !world.Agents.Get(id).IsAlive;
            var worked = !gone && index < legsTotal.Length && legsTotal[index] > pendingLegs[i];
            // No deadline: a spell stays open until the body finishes a leg or the run stops. Waiting is
            // free, and a window shorter than a work shift is what made the first reading meaningless.
            if (!force && !worked) continue;

            if (!worked && elapsedSeconds - pendingEndedAt[i] < TailSeconds) UnjudgedInTheTail++;
            episodes.Add(pendingEpisode[i] with { WentOnToWork = worked });
            pendingBody.RemoveAt(i);
            pendingEpisode.RemoveAt(i);
            pendingEndedAt.RemoveAt(i);
            pendingLegs.RemoveAt(i);
        }
    }

    private void Grow(int count)
    {
        if (startedAt.Length >= count) return;
        var size = Math.Max(count, Math.Max(64, startedAt.Length * 2));
        Array.Resize(ref startedAt, size);
        Array.Resize(ref peak, size);
        Array.Resize(ref contactAtPeak, size);
        Array.Resize(ref doing, size);
        Array.Resize(ref legsSeen, size);
        Array.Resize(ref legsTotal, size);
        Array.Resize(ref keptFromWorkSince, size);
        Array.Resize(ref worstKeptFromWork, size);
        Array.Resize(ref keptFrom, size);
        Array.Resize(ref keptBy, size);
        Array.Resize(ref keptAtDistance, size);
        for (var i = 0; i < size; i++)
        {
            if (keptFromWorkSince[i] == 0f && worstKeptFromWork[i] == 0f)
            {
                keptFromWorkSince[i] = elapsedSeconds;
            }
        }
    }

    /// <summary>
    /// The bodies that held a job longest without doing any of it, worst first.
    /// </summary>
    /// <remarks>
    /// <b>The zombie report.</b> §186. Walking to a distant field is legitimately a minute, so this is a
    /// tail and not a threshold: what matters is a body whose stretch is out of all proportion to the
    /// others'. Each one names what it was assigned to, what interrupt was holding it, and how far it was
    /// standing from the place it was supposed to be — which together say whether it was ordered away,
    /// could not get there, or simply never tried.
    /// </remarks>
    public string DescribeKeptFromWork(int worst = 4)
    {
        var ranked = Enumerable.Range(0, worstKeptFromWork.Length)
            .Where(i => worstKeptFromWork[i] > 0f)
            .OrderByDescending(i => worstKeptFromWork[i])
            .Take(worst)
            .Select(i =>
                $"#{i} {worstKeptFromWork[i]:F0}s on {keptFrom[i]}" +
                (keptBy[i] == InterruptKind.None ? string.Empty : $" under {keptBy[i]}") +
                $" at {keptAtDistance[i]:F0} m from its place")
            .ToArray();

        return ranked.Length == 0
            ? "  [kept from work] nobody held a job without working at it"
            : "  [kept from work] worst: " + string.Join("; ", ranked);
    }

    /// <summary>
    /// The distribution, which is the whole point: one number could not have answered the question.
    /// </summary>
    /// <remarks>
    /// Printed as a histogram of spell lengths split by whether the body went on to do any work, because the
    /// thing being looked for is a <em>gap</em> — a length past which spells stop being followed by work
    /// would be the stranded line §182 could not find. A continuous distribution with no gap is also an
    /// answer, and a more interesting one: it would mean stranding is not a distinct state and §180's throw
    /// needs a different trigger than a stopwatch.
    /// </remarks>
    public string Describe(string label)
    {
        if (episodes.Count == 0)
        {
            return $"  [stalls] {label}: nobody stalled past " +
                   $"{StallReporting.StalledSeconds:F2}s in {elapsedSeconds:F0}s";
        }

        var bands = new[] { 0.5f, 1f, 2f, 4f, 8f, 16f, 32f, float.PositiveInfinity };
        var productive = new int[bands.Length];
        var barren = new int[bands.Length];
        foreach (var episode in episodes)
        {
            var band = 0;
            while (band < bands.Length - 1 && episode.Seconds > bands[band]) band++;
            if (episode.WentOnToWork) productive[band]++;
            else barren[band]++;
        }

        var rows = new List<string>();
        for (var band = 0; band < bands.Length; band++)
        {
            if (productive[band] == 0 && barren[band] == 0) continue;
            var upper = float.IsPositiveInfinity(bands[band]) ? "+" : $"{bands[band]:0.#}s";
            rows.Add($"<={upper} {productive[band]}w/{barren[band]}n");
        }

        var touching = episodes.Count(e => e.ContactAtPeak);
        var worked = episodes.Count(e => e.WentOnToWork);
        var longest = episodes.Max(e => e.Seconds);
        var longestProductive = episodes.Where(e => e.WentOnToWork).Select(e => e.Seconds)
            .DefaultIfEmpty(0f).Max();

        // <b>A body with nothing to do cannot finish a leg, so it would score as unproductive forever.</b>
        // First reading of this instrument said 108 of 171 spells were followed by no work, which looks
        // alarming until you ask what those bodies had been told to do: an idle villager twitching for a
        // third of a second has no leg to complete and is not evidence of anything. So the unproductive
        // spells are split by assignment, and only the ones under orders are a finding.
        var idle = episodes.Count(e => !e.WentOnToWork && e.Assignment == AssignmentKind.None);
        var barrenUnderOrders = episodes
            .Where(e => !e.WentOnToWork && e.Assignment != AssignmentKind.None)
            .GroupBy(e => e.Assignment)
            .OrderByDescending(group => group.Count())
            .Select(group => $"{group.Key} x{group.Count()} (worst {group.Max(e => e.Seconds):F1}s)")
            .ToArray();

        return $"  [stalls] {label}: {episodes.Count} spell(s) over {elapsedSeconds:F0}s across " +
               $"{BodiesEverStalled} body(s); {worked} were followed by work, {episodes.Count - worked} " +
               $"were not — of which {idle} had no assignment to work at; " +
               $"{touching} had something touching them at their worst" +
               $"\n    lengths (w=went on to work, n=did not): {string.Join("  ", rows)}" +
               $"\n    unproductive while under orders: " +
               (barrenUnderOrders.Length == 0 ? "none" : string.Join(", ", barrenUnderOrders)) +
               $"\n    longest spell {longest:F1}s, longest still followed by work {longestProductive:F1}s, " +
               $"peak stall clock {PeakStuckSeconds:F1}s, " +
               $"DRAWN RED (past {AgentDefaults.WedgedSeconds:F0}s) {BodiesEverWedged} body(s), " +
               $"still stalled at the end {StalledAtTheEnd}, " +
               $"unjudged in the last {TailSeconds:F0}s {UnjudgedInTheTail}, " +
               $"activities finished (churn, not work) {ActivitiesFinished}";
    }
}
