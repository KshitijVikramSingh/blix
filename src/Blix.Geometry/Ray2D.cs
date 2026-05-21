using System.Numerics;

namespace Blix.Geometry;

// 2D analogue of Ray: half-line starting at Origin, extending along Direction.
// Direction is expected to be unit length — callers normalise before constructing.
// Raycast methods in Intersection2D take an optional max distance for finite ray
// queries (mouse picking in screen-space, line-of-sight up to a wall).
public readonly record struct Ray2D(Vector2 Origin, Vector2 Direction)
{
    public Vector2 PointAt(float t) => Origin + Direction * t;
}
