using System.Numerics;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Navigation;
using RTSGame.Simulation.Spatial;
using RTSGame.Simulation.Terrain;

namespace RTSGame.Simulation.Movement;

internal sealed class CollisionSystem
{
    // Five. Three was the optimum before contacts cancelled closing speed and
    // before the solver aimed past tangency on contact; with both of those, three
    // still leaves chokepoint overlap (0.697 against a 0.730 bar) and four leaves
    // four units permanently wedged in a blocked pen. Six and seven are not better
    // still: both break the terrain-ramp crossing, so this is a genuine optimum and
    // not a floor. The loop exits early once nothing is touching, so open ground
    // pays almost nothing for it.
    private const int RelaxationPasses = 5;
    // Still under-relaxed, so a dense pack cannot pump energy back into itself.
    // 0.80. Raising it is not a substitute for more passes: 0.90 and 0.95 both
    // left more overlap than 0.80 does (0.683 and 0.706 against 0.728 at equal
    // pass count), because over-correcting a body with several neighbours sums
    // into an overshoot the next pass has to undo.
    private const float RelaxationFactor = 0.80f;
    /// <summary>Overlap ignored as a single tick's contact, in metres.</summary>
    private const float ContactSlop = 0.00035f;
    // How much of a mover/idle contact the yielding body absorbs.
    private const float YieldShare = 0.85f;
    // How far the separation is rotated from the contact normal toward the
    // mover's lateral axis. Fully lateral would let bodies pass through each
    // other head-on; this keeps enough normal component to guarantee separation.
    private const float YieldLateralBias = 0.75f;
    /// <summary>Overlap at which the sidestep gives way to separating outright.</summary>
    /// <remarks>
    /// Rotating a correction toward the mover's flank is what turns a shove into a
    /// sidestep, but it also throws away most of its separating power — at the full
    /// bias only about two thirds of the correction lies along the normal, so a pair
    /// that is genuinely interpenetrated comes apart slowly. That is invisible for
    /// the brush-past this exists to make look right, and it is not invisible in a
    /// crowd converging on one destination, where arriving bodies contact settled
    /// ones continuously and the residual never gets a quiet tick to clear. So the
    /// bias is a function of how bad the overlap is: a sidestep for a touch, and the
    /// shortest way apart for anything deep enough to see.
    /// </remarks>
    private const float YieldLateralOverlapLimit = 0.015f;

    private readonly List<ColliderId> contacts = new();
    private readonly List<int> neighbors = new();
    private Vector2[] corrections = Array.Empty<Vector2>();

    public int Resolve(
        AgentStore agents,
        ColliderWorld colliders,
        AgentSpatialIndex index,
        PathService paths,
        TerrainMap terrain)
    {
        var contactCount = ResolveStaticContacts(agents, colliders, terrain);
        contactCount += ResolveAgentContacts(agents, index, paths, terrain);
        SyncMovementColliders(agents, colliders);
        return contactCount;
    }

