using System.Numerics;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;

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

    private readonly List<AgentId> fallen = new();

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
