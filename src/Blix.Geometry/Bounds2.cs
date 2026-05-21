using System.Numerics;

namespace Blix.Geometry;

// 2D analogue of Bounds3: axis-aligned bounding rectangle. Same half-open semantics
// (Min == Max is a degenerate point with sensible Center/Size). 2D physics broadphase
// + raycast queries all early-out against this in CollisionWorld2D.
public readonly record struct Bounds2(Vector2 Min, Vector2 Max)
{
    public static Bounds2 Empty { get; } = new(Vector2.Zero, Vector2.Zero);

    public Vector2 Center => (Min + Max) * 0.5f;

    public Vector2 Size => Max - Min;

    public static Bounds2 FromPoints(ReadOnlySpan<Vector2> points)
    {
        if (points.IsEmpty)
        {
            return Empty;
        }

        var min = points[0];
        var max = points[0];

        for (var i = 1; i < points.Length; i++)
        {
            min = Vector2.Min(min, points[i]);
            max = Vector2.Max(max, points[i]);
        }

        return new Bounds2(min, max);
    }
}