    /// <summary>
    /// Positional depenetration between agent bodies.
    /// </summary>
    /// <remarks>
    /// ORCA is a velocity filter: it can be overruled by a rejected integration
    /// step, by the terrain-safe fallback collapsing to zero, or by a body being
    /// reverted to its previous position. Without a position-level solve there is
    /// nothing that actually guarantees non-overlap, and dense crowds settle
    /// interpenetrated. Corrections accumulate into a buffer and are applied
    /// after each pass, so the result does not depend on iteration order.
    /// </remarks>
    private int ResolveAgentContacts(
        AgentStore agents,
        AgentSpatialIndex index,
        PathService paths,
        TerrainMap terrain)
    {
        var mutable = agents.MutableSpan();
        if (mutable.Length < 2) return 0;
        if (corrections.Length < mutable.Length) Array.Resize(ref corrections, mutable.Length);

        var largestRadius = MaximumRadius(agents.All);
        var resolved = 0;
        for (var pass = 0; pass < RelaxationPasses; pass++)
        {
            index.Rebuild(agents.All);
            Array.Clear(corrections, 0, mutable.Length);
            var contactsThisPass = 0;

            for (var i = 0; i < mutable.Length; i++)
            {
                ref var agent = ref mutable[i];
                if (!agent.IsAlive) continue;
                var searchRadius = agent.Radius + largestRadius;
                index.Query(agent.Position, searchRadius, i, neighbors);

                foreach (var j in neighbors)
                {
                    // Each unordered pair is handled once, by its lower index.
                    if (j <= i) continue;
                    ref var other = ref mutable[j];
                    var offset = other.Position - agent.Position;
                    var minimumDistance = agent.Radius + other.Radius;
                    var distanceSquared = offset.LengthSquared();
                    // Compared in distance, not in squared distance. The slop used to
                    // be subtracted from the squared bound, where it is not a length
                    // at all and its effect scales with how big the bodies are.
                    var contactDistance = minimumDistance - ContactSlop;
                    if (distanceSquared >= contactDistance * contactDistance) continue;

                    var distance = MathF.Sqrt(distanceSquared);
                    var normal = distance > 0.0001f
                        ? offset / distance
                        : StableSeparationDirection(agent.Id, other.Id);
                    var penetration = minimumDistance - distance;

                    // A stationary body (a wall unit, or anything with no speed
                    // budget) behaves as infinite mass so a crowd cannot shove it
                    // out of a doorway it is deliberately holding.
                    var agentMovable = agent.MaximumSpeed > 0f;
                    var otherMovable = other.MaximumSpeed > 0f;
                    if (!agentMovable && !otherMovable) continue;
                    var agentShare = !agentMovable ? 0f : !otherMovable ? 1f : 0.5f;

                    // A unit walking into a settled ally should make it step
                    // aside, not push it down the corridor. The contact normal
                    // between them points along the direction of travel, so an
                    // even split just bulldozes the idle body ahead of the mover
                    // for as long as the order lasts. Redirect the separation
                    // across the mover's heading and let the yielding body take
                    // most of it: that is a sidestep, and it costs the traveller
                    // no forward progress.
                    if (agentMovable && otherMovable && agent.HasDestination != other.HasDestination)
                    {
                        var mover = agent.HasDestination ? agent.Velocity : other.Velocity;
                        var bias = YieldLateralBias *
                                   (1f - Math.Clamp(penetration / YieldLateralOverlapLimit, 0f, 1f));
                        if (bias > 0.0001f && mover.LengthSquared() > 0.04f)
                        {
                            var travel = Vector2.Normalize(mover);
                            var side = new Vector2(travel.Y, -travel.X);
                            if (Vector2.Dot(normal, side) < 0f) side = -side;
                            normal = Vector2.Normalize(Vector2.Lerp(normal, side, bias));
                        }
                        agentShare = agent.HasDestination ? 1f - YieldShare : YieldShare;
                    }
                    var otherShare = 1f - agentShare;

                    var correction = normal * (penetration * RelaxationFactor);
                    corrections[i] -= correction * agentShare;
                    corrections[j] += correction * otherShare;

                    // Take the closing speed out of the pair as well as the
                    // overlap. Separating them positionally while leaving them
                    // driving into each other means they are overlapping again on
                    // the next step, which is the whole of the residual contact
                    // that survived at a chokepoint: not a failure to converge,
                    // but a solve that was being undone as fast as it ran.
                    var closing = Vector2.Dot(other.Velocity - agent.Velocity, normal);
                    if (closing < 0f)
                    {
                        agent.Velocity += normal * (closing * agentShare);
                        other.Velocity -= normal * (closing * otherShare);
                    }
                    contactsThisPass++;
                    agent.HadAgentContactThisTick = true;
                    other.HadAgentContactThisTick = true;
                }
            }

            if (contactsThisPass == 0) break;
            resolved += contactsThisPass;

            for (var i = 0; i < mutable.Length; i++)
            {
                if (corrections[i].LengthSquared() <= 0f) continue;
                ref var agent = ref mutable[i];
                if (!agent.IsAlive) continue;
                // Depenetration must never push a body into static geometry; a
                // crowd squeezing against a wall would otherwise extrude units
                // through it. But forfeiting the whole correction is why residual
                // overlap survived at a chokepoint: the separation a crowd needs
                // there is mostly along the wall, and it was being thrown away
                // together with the component into it. Slide instead of giving up.
                if (!TryApplyCorrection(ref agent, corrections[i], paths, terrain) &&
                    !TryApplyCorrection(ref agent, corrections[i] with { Y = 0f }, paths, terrain))
                {
                    TryApplyCorrection(ref agent, corrections[i] with { X = 0f }, paths, terrain);
                }
            }
        }
        return resolved;
    }

