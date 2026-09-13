using System.Numerics;
using System.Runtime.InteropServices;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Navigation;
using RTSGame.Simulation.Persistence;

namespace RTSGame.Simulation.Movement;

/// <summary>
/// A set of units under one shared intent: who they are, whether they are moving, and what they have
/// learned about travelling together.
/// </summary>
/// <remarks>
/// Handing every selected unit the identical destination is what created the
/// contested-arrival problem: the whole selection converged on one square metre
/// and then spent its time deciding who was allowed to stand there. A group
/// instead travels as a cohort toward the command point and, once it arrives,
/// each member peels off to a slot it owns outright.
/// <para>
/// <b>What this class is, after §107-§111, is worth stating because it was three things and is now one.</b>
/// It owns the roster and is the authority on membership (§107). It has a lifetime of its own, longer than
/// any one move: it goes to rest with its people and ends only when nobody is left in it (§109), which is
/// what lets the next order to the same set adopt it rather than build a new one. It never grows or merges —
/// a changed set is a new cohort (§110). And the geometry it used to <em>be</em> it now merely carries, in a
/// <see cref="SlotPlan"/> that a new target swaps out (§111).
/// </para>
/// <para>
/// The travel state is the part that makes carrying rather than embodying worth the indirection.
/// <see cref="TransitCentroid"/> and <see cref="TransitFlow"/> are what the cohort has worked out about
/// where it is and whether it agrees with itself, and they survive a re-order precisely because they belong
/// to the set and not to the walk.
/// </para></remarks>
internal sealed class MoveGroup
{
    public int Id { get; }

    /// <summary>Where the current move is going. Changes when a new order adopts this cohort.</summary>
    public Vector2 Target => plan.Target;

    /// <summary>
    /// Whether the cohort has finished moving and is standing on its slots.
    /// </summary>
    /// <remarks>
    /// <b>This flag is the lifetime split.</b> Everybody settled used to mean the group was over: the members
    /// were released and the group retired, because the group had never been anything but the move. It is now
    /// a state the cohort is in rather than the end of it — the set outlives the walk, and the next order to
    /// the same set adopts it instead of building a new one. Retirement moved to the only condition that
    /// really ends a set, which is having nobody left in it.
    /// </remarks>
    public bool AtRest { get; set; }

    /// <summary>
    /// The roster, and the authority on who is in this cohort.
    /// </summary>
    /// <remarks>
    /// <b>Membership was stored twice and the two copies meant different things.</b> This list was fixed at
    /// creation and never edited — the roster at the moment of the order — while
    /// <see cref="AgentState.MoveGroupId"/> was the live truth, and every read of the list was filtered by
    /// the field to reconcile them. The group could therefore lose a member without anything happening: the
    /// filter simply matched one fewer body, and a departure became indistinguishable from a body that was
    /// never in the cohort. That is the shape §105 took two instruments and a chair report to find.
    /// <para>
    /// So the list is now the authority and it shrinks, through <see cref="Remove"/>, with a reason. The
    /// field on the body stays as a back-pointer cache for the per-tick lookups that cannot afford a scan,
    /// written only where this list is written. Kept in id order because it is created in id order and
    /// <see cref="List{T}.RemoveAt"/> preserves it, which is what lets the fingerprint walk it directly.
    /// </para></remarks>
    public IReadOnlyList<AgentId> Members => members;

    /// <summary>Slot world position, parallel to <see cref="Members"/>.</summary>
    public IReadOnlyList<Vector2> Slots => plan.Slots;

    private readonly List<AgentId> members;

    /// <summary>
    /// Where this cohort's members stand when they arrive, for the target they are going to now.
    /// </summary>
    /// <remarks>
    /// <b>Carried, not embodied.</b> The cohort used to be its own geometry — slots, frontage, envelope and
    /// all — which is the last of the four collapses §82 warned about: a set that owns a block layout has
    /// opinions about frontage, and a combat formation with roles and facing would then have had to be
    /// bolted onto the thing that also holds identity, roster and travel state. A plan is swapped by
    /// <see cref="Retarget"/> and everything that makes this cohort itself survives the swap.
    /// </remarks>
    private SlotPlan plan;

    /// <summary>
    /// Distance from the command point at which a member stops following the
    /// shared route and heads for its own slot.
    /// </summary>
    public float FormationRadius => plan.FormationRadius;
    public int SettlingTicks { get; set; }
    /// <summary>Live centroid of the members still travelling as a cohort.</summary>
    public Vector2 TransitCentroid { get; set; }
    /// <summary>Whether <see cref="TransitCentroid"/> has been seeded this order.</summary>
    public bool HasTransitCentroid { get; set; }
    /// <summary>Mean travel direction of the members still in transit, unnormalised.</summary>
    /// <remarks>
    /// Its length doubles as a measure of agreement: near one when everybody is
    /// heading the same way, near zero when the route has split the group.
    /// </remarks>
    public Vector2 TransitFlow { get; set; }

    /// <summary>Slot position relative to the command point.</summary>
    public Vector2 SlotOffset(int member) => plan.Offset(member);

