using System.Numerics;
using RTSGame.Simulation.Agents;
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
    /// Run a route: carry whichever useful resource is available from one node to another.
    /// </summary>
    /// <remarks>
    /// <b>A haul the player authored, as against a haul the board auctioned.</b> The difference is not
    /// cosmetic and it is why this is a second kind rather than a flag on <see cref="Haul"/>:
    /// <list type="bullet">
    /// <item>A <see cref="Haul"/> is <em>one round trip</em>, and that is the whole point of pricing it —
    /// a body that kept a route for life would be priced once, at the moment it was hired, and the promise
    /// that a jammed lane makes a different body cheaper would be a promise about a decision nobody ever
    /// revisits. Collect, deliver, go back on the board.</item>
    /// <item>A <see cref="Carry"/> is a <em>standing commitment</em>: this person, this route, until you
    /// tell them otherwise. An empty source or a destination with no present need makes them wait at the
    /// source; it does not erase the player's arrangement. Nobody re-auctions it because nobody is meant
    /// to. It is the player saying "these three are on the run", which is the shape of decision §2 wants
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

    /// <summary>
    /// Supply and raise one building project, then return any surplus to storage.
    /// </summary>
    /// <remarks>
    /// Leg zero is the current stock source and leg one is the project site. The source and cargo may
    /// change between trips; the sink remains the stable node being built until completion. Unlike
    /// <see cref="Work"/>, nothing is produced into the builder's hands — it carries existing physical
    /// stock inward and labour consumes it into the structure.
    /// </remarks>
    Build,

    /// <summary>
    /// Carry militia equipment to one barracks, then remain there until this same villager is trained.
    /// </summary>
    Train,

    /// <summary>
    /// Where a soldier belongs: stand about here, and come back here when a fight is over.
    /// </summary>
    /// <remarks>
    /// <b>The standing purpose militia did not have.</b> §143. Training converted a villager and left it on
    /// <see cref="Assignment.None"/>, so militia stood wherever the barracks happened to be until §30's
    /// am-I-needed committed them, and after a fight they stood wherever the fight ended. There was no verb
    /// for "this is your post", which is why a posture was inexpressible — not because a planner was too
    /// simple, but because the vocabulary had no word for it.
    /// <para>
    /// Exactly parallel to <see cref="Work"/> for a villager, and it earns the parallel: a rally point is a
    /// Guard anchor, a stance is which anchor and radius the plan picks, and <b>what a committed defence does
    /// on arrival — §30's open half — is that it goes back to its Guard</b>, the way a reaper goes back to
    /// its field. No stance enum, no new interrupt, no new command.
    /// </para>
    /// <para>
    /// Appended rather than inserted, because the save format writes this enum by value and a settlement
    /// saved before today has to load as the same settlement.
    /// </para>
    /// </remarks>
    Guard,

    /// <summary>
    /// Stay on one particular thing until it is dead or gone.
    /// </summary>
    /// <remarks>
    /// <b>The atomic offensive verb, and deliberately the only one.</b> §166. A whole family of words —
    /// siege, blockade, raid, skirmish — reads like a family of verbs and is not: each is <em>attack this</em>
    /// or <em>guard this</em> plus a rule about what to do next, which target to pick, and when to leave.
    /// Those rules belong to the roster, the economy and the moment ("in winter I might raid, get out, and
    /// live off it for a while"), so they are plans built on this and not mechanisms beside it.
    /// <para>
    /// One verb for buildings and bodies alike, because the difference between them is a fact about the
    /// target and not about the order. And one end condition, which is the part that makes it atomic:
    /// <b>an attack is over when what it was aimed at cannot be found any more or is already dead.</b> No
    /// duration, no leash, no retreat rule — those are the composed verbs' business.
    /// </para>
    /// <para>
    /// Note what it is <em>not</em>: the name <c>Muster</c> was the obvious one and is already taken by
    /// <c>ThreatSystem.Muster</c>, which asks how much strength can reach a place in time. That is a query,
    /// defensive, and automatic — and calling a command by the same name would have manufactured this
    /// session's tenth near-synonym on purpose.
    /// </para>
    /// </remarks>
    Attack,

    /// <summary>
    /// Take what is in somebody else's store and carry it home, until there is none left.
    /// </summary>
    /// <remarks>
    /// <b>A haul with a hostile source, and that is the whole mechanism.</b> §167. The economy has been
    /// physical since §6 — stock sits in nodes and rides on bodies, and a body killed drops what it held —
    /// so robbery needs no new substance, only somebody willing to walk into a place that is not theirs.
    /// <para>
    /// <b>Who can do it is the design.</b> Militia carry nothing at all: their capacity is zero, so an army
    /// can wreck a granary and cannot rob one. To loot you send <em>villagers</em> — the labour force, at
    /// strength one and health twenty — which makes a raid a bill paid out of the harvest and the escort,
    /// rather than a trick of the hands. Nobody had to design that; it was already true in the roster.
    /// </para>
    /// <para>
    /// Distinct from <see cref="Attack"/> on purpose: looting is <em>taking</em> and attacking is
    /// <em>breaking</em>. Keeping them apart is what lets the richer words be compositions — a sack is both,
    /// a raid is this plus an escort, a blockade is a guard on the ground between.
    /// </para>
    /// </remarks>
    Loot,
}

