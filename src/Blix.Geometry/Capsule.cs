using System.Numerics;

namespace Blix.Geometry;

// A swept-sphere primitive: the set of points within Radius of the line segment
// running from PointA to PointB. Common shape for character controllers (the
// "humanoid capsule" -- vertical segment with end caps that handle stair / step
// climbing naturally) and casting volumes for visibility / projectile queries.
//
// A degenerate capsule with PointA == PointB is just a sphere centred there; the
// math handles this case via parametric-t clamping.
//
// Convention notes:
//   - Bounds is the world-space AABB enclosing the entire capsule (segment +
//     hemispherical caps). Useful for broadphase culling against TriangleMesh3D
//     and other axis-aligned tests.
//   - Endpoints are world-space. A future "TransformedCapsule" with local
//     endpoints + a Transform3D reference would let posed capsules track their
//     parent without rebuilding -- not built until first use case demands it.
public readonly record struct Capsule(Vector3 PointA, Vector3 PointB, float Radius)
{
    public float SegmentLengthSquared => (PointB - PointA).LengthSquared();

    public Bounds3 Bounds
    {
        get
        {
            var r = new Vector3(Radius);
            return new Bounds3(Vector3.Min(PointA, PointB) - r, Vector3.Max(PointA, PointB) + r);
        }
    }
}
