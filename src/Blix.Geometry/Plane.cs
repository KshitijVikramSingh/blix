using System.Numerics;

namespace Blix.Geometry;

// Infinite plane in 3D defined by a unit normal and a signed offset from origin along
// that normal. The plane equation is `Normal · P = Offset` — any point P satisfying it
// lies on the plane.
//
// Collision convention: `Normal` points AWAY from the solid halfspace. The plane
// represents the boundary; the solid region is on the negative-N side
// (`SignedDistance(P) < 0`). A floor with `Normal = +Y` therefore has its solid region
// below the plane — the typical "stand on top of the floor" case. Walls and ceilings
// follow the same convention with the appropriate normal.
//
// Normal is expected to be unit length. Use FromPointNormal if you have an arbitrary
// normal direction and want it normalised automatically.
public readonly record struct Plane(Vector3 Normal, float Offset)
{
    public static Plane FromPointNormal(Vector3 point, Vector3 normal)
    {
        var n = Vector3.Normalize(normal);
        return new Plane(n, Vector3.Dot(n, point));
    }

    // Signed perpendicular distance from `point` to the plane along Normal. Positive
    // when the point is on the same side as the normal (the "outside" / free space);
    // negative when inside the solid halfspace. Zero when on the plane.
    public float SignedDistance(Vector3 point) => Vector3.Dot(Normal, point) - Offset;
}