    /// <summary>
    /// Takes a body off the roster. Returns false if it was not on it.
    /// </summary>
    /// <remarks>
    /// The slot goes with the member rather than being left behind, because a slot with nobody in it is not
    /// a vacancy the cohort can offer anyone: slots are laid out once, against particular ground and a
    /// particular approach, for the bodies that were there at the time.
    /// <para>
    /// <b>And there is no vacancy to offer, because cohorts do not take new members at all.</b> §107 left
    /// that open; §110 closed it. A cohort only ever shrinks — a set that has changed is a new cohort, not a
    /// grown one. That is what keeps this method from needing a counterpart.
    /// </para></remarks>
    public bool Remove(AgentId member)
    {
        var index = members.IndexOf(member);
        if (index < 0) return false;
        RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Whether this cohort's roster is exactly the given set, which is the test for adopting it.
    /// </summary>
    /// <remarks>
    /// <b>Exactly, and the whole rule is that one word.</b> A set that matches a roster adopts it; every
    /// other set — a subset, a superset, a reach across two cohorts — is a new cohort. Ordering six of a
    /// cohort's ten is a genuinely different intention from ordering all ten, and there is no reading of it
    /// under which the four left behind should be dragged along or the six should inherit a formation laid
    /// out for ten. Nor is there a good answer to "whose travel state is this now" when two cohorts merge.
    /// <para>
    /// So cohorts shrink and are replaced, and never grow or merge. That also gives the ruling that a player
    /// holds one cohort at a time somewhere to stand: any ordered set resolves to exactly one cohort, old or
    /// new, never to two. All four corners are asserted — see <c>ACohortIsNeverGrown</c> — because three of
    /// them fall out of "no match means create one" rather than being written anywhere, and behaviour that is
    /// right by accident is one refactor from being wrong.
    /// </para>
    /// <para>
    /// Both sides are in id order — the roster because it is built that way and removal preserves it, the
    /// ordered set because every command path sorts before it queues — so this is a walk rather than a
    /// lookup.
    /// </para></remarks>
    public bool RosterIs(IReadOnlyList<AgentId> ordered)
    {
        if (members.Count != ordered.Count) return false;
        for (var i = 0; i < members.Count; i++)
        {
            if (members[i] != ordered[i]) return false;
        }
        return true;
    }

    /// <summary>Takes the body at a known roster position off, for callers already walking the roster.</summary>
    public void RemoveAt(int index)
    {
        members.RemoveAt(index);
        plan.RemoveAt(index);
    }

    private MoveGroup(int id, List<AgentId> members, SlotPlan plan)
    {
        Id = id;
        this.members = members;
        this.plan = plan;
    }

    /// <summary>The order itself, its slots, and how far through settling it is.</summary>
    internal void Write(WorldWriter writer)
    {
        writer.Int(Id);
        writer.Int(SettlingTicks);
        writer.Vector(TransitCentroid);
        writer.Bool(HasTransitCentroid);
        writer.Bool(AtRest);
        writer.Vector(TransitFlow);
        writer.Blob<AgentId>(CollectionsMarshal.AsSpan(members));
        plan.Write(writer);
    }

    /// <summary>
    /// Rebuilds a group from a save rather than laying one out.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Create"/>. Laying out slots asks the router where a body can
    /// stand, and the answer depends on the congestion field and on who is standing where — so
    /// re-deriving the slots on load would hand a restored cohort different ground to walk to than
    /// the one it was saved from, which is the whole failure a save is supposed to avoid. The slots
    /// are state, and they come back as state.
    /// </remarks>
    internal static MoveGroup Read(WorldReader reader)
    {
        // Read into locals in order rather than into an initialiser: the fields go out in a fixed
        // sequence and two of them are constructor arguments, so the sequence has to be visible.
        var id = reader.Int();
        var settlingTicks = reader.Int();
        var transitCentroid = reader.Vector();
        var hasTransitCentroid = reader.Bool();
        var atRest = reader.Bool();
        var transitFlow = reader.Vector();
        var members = new List<AgentId>(reader.Blob<AgentId>());
        return new MoveGroup(id, members, SlotPlan.Read(reader))
        {
            SettlingTicks = settlingTicks,
            TransitCentroid = transitCentroid,
            HasTransitCentroid = hasTransitCentroid,
            AtRest = atRest,
            TransitFlow = transitFlow,
        };
    }

    /// <summary>
    /// Lays out one slot per member around <paramref name="target"/> and assigns
    /// them so that approach order is preserved.
    /// </summary>
    public static MoveGroup Create(
        int id,
        Vector2 target,
        IReadOnlyList<AgentId> members,
        AgentStore agents,
        PathService paths)
    {
        return new MoveGroup(
            id, new List<AgentId>(members), SlotPlan.Lay(target, members, agents, paths));
    }

    /// <summary>
    /// Points an existing cohort at a new command point, keeping its identity and its membership.
    /// </summary>
    /// <remarks>
    /// <b>Adoption, and the reason the roster had to become the cohort's before this was possible.</b> The
    /// same twenty people ordered somewhere else are the same twenty people: rebuilding the group would give
    /// them a new id, book twenty departures and twenty joins, and throw away what the cohort had learned
    /// about how it was travelling — the transit centroid and the flow agreement that station-keeping reads.
    /// Adopting keeps all of it and re-lays only what the new target actually changes, which is the slots.
    /// <para>
    /// The transit state is deliberately <em>not</em> cleared. A cohort that is already moving together and
    /// is turned toward somewhere else should carry its shape through the turn; resetting it would make every
    /// re-order start from "we have not agreed on a direction yet", which is the one state station-keeping is
    /// written to stay out of.
    /// </para></remarks>
    public void Retarget(Vector2 target, AgentStore agents, PathService paths)
    {
        plan = SlotPlan.Lay(target, members, agents, paths);
        SettlingTicks = 0;
        AtRest = false;
    }

    /// <summary>
    /// One slot per member around a command point, paired so approach order is preserved.
    /// </summary>
}
