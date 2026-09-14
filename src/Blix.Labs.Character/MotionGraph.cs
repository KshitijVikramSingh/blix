namespace Blix.Labs.Character;

/// <summary>
/// What the graph is allowed to know about the body. Small on purpose.
/// </summary>
/// <remarks>
/// <b>Slow facts, not fast ones.</b> This is the whole lesson RTSGame paid three bug reports for:
/// a pose derived from something that changes at several hertz alternates at several hertz. Its
/// builder animation read the jobs layer's ACTIVITY, which opens and abandons as the layer cycles
/// its legs hunting for timber; the fix was to read the PROJECT instead, which a delivery changes
/// and nothing changes per tick. The same mistake one layer along was gating on velocity for a body
/// at a work site, which is jostled constantly and crosses the walking threshold for single frames.
/// <para>
/// Nothing here can enforce that — a caller can always feed a fast fact into a slow-looking field.
/// What the lab can do is make the failure reproducible, which is what the jitter source and the
/// dwell switch are for.
/// </para>
/// </remarks>
public readonly record struct MotionInput(
    float Speed = 0f,
    bool Grounded = true,
    float VerticalSpeed = 0f,
    bool Act = false,
    bool Hurt = false);

/// <summary>Everything a transition may ask about: the body, and how long this state has been held.</summary>
/// <remarks>
/// Time in state is in here rather than in <see cref="MotionInput"/> because it is the MACHINE's
/// fact, not the body's — and because the one-shot states (a landing, a flinch) are exactly the ones
/// whose exit condition is "this has run long enough" and nothing else.
/// </remarks>
public readonly record struct MotionQuery(MotionInput Input, float TimeInState);

/// <summary>A state, and the clip it means to be showing.</summary>
public sealed record MotionState(string Name, string Clip, bool Loops = true);

/// <summary>
/// One edge: where from, where to, why, and whether it may cut in before the dwell is up.
/// </summary>
/// <remarks>
/// <paramref name="Why"/> is not decoration. "The animation glitches" is the report every one of
/// these faults arrives as, and the first question is always WHICH transition fired — a machine that
/// can only say what state it is in has answered the easy half. The panel and the history both print
/// this, so a flicker names the condition that caused it.
/// </remarks>
public sealed record MotionTransition(
    string From,
    string To,
    string Why,
    Func<MotionQuery, bool> When,
    bool Interrupts = false)
{
    /// <summary>A <see cref="From"/> of this matches any state.</summary>
    public const string AnyState = "*";

    public bool Matches(string state) => From == AnyState || From == state;
}

/// <summary>
/// States and the ordered edges between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order is the design, and it is the part RTSGame actually paid for.</b> Transitions are tried in
/// declaration order and the first whose condition holds wins, which makes the list a priority
/// ranking rather than a set. Its ordering rules, each of which is a bug report in a comment:
/// flinching first, because a body taking a hit is the most urgent thing on screen; and the ACT
/// before the MOVEMENT, because a body at its place is nudged constantly and those nudges cross the
/// walking threshold for single frames — with movement tested first the pose flickered between
/// labouring and walking.
/// </para>
/// <para>
/// A graph is data rather than code because that is what makes it inspectable: the panel can list
/// the edges in the order they will be tried, which is the only way to see a priority mistake before
/// it becomes a flicker.
/// </para>
/// </remarks>
public sealed class MotionGraph
{
    private readonly List<MotionState> states = new();
    private readonly List<MotionTransition> transitions = new();

    public IReadOnlyList<MotionState> States => states;

    public IReadOnlyList<MotionTransition> Transitions => transitions;

    public string Initial { get; private set; } = string.Empty;

    public MotionGraph State(string name, string clip, bool loops = true)
    {
        if (states.Count == 0) Initial = name;
        states.Add(new MotionState(name, clip, loops));
        return this;
    }

    public MotionGraph On(string from, string to, string why, Func<MotionQuery, bool> when, bool interrupts = false)
    {
        transitions.Add(new MotionTransition(from, to, why, when, interrupts));
        return this;
    }

    public MotionState? Find(string name)
    {
        foreach (var s in states)
        {
            if (s.Name == name) return s;
        }
        return null;
    }

