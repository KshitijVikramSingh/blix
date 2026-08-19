using System.Numerics;

namespace RTSGame.Simulation.Jobs;

/// <summary>What a unit is standing committed to. Persistent; orders do not change it.</summary>
internal enum AssignmentKind
{
    /// <summary>Nothing. The unit does what it is told and then stands there, as it always has.</summary>
    None,

    /// <summary>Be at one place. A garrison, a post, a pair of hands on one farm.</summary>
    Hold,

    /// <summary>
    /// Be at one place, then the other, indefinitely. Hauling with the cargo left out.
    /// </summary>
    Shuttle,
}

/// <summary>What a unit is doing at this instant, in service of its assignment.</summary>
/// <remarks>
/// One kind, on purpose. An activity is <em>be at this place for this long</em>, which covers
/// walking there and standing there without the two being separate states — so a unit shoved
/// off its post mid-dwell walks back and finishes the dwell, and none of that needs a
/// transition. Loading, harvesting and repairing are all the same shape with a rate attached,
/// which is what Session 6 attaches.
/// </remarks>
internal enum ActivityKind
{
    None,
    Working,
}

/// <summary>Why a unit's assignment is currently suspended.</summary>
/// <remarks>
/// An interrupt overrides the activity and expires; it never touches the assignment. That is
/// the whole of §7's "taking control is an interrupt, not a mode switch" — there is no manual
/// mode, so there is nothing to toggle and no way to strand a unit in it.
/// </remarks>
internal enum InterruptKind
{
    None,

    /// <summary>Somebody gave this unit a direct order.</summary>
    Order,
}

/// <summary>
/// A standing commitment: plain data, no references, so it survives a save and can be
/// compared bit for bit.
/// </summary>
/// <remarks>
/// The fields are shared between kinds rather than being a union per kind. Deliberately:
/// serialization discipline is a rule this project holds every session, stable ids and values
/// over references, and a tagged struct of numbers is the shape that keeps it. When Session 6
/// gives hauling real endpoints they become node ids in <see cref="Anchor"/>'s place, not
/// pointers to a granary.
/// </remarks>
/// <param name="Kind">Which commitment this is.</param>
/// <param name="Anchor">Where a hold is held, or the first end of a shuttle.</param>
/// <param name="FarAnchor">The second end of a shuttle; unused by a hold.</param>
/// <param name="DwellSeconds">
/// How long the unit stays at a place before the assignment moves it on. In seconds, like
/// everything else in this design: it is how long the work takes.
/// </param>
internal readonly record struct Assignment(
    AssignmentKind Kind,
    Vector2 Anchor,
    Vector2 FarAnchor,
    float DwellSeconds)
{
    public static Assignment None => default;

    /// <summary>Stand at a post, checking in every <paramref name="dwellSeconds"/>.</summary>
    public static Assignment Hold(Vector2 post, float dwellSeconds) =>
        new(AssignmentKind.Hold, post, post, dwellSeconds);

    /// <summary>Work one end, then the other, dwelling at each.</summary>
    public static Assignment Shuttle(Vector2 first, Vector2 second, float dwellSeconds) =>
        new(AssignmentKind.Shuttle, first, second, dwellSeconds);

    /// <summary>Where the given leg of this assignment is served.</summary>
    public Vector2 PlaceOfLeg(int leg) => Kind == AssignmentKind.Shuttle && leg % 2 != 0
        ? FarAnchor
        : Anchor;
}

/// <summary>
/// The three layers of the jobs model as they sit on a body: the assignment it is committed
/// to, the activity it is doing, and the interrupt currently overriding it.
/// </summary>
/// <remarks>
/// All three live on <c>AgentState</c> rather than in a side table keyed by unit. That costs a
/// few words per body and buys two things the project already depends on: the determinism
/// fingerprint reads it without being told, because it reads whatever the struct declares; and
/// there is no second collection to keep in step with spawning, despawning and tombstoned
/// slots, which is where a side table would eventually be wrong.
/// <para>
/// <see cref="LegsCompleted"/> is the number the gate is about. An interrupt may take the unit
/// anywhere and hold it for as long as it likes; when it expires, this must be exactly what it
/// was, and the unit must go back to the place it was serving.
/// </para>
/// </remarks>
internal struct AgentJobs
{
    public Assignment Assignment;

    /// <summary>Which leg of the assignment is being served. Progress, not activity.</summary>
    public int Leg;

    /// <summary>Legs finished under this assignment. Untouched by interrupts, by design.</summary>
    public int LegsCompleted;

    public ActivityKind Activity;

    /// <summary>Where the current activity happens.</summary>
    public Vector2 Place;

    /// <summary>Seconds of work left once the unit is at <see cref="Place"/>.</summary>
    public float DwellRemaining;

    /// <summary>A walk to <see cref="Place"/> is outstanding, so a stop means it fell short.</summary>
    public bool WalkIssued;

    /// <summary>Walks to the current place that ended somewhere else.</summary>
    /// <remarks>
    /// A place can be unreachable — inside a wall somebody built, across ground a landslide
    /// took away — and the honest response is to keep trying at a bounded rate rather than
    /// either spinning on it every tick or giving up on the job. This counts the attempts so a
    /// soak run can tell a unit working from a unit failing politely.
    /// </remarks>
    public int Retries;

    /// <summary>Seconds before another attempt at a place the unit could not reach.</summary>
    public float RetryCooldown;

    /// <summary>
    /// This body gave up on standing exactly at its place and is working from where the crowd
    /// left it.
    /// </summary>
    /// <remarks>
    /// Set only for a place the router says is reachable, so it never papers over a job nobody
    /// can get to. Cleared whenever the activity changes, because the next place is a fresh
    /// question.
    /// </remarks>
    public bool SettledNearby;

    public InterruptKind Interrupt;

    /// <summary>
    /// Seconds of not being told anything before the assignment resumes.
    /// </summary>
    /// <remarks>
    /// Refreshed for as long as the ordered movement lasts and only counted down once the unit
    /// is standing free, so a player issuing a run of orders is never fighting the unit's own
    /// job halfway through the sequence. Grace, not a mode: it runs out on its own.
    /// </remarks>
    public float InterruptGrace;

    public readonly bool HasAssignment => Assignment.Kind != AssignmentKind.None;
    public readonly bool IsInterrupted => Interrupt != InterruptKind.None;

    /// <summary>
    /// This body has a job it cannot get to — as opposed to one it is doing from slightly the
    /// wrong spot, which is normal in a crowd.
    /// </summary>
    public readonly bool CannotReachWork => Retries > 0 && !SettledNearby;
}