/// <summary>What a unit is doing at this instant, in service of its assignment.</summary>
/// <remarks>
/// <b>One shape, on purpose — but now it says which work it is.</b> The shape is unchanged and is still
/// the point: an activity is <em>be at this place for this long</em>, which covers walking there and
/// standing there without the two being separate states, so a unit shoved off its post mid-dwell walks
/// back and finishes the dwell and none of that needs a transition. Loading, harvesting and repairing take
/// identical code paths through <see cref="JobSystem"/> — the same place, the same dwell, the same handover
/// — and every one of them is entered and left by the same two functions.
/// <para>
/// What changed in §180 is that the kind is <em>named</em> rather than being the single word
/// <c>Working</c>. For a long time it was not, on the reasoning that a distinction nothing consumed was a
/// distinction not worth having. Something consumes it now: the screen. A body's pose was being inferred
/// from <c>Assignment.Cargo</c>, and that inference produced both animation faults reported from the chair
/// — a kneeling repair standing in for felling a tree, and idle militia reaping, because
/// <c>default(Resource)</c> is grain and a guard post never sets a cargo. <b>The fix is not a better
/// inference; it is that the thing being inferred should have been recorded.</b>
/// </para>
/// <para>
/// So the naming happens once, in <see cref="JobSystem.NameActivity"/>, at the instant an activity begins,
/// out of the assignment that is beginning it — where the assignment kind is checked <em>first</em> and a
/// cargo is only ever read on the branch that requires one. That ordering is the whole of why this cannot
/// reproduce the bug it replaces.
/// </para>
/// <para>
/// <b>Compare on <c>None</c>, never on a member.</b> Every rule in the simulation wants to know whether a
/// body has an activity at all, and none of them want to know which — that is the screen's business. A
/// test against a particular kind is how these members would start to mean "is this body working", which
/// is <see cref="JobSystem.IsWorking"/>'s job and is defined once. The rename from <c>Working</c> was
/// chosen partly to make the compiler find every existing comparison.
/// </para>
/// </remarks>
internal enum ActivityKind
{
    /// <summary>Not doing anything in service of an assignment: between legs, or unassigned.</summary>
    None,

    /// <summary>
    /// Being at a place for a while, with nothing more particular to say about it.
    /// </summary>
    /// <remarks>
    /// A garrison, a post, a villager at a barracks waiting to be trained, either end of a bare shuttle.
    /// <b>Not a residual catch-all</b> — those are genuinely all the same act, and the difference between
    /// a guard looking alert and a villager standing about is a fact about the body's role rather than
    /// about what it is doing.
    /// </remarks>
    Standing,