    /// <summary>
    /// The lab's default humanoid graph, in priority order.
    /// </summary>
    /// <remarks>
    /// Clip names are the Rogue's. The thresholds are lab dials rather than truths — they are exactly
    /// the numbers a state machine is supposed to let you find, and the jitter source exists to make
    /// a body sit right on one of them.
    /// </remarks>
    /// <remarks>
    /// <b>Two thresholds per gait, not one, and the lab found out why.</b> A dwell stops a spike from
    /// moving the body; it does NOT stop a body sitting exactly on a threshold, which simply
    /// alternates once per dwell instead of once per frame — measured at 5.4 changes a second, which
    /// is slower flicker and still flicker. What settles that is HYSTERESIS: leave a gait at a lower
    /// speed than you entered it at, so a body on the boundary stays where it is.
    /// <para>
    /// Worth saying plainly because RTSGame has the dwell and not the band: its bodies hovering at
    /// the walk threshold alternate at roughly 1/ActionHoldSeconds, and the hold is bounding that
    /// rather than preventing it. The two mechanisms answer different questions and a graph wants
    /// both.
    /// </para>
    /// </remarks>
    public static MotionGraph Humanoid(
        float walkAbove = 0.2f, float walkBelow = 0.12f,
        float runAbove = 2.6f, float runBelow = 2.0f) =>
        new MotionGraph()
            .State("idle", "Idle")
            .State("walk", "Walking_A")
            .State("run", "Running_A")
            .State("jump", "Jump_Idle", loops: false)
            .State("fall", "Jump_Idle")
            .State("land", "Jump_Land", loops: false)
            .State("act", "1H_Melee_Attack_Chop", loops: false)
            .State("flinch", "Hit_A", loops: false)

            // ── Interrupts, FIRST, because that is what "may cut in" means once the list is a
            //    priority ranking. An interrupt declared below an ordinary transition that also
            //    matches would never be reached, which is a silent kind of wrong — so the probe
            //    checks the ordering rather than trusting the author.
            .On("*", "flinch", "hurt", q => q.Input.Hurt, interrupts: true)
            .On("*", "fall", "left the ground, descending",
                q => !q.Input.Grounded && q.Input.VerticalSpeed <= 0f, interrupts: true)
            .On("*", "jump", "left the ground, rising",
                q => !q.Input.Grounded && q.Input.VerticalSpeed > 0f, interrupts: true)

            // ── One-shots leave on their own time, which is the machine's fact rather than the body's.
            .On("flinch", "idle", "flinch finished", q => q.TimeInState > 0.45f)
            .On("land", "idle", "landing finished", q => q.TimeInState > 0.25f)
            .On("act", "idle", "act released", q => !q.Input.Act)

            // ── Landing before locomotion: a body that has just touched down is moving, and testing
            //    speed first would skip the landing entirely on every run-off-a-ledge.
            .On("fall", "land", "touched down", q => q.Input.Grounded)
            .On("jump", "fall", "started descending", q => q.Input.VerticalSpeed <= 0f)

            // ── THE ACT BEFORE THE MOVEMENT. RTSGame's hardest-won ordering: a body at its place is
            //    nudged constantly and those nudges cross the walking threshold for single frames, so
            //    with movement tested first the pose flickers between labouring and walking.
            .On("*", "act", "act requested", q => q.Input.Act && q.Input.Grounded)

            // ── Locomotion last, only on the ground, and with a BAND rather than a line. Entering a
            //    gait and leaving it are different speeds, which is what stops a body sitting on the
            //    boundary from oscillating — the dwell above only bounds how fast it oscillates.
            .On("idle", "walk", "reached walk speed", q => q.Input.Grounded && q.Input.Speed >= walkAbove)
            .On("walk", "run", "reached run speed", q => q.Input.Grounded && q.Input.Speed >= runAbove)
            .On("run", "walk", "dropped below run speed", q => q.Input.Grounded && q.Input.Speed < runBelow)
            .On("walk", "idle", "stopped", q => q.Input.Grounded && q.Input.Speed < walkBelow);
}
