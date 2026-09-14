namespace Blix.Labs.Character;

/// <summary>One transition that happened, kept so the panel can say what fired and why.</summary>
public readonly record struct MotionChange(float AtSeconds, string From, string To, string Why, bool Interrupted);

/// <summary>
/// Runs a <see cref="MotionGraph"/>: holds a state, and changes it when an edge says to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two mechanisms and one of them is the whole point.</b> The edges decide WHAT could happen; the
/// dwell decides whether it is allowed to happen YET. RTSGame carries both — a held action with a
/// hold timer, and an interrupt set that bypasses it — and the reason is that its inputs cross
/// thresholds for single frames constantly, so a machine with edges alone alternates at frame rate.
/// </para>
/// <para>
/// <b>The dwell is a switch, not a constant.</b> Every claim a dwell makes is only worth anything if
/// turning it off is shown to break it — "a test that can only pass is not a test" — so
/// <see cref="DwellEnabled"/> exists to be turned off in a probe and in the panel, and the flicker
/// that follows is measured rather than described.
/// </para>
/// </remarks>
public sealed class MotionMachine
{
    private readonly MotionGraph graph;
    private readonly List<MotionChange> history = new();

    public MotionMachine(MotionGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        this.graph = graph;
        State = graph.Initial;
    }

    /// <summary>How long a state is held before an ordinary transition may leave it.</summary>
    /// <remarks>
    /// 0.18 s is roughly a tenth of a walk cycle: long enough that a threshold crossed for a frame or
    /// two cannot move the body, short enough that a real change of intent is not visibly late. It is
    /// a lab dial because it is exactly the sort of number a lab exists to find.
    /// </remarks>
    public float Dwell { get; set; } = 0.18f;

    /// <summary>The negative control. Off, an ordinary transition may fire the instant it holds.</summary>
    public bool DwellEnabled { get; set; } = true;

    /// <summary>How many changes to remember for the panel.</summary>
    public int HistoryLength { get; set; } = 24;

    public string State { get; private set; }

    public MotionState? Current => graph.Find(State);

    public float TimeInState { get; private set; }

    public float Elapsed { get; private set; }

    public int Changes { get; private set; }

    /// <summary>Why the machine last moved, and where from — the first question every report raises.</summary>
    public MotionChange? Last => history.Count == 0 ? null : history[^1];

    /// <summary>Most recent last.</summary>
    public IReadOnlyList<MotionChange> History => history;

    /// <summary>
    /// Changes per second over the whole run — the number a flicker IS.
    /// </summary>
    /// <remarks>
    /// Reported as a rate rather than a count because a count says nothing without a duration, and
    /// "the animation glitches" is a complaint about a rate. A body walking about sensibly changes
    /// state a few times a second at most; one sitting on a threshold with the dwell off changes
    /// tens of times a second, and the two are a factor of ten apart rather than a matter of taste.
    /// </remarks>
    public float ChangesPerSecond => Elapsed > 1e-4f ? Changes / Elapsed : 0f;

    public void Step(in MotionInput input, float deltaSeconds)
    {
        var dt = MathF.Max(0f, deltaSeconds);
        Elapsed += dt;
        TimeInState += dt;

        var query = new MotionQuery(input, TimeInState);

        // FIRST MATCH WINS, in declaration order. The list is a priority ranking — see MotionGraph —
        // and evaluating it in order is what makes an ordering mistake a thing you can read off the
        // panel rather than a thing you deduce from a flicker.
        foreach (var edge in graph.Transitions)
        {
            if (!edge.Matches(State) || edge.To == State) continue;
            if (!edge.When(query)) continue;

            // The dwell gate, and the one exception to it. An interrupt is exactly "this matters more
            // than not flickering" — a body taking a hit should flinch on the frame it is hit, and a
            // body that has left the ground is not deciding, it has departed.
            var blocked = DwellEnabled && !edge.Interrupts && TimeInState < Dwell;
            if (blocked) continue;

            Enter(edge.To, edge.Why, edge.Interrupts);
            return;
        }
    }

    /// <summary>Put the machine in a state without a transition, for a reset.</summary>
    public void Reset(string? state = null)
    {
        State = state ?? graph.Initial;
        TimeInState = 0f;
        Elapsed = 0f;
        Changes = 0;
        history.Clear();
    }

    private void Enter(string next, string why, bool interrupted)
    {
        history.Add(new MotionChange(Elapsed, State, next, why, interrupted));
        if (history.Count > HistoryLength) history.RemoveAt(0);

        State = next;
        TimeInState = 0f;
        Changes++;
    }
}