    /// <summary>Bringing in a harvest.</summary>
    Reaping,

    /// <summary>Felling a tree.</summary>
    Felling,

    /// <summary>Cutting stone.</summary>
    Quarrying,

    /// <summary>Working at a structure: raising it, repairing it, upgrading it.</summary>
    Building,

    /// <summary>Taking stock onto the body, at whichever end of the trip that happens.</summary>
    Loading,

    /// <summary>Putting stock down.</summary>
    Unloading,

    /// <summary>Swinging at something that is not its own.</summary>
    Striking,
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
    float FarDwellSeconds = -1f,
    AgentId Quarry = default)
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

    /// <summary>
    /// Run useful goods from one node to another, beginning with <paramref name="cargo"/>.
    /// </summary>
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

    /// <summary>Supply and build <paramref name="site"/> until the project and its cleanup are done.</summary>
    public static Assignment Build(
        NodeId site,
        Vector2 sitePosition,
        float siteExtent,
        float handoverSeconds,
        float workShiftSeconds) =>
        new(
            AssignmentKind.Build,
            sitePosition,
            sitePosition,
            handoverSeconds,
            site,
            site,
            Resource.Wood,
            siteExtent,
            siteExtent,
            workShiftSeconds);

    /// <summary>Equip and train this body at one completed barracks.</summary>
    public static Assignment Train(
        NodeId barracks,
        Vector2 position,
        float extent,
        float handoverSeconds,
        float trainingShiftSeconds) =>
        new(
            AssignmentKind.Train,
            position,
            position,
            handoverSeconds,
            barracks,
            barracks,
            Resource.Wood,
            extent,
            extent,
            trainingShiftSeconds);

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
    /// <remarks>
    /// <b>The node ids are None, explicitly.</b> They default to <c>default(NodeId)</c>, which is node
    /// <em>zero</em> and not "no node" — so a bare post named the first node in the world, and anything
    /// that asked a Hold which node it was serving got a real answer about somebody else's granary. It
    /// happened to be harmless only because the one caller that asks went on to check the node was a work
    /// site and node zero never is. That is not a thing to leave standing.
    /// </remarks>
    public static Assignment Hold(Vector2 post, float dwellSeconds, float placeExtent = 0f) =>
        new(
            AssignmentKind.Hold, post, post, dwellSeconds, NodeId.None, NodeId.None,
            PlaceExtent: placeExtent);

    /// <summary>Stand at a node and work on it — a builder at a site, and what counts them as hands.</summary>
    public static Assignment Post(
        NodeId site,
        Vector2 at,
        float extent,
        float dwellSeconds) =>
        new(
            AssignmentKind.Hold, at, at, dwellSeconds, site, NodeId.None, PlaceExtent: extent);

    /// <summary>Work one end, then the other, dwelling at each.</summary>
    /// <summary>
    /// Posts a soldier where it belongs. See <see cref="AssignmentKind.Guard"/>.
    /// </summary>
    /// <param name="radius">
    /// How much ground counts as the post. It is the place's extent, which is the same term a building's
    /// footprint uses — so a guard with a wide radius spreads out along its wall instead of stacking on one
    /// point, and the crowding rules that already exist do the spreading.
    /// </param>
    /// <param name="dwellSeconds">
    /// How long one turn of duty lasts before the leg finishes and the assignment repeats. Not a timeout: it
    /// repeats forever, and the only reason it is finite is that a leg is the unit in which the jobs layer
    /// notices anything at all.
    /// </param>
    /// <summary>Puts a body onto one particular target until it is gone. See <see cref="AssignmentKind.Attack"/>.</summary>
    /// <param name="quarry">The body to attack, or <see cref="AgentId.None"/> for a structure.</param>
    /// <param name="structure">The node to attack, or <c>NodeId.None</c> when the target is a body.</param>
    /// <remarks>
    /// The two are exclusive and one field holds each, rather than one field holding "whatever it is": a
    /// target that could be either, read through one accessor, is precisely the shape that made
    /// <c>LakeDepth</c>, <c>WidthAt</c> and <c>LevelAt</c> cost this session nine corrections.
    /// </remarks>
    public static Assignment Attack(
        AgentId quarry,
        NodeId structure,
        Vector2 at,
        float extent) =>
        new(
            AssignmentKind.Attack, at, at, SwingSeconds, structure, NodeId.None,
            PlaceExtent: extent, Quarry: quarry);

    /// <summary>
    /// How long a body stays put between re-aiming at its target.
    /// </summary>
    /// <remarks>
    /// <b>Not zero, and zero was the first answer.</b> §166: a leg with no dwell finishes on the tick it
    /// begins, so an attacker spent every tick being re-aimed and none of them <em>standing</em> at what it
    /// was hitting — and the damage rule, which asks the jobs layer whether the body has arrived, saw a body
    /// permanently in transit. A soldier chewed a palisade at a fraction of its strength and never brought it
    /// down. One second is a swing: long enough to be arrival, short enough that a quarry which moves is
    /// followed rather than lost.
    /// </remarks>
    private const float SwingSeconds = 1f;

    /// <summary>Sends a carrier to empty somebody else's store. See <see cref="AssignmentKind.Loot"/>.</summary>
    public static Assignment Loot(NodeId theirs, Vector2 at, float extent, float handoverSeconds) =>
        new(
            AssignmentKind.Loot, at, at, handoverSeconds, theirs, NodeId.None,
            PlaceExtent: extent, FarPlaceExtent: extent, Quarry: AgentId.None);

    public static Assignment Guard(Vector2 post, float radius, float dwellSeconds) =>
        new(
            AssignmentKind.Guard, post, post, dwellSeconds, NodeId.None, NodeId.None,
            PlaceExtent: radius);

    public static Assignment Shuttle(Vector2 first, Vector2 second, float dwellSeconds) =>
        new(AssignmentKind.Shuttle, first, second, dwellSeconds, NodeId.None, NodeId.None);

    /// <summary>Where the given leg of this assignment is served.</summary>
    public Vector2 PlaceOfLeg(int leg) => HasTwoEnds && leg % 2 != 0 ? FarAnchor : Anchor;

    /// <summary>Whether this assignment alternates between two places.</summary>
    public bool HasTwoEnds =>
        Kind is AssignmentKind.Shuttle or AssignmentKind.Haul or AssignmentKind.Carry or AssignmentKind.Work or
            AssignmentKind.Build or AssignmentKind.Train or AssignmentKind.Loot;

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
        Kind is AssignmentKind.Hold or AssignmentKind.Shuttle or AssignmentKind.Carry or AssignmentKind.Work or
            AssignmentKind.Build or AssignmentKind.Train or AssignmentKind.Guard or AssignmentKind.Attack or AssignmentKind.Loot;
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

    /// <summary>The stable building project served by a <see cref="AssignmentKind.Build"/> assignment.</summary>
    /// <remarks>
    /// Kept separately from the assignment's current source and sink because cleanup turns those endpoints
    /// into project-to-store while the commitment still needs to remember which node it belongs to.
    /// </remarks>
    public NodeId Project;

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

    /// <summary>
    /// Units this body has claimed at a source for its next construction load.
    /// </summary>
    /// <remarks>
    /// A promise, not stock: it is excluded from conservation and exists only so two builders do not both
    /// answer the same final thirty-unit deficit. Cleared when the source is reached, and saved and
    /// fingerprinted with the rest of the job so a mid-trip save resumes the same allocation.
    /// </remarks>
    public int ReservedUnits;

    /// <summary>Seconds this same villager has spent training at its barracks.</summary>
    public float TrainingWork;

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
