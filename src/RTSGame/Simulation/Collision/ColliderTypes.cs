using System.Numerics;
using RTSGame.Simulation.Agents;
using RTSGame.Simulation.Spatial;

namespace RTSGame.Simulation.Collision;

[Flags]
internal enum ColliderRole
{
    None = 0,
    MovementSolid = 1 << 0,
    Avoidance = 1 << 1,
    PlacementBlocker = 1 << 2,
    Interactable = 1 << 3,
    Damageable = 1 << 4,
}

[Flags]
internal enum ColliderLayer
{
    None = 0,
    Agent = 1 << 0,
    Structure = 1 << 1,
    Resource = 1 << 2,
    Projectile = 1 << 3,
    All = Agent | Structure | Resource | Projectile,
}

[Flags]
internal enum RelationMask
{
    None = 0,
    Self = 1 << 0,
    Ally = 1 << 1,
    Neutral = 1 << 2,
    Enemy = 1 << 3,
    All = Self | Ally | Neutral | Enemy,
}

internal enum ColliderOwnerKind
{
    Agent,
    Placement,
}

internal enum ColliderShapeKind
{
    Circle,
    Aabb,
}

internal readonly record struct ColliderId(int Value);

internal readonly record struct FactionId(int Value)
{
    public static FactionId None => new(-1);
}

internal readonly record struct ColliderOwner(ColliderOwnerKind Kind, int Value)
{
    public static ColliderOwner Agent(AgentId id) => new(ColliderOwnerKind.Agent, id.Value);
    public static ColliderOwner Placement(GridCell cell, GridTransform grid) =>
        new(ColliderOwnerKind.Placement, grid.Index(cell));
}

internal readonly record struct ColliderShape(ColliderShapeKind Kind, float Radius, Vector2 HalfExtents)
{
    public static ColliderShape Circle(float radius) => new(ColliderShapeKind.Circle, radius, default);
    public static ColliderShape Aabb(Vector2 halfExtents) => new(ColliderShapeKind.Aabb, 0f, halfExtents);

    public void Bounds(Vector2 center, out Vector2 minimum, out Vector2 maximum)
    {
        var extent = Kind == ColliderShapeKind.Circle ? new Vector2(Radius) : HalfExtents;
        minimum = center - extent;
        maximum = center + extent;
    }
}

internal readonly record struct ColliderQueryFilter(
    ColliderRole Roles,
    ColliderLayer Layers,
    RelationMask Relations,
    ColliderOwner? SourceOwner = null,
    FactionId? SourceFaction = null);

internal readonly record struct AgentColliderSet(
    ColliderId Movement,
    ColliderId Avoidance,
    ColliderId Placement,
    ColliderId Interaction);

internal sealed class ColliderProxy
{
    public required ColliderId Id { get; init; }
    public required ColliderOwner Owner { get; init; }
    public required FactionId Faction { get; init; }
    public required ColliderLayer Layer { get; init; }
    public required ColliderRole Roles { get; init; }
    public required ColliderShape Shape { get; init; }
    public Vector2 Center { get; set; }
    public bool Enabled { get; set; } = true;
}
