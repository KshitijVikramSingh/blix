using System.Numerics;
using System.Runtime.InteropServices;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Navigation;
using RTSGame.Simulation.Persistence;

namespace RTSGame.Simulation.Movement;

/// <summary>
/// Shared intent for a multi-unit move order, plus one destination slot per
/// member.
/// </summary>
/// <remarks>
/// Handing every selected unit the identical destination is what created the
/// contested-arrival problem: the whole selection converged on one square metre
/// and then spent its time deciding who was allowed to stand there. A group
/// instead travels as a cohort toward the command point and, once it arrives,
/// each member peels off to a slot it owns outright.
/// </remarks>
internal sealed class MoveGroup
{
    /// <summary>Gap between neighbouring slots, on top of both bodies.</summary>
    private const float SlotGap = 0.32f;
    /// <summary>Frontage-to-depth bias; above one makes the block wider than deep.</summary>
    private const float FrontageBias = 2.2f;

    public int Id { get; }
    public Vector2 Target { get; }

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
    public IReadOnlyList<Vector2> Slots => slots;

    private readonly List<AgentId> members;
    private readonly List<Vector2> slots;

    /// <summary>
    /// Distance from the command point at which a member stops following the
    /// shared route and heads for its own slot.
    /// </summary>
    public float FormationRadius { get; }
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
    public Vector2 SlotOffset(int member) => slots[member] - Target;

    /// <summary>
    /// Takes a body off the roster. Returns false if it was not on it.
    /// </summary>
    /// <remarks>
    /// The slot goes with the member rather than being left behind, because the two lists are paired by
    /// index and a slot with nobody to stand in it is not a vacancy the cohort can offer anyone: slots are
    /// laid out once, against the ground and the approach, for the bodies that were there at the time.
    /// Handing a departed member's slot to somebody else is a question for the arc that lets a cohort take
    /// new members, and it is not answered here.
    /// </remarks>
    public bool Remove(AgentId member)
    {
        var index = members.IndexOf(member);
        if (index < 0) return false;
        RemoveAt(index);
        return true;
    }

    /// <summary>Takes the body at a known roster position off, for callers already walking the roster.</summary>
    public void RemoveAt(int index)
    {
        members.RemoveAt(index);
        slots.RemoveAt(index);
    }

    private MoveGroup(int id, Vector2 target, List<AgentId> members, List<Vector2> slots, float formationRadius)
    {
        Id = id;
        Target = target;
        this.members = members;
        this.slots = slots;
        FormationRadius = formationRadius;
    }

