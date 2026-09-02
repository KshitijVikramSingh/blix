using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;

namespace RTSGame.AI;

/// <summary>
/// A rule-bot that runs one faction's settlement through the verbs a person has.
/// </summary>
/// <remarks>
/// <b>The acceptance test §4 set is "the AI can play the player's settlement", and it is only checkable if the
/// AI has no other way in.</b> So this holds a <see cref="SimulationWorld"/> and touches it through
/// <c>QueueAssign</c> and the other queued commands only — never a node's fields, never a body's state, never
/// the jobs layer directly. Everything it decides is a command a mouse could have issued, which is what makes
/// "no resource cheats, no vision cheats" a property of the code rather than a promise.
/// <para>
/// It reads two things: its own faction's nodes and bodies, which a player can see on their own screen, and
/// <see cref="FactionKnowledge"/>, which §131 built as the simulation-side answer to what a faction knows.
/// It does not read the fog — that is view state and §119's rule is a direction — and it does not read
/// another faction's stores.
/// </para>
/// <para>
/// <b>Outside the world, on the RaidDirector's precedent, and stepped inside the fixed tick.</b> A player is
/// not part of the world and its state is not world state; what makes that safe is that the bot is a pure
/// function of what it can see plus a tick counter, so two runs of the same fixture drive identically and the
/// determinism check still compares the worlds rather than the drivers.
/// </para>
/// <para>
/// <b>What this first cut is for.</b> Not to play well — to play at all, through the real interface, so the
/// seams show up while the settlement is the only thing at stake. It keeps its people employed and moves them
/// between grain and wood as the stores run down. Building, training and anything to do with a neighbour come
/// after there is a measurement saying this much works.
/// </para></remarks>
internal sealed class SettlementBot
{
    /// <summary>
    /// Ticks between decisions.
    /// </summary>
    /// <remarks>
    /// <b>A delegation-layer player thinks on a human cadence, not a simulation one.</b> §7 makes AI
    /// difficulty an attention budget rather than a cheat, and a bot re-deciding every tick would be spending
    /// attention no person has — thirty times a second on a settlement whose fastest meaningful change takes
    /// seconds. Half a second is faster than a player and slow enough that the cost is nothing.
    /// </remarks>
    private const int DecideEveryTicks = 30;

    /// <summary>Seasons of grain below which the bot starts moving hands onto the fields.</summary>
    /// <remarks>
    /// Expressed in the same unit the settlement report uses — seasons of eating left — because that is the
    /// figure a player would look at, and a bot reasoning in units nobody displays is a bot whose decisions
    /// cannot be argued with from the chair.
    /// </remarks>
    private const float HungrySeasons = 2.5f;

    private readonly FactionId faction;
    private long nextDecision;

    /// <summary>Assignments issued, for a report that would rather count than assume.</summary>
    public int OrdersIssued { get; private set; }

    public int Decisions { get; private set; }

    public SettlementBot(FactionId faction) => this.faction = faction;

    public void Update(SimulationWorld world)
    {
        if (world.TickNumber < nextDecision) return;
        nextDecision = world.TickNumber + DecideEveryTicks;
        Decisions++;

        // What it has: its own stores, its own people, its own places of work. All of it visible on a screen.
        var store = NodeId.None;
        var fields = new List<(NodeId Id, Vector2 At, float Extent)>();
        foreach (var id in world.Nodes.SettlementNodes)
        {
            ref readonly var node = ref world.Nodes.Get(id);
            if (!node.IsAlive || node.Faction != faction) continue;
            if (node.Stores && !store.IsValid) store = node.Id;

            if (node.Kind == NodeKind.Farm) fields.Add((node.Id, node.Position, node.HalfExtent));
        }

        if (!store.IsValid) return;

        var mouths = 0;
        var idle = new List<AgentId>();
        foreach (ref readonly var body in world.Agents.All)
        {
            if (!body.IsAlive || body.Faction != faction) continue;
            mouths++;
            // Idle means no standing commitment at all. A body under an order is left alone: §7 makes a manual
            // order an interrupt that expires, and a bot that re-assigned over one would be fighting the
            // player for its own units.
            if (!body.Jobs.HasAssignment && !body.Jobs.IsInterrupted) idle.Add(body.Id);
        }

        if (mouths == 0) return;

        // <b>The one judgement this cut makes, read from the figure a player is shown.</b> §8's autonomy time,
        // through the same Outlook call the HUD makes — filtered to this faction, because unfiltered it would
        // be answering about the opponent's larder as well as its own. Not recomputed from a rate of its own:
        // a bot reasoning in units nobody displays is a bot whose decisions cannot be argued with from the
        // chair, and a second opinion about what a mouth eats is the same risk as a second opinion about what
        // can be seen.
        var outlook = world.Economy.Outlook(
            Resource.Grain, world.Nodes, world.Agents, world.Date.Season, faction);
        var wantsGrain = outlook.Seasons < HungrySeasons;

        // Everybody idle goes to work, on the fields if the larder is low and at the wood otherwise. Posted
        // one at a time so each gets its own site rather than all crowding the first.
        var placed = 0;
        foreach (var hand in idle)
        {
            var work = wantsGrain && fields.Count > 0
                ? NextField(fields, placed)
                : NearestTreeTo(world, store);
            if (work is not { } site) continue;
            world.QueueAssign(new[] { hand }, site);
            OrdersIssued++;
            placed++;
        }
    }

    private static Assignment NextField(
        List<(NodeId Id, Vector2 At, float Extent)> fields,
        int index)
    {
        var (id, at, extent) = fields[index % fields.Count];
        return Assignment.Work(
            id, at, extent, Resource.Grain,
            EconomySystem.WorkShiftSeconds, EconomySystem.HandoverSeconds);
    }

    /// <summary>
    /// The nearest standing tree to the store, as a place to post a cutter.
    /// </summary>
    /// <remarks>
    /// The same rule the founding uses, for the same reason §22 gives: the settlement's wood comes out of the
    /// trees nearest a store, and when those are gone the line moves outward. Read from the node store, which
    /// is ground a faction standing there can see.
    /// </remarks>
    private Assignment? NearestTreeTo(SimulationWorld world, NodeId store)
    {
        var from = world.Nodes.Get(store).Position;
        var best = NodeId.None;
        var bestDistance = float.MaxValue;
        var at = Vector2.Zero;
        var extent = 0f;
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive || node.Kind != NodeKind.Tree || node.Stock[Resource.Wood] <= 0) continue;
            // Only ground this faction knows. A bot that could post a cutter at a trunk it has never seen
            // would be reading the world rather than its own knowledge, which is the one cheat this class
            // exists to make impossible.
            if (!world.Knowledge.Knows(faction, node.Position)) continue;
            var distance = Vector2.DistanceSquared(node.Position, from);
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