    private static bool TryApplyCorrection(
        ref AgentState agent,
        Vector2 correction,
        PathService paths,
        TerrainMap terrain)
    {
        if (correction.LengthSquared() <= 0f) return false;
        var proposed = terrain.ClampPosition(
            agent.Position + correction,
            agent.Radius + 0.035f);
        if (!paths.IsPositionNavigable(proposed, agent.Radius)) return false;
        agent.Position = proposed;
        return true;
    }

    private static float MaximumRadius(ReadOnlySpan<AgentState> agents)
    {
        var maximum = 0f;
        foreach (ref readonly var agent in agents)
        {
            if (agent.IsAlive) maximum = MathF.Max(maximum, agent.Radius);
        }
        return maximum;
    }

    private static Vector2 StableSeparationDirection(AgentId first, AgentId second)
    {
        var hash = unchecked((uint)(first.Value * 73856093) ^ (uint)(second.Value * 19349663));
        var angle = hash % 1024 / 1024f * MathF.Tau;
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
    }

    private int ResolveStaticContacts(AgentStore agents, ColliderWorld colliders, TerrainMap terrain)
    {
        var count = 0;
        var mutable = agents.MutableSpan();
        for (var i = 0; i < mutable.Length; i++)
        {
            ref var agent = ref mutable[i];
            if (!agent.IsAlive) continue;
            colliders.QueryCircle(
                agent.Position,
                agent.Radius,
                new ColliderQueryFilter(
                    ColliderRole.MovementSolid,
                    ColliderLayer.Structure,
                    RelationMask.All,
                    ColliderOwner.Agent(agent.Id),
                    agent.Faction),
                contacts);

            foreach (var contact in contacts)
            {
                var obstacle = colliders.Get(contact);
                if (obstacle.Shape.Kind != ColliderShapeKind.Aabb) continue;
                if (!ResolveCircleAabb(ref agent, obstacle.Center, obstacle.Shape.HalfExtents)) continue;
                count++;
            }
            agent.Position = terrain.ClampPosition(agent.Position, agent.Radius + 0.035f);
        }
        return count;
    }

    private static bool ResolveCircleAabb(ref AgentState agent, Vector2 center, Vector2 halfExtents)
    {
        var minimum = center - halfExtents;
        var maximum = center + halfExtents;
        var closest = Vector2.Clamp(agent.Position, minimum, maximum);
        var offset = agent.Position - closest;
        var distanceSquared = offset.LengthSquared();
        if (distanceSquared >= agent.Radius * agent.Radius) return false;

        Vector2 normal;
        float penetration;
        if (distanceSquared > 0.000001f)
        {
            var distance = MathF.Sqrt(distanceSquared);
            normal = offset / distance;
            penetration = agent.Radius - distance;
        }
        else
        {
            var left = agent.Position.X - minimum.X;
            var right = maximum.X - agent.Position.X;
            var top = agent.Position.Y - minimum.Y;
            var bottom = maximum.Y - agent.Position.Y;
            var nearest = MathF.Min(MathF.Min(left, right), MathF.Min(top, bottom));
            if (nearest == left) normal = -Vector2.UnitX;
            else if (nearest == right) normal = Vector2.UnitX;
            else if (nearest == top) normal = -Vector2.UnitY;
            else normal = Vector2.UnitY;
            penetration = agent.Radius + nearest;
        }

        agent.Position += normal * penetration;
        var intoObstacle = Vector2.Dot(agent.Velocity, normal);
        if (intoObstacle < 0f) agent.Velocity -= normal * intoObstacle;
        return true;
    }

    private static void SyncMovementColliders(AgentStore agents, ColliderWorld colliders)
    {
        foreach (ref readonly var agent in agents.All)
        {
            if (!agent.IsAlive) continue;
            colliders.Move(agent.Colliders.Movement, agent.Position);
        }
    }
}
