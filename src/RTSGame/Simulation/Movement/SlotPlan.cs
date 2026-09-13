using System.Numerics;
using System.Runtime.InteropServices;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Navigation;
using RTSGame.Simulation.Persistence;

namespace RTSGame.Simulation.Movement;

/// <summary>
/// Where each member of a cohort stands when it gets where it is going, and the envelope at which it stops
/// following the shared route and goes to claim its own ground.
/// </summary>
/// <remarks>
/// <b>This is the geometry, and it is not §82's fourth representation.</b> A combat formation adds spatial
/// roles, a facing and ranks — who is in front, who is on the flank, which way the whole body is pointing —
/// and none of that is here. What is here is the answer to a much smaller question: twenty people were sent
/// to one square metre, so where does each of them actually stand. Keeping the two apart is the point of
/// pulling this out at all; a combat formation should be able to replace or extend this layer without
/// touching the cohort that carries it, and the cohort should not have opinions about frontage.
/// <para>
/// It is a plan for one target, held by the cohort rather than being the cohort. A cohort re-ordered swaps
/// its plan and keeps everything else — its identity, its roster, and what it had learned about travelling
/// together. That is what makes a plan the right shape for this: the target is the only thing a new order
/// actually changes.
/// </para>
/// <para>
/// The slots are state and not a derivation. Laying them out asks the router where a body can stand, and the
/// answer depends on the congestion field and on who is standing where — so re-deriving them on load would
/// hand a restored cohort different ground to walk to than the one it was saved from.
/// </para></remarks>
internal sealed class SlotPlan
{
    /// <summary>Gap between neighbouring slots, on top of both bodies.</summary>
    private const float SlotGap = 0.32f;
    /// <summary>Frontage-to-depth bias; above one makes the block wider than deep.</summary>
    private const float FrontageBias = 2.2f;

    /// <summary>The command point this plan was laid out around.</summary>
    public Vector2 Target { get; }

    /// <summary>Slot world position, parallel to the cohort's roster.</summary>
    public IReadOnlyList<Vector2> Slots => slots;

    /// <summary>
    /// Distance from the command point at which a member stops following the
    /// shared route and heads for its own slot.
    /// </summary>
    public float FormationRadius { get; }

    private readonly List<Vector2> slots;

    private SlotPlan(Vector2 target, List<Vector2> slots, float formationRadius)
    {
        Target = target;
        this.slots = slots;
        FormationRadius = formationRadius;
    }

    /// <summary>Slot position relative to the command point.</summary>
    public Vector2 Offset(int member) => slots[member] - Target;

    /// <summary>
    /// Drops the slot at a roster position, for a cohort losing the member that held it.
    /// </summary>
    /// <remarks>
    /// The slot goes rather than being left empty, because an empty slot is not a vacancy anybody can be
    /// given: cohorts are never grown (§110), so nothing will ever arrive to stand in it, and a plan holding
    /// a slot with no member would only put the two lists out of step.
    /// </remarks>
    public void RemoveAt(int index) => slots.RemoveAt(index);

    /// <summary>Lays out one slot per member and pairs them so approach order is preserved.</summary>
    public static SlotPlan Lay(
        Vector2 target,
        IReadOnlyList<AgentId> members,
        AgentStore agents,
        PathService paths)
    {
        var laid = Layout(target, members, agents, paths, out var formationRadius);
        return new SlotPlan(target, laid, formationRadius);
    }

    internal void Write(WorldWriter writer)
    {
        writer.Vector(Target);
        writer.Float(FormationRadius);
        writer.Blob<Vector2>(CollectionsMarshal.AsSpan(slots));
    }

    internal static SlotPlan Read(WorldReader reader)
    {
        var target = reader.Vector();
        var formationRadius = reader.Float();
        var slots = new List<Vector2>(reader.Blob<Vector2>());
        return new SlotPlan(target, slots, formationRadius);
    }

    private static List<Vector2> Layout(
        Vector2 target,
        IReadOnlyList<AgentId> members,
        AgentStore agents,
        PathService paths,
        out float formationRadius)
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

        var laid = new Vector2[members.Count];
        for (var rank = 0; rank < memberOrder.Length; rank++)
        {
            laid[memberOrder[rank].index] = rank < slotOrder.Length ? slotOrder[rank] : target;
        }

        // One slot ring beyond the outermost occupied slot, so a member counts as
        // "arrived at the formation" slightly before it reaches its own square.
        formationRadius = MathF.Max(
            1.25f,
            laid.Length == 0 ? 0f : laid.Max(slot => Vector2.Distance(slot, target)) + spacing);
        return new List<Vector2>(laid);
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
