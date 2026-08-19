using System.Numerics;
using RTSGame.Simulation.Economy;

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

    /// <summary>
    /// Carry one resource from a source node to a sink node, indefinitely. A shuttle with cargo.
    /// </summary>
    /// <remarks>
    /// The nodes are named by id and their positions are carried alongside as a convenience, and the
    /// ids are the truth: a granary that burns down leaves an assignment naming a dead node, which the
    /// board drops, rather than an assignment naming a point in an empty field that a hauler keeps
    /// walking to.
    /// </remarks>
    Haul,

    /// <summary>
    /// Run a route: carry one resource from a source node to a sink node until the source is empty.
    /// </summary>
    /// <remarks>
    /// <b>A haul the player authored, as against a haul the board auctioned.</b> The difference is not
    /// cosmetic and it is why this is a second kind rather than a flag on <see cref="Haul"/>:
    /// <list type="bullet">
    /// <item>A <see cref="Haul"/> is <em>one round trip</em>, and that is the whole point of pricing it —
    /// a body that kept a route for life would be priced once, at the moment it was hired, and the promise
    /// that a jammed lane makes a different body cheaper would be a promise about a decision nobody ever
    /// revisits. Collect, deliver, go back on the board.</item>
    /// <item>A <see cref="Carry"/> is a <em>standing commitment</em>: this person, this route, until the
    /// source runs dry or you tell them otherwise. Nobody re-auctions it because nobody is meant to. It is
    /// the player saying "these three are on the timber run", which is the shape of decision §2 wants
    /// attention spent on.</item>
    /// </list>
    /// <para>
    /// The board keeps the first for the two cases nobody would ever micromanage — stranded stock, and
    /// goods lying in the road — and the player owns the second.
    /// </para>
    /// </remarks>
    Carry,

    /// <summary>
    /// Work a place, and carry what it yields to the nearest store.
    /// </summary>
    /// <remarks>
    /// The producer's own loop, and the one the whole economy rests on: work until your hands are full,
    /// walk it in, come back. It is not hauling — a haul is one round trip between two stores and ends —
    /// and it is not a hold, because a hold is standing somewhere for a while and this alternates. What it
    /// shares with a haul is the shape: two legs, cargo, a handover at each end.
    /// <para>
    /// The store is chosen when there is something to carry rather than when the job is given, because
    /// which store is nearest depends on where the body is and what has been built since.
    /// </para>
    /// </remarks>
    Work,
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
/// <param name="Source">Node a haul collects from; <see cref="NodeId.None"/> otherwise.</param>
/// <param name="Sink">Node a haul delivers to; <see cref="NodeId.None"/> otherwise.</param>
/// <param name="Cargo">What a haul carries.</param>
/// <param name="PlaceExtent">
/// Radius of the thing at <see cref="Anchor"/>, so that <em>touching</em> it counts as arriving.
/// </param>
/// <param name="FarPlaceExtent">Radius of the thing at <see cref="FarAnchor"/>.</param>
/// <param name="FarDwellSeconds">
/// How long the far end takes, when it is not the same as the near end. Negative means "the same".
/// </param>
/// <remarks>
/// The two ends of a two-legged job are usually not the same job. A shift working a field is measured in
/// tens of seconds and ends early when the worker's hands fill; putting the load down at the other end is
/// a handover and takes a few. One number for both made a farmer stand at the granary for as long as it
/// had just spent in the field, and a settlement starved with full fields.
/// </remarks>
/// <remarks>
/// <see cref="PlaceExtent"/> is zero for a bare point on the ground, which is what a hand-given post is,
/// and the radius of the building for anything attached to a node. Without it a body has to reach the
/// centre of a farm to have arrived, which means walking into the farm and shouldering past whoever is
/// working there — and a wagon, needing to put its middle where a villager's middle would go, never
/// quite manages it.
/// </remarks>
internal readonly record struct Assignment(
    AssignmentKind Kind,
    Vector2 Anchor,
    Vector2 FarAnchor,
    float DwellSeconds,
    NodeId Source = default,
    NodeId Sink = default,
    Resource Cargo = default,
    float PlaceExtent = 0f,
    float FarPlaceExtent = 0f,
    float FarDwellSeconds = -1f)
{
    public static Assignment None => default;

    /// <summary>Carry <paramref name="cargo"/> from one node to another, indefinitely.</summary>
    public static Assignment Haul(
        NodeId source,
        Vector2 sourcePosition,
        NodeId sink,
        Vector2 sinkPosition,
        Resource cargo,
        float handoverSeconds,
        float sourceExtent,
        float sinkExtent) =>
        new(
            AssignmentKind.Haul, sourcePosition, sinkPosition, handoverSeconds, source, sink, cargo,
            sourceExtent, sinkExtent);

    /// <summary>Which node a leg of a haul or a shift of work is served at.</summary>
    public NodeId NodeOfLeg(int leg) => leg % 2 != 0 ? Sink : Source;

    /// <summary>Run <paramref name="cargo"/> from one node to another until the source is empty.</summary>
    public static Assignment Carry(
        NodeId source,
        Vector2 sourcePosition,
        NodeId sink,
        Vector2 sinkPosition,
        Resource cargo,
        float handoverSeconds,
        float sourceExtent,
        float sinkExtent) =>
        new(
            AssignmentKind.Carry, sourcePosition, sinkPosition, handoverSeconds, source, sink, cargo,
            sourceExtent, sinkExtent);

    /// <summary>Whether this assignment moves cargo between two nodes, however it was authored.</summary>
    public bool MovesCargo => Kind is AssignmentKind.Haul or AssignmentKind.Carry;

    /// <summary>Work <paramref name="site"/> and carry what it yields to a store.</summary>
    public static Assignment Work(
        NodeId site,
        Vector2 sitePosition,
        float siteExtent,
        Resource output,
        float shiftSeconds,
        float handoverSeconds) =>
        new(
            AssignmentKind.Work, sitePosition, sitePosition, shiftSeconds, site, NodeId.None, output,
            siteExtent, siteExtent, handoverSeconds);

    /// <summary>How long the work at a given leg takes.</summary>
    public float DwellOfLeg(int leg) => HasTwoEnds && leg % 2 != 0 && FarDwellSeconds >= 0f
        ? FarDwellSeconds
        : DwellSeconds;

    /// <summary>How much ground the thing at a given leg stands on.</summary>
    /// <remarks>
    /// Two ends can be two sizes — a farmyard is not a granary — so the extent travels with the leg
    /// rather than being one number for the assignment.
    /// </remarks>
    public float ExtentOfLeg(int leg) =>
        HasTwoEnds && leg % 2 != 0 ? FarPlaceExtent : PlaceExtent;

    /// <summary>Stand at a post, checking in every <paramref name="dwellSeconds"/>.</summary>
    public static Assignment Hold(Vector2 post, float dwellSeconds, float placeExtent = 0f) =>
        new(AssignmentKind.Hold, post, post, dwellSeconds, PlaceExtent: placeExtent);

    /// <summary>Work one end, then the other, dwelling at each.</summary>
    public static Assignment Shuttle(Vector2 first, Vector2 second, float dwellSeconds) =>
        new(AssignmentKind.Shuttle, first, second, dwellSeconds);

    /// <summary>Where the given leg of this assignment is served.</summary>
    public Vector2 PlaceOfLeg(int leg) => HasTwoEnds && leg % 2 != 0 ? FarAnchor : Anchor;

    /// <summary>Whether this assignment alternates between two places.</summary>
    public bool HasTwoEnds =>
        Kind is AssignmentKind.Shuttle or AssignmentKind.Haul or AssignmentKind.Carry or AssignmentKind.Work;

    /// <summary>
    /// Whether the assignment goes on indefinitely, or ends when its last leg does.
    /// </summary>
    /// <remarks>
    /// A haul is <b>one round trip</b> and not a standing route, and that is the whole point of pricing
    /// it: a hauler that kept a route for the rest of its life would be priced once, at the moment it
    /// was hired, and the promise that a jammed lane makes a different hauler cheaper would be a
    /// promise about a decision nobody ever revisits. Collect, deliver, and go back on the board.
    /// </remarks>
    public bool RepeatsForever =>
        Kind is AssignmentKind.Hold or AssignmentKind.Shuttle or AssignmentKind.Carry or AssignmentKind.Work;
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

    /// <summary>
    /// How much ground the thing at <see cref="Place"/> stands on.
    /// </summary>
    /// <remarks>
    /// Kept beside the place rather than looked up from the assignment each time, because the two are a
    /// pair: the place is chosen by leg and so is the extent, and reading one from the current leg while
    /// the other was set from a previous one is a bug that would only show up as a body arriving at a
    /// farm using a granary's tolerance.
    /// </remarks>
    public float PlaceExtent;

    /// <summary>
    /// Half the width of the square at <see cref="Place"/>, which is what its walls actually are.
    /// </summary>
    /// <remarks>
    /// <see cref="PlaceExtent"/> is the half-diagonal — the radius of the circle that just contains the
    /// building — because that is the safe figure for a circular tolerance. But a circle around a square
    /// is a poor description of where the square is: a body approaching a <em>face</em> would stop
    /// 2.3 metres short of a granary, which reads as hesitation rather than arrival. Anything that wants
    /// to know where the wall is uses this and measures to the box.
    /// </remarks>
    public readonly float PlaceHalfWidth => PlaceExtent * 0.70710678f;

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

    /// <summary>What this body is carrying, for a haul.</summary>
    public Resource Carrying;

    /// <summary>Whole units on this body's back. Neither stored nor consumed until delivered.</summary>
    public int CarriedUnits;

    /// <summary>The assignment has run its course and will be cleared on the next tick.</summary>
    public bool Finished;

    public readonly bool HasAssignment => Assignment.Kind != AssignmentKind.None;
    public readonly bool IsInterrupted => Interrupt != InterruptKind.None;

    /// <summary>
    /// This body has a job it cannot get to — as opposed to one it is doing from slightly the
    /// wrong spot, which is normal in a crowd.
    /// </summary>
    public readonly bool CannotReachWork => Retries > 0 && !SettledNearby;
}
