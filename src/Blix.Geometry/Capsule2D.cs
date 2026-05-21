using System.Numerics;

namespace Blix.Geometry;

// 2D analogue of Capsule: a swept-circle. Set of points within Radius of the
// segment PointA -> PointB. Common shape for side-scroller / top-down character
// hit volumes (a circle is fine for round characters, but a vertical capsule
// climbs ledges and slopes more naturally) and short-range projectile sweeps.
//
// PointA == PointB is a degenerate capsule equal to a circle centred there; the
// math handles this via parametric-t clamping in the closest-point helper.
public readonly record struct Capsule2D(Vector2 PointA, Vector2 PointB, float Radius)
{
    public float SegmentLengthSquared => (PointB - PointA).LengthSquared();

    public Bounds2 Bounds
    {
        get
        {
            var r = new Vector2(Radius);
            return new Bounds2(Vector2.Min(PointA, PointB) - r, Vector2.Max(PointA, PointB) + r);
        }
    }
}
