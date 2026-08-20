using System.Numerics;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;

namespace RTSGame.Simulation.Threat;

/// <summary>
/// Harm: what happens when two hostile bodies are standing next to each other.
/// </summary>
/// <remarks>
/// Deliberately not a combat model. There is no attack animation, no cooldown, no facing requirement and no
/// to-hit roll — a body next to something hostile is hurting it, at its strength, per second. That is enough
/// for the only thing Stage E has to find out, which is whether the <em>decision</em> to fight or run is
/// interesting; a richer model would add numbers to tune before anybody knows whether the mechanic works.
/// <para>
/// Written against <b>hostility</b> rather than against raiders, which is the line §28 draws. The scripted
/// thief is scaffolding and lives in <c>Debug/</c>; this is in the simulation because it has to behave the
/// same when the hostility is another player, and it would be the wrong shape if it knew what a raider was.
/// </para>
/// </remarks>
internal sealed class ThreatSystem
{
    /// <summary>
    /// How far apart two bodies can be and still be fighting, on top of their radii.
    /// </summary>
    /// <remarks>
    /// A body's length again, roughly — near enough that the pair are visibly in contact, and loose enough
    /// that the velocity solve keeping them a hair apart does not stop the fight. Tying it to the radii
    /// rather than fixing a distance means a wide body reaches further, which is what a wide body should do.
    /// </remarks>
    internal static float ReachShare = 1.1f;

    /// <summary>Bodies killed by hostiles since the world began.</summary>
    public long Killed { get; private set; }

    /// <summary>Damage dealt since the world began, in body-seconds.</summary>
    public float Dealt { get; private set; }

    /// <summary>
    /// How far from a resource a hostile has to be to be threatening it, in metres.
    /// </summary>
    /// <remarks>
    /// A raider walking past a granary two hundred metres away is not a reason to abandon a harvest. Twelve
    /// metres is close enough to be reaching for it and far enough that the decision is made before the
    /// grain is in its hands.
    /// </remarks>
    internal static float ThreatMetres = 12f;

    /// <summary>
    /// Seconds a defender may be away from a fight and still count toward whether it can be won.
    /// </summary>
    /// <remarks>
    /// <b>This is the answer to "distance belongs in question 2".</b> Strength that cannot arrive before the
    /// fight is decided is not strength: a farmhand twenty metres off counts toward the sum that tells two
    /// people at the granary to stand, and then they die alone while it walks. Eight seconds is about how
    /// long a villager survives a raider, so it is the window inside which help is help.
    /// </remarks>
    internal static float RallySeconds = 8f;

    /// <summary>
    /// How much stronger than the assailants a defence wants to be before it stands.
    /// </summary>
    /// <remarks>
    /// Above one, because equal strength means everybody dies and the grain is taken anyway. A defence that
    /// only fights when it expects to win is not cowardice, it is the whole content of the decision.
    /// </remarks>
    internal static float StandMargin = 1.25f;

    /// <summary>Seconds a body holds a decision to stand or run before asking again.</summary>
    internal static float ResolveSeconds = 3f;

    private readonly List<AgentId> fallen = new();
    private readonly List<int> hostiles = new();
    private readonly List<Vector2> guarded = new();
    private readonly List<float> menace = new();

