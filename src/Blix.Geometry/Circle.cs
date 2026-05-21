using System.Numerics;

namespace Blix.Geometry;

// 2D analogue of BoundingSphere. Renamed because "sphere" doesn't fit in 2D — a
// circle is the right word and reads more naturally in 2D game code. Cheap
// intersect tests; natural shape for roughly-round 2D content (characters, coins,
// projectile blasts).
public readonly record struct Circle(Vector2 Center, float Radius)
{
    public static Circle Empty { get; } = new(Vector2.Zero, 0.0f);

    // Smallest circle that contains an existing AABB. Looser than the optimal
    // bounding circle of an arbitrary point set, but fast and stable for broadphase.
    public static Circle FromBounds(Bounds2 bounds) =>
        new(bounds.Center, (bounds.Max - bounds.Min).Length() * 0.5f);
}
