using System.Numerics;

namespace Blix.Geometry;

// Axis-aligned bounding box. Mesh importers compute one per mesh at load time; collision
// tests, debug draw, and (eventually) culling all consume it. Half-open semantics: a
// degenerate box where Min == Max represents a single point and still has a sensible
// Center / Size.
public readonly record struct Bounds3(Vector3 Min, Vector3 Max)
{
    public static Bounds3 Empty { get; } = new(Vector3.Zero, Vector3.Zero);

    public Vector3 Center => (Min + Max) * 0.5f;

    public Vector3 Size => Max - Min;

    public static Bounds3 FromPoints(ReadOnlySpan<Vector3> points)
    {
        if (points.IsEmpty)
        {
            return Empty;
        }

        var min = points[0];
        var max = points[0];

        for (var i = 1; i < points.Length; i++)
        {
            min = Vector3.Min(min, points[i]);
            max = Vector3.Max(max, points[i]);
        }

        return new Bounds3(min, max);
    }
}