    /// <summary>
    /// Applies a tick of harm, and reports who died so the world can take them out of it.
    /// </summary>
    /// <remarks>
    /// Damage is accumulated for every body first and applied afterwards, in id order, so that two bodies
    /// killing each other in the same tick both die — the alternative resolves whoever the loop reached
    /// first and lets them survive, which makes the outcome of a fight depend on spawn order.
    /// <para>
    /// Deaths are handed back rather than acted on, because removing a body means dropping its cargo,
    /// releasing four colliders and finishing its path, and only the world knows how to do that.
    /// </para>
    /// </remarks>
    public IReadOnlyList<AgentId> Update(AgentStore agents, FactionRelations factions, float deltaSeconds)
    {
        fallen.Clear();
        var bodies = agents.MutableSpan();

        // One pass to find who is hurting whom. Quadratic in bodies, which is affordable only because it
        // early-outs on hostility: a settlement at peace does one faction comparison per pair and nothing
        // else, and there is only ever a handful of hostiles on the map.
        for (var i = 0; i < bodies.Length; i++)
        {
            ref var attacker = ref bodies[i];
            if (!attacker.IsAlive || attacker.Strength <= 0f) continue;
            for (var j = 0; j < bodies.Length; j++)
            {
                if (i == j) continue;
                ref var target = ref bodies[j];
                if (!target.IsAlive) continue;
                if ((factions.Between(attacker.Faction, target.Faction) & RelationMask.Enemy) == 0)
                {
                    continue;
                }

                var reach = (attacker.Radius + target.Radius) * ReachShare;
                if (Vector2.DistanceSquared(attacker.Position, target.Position) > reach * reach) continue;
                target.Health -= attacker.Strength * deltaSeconds;
                Dealt += attacker.Strength * deltaSeconds;
            }
        }

        for (var i = 0; i < bodies.Length; i++)
        {
            ref readonly var body = ref bodies[i];
            if (!body.IsAlive || body.Health > 0f) continue;
            fallen.Add(body.Id);
            Killed++;
        }

        return fallen;
    }

    /// <summary>What a body may be told to do about a threat, and where.</summary>
    internal delegate void March(AgentId body, Vector2 toward);

    /// <summary>
    /// Stop, because there is nothing to defend any more.
    /// </summary>
    /// <remarks>
    /// <b>The half of the interrupt that was missing.</b> An interrupt that only ever starts things leaves
    /// its last order standing: a defender marched at a raider, the raider ran, question one stopped finding
    /// anything worth protecting — and the defender kept walking to where the raider had been, in a straight
    /// line, until it got there. Watched, that is a column of villagers filing across the map after nothing
    /// at all. Standing down is an action and has to be taken.
    /// </remarks>
    internal delegate void Halt(AgentId body);

    /// <summary>Whether an observer can see a point — the world's sight test, trees and all.</summary>
    internal delegate bool Sees(in AgentState observer, Vector2 target);

    /// <summary>Bodies standing and bodies running, this instant, for the report.</summary>
    public int Standing { get; private set; }

    public int Fleeing { get; private set; }

