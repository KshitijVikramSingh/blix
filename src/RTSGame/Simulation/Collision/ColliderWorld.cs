using System.Numerics;

namespace RTSGame.Simulation.Collision;

internal sealed class ColliderWorld
{
    private readonly List<ColliderProxy> colliders = new();
    /// <summary>Broad phase over everything that is not an agent body.</summary>
    /// <remarks>
    /// Split by layer because agent proxies move constantly and the queries that
    /// run every tick never want them. Every agent carries four proxies, all four
    /// are re-centred twice a tick, and a single hash marked itself stale on each
    /// of those eight writes — so the next query rebuilt the whole structure,
    /// several thousand proxies, from a fresh dictionary. The one query the tick
    /// actually makes is for static geometry, and structures only move when
    /// something is built or demolished. Keeping the two apart means the static
    /// side is rebuilt when the map changes rather than when a unit walks.
    /// </remarks>
    private readonly SpatialHash staticHash = new(cellSize: 2f);
    private readonly SpatialHash agentHash = new(cellSize: 2f);
    private readonly List<ColliderProxy> staticColliders = new();
    private readonly List<ColliderProxy> agentColliders = new();
    private readonly HashSet<int> candidateIds = new();
    private readonly HashSet<int> agentCandidateIds = new();
    private bool staticHashDirty = true;
    private bool agentHashDirty = true;
    private bool partitionsDirty = true;

    public FactionRelations Factions { get; } = new();

    /// <summary>
    /// Every proxy ever added, in id order, including removed ones.
    /// </summary>
    /// <remarks>
    /// For the determinism fingerprint, which needs the whole list rather than the enabled
    /// part of it: ids are handed out by position, so two worlds that removed different
    /// proxies would go on agreeing about everything enabled while disagreeing about which
    /// id the next structure gets.
    /// </remarks>
    internal IReadOnlyList<ColliderProxy> All => colliders;

    public ColliderId Add(
        ColliderOwner owner,
        FactionId faction,
        ColliderLayer layer,
        ColliderRole roles,
        ColliderShape shape,
        Vector2 center)
    {
        var id = new ColliderId(colliders.Count);
        colliders.Add(new ColliderProxy
        {
            Id = id,
            Owner = owner,
            Faction = faction,
            Layer = layer,
            Roles = roles,
            Shape = shape,
            Center = center,
        });
        partitionsDirty = true;
        staticHashDirty = true;
        agentHashDirty = true;
        return id;
    }

    public void Remove(ColliderId id)
    {
        if (!Contains(id)) return;
        colliders[id.Value].Enabled = false;
        partitionsDirty = true;
        staticHashDirty = true;
        agentHashDirty = true;
    }

    public void Move(ColliderId id, Vector2 center)
    {
        if (!Contains(id)) return;
        var collider = colliders[id.Value];
        if (collider.Center == center) return;
        collider.Center = center;
        if ((collider.Layer & ColliderLayer.Agent) != 0) agentHashDirty = true;
        else staticHashDirty = true;
    }

    public ColliderProxy Get(ColliderId id)
    {
        if (!Contains(id)) throw new ArgumentOutOfRangeException(nameof(id));
        return colliders[id.Value];
    }

    public RelationMask Relationship(ColliderOwner sourceOwner, FactionId sourceFaction, ColliderId target)
    {
        var candidate = Get(target);
        return Relationship(candidate, sourceOwner, sourceFaction);
    }

    public void QueryCircle(Vector2 center, float radius, ColliderQueryFilter filter, List<ColliderId> results)
    {
        results.Clear();
        var extent = new Vector2(radius);
        foreach (var candidateId in Gather(center - extent, center + extent, filter.Layers))
        {
            var candidate = colliders[candidateId];
            if (!MatchesFilter(candidate, filter) || !CircleOverlaps(center, radius, candidate)) continue;
            results.Add(candidate.Id);
        }
    }

    public void QueryAabb(Vector2 center, Vector2 halfExtents, ColliderQueryFilter filter, List<ColliderId> results)
    {
        results.Clear();
        foreach (var candidateId in Gather(center - halfExtents, center + halfExtents, filter.Layers))
        {
            var candidate = colliders[candidateId];
            if (!MatchesFilter(candidate, filter) || !AabbOverlaps(center, halfExtents, candidate)) continue;
            results.Add(candidate.Id);
        }
    }