    /// <summary>The order itself, its slots, and how far through settling it is.</summary>
    internal void Write(WorldWriter writer)
    {
        writer.Int(Id);
        writer.Vector(Target);
        writer.Float(FormationRadius);
        writer.Int(SettlingTicks);
        writer.Vector(TransitCentroid);
        writer.Bool(HasTransitCentroid);
        writer.Vector(TransitFlow);
        writer.Blob<AgentId>(CollectionsMarshal.AsSpan(members));
        writer.Blob<Vector2>(CollectionsMarshal.AsSpan(slots));
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
        var target = reader.Vector();
        var formationRadius = reader.Float();
        var settlingTicks = reader.Int();
        var transitCentroid = reader.Vector();
        var hasTransitCentroid = reader.Bool();
        var transitFlow = reader.Vector();
        var members = new List<AgentId>(reader.Blob<AgentId>());
        var slots = new List<Vector2>(reader.Blob<Vector2>());
        return new MoveGroup(id, target, members, slots, formationRadius)
        {
            SettlingTicks = settlingTicks,
            TransitCentroid = transitCentroid,
            HasTransitCentroid = hasTransitCentroid,
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
        var largestRadius = 0f;
        // Two different questions. How far apart to space slots is about how much room the bodies
        // take up; whether a slot exists at all is a path query, and those are asked at the class
        // radius so a mixed cohort does not build a second identical field.
        var largestNavigationRadius = 0f;
        var centroid = Vector2.Zero;
        foreach (var memberId in members)
        {
            ref readonly var agent = ref agents.Get(memberId);
            largestRadius = MathF.Max(largestRadius, agent.Radius);
            largestNavigationRadius = MathF.Max(largestNavigationRadius, agent.NavigationRadius);
            centroid += agent.Position;
        }
        centroid /= members.Count;

        var spacing = largestRadius * 2f + SlotGap;

        // Sorting both members and slots along the approach axis and pairing them
        // rank for rank keeps the cohort from threading through itself: the unit
        // that arrives first takes the slot nearest the front.
        var approach = target - centroid;
        approach = approach.LengthSquared() > 0.0001f
            ? Vector2.Normalize(approach)
            : Vector2.UnitX;

        var candidates = BuildSlotCandidates(
            target, approach, spacing, largestNavigationRadius, members.Count, paths);

        var memberOrder = members
            .Select((memberId, index) => (memberId, index))
            .OrderByDescending(entry => Vector2.Dot(agents.Get(entry.memberId).Position, approach))
            .ThenBy(entry => entry.memberId.Value)
            .ToArray();
        var slotOrder = candidates
            .OrderByDescending(slot => Vector2.Dot(slot, approach))
            .ThenBy(slot => slot.X)
            .ThenBy(slot => slot.Y)
            .ToArray();

        var slots = new Vector2[members.Count];
        for (var rank = 0; rank < memberOrder.Length; rank++)
        {
            slots[memberOrder[rank].index] = rank < slotOrder.Length ? slotOrder[rank] : target;
        }

        // One slot ring beyond the outermost occupied slot, so a member counts as
        // "arrived at the formation" slightly before it reaches its own square.
        var formationRadius = MathF.Max(
            1.25f,
            slots.Length == 0 ? 0f : slots.Max(slot => Vector2.Distance(slot, target)) + spacing);
        return new MoveGroup(
            id, target, new List<AgentId>(members), new List<Vector2>(slots), formationRadius);
    }

    /// <summary>
    /// Lays out a rectangular block facing the direction of travel, falling back
    /// to rings for any slot the block cannot place.
    /// </summary>
    /// <remarks>
    /// Concentric rings pack well and read as a swarm. A move order is expected to
    /// produce a body of troops with a frontage and a depth, oriented the way it
    /// was sent, so the block is built in the approach frame and is wider than it
    /// is deep. Rings remain as the fallback because a block has no answer when
    /// the ground it wants is partly unusable.
    /// </remarks>
    private static List<Vector2> BuildSlotCandidates(
        Vector2 target,
        Vector2 approach,
        float spacing,
        float agentRadius,
        int required,
        PathService paths)
    {
        var candidates = new List<Vector2>(required);
        var right = new Vector2(approach.Y, -approach.X);

        // Wider than deep: a frontage reads as a formation, a square reads as a blob.
        var columns = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(required * FrontageBias)));
        var rows = (int)MathF.Ceiling(required / (float)columns);
        for (var row = 0; row < rows && candidates.Count < required; row++)
        for (var column = 0; column < columns && candidates.Count < required; column++)
        {
            var lateral = (column - (columns - 1) * 0.5f) * spacing;
            var depth = (row - (rows - 1) * 0.5f) * spacing;
            var position = target + right * lateral - approach * depth;
            if (!paths.IsPositionNavigable(position, agentRadius)) continue;
            if (!paths.IsSlotReachable(target, position, agentRadius)) continue;
            candidates.Add(position);
        }

        for (var ring = 1; candidates.Count < required && ring <= required; ring++)
        {
            var ringRadius = ring * spacing;
            var slotCount = ring * 6;
            for (var slot = 0; slot < slotCount && candidates.Count < required; slot++)
            {
                var angle = slot / (float)slotCount * MathF.Tau;
                var position = target + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * ringRadius;
                if (!paths.IsPositionNavigable(position, agentRadius)) continue;
                if (!paths.IsSlotReachable(target, position, agentRadius)) continue;
                if (candidates.Any(existing =>
                        Vector2.DistanceSquared(existing, position) < spacing * spacing * 0.64f))
                {
                    continue;
                }
                candidates.Add(position);
            }
        }

        if (candidates.Count == 0) candidates.Add(target);
        return candidates;
    }
}