    /// <summary>
    /// Civilians decide, without being asked, whether to defend what they can see or run for help.
    /// </summary>
    /// <remarks>
    /// <b>Three questions, in order, and nobody gives the order.</b> Protecting your own food should not need
    /// asking, which is what the interrupt layer was invented for long before it was ever a manual command —
    /// so this suspends the assignment and never touches it, and a villager who fights goes back to the
    /// field afterwards with its shift intact.
    /// <list type="number">
    /// <item><b>What can I see that I want to protect?</b> A store with something in it, a heap on the
    /// ground, or an ally carrying a load — anything that would leave with a raider. <em>Seen</em> is
    /// load-bearing: sight is occluded by trees, so an approach through a wood is not answered until it
    /// clears the trees.</item>
    /// <item><b>Can I protect it?</b> The assailants' strength against the strength of everyone who can see
    /// the same thing <em>and could reach it in time</em>. Not my own strength, which is the point:
    /// everybody looking at the same threatened granary is weighing the same sum and reaching the same
    /// answer, so they act together with no leader and no rally order. And distance is in the sum, because
    /// strength that cannot arrive before the fight is decided is not strength.</item>
    /// <item><b>Fight, or run toward the nearest group bigger than mine.</b> Toward rather than away, which
    /// balls a settlement up under threat without anybody authoring a rally point — and the ball, once
    /// formed, may be strong enough that question two answers differently next time it is asked.</item>
    /// </list>
    /// </remarks>
    public void Defend(
        AgentStore agents,
        NodeStore nodes,
        FactionRelations factions,
        float deltaSeconds,
        Sees sees,
        March march,
        Halt halt)
    {
        Standing = 0;
        Fleeing = 0;
        var bodies = agents.MutableSpan();

        // Who is hostile to the settlement. Faction-agnostic: gathered once as indices, and each defender
        // asks the relation itself, so this works the same when the hostility is another player's people.
        hostiles.Clear();
        for (var i = 0; i < bodies.Length; i++)
        {
            if (bodies[i].IsAlive && bodies[i].Strength > 0f) hostiles.Add(i);
        }

        for (var i = 0; i < bodies.Length; i++)
        {
            ref var body = ref bodies[i];
            if (!body.IsAlive) continue;

            // Somebody else is already deciding where this body goes. See AgentState.Directed: the defence
            // is deliberately written against hostility rather than against raiders, and the price of that
            // is that it would run the raiders too if nothing said not to.
            if (body.Directed) continue;
            body.Resolve = MathF.Max(0f, body.Resolve - deltaSeconds);

            // 1. What can I see that is worth protecting, and is something reaching for it?
            var threat = NearestThreatSeen(bodies, nodes, factions, sees, in body, out var where, out var at);
            if (threat <= 0f)
            {
                // Stand down, once, on the tick the danger passes — rather than walking off after the
                // memory of it. A body with no commitment to drop was never engaged and is left alone,
                // which is what keeps this off the twenty-five people who are simply working.
                if (body.Resolve > 0f)
                {
                    body.Resolve = 0f;
                    halt(body.Id);
                }

                body.Standing = false;
                continue;
            }

            // 2. Can we? Everyone who sees the same thing and could get there in time.
            var ours = FriendlyStrengthAt(bodies, factions, sees, in body, where);
            var stand = ours >= threat * StandMargin;

            if (stand) Standing++;
            else Fleeing++;

            // Held for a few seconds, or a body on the margin flips forever and does neither.
            if (body.Resolve > 0f && body.Standing == stand) continue;
            body.Resolve = ResolveSeconds;
            body.Standing = stand;

            // 3. At them, or toward the nearest group bigger than ours.
            if (stand)
            {
                march(body.Id, at);
                continue;
            }

            march(body.Id, Refuge(bodies, factions, in body, ours, at));
        }
    }

    /// <summary>
    /// The nearest thing this body can see that it wants to protect and something is reaching for.
    /// </summary>
    /// <remarks>
    /// Stores and heaps are places; a loaded ally is a resource that walks, which is why a carter on the
    /// road is worth defending and an empty one is not. Returns the assailants' strength there, so question
    /// two has the number it needs without looking again.
    /// </remarks>
    private float NearestThreatSeen(
        Span<AgentState> bodies,
        NodeStore nodes,
        FactionRelations factions,
        Sees sees,
        in AgentState body,
        out Vector2 where,
        out Vector2 at)
    {
        where = default;
        at = default;
        var bestDistance = float.PositiveInfinity;
        var bestThreat = 0f;

        guarded.Clear();
        foreach (ref readonly var node in nodes.All)
        {
            if (!node.IsAlive || node.Stock.Total <= 0) continue;
            if (node.IsStanding) continue;
            if (!node.Stores && !node.IsPile) continue;
            if (node.Faction != body.Faction && !node.IsPile) continue;
            guarded.Add(node.Position);
        }

        for (var i = 0; i < bodies.Length; i++)
        {
            ref readonly var ally = ref bodies[i];
            if (!ally.IsAlive || ally.Jobs.CarriedUnits <= 0) continue;
            if ((factions.Between(body.Faction, ally.Faction) & RelationMask.Ally) == 0) continue;
            guarded.Add(ally.Position);
        }

        foreach (var resource in guarded)
        {
            var distance = Vector2.Distance(body.Position, resource);
            if (distance >= bestDistance || distance > body.SightMetres) continue;
            if (!sees(in body, resource)) continue;

            // Anything hostile close enough to be reaching for it.
            var strength = 0f;
            var nearest = float.PositiveInfinity;
            var attacker = Vector2.Zero;
            foreach (var index in hostiles)
            {
                ref readonly var hostile = ref bodies[index];
                if ((factions.Between(body.Faction, hostile.Faction) & RelationMask.Enemy) == 0) continue;
                var reach = Vector2.Distance(hostile.Position, resource);
                if (reach > ThreatMetres) continue;
                strength += hostile.Strength;
                if (reach >= nearest) continue;
                nearest = reach;
                attacker = hostile.Position;
            }

            if (strength <= 0f) continue;
            bestDistance = distance;
            bestThreat = strength;
            where = resource;
            at = attacker;
        }

        return bestThreat;
    }

