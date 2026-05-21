using System.Numerics;

namespace Blix.Geometry;

// 2D analogue of OrientedBounds3: an AABB rotated by Rotation (radians, CCW around
// the implicit +Z axis matching Transform2D's convention) about Center. HalfExtents
// are in the box's local frame.
//
// Storage is 5 floats vs. Bounds2's 4 — adding rotation adds one scalar in 2D vs.
// 3D's quaternion (4 floats). Intersection tests use the SAT-with-4-axes pattern
// (each box contributes 2 unique edge normals; the other 2 are colinear and dropped)
// or transform the query to the OBB-local frame and run the AABB version.
public readonly record struct OrientedBounds2(
    Vector2 Center,
    float Rotation,
    Vector2 HalfExtents)
{
    // Local X / Y axes in world space. Stored for SAT projections.
    public Vector2 AxisX => new(MathF.Cos(Rotation), MathF.Sin(Rotation));

    public Vector2 AxisY => new(-MathF.Sin(Rotation), MathF.Cos(Rotation));

    // World-space AABB enclosing the OBB. Useful for broadphase culling — the
    // world extent on each axis is the sum of |projection| of each local axis onto
    // that world axis, scaled by the matching half-extent.
    public Bounds2 Bounds
    {
        get
        {
            var ax = AxisX;
            var ay = AxisY;
            var extent = new Vector2(
                MathF.Abs(ax.X) * HalfExtents.X + MathF.Abs(ay.X) * HalfExtents.Y,
                MathF.Abs(ax.Y) * HalfExtents.X + MathF.Abs(ay.Y) * HalfExtents.Y);
            return new Bounds2(Center - extent, Center + extent);
        }
    }
}
