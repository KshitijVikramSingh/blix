using System.Numerics;

namespace Blix.Geometry;

// Half-line in 3D: starts at Origin, extends infinitely along Direction. Direction is
// expected to be unit length — callers normalise before constructing. Raycast methods
// in Intersection take an optional max distance for finite ray queries (picking under
// the cursor, line-of-sight up to a wall, etc.).
public readonly record struct Ray(Vector3 Origin, Vector3 Direction)
{
    public Vector3 PointAt(float t) => Origin + Direction * t;
}
