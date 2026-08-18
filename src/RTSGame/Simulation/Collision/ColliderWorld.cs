using System.Numerics;

namespace RTSGame.Simulation.Collision;

internal sealed class ColliderWorld
{
    private readonly List<ColliderProxy> colliders = new();
    private readonly SpatialHash spatialHash = new(cellSize: 2f);
    private readonly HashSet<int> candidateIds = new();
    private bool spatialHashDirty = true;

    public FactionRelations Factions { get; } = new();

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
        spatialHashDirty = true;
        return id;
    }

    public void Remove(ColliderId id)
    {
        if (!Contains(id)) return;
        colliders[id.Value].Enabled = false;
        spatialHashDirty = true;
    }

    public void Move(ColliderId id, Vector2 center)
    {
        if (!Contains(id)) return;
        colliders[id.Value].Center = center;
        spatialHashDirty = true;
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
        EnsureSpatialHash();
        var extent = new Vector2(radius);
        spatialHash.Gather(center - extent, center + extent, candidateIds);
        foreach (var candidateId in candidateIds)
        {
            var candidate = colliders[candidateId];
            if (!MatchesFilter(candidate, filter) || !CircleOverlaps(center, radius, candidate)) continue;
            results.Add(candidate.Id);
        }
    }

    public void QueryAabb(Vector2 center, Vector2 halfExtents, ColliderQueryFilter filter, List<ColliderId> results)
    {
        results.Clear();
        EnsureSpatialHash();
        spatialHash.Gather(center - halfExtents, center + halfExtents, candidateIds);
        foreach (var candidateId in candidateIds)
        {
            var candidate = colliders[candidateId];
            if (!MatchesFilter(candidate, filter) || !AabbOverlaps(center, halfExtents, candidate)) continue;
            results.Add(candidate.Id);
        }
    }

    private bool Contains(ColliderId id) => id.Value >= 0 && id.Value < colliders.Count && colliders[id.Value].Enabled;

    private void EnsureSpatialHash()
    {
        if (!spatialHashDirty) return;
        spatialHash.Rebuild(colliders);
        spatialHashDirty = false;
    }

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
