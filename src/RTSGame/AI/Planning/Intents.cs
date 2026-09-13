using System;
using System.Numerics;
using RTSGame.Simulation;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;

namespace RTSGame.AI.Planning;

/// <summary>Wants a number of one kind of building standing, and lays a site when short.</summary>
/// <remarks>
/// One site at a time: a settlement with four half-built things and nobody standing at any of them is worse
/// off than one building them in turn, and the timber is spent either way.
/// </remarks>
internal sealed class Structure : Intent
{
    private readonly int count;
    private readonly float ring;

    /// <summary>What it wants standing, so a plan can be filtered by what it builds.</summary>
    public NodeKind Kind { get; }

    public Structure(NodeKind kind, int count, float ring)
    {
        Kind = kind;
        this.count = count;
        this.ring = ring;
    }

    public override string Name => $"structure {Kind.ToString().ToLowerInvariant()} {count}";

    private string Key => $"structure:{Kind}";

    public override int Gap(Census census, OrderLedger ledger, long now)
    {
        var standing = Kind switch
        {
            NodeKind.Barracks => census.Barracks.IsValid ? 1 : 0,
            NodeKind.PalisadeWall => census.Walls,
            _ => 0,
        };

        // A site already down is a site already decided. Counted here rather than as an outstanding order
        // because it is visible in the world: the placement is not queued, it happens at once.
        var placed = Kind == NodeKind.Barracks && census.HasBarracksSite ? 1 : 0;
        return Math.Max(0, count - standing - placed - ledger.Outstanding(Key, now));
    }

    public override int Close(
        SimulationWorld world,
        Census census,
        OrderLedger ledger,
        HandPool hands,
        long now,
        int allowance)
    {
        // <b>No conditions here, deliberately.</b> §141: this used to refuse quietly when materials were
        // short or something else was half-built, and the explainer then printed "gap 2, closed 0 (short)"
        // every half second for the rest of the run — a rule that wanted something it was never going to ask
        // for. Conditions belong in the plan where they are printed and can be argued with; an intent that
        // hides one is a rule whose text is a lie. The only refusal left is the honest one: no ground.
        if (allowance <= 0 || !census.Store.IsValid) return 0;
        if (!Place(world, census, Kind, ring)) return 0;
        ledger.Record(Key, now);
        return 1;
    }

    /// <summary>
    /// Lays a site on open ground near the store, and says whether it found any.
    /// </summary>
    /// <remarks>
    /// <b>The one thing in the vocabulary that is not a queued command, and it is not a privilege.</b> The
    /// player's own build key calls <c>AddNode</c> too — see <c>RtsGameLoop.Build</c> — because placing a
    /// site is not an order to a unit and there is nothing about it to interrupt or to survive a save: it
    /// happens between one tick and the next or not at all. What the planner does not get is ground a player
    /// could not use, which is why this goes through the same <c>Terrain.CanPlace</c> the placement rules use.
    /// <para>
    /// A ring walked at a fixed step from a fixed bearing, so the same fixture places the same building in
    /// the same spot twice.
    /// </para>
    /// </remarks>
    private static bool Place(SimulationWorld world, Census census, NodeKind kind, float radius)
    {
        var half = NodeFootprint.HalfExtentOf(kind);
        var extents = new Vector2(half);
        for (var out_ = 0; out_ < 3; out_++)
        for (var step = 0; step < 24; step++)
        {
            var angle = step / 24f * MathF.Tau;
            var at = census.Centre +
                     new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (radius + out_ * 6f);
            if (!world.Terrain.CanPlace(at, extents)) continue;
            if (Occupied(world, at, half)) continue;
            world.AddNode(
                kind, at, capacity: 0, Resource.Wood,
                faction: census.Faction, built: !Construction.NeedsBuilding(kind));
            return true;
        }

        return false;
    }

    private static bool Occupied(SimulationWorld world, Vector2 at, float half)
    {
        foreach (ref readonly var node in world.Nodes.All)
        {
            if (!node.IsAlive) continue;
            // A tree is not an obstruction to a building: it does not block, and the ground under it is
            // ordinary ground. A standing outcrop is.
            if (node.Kind == NodeKind.Tree) continue;
            var apart = half + node.HalfExtent + 1.5f;
            if (Vector2.DistanceSquared(node.Position, at) < apart * apart) return true;
        }

        return false;
    }
}

/// <summary>Wants hands standing at whatever is going up.</summary>
internal sealed class StaffProjects : Intent
{
    private readonly int builders;

    public StaffProjects(int builders) => this.builders = builders;

    public override string Name => $"staff projects {builders}";

    public override int Gap(Census census, OrderLedger ledger, long now)
    {
        var missing = 0;
        foreach (var (site, _, _) in census.Projects)
        {
            missing += Math.Max(
                0,
                builders - census.StandingAt(site) - ledger.Outstanding(Key(site), now));
        }

        return missing;
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
        foreach (var (site, at, extent) in census.Projects)
        {
            var short_ = builders - census.StandingAt(site) - ledger.Outstanding(Key(site), now);
            for (var i = 0; i < short_ && closed < allowance; i++)
            {
                var hand = hands.Take();
                if (hand is not { } body) return closed;
                // Posted, not ordered to build: PostedOnAWorkSite turns a hold on a site into the build
                // assignment, which is the same conversion the player's post key relies on. One verb.
                world.QueueAssign(
                    new[] { body }, Assignment.Hold(at, EconomySystem.WorkShiftSeconds, extent));
                ledger.Record(Key(site), now);
                closed++;
            }
        }

        return closed;
    }

