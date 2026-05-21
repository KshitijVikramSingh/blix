using System.Numerics;
using Blix.Graphics;

namespace Blix;

public sealed class Transform3D
{
    public Vector3 Position { get; set; } = Vector3.Zero;

    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    public Vector3 Scale { get; set; } = Vector3.One;

    // Local-forward convention matches Camera3D: identity rotation looks down -Z. A
    // GameObject with default rotation faces the same direction as a default camera.
    public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, Rotation);

    public Vector3 Right => Vector3.Transform(Vector3.UnitX, Rotation);

    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Rotation);

    public Matrix4x4 ToMatrix() => GraphicsMatrices.CreateModel(Position, Rotation, Scale);

    public void LookAt(Vector3 target, Vector3 up)
    {
        // Mirrors Camera3D.LookAt: solves the rotation so local -Z aligns with
        // (target - Position). Row-vector basis matrix matches Quaternion.CreateFromRotationMatrix.
        var forward = Vector3.Normalize(target - Position);
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        var actualUp = Vector3.Cross(right, forward);

        var basis = new Matrix4x4(
            right.X, right.Y, right.Z, 0.0f,
            actualUp.X, actualUp.Y, actualUp.Z, 0.0f,
            -forward.X, -forward.Y, -forward.Z, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f);

        Rotation = Quaternion.CreateFromRotationMatrix(basis);
    }
}