    /// <summary>
    /// Strength that can see a place and could reach it before the fight there is decided.
    /// </summary>
    private float FriendlyStrengthAt(
        Span<AgentState> bodies,
        FactionRelations factions,
        Sees sees,
        in AgentState body,
        Vector2 where)
    {
        var total = 0f;
        for (var i = 0; i < bodies.Length; i++)
        {
            ref readonly var ally = ref bodies[i];
            if (!ally.IsAlive || ally.Strength <= 0f) continue;
            if ((factions.Between(body.Faction, ally.Faction) & RelationMask.Ally) == 0) continue;
            // Distance is in the sum, in seconds of walking rather than metres, because what decides
            // whether help is help is whether it arrives.
            if (Vector2.Distance(ally.Position, where) > ally.MaximumSpeed * RallySeconds) continue;
            if (!sees(in ally, where)) continue;
            total += ally.Strength;
        }

        return total;
    }

    /// <summary>
    /// Where to run: toward the nearest ally standing in a bigger group than this one.
    /// </summary>
    /// <remarks>
    /// Toward rather than away, which is what makes a settlement gather rather than scatter — and a
    /// gathering is how question two comes to be answered differently. Nearest, with ties broken by id, or
    /// two runs of the same raid part company over which way somebody ran.
    /// <para>
    /// With nowhere better to be, directly away from whatever is coming. That is the honest fallback: a lone
    /// villager with no larger group anywhere should still not stand there.
    /// </para>
    /// </remarks>
    private Vector2 Refuge(
        Span<AgentState> bodies,
        FactionRelations factions,
        in AgentState body,
        float ours,
        Vector2 attacker)
    {
        var best = Vector2.Zero;
        var bestDistance = float.PositiveInfinity;
        for (var i = 0; i < bodies.Length; i++)
        {
            ref readonly var ally = ref bodies[i];
            if (!ally.IsAlive || ally.Id == body.Id || ally.Strength <= 0f) continue;
            if ((factions.Between(body.Faction, ally.Faction) & RelationMask.Ally) == 0) continue;

            var theirs = 0f;
            for (var j = 0; j < bodies.Length; j++)
            {
                ref readonly var other = ref bodies[j];
                if (!other.IsAlive || other.Strength <= 0f) continue;
                if ((factions.Between(ally.Faction, other.Faction) & RelationMask.Ally) == 0) continue;
                if (Vector2.Distance(other.Position, ally.Position) > other.MaximumSpeed * RallySeconds)
                {
                    continue;
                }

                theirs += other.Strength;
            }

            if (theirs <= ours) continue;
            var distance = Vector2.Distance(body.Position, ally.Position);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = ally.Position;
        }

        if (bestDistance < float.PositiveInfinity) return best;
        var away = body.Position - attacker;
        return away.LengthSquared() > 0.0001f
            ? body.Position + Vector2.Normalize(away) * (body.MaximumSpeed * RallySeconds)
            : body.Position;
    }

    internal void Write(Persistence.WorldWriter writer)
    {
        writer.Long(Killed);
        writer.Float(Dealt);
    }

    internal void Read(Persistence.WorldReader reader)
    {
        Killed = reader.Long();
        Dealt = reader.Float();
    }
}