    private static string Key(NodeId site) => $"staff:{site.Value}";
}

/// <summary>Wants a number of militia standing, and converts villagers when short.</summary>
/// <remarks>
/// Counted permanently rather than believed for a while, because conversion cannot fail once the assignment
/// applies — see the note on <see cref="OrderLedger.RecordPermanent"/>. Militia eat more and produce nothing,
/// so this is a real cost paid out of the same larder the employment rules are protecting.
/// </remarks>
internal sealed class Garrison : Intent
{
    private readonly int count;

    public Garrison(int count) => this.count = count;

    public override string Name => $"garrison {count}";

    private const string Key = "garrison";

    public override int Gap(Census census, OrderLedger ledger, long now) =>
        Math.Max(0, count - Math.Max(census.Militia, ledger.Permanent(Key)));

    public override int Close(
        SimulationWorld world,
        Census census,
        OrderLedger ledger,
        HandPool hands,
        long now,
        int allowance)
    {
        if (!census.Barracks.IsValid) return 0;
        var closed = 0;
        for (var i = 0; i < allowance; i++)
        {
            var hand = hands.Take();
            if (hand is not { } body) break;
            world.QueueTrainMilitia(new[] { body }, census.Barracks);
            ledger.RecordPermanent(Key);
            closed++;
        }

        return closed;
    }
}

/// <summary>
/// Wants every militia standing somewhere, which is what a posture is.
/// </summary>
/// <remarks>
/// <b>The whole of "posture", and it is one intent because the vocabulary got the verb it was missing.</b>
/// §143: a rally point is a Guard anchor and a stance is which anchor and radius a plan picks, so there is no
/// stance enum here and nothing to enumerate. Changing where a faction stands is changing this rule's radius
/// or its anchor, and a plan can hold two of them under different conditions.
/// <para>
/// It draws from the militia directly rather than from the hand pool: the pool exists to arbitrate hands that
/// several rules want, and nothing else in a plan wants a soldier.
/// </para>
/// </remarks>
internal sealed class Guard : Intent
{
    private readonly float radius;

    public Guard(float radius) => this.radius = radius;

    public override string Name => $"guard the store within {radius:0}m";

    public override int Gap(Census census, OrderLedger ledger, long now)
    {
        if (!census.Store.IsValid) return 0;
        // Militia with no post, plus militia standing at somebody else's post: a plan that changes its
        // posture has to be able to move a garrison and not only fill one.
        var misplaced = 0;
        foreach (var (_, at, held) in census.MilitiaPosts)
        {
            if (Math.Abs(held - radius) > 0.5f || Vector2.DistanceSquared(at, census.Centre) > 4f) misplaced++;
        }

        return Math.Max(0, census.UnpostedMilitia.Count + misplaced - ledger.Outstanding(Key, now));
    }

    private string Key => $"guard:{radius:0}";

    public override int Close(
        SimulationWorld world,
        Census census,
        OrderLedger ledger,
        HandPool hands,
        long now,
        int allowance)
    {
        if (!census.Store.IsValid) return 0;
        var closed = 0;
        foreach (var body in census.UnpostedMilitia)
        {
            if (closed >= allowance) break;
            Post(world, census, ledger, now, body);
            closed++;
        }

        foreach (var (body, at, held) in census.MilitiaPosts)
        {
            if (closed >= allowance) break;
            if (Math.Abs(held - radius) <= 0.5f && Vector2.DistanceSquared(at, census.Centre) <= 4f) continue;
            Post(world, census, ledger, now, body);
            closed++;
        }

        return closed;
    }

    private void Post(SimulationWorld world, Census census, OrderLedger ledger, long now, AgentId body)
    {
        world.QueueAssign(
            new[] { body },
            Assignment.Guard(census.Centre, radius, EconomySystem.WorkShiftSeconds));
        ledger.Record(Key, now);
    }
}

/// <summary>Wants a number of palisades turned to stone.</summary>
internal sealed class Upgrade : Intent
{
    private readonly int count;

    public Upgrade(int count) => this.count = count;

    public override string Name => $"upgrade palisade->stone {count}";

    // As with Structure: stone and open projects are conditions the plan states, not ones this hides.
    public override int Gap(Census census, OrderLedger ledger, long now) =>
        Math.Min(count, census.SoundPalisades.Count);

    public override int Close(
        SimulationWorld world,
        Census census,
        OrderLedger ledger,
        HandPool hands,
        long now,
        int allowance)
    {
        if (allowance <= 0) return 0;
        foreach (var wall in census.SoundPalisades)
        {
            if (!world.BeginUpgrade(wall, NodeKind.StoneWall)) continue;
            // One a decision, and the project it creates stops the gap from reopening: Gap requires no open
            // projects, which the upgrade it just began now is.
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Stone a sound palisade wants to become a stone wall. §71's one proved upgrade.
    /// </summary>
    /// <remarks>
    /// Held here rather than read from <c>StructuralProjects</c> because the cost of a project that has not
    /// begun is not a thing that layer answers — <c>CostFor</c> takes a node already upgrading. A figure the
    /// planner needs before it commits is a figure it has to hold, and this one is asserted against the real
    /// cost by a self-test rather than trusted.
    /// </remarks>
    internal const int StoneForAWall = 120;
}
