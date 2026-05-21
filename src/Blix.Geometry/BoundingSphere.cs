using System.Numerics;

namespace Blix.Geometry;

// Axis-free bounding volume: a point and a radius. Cheap intersect-tests and the most
// natural fit for roughly-spherical objects (heads, balls, magic effects). For boxy
// content prefer Bounds3 — AABBs hug oriented-rectangular shapes more tightly than
// a sphere ever will.
public readonly record struct BoundingSphere(Vector3 Center, float Radius)
{
    public static BoundingSphere Empty { get; } = new(Vector3.Zero, 0.0f);

    // Smallest sphere that contains an existing AABB. Looser than the optimal bounding
    // sphere of an arbitrary point set, but fast and stable for collision broadphases.
    public static BoundingSphere FromBounds(Bounds3 bounds) =>
        new(bounds.Center, (bounds.Max - bounds.Min).Length() * 0.5f);
}