    /// <summary>
    /// Candidate ids from whichever partitions the requested layers can occupy.
    /// </summary>
    /// <remarks>
    /// A query that wants only structures never touches the agent partition, and so
    /// never pays to have it rebuilt — which is the whole point, since that is the
    /// only query the tick makes. Gathering is deterministic for a given sequence of
    /// edits, as it was before the split, but callers that care about the order they
    /// see contacts in must impose it themselves.
    /// </remarks>
    private IEnumerable<int> Gather(Vector2 minimum, Vector2 maximum, ColliderLayer layers)
    {
        EnsurePartitions();
        var wantsStatic = (layers & ~ColliderLayer.Agent) != 0;
        var wantsAgents = (layers & ColliderLayer.Agent) != 0;
        if (wantsStatic)
        {
            if (staticHashDirty)
            {
                staticHash.Rebuild(staticColliders);
                staticHashDirty = false;
            }
            staticHash.Gather(minimum, maximum, candidateIds);
        }
        else
        {
            candidateIds.Clear();
        }

        if (!wantsAgents) return candidateIds;
        if (agentHashDirty)
        {
            agentHash.Rebuild(agentColliders);
            agentHashDirty = false;
        }
        agentHash.Gather(minimum, maximum, agentCandidateIds);
        candidateIds.UnionWith(agentCandidateIds);
        return candidateIds;
    }

    private void EnsurePartitions()
    {
        if (!partitionsDirty) return;
        staticColliders.Clear();
        agentColliders.Clear();
        foreach (var collider in colliders)
        {
            if ((collider.Layer & ColliderLayer.Agent) != 0) agentColliders.Add(collider);
            else staticColliders.Add(collider);
        }
        partitionsDirty = false;
        staticHashDirty = true;
        agentHashDirty = true;
    }

    private bool Contains(ColliderId id) => id.Value >= 0 && id.Value < colliders.Count && colliders[id.Value].Enabled;

    private bool MatchesFilter(ColliderProxy candidate, ColliderQueryFilter filter)
    {
        if (!candidate.Enabled || (candidate.Roles & filter.Roles) == 0 || (candidate.Layer & filter.Layers) == 0)
        {
            return false;
        }

        var relationship = Relationship(candidate, filter.SourceOwner, filter.SourceFaction);
        return (filter.Relations & relationship) != 0;
    }

    private RelationMask Relationship(ColliderProxy candidate, ColliderOwner? sourceOwner, FactionId? sourceFaction)
    {
        if (sourceOwner is { } owner && owner == candidate.Owner) return RelationMask.Self;
        return sourceFaction is { } source
            ? Factions.Between(source, candidate.Faction)
            : RelationMask.Neutral;
    }

    private static bool CircleOverlaps(Vector2 center, float radius, ColliderProxy candidate)
    {
        if (candidate.Shape.Kind == ColliderShapeKind.Circle)
        {
            var totalRadius = radius + candidate.Shape.Radius;
            return Vector2.DistanceSquared(center, candidate.Center) <= totalRadius * totalRadius;
        }

        var closest = Vector2.Clamp(
            center,
            candidate.Center - candidate.Shape.HalfExtents,
            candidate.Center + candidate.Shape.HalfExtents);
        return Vector2.DistanceSquared(center, closest) <= radius * radius;
    }

    private static bool AabbOverlaps(Vector2 center, Vector2 halfExtents, ColliderProxy candidate)
    {
        if (candidate.Shape.Kind == ColliderShapeKind.Aabb)
        {
            var distance = Vector2.Abs(center - candidate.Center);
            var combined = halfExtents + candidate.Shape.HalfExtents;
            return distance.X <= combined.X && distance.Y <= combined.Y;
        }

        var closest = Vector2.Clamp(
            candidate.Center,
            center - halfExtents,
            center + halfExtents);
        return Vector2.DistanceSquared(candidate.Center, closest) <= candidate.Shape.Radius * candidate.Shape.Radius;
    }
}
