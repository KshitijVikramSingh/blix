using System.Numerics;

namespace Blix.Geometry;

// Single 2D line segment (PointA -> PointB). The atomic primitive that LineMesh2D
// is built from; also useful standalone when game code wants a one-off wall or
// ground line. Held as a struct so segment arrays pack tightly.
public readonly record struct Segment2D(Vector2 PointA, Vector2 PointB)
{
    public Vector2 Direction => PointB - PointA;

    public float LengthSquared => (PointB - PointA).LengthSquared();

    public Bounds2 Bounds => new(Vector2.Min(PointA, PointB), Vector2.Max(PointA, PointB));
}
