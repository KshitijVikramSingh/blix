using System.Numerics;

namespace Blix.Geometry;

// Oriented bounding box: an AABB rotated by Orientation around Center. Half-extents
// are in the box's local frame (axis-aligned in OBB-local; rotated to world via
// Orientation). Common for tilted props, debris pieces, character-on-slope hit
// volumes -- anything that doesn't sit axis-aligned but still bounds well with a box.
//
// Storage is 10 floats (Vector3 + Quaternion + Vector3) vs. AABB's 6 (Vector3 + Vector3).
// The intersection tests delegate to "transform query into OBB-local frame, then run
// the AABB version" wherever possible to keep the math compact.
public readonly record struct OrientedBounds3(
    Vector3 Center,
    Quaternion Orientation,
    Vector3 HalfExtents)
{
    // World-space AABB enclosing the OBB. Useful for broadphase culling against
    // mesh bounds; the world extent on each axis is the sum of |projection| of
    // each OBB axis onto that world axis, scaled by the matching half-extent.
    public Bounds3 Bounds
    {
        get
        {
            var axisX = Vector3.Transform(Vector3.UnitX, Orientation);
            var axisY = Vector3.Transform(Vector3.UnitY, Orientation);
            var axisZ = Vector3.Transform(Vector3.UnitZ, Orientation);
            var extent = new Vector3(
                MathF.Abs(axisX.X) * HalfExtents.X + MathF.Abs(axisY.X) * HalfExtents.Y + MathF.Abs(axisZ.X) * HalfExtents.Z,
                MathF.Abs(axisX.Y) * HalfExtents.X + MathF.Abs(axisY.Y) * HalfExtents.Y + MathF.Abs(axisZ.Y) * HalfExtents.Z,
                MathF.Abs(axisX.Z) * HalfExtents.X + MathF.Abs(axisY.Z) * HalfExtents.Y + MathF.Abs(axisZ.Z) * HalfExtents.Z);
            return new Bounds3(Center - extent, Center + extent);
        }
    }
}
