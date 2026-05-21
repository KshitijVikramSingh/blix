using System.Numerics;
using Blix.Geometry;

namespace Blix.Diagnostics;

public static class BoundsTransform
{
    public static Bounds3 Transform(Bounds3 bounds, Matrix4x4 transform)
    {
        Span<Vector3> corners =
        [
            new(bounds.Min.X, bounds.Min.Y, bounds.Min.Z),
            new(bounds.Max.X, bounds.Min.Y, bounds.Min.Z),
            new(bounds.Min.X, bounds.Max.Y, bounds.Min.Z),
            new(bounds.Max.X, bounds.Max.Y, bounds.Min.Z),
            new(bounds.Min.X, bounds.Min.Y, bounds.Max.Z),
            new(bounds.Max.X, bounds.Min.Y, bounds.Max.Z),
            new(bounds.Min.X, bounds.Max.Y, bounds.Max.Z),
            new(bounds.Max.X, bounds.Max.Y, bounds.Max.Z)
        ];

        for (var i = 0; i < corners.Length; i++)
        {
            corners[i] = TransformPoint(transform, corners[i]);
        }

        return Bounds3.FromPoints(corners);
    }

    private static Vector3 TransformPoint(Matrix4x4 matrix, Vector3 point)
    {
        var world = TransformColumnVector(matrix, new Vector4(point, 1.0f));
        return new Vector3(world.X / world.W, world.Y / world.W, world.Z / world.W);
    }

    private static Vector4 TransformColumnVector(Matrix4x4 m, Vector4 v)
    {
        return new Vector4(
            m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z + m.M14 * v.W,
            m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z + m.M24 * v.W,
            m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z + m.M34 * v.W,
            m.M41 * v.X + m.M42 * v.Y + m.M43 * v.Z + m.M44 * v.W);
    }
}
