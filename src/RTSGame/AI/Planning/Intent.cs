using System;
using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;

namespace RTSGame.AI.Planning;

/// <summary>
/// A want with a quantity, which knows how far it is from being met and how to close some of the distance.
/// </summary>
/// <remarks>
/// <b>A target, not an act.</b> §141. "Train a militia" is an act and has to ask how many are already
/// training; "I want four militia" is a target and the framework does the subtraction. The difference is the
/// whole design: an intent never counts its own outstanding orders and never touches the command queue
/// except through <see cref="Issue"/>, which records what it did in the same call.
/// </remarks>
internal abstract class Intent
{
    /// <summary>What it wants, printable, because the explainer prints it.</summary>
    public abstract string Name { get; }

    /// <summary>
    /// How many units of this want are missing, counting orders already issued and not yet visible.
    /// </summary>
    public abstract int Gap(Census census, OrderLedger ledger, long now);

    /// <summary>
    /// Closes up to <paramref name="allowance"/> of the gap, and returns how much it closed.
    /// </summary>
    public abstract int Close(
        SimulationWorld world,
        Census census,
        OrderLedger ledger,
        HandPool hands,
        long now,
        int allowance);
}

/// <summary>Puts a share of the workforce on one resource.</summary>
/// <remarks>
/// <b>A share rather than a threshold, and a lean rather than a switch.</b> §135 measured what a threshold
/// alone does — the bot survived a year and never grew, because a settlement founded with twenty-two seasons
/// in store is nowhere near any shortage, so everybody went to the wood and the larder paid for it. §140
/// measured what an absolute switch does once the planner can spend: buying a 300-timber barracks drops the
/// woodpile under the threshold in one act, and a switch then sent all thirteen hands to the trees and left
/// eight fields empty through a whole summer. The plan expresses both by ordering two of these.
/// </remarks>
internal sealed class Employ : Intent
{
    private readonly Resource resource;
    private readonly float share;
    private readonly bool remainder;

    private Employ(Resource resource, float share, bool remainder)
    {
        this.resource = resource;
        this.share = share;
        this.remainder = remainder;
    }

    public Employ(Resource resource, float share)
        : this(resource, share, remainder: false)
    {
    }

    /// <summary>
    /// Everybody the other rules did not want.
    /// </summary>
    /// <remarks>
    /// <b>A remainder and not a share of one, which is not the same thing and cost a barracks to learn.</b>
    /// §141: written first as <c>Employ(Wood, 1f)</c>, meaning "target the whole workforce", which is a gap
    /// the size of the settlement that can never close — and because an always-rule ahead of the structures
    /// spends the attention budget before they are reached, the planner posted cutters twice a second and
    /// never finished a building. Measured on the first run of the layer: 1,786 gaps left open, no barracks,
    /// no militia. A remainder is self-limiting: when everybody is employed it is zero.
    /// </remarks>
    public static Employ Rest(Resource resource) => new(resource, 1f, remainder: true);

    public override string Name => remainder
        ? $"employ {resource.ToString().ToLowerInvariant()} with the rest"
        : $"employ {resource.ToString().ToLowerInvariant()} {share:0.##}";

    private string Key => $"employ:{resource}";

    public override int Gap(Census census, OrderLedger ledger, long now)
    {
        var standing = resource == Resource.Grain ? census.OnGrain : census.OnWood;
        var target = remainder
            ? census.EconomyHands - (resource == Resource.Grain ? census.OnWood : census.OnGrain)
            : (int)MathF.Ceiling(census.EconomyHands * share);
        return Math.Max(0, target - standing - ledger.Outstanding(Key, now));
    }

    public override int Close(
        SimulationWorld world,
        Census census,
        OrderLedger ledger,
        HandPool hands,
        long now,
        int allowance)
    {
        var closed = 0;
        for (var i = 0; i < allowance; i++)
        {
            var work = resource == Resource.Grain ? LeastMannedField(census) : NearestTree(world, census);
            if (work is not { } assignment) break;
            // Idle hands, or hands off the other resource when that resource has more than its own share.
            // See the note on TakeFrom: without the over-served test the two employment rules take from
            // each other and the workforce walks between the fields and the trees all year.
            var mine = (int)MathF.Ceiling(census.EconomyHands * share);
            var other = resource == Resource.Grain ? census.OnWood : census.OnGrain;
            var otherOverServed = other > census.EconomyHands - mine;
            var hand = otherOverServed
                ? hands.TakeFrom(grain: resource != Resource.Grain)
                : hands.TakeFrom(grain: resource == Resource.Grain);
            if (hand is not { } body) break;
            world.QueueAssign(new[] { body }, assignment);
            ledger.Record(Key, now);
            closed++;
        }

        return closed;
    }

    /// <summary>
    /// The field with the fewest hands, counting the ones this decision has already sent.
    /// </summary>
    /// <remarks>
    /// <b>Counted from the bodies and then incremented as it assigns.</b> §135: indexing
    /// <c>fields[placed % count]</c> distributes the first round perfectly and then wraps onto fields that
    /// already have somebody — nine hands producing 2,036 grain against eight producing 5,235. §137: reading
    /// the applied count instead put twelve hands on one field, because nothing a decision issues has taken
    /// effect while that decision is still running. The census counts once; this keeps it current.
    /// </remarks>
    private static Assignment? LeastMannedField(Census census)
    {
        if (census.Fields.Count == 0) return null;
        var best = 0;
        for (var i = 1; i < census.Fields.Count; i++)
        {
            if (census.FieldHands[i] < census.FieldHands[best]) best = i;
        }

        census.FieldHands[best]++;
        var (id, at, extent) = census.Fields[best];
        return Assignment.Work(
            id, at, extent, Resource.Grain,
            EconomySystem.WorkShiftSeconds, EconomySystem.HandoverSeconds);
    }

    /// <summary>
    /// The nearest standing tree to the store that this faction has actually seen.
    /// </summary>
    /// <remarks>
    /// The same rule the founding uses (§22): a settlement's wood comes out of the trees nearest a store, and
    /// when those are gone the line moves outward. Gated on §131's knowledge, which is the one cheat this
    /// whole class exists to make impossible — a bot posting a cutter at a trunk it has never seen would be
    /// reading the world rather than what it knows.
    /// </remarks>
    private static Assignment? NearestTree(SimulationWorld world, Census census)
    {
        var best = NodeId.None;
        var bestDistance = float.MaxValue;
        var at = Vector2.Zero;
        var extent = 0f;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || node.Kind != NodeKind.Tree || node.Stock[Resource.Wood] <= 0) continue;
            if (!world.Knowledge.Knows(census.Faction, node.Position)) continue;
            var distance = Vector2.DistanceSquared(node.Position, census.Centre);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = node.Id;
            at = node.Position;
            extent = node.HalfExtent;
        }

        return best.IsValid
            ? Assignment.Work(
                best, at, extent, Resource.Wood,
                EconomySystem.WorkShiftSeconds, EconomySystem.HandoverSeconds)
            : null;
    }
}
