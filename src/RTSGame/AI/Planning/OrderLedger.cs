using System.Collections.Generic;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;

namespace RTSGame.AI.Planning;

/// <summary>
/// What this planner has asked for and has not seen happen yet.
/// </summary>
/// <remarks>
/// <b>This class is the reason the planning layer exists.</b> §141. Three bugs in one session had one shape:
/// a rule asking "how many are already doing X", which is a question about the <em>queue</em> and not about
/// the world whenever the asker is the one filling the queue. §137 put twelve hands on one field, §140 issued
/// 24,693 assignments in a year and ordered twenty-eight militia out of thirteen people.
/// <para>
/// So no intent counts anything itself. It states a target; this holds the outstanding half; the gap is
/// <c>target - (standing + outstanding)</c> and the subtraction happens in one place. <b>A rule cannot
/// re-arm the trap because a rule has no way to issue anything.</b>
/// </para>
/// <para>
/// Outstanding orders age out rather than being retired on confirmation: a hand can die, a site can be
/// finished by somebody else, a body can be taken by a player's order, and an intent that waited for
/// confirmation of every order would deadlock on the first one that never arrives. Ten seconds is long enough
/// for a hand to cross a settlement and short enough that a dead intent is picked up again — the same figure
/// and the same reasoning as §140's per-site cooldown, which this generalises.
/// </para>
/// </remarks>
internal sealed class OrderLedger
{
    /// <summary>Ticks an unconfirmed order is believed for.</summary>
    public const int BelievedForTicks = 300;

    private readonly Dictionary<string, List<long>> outstanding = new();

    /// <summary>How many orders of one kind are still believed in.</summary>
    public int Outstanding(string key, long now)
    {
        if (!outstanding.TryGetValue(key, out var stamps)) return 0;
        stamps.RemoveAll(at => now - at >= BelievedForTicks);
        return stamps.Count;
    }

    public void Record(string key, long now)
    {
        if (!outstanding.TryGetValue(key, out var stamps)) outstanding[key] = stamps = new List<long>();
        stamps.Add(now);
    }

    /// <summary>
    /// Orders that are permanent once they land, and are therefore never believed away.
    /// </summary>
    /// <remarks>
    /// Training is the case: a villager walking to a barracks is not militia yet, and if the belief in that
    /// order expires the planner orders another. §140 measured that as twenty-eight militia ordered against a
    /// target of four. A permanent tally is only sound for an act that cannot fail to change the world —
    /// which conversion cannot, since the body is committed the moment the assignment applies.
    /// </remarks>
    private readonly Dictionary<string, int> permanent = new();

    public int Permanent(string key) => permanent.TryGetValue(key, out var n) ? n : 0;

    public void RecordPermanent(string key) =>
        permanent[key] = permanent.TryGetValue(key, out var n) ? n + 1 : 1;
}

/// <summary>
/// The hands one decision may spend, drawn in a fixed order.
/// </summary>
/// <remarks>
/// Idle first, then bodies on grain. <b>Taking a worker is not a cheat and refusing to was why nothing ever
/// got built</b> — the founding posts every hand it has, so the idle list is usually empty and a planner that
/// only employed idlers would lay a site down and leave it standing for a year. Pulling two villagers off the
/// wheat to raise a barracks is the ordinary thing a person does.
/// <para>
/// Lowest id within each tier, so two runs of the same fixture take the same body and a reordering of the
/// agent store cannot become a determinism fault.
/// </para>
/// </remarks>
internal sealed class HandPool
{
    /// <summary>Three tiers, drawn in order. Idle is free; the other two cost something.</summary>
    private readonly List<AgentId> idle = new();
    private readonly List<AgentId> onGrain = new();
    private readonly List<AgentId> onWood = new();
    private readonly HashSet<int> spent = new();

    public void Refill(Census census)
    {
        spent.Clear();
        Fill(idle, census.Idle);
        Fill(onGrain, census.OnGrainBodies);
        Fill(onWood, census.OnWoodBodies);
        return;

        // Lowest id within each tier, so two runs of the same fixture take the same body and a reordering of
        // the agent store cannot become a determinism fault.
        static void Fill(List<AgentId> into, List<AgentId> from)
        {
            into.Clear();
            into.AddRange(from);
            into.Sort((a, b) => a.Value.CompareTo(b.Value));
        }
    }

    public int Left => Available(idle) + Available(onGrain) + Available(onWood);

    /// <summary>
    /// Any hand, idle first. For an intent that outranks the economy.
    /// </summary>
    /// <remarks>
    /// <b>Taking a worker is not a cheat and refusing to was why nothing ever got built.</b> The founding
    /// posts every hand it has, so the idle tier is usually empty and a planner that only employed idlers
    /// would lay a site down and leave it standing for a year. Pulling two villagers off the wheat to raise a
    /// barracks is the ordinary thing a person does.
    /// <para>
    /// The wood tier is drawn last and, measured over a year, never actually reached — removing it entirely
    /// produced a byte-identical run. It stays because "any hand" is the honest rule for something that
    /// outranks the economy, not because it has been shown to matter.
    /// </para>
    /// </remarks>
    public AgentId? Take() => Draw(idle) ?? Draw(onGrain) ?? Draw(onWood);

    /// <summary>
    /// An idle hand, or one off the named resource — for an economy rule rebalancing the workforce.
    /// </summary>
    /// <remarks>
    /// <b>An employment rule may only take from the resource it is taking share from, and only when that
    /// resource is over-served.</b> §141: with a flat "any hand" draw the two employment rules take from each
    /// other — grain pulls a cutter, wood's remainder pulls it straight back — and the settlement spends its
    /// year walking between the fields and the trees. The over-served test is what makes it converge: at
    /// equilibrium neither rule can reach the other's hands at all.
    /// </remarks>
    public AgentId? TakeFrom(bool grain) => Draw(idle) ?? Draw(grain ? onGrain : onWood);

    private int Available(List<AgentId> tier)
    {
        var count = 0;
        foreach (var hand in tier)
        {
            if (!spent.Contains(hand.Value)) count++;
        }

        return count;
    }

    private AgentId? Draw(List<AgentId> tier)
    {
        foreach (var hand in tier)
        {
            if (spent.Contains(hand.Value)) continue;
            spent.Add(hand.Value);
            return hand;
        }

        return null;
    }
}
