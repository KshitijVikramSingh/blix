using System.Numerics;

namespace Blix;

public sealed class Transform2D
{
    public Vector2 Position { get; set; } = Vector2.Zero;

    // Radians around the +Z axis (counter-clockwise when viewed from +Z toward -Z).
    public float Rotation { get; set; }

    public Vector2 Scale { get; set; } = Vector2.One;

    // Local +X after rotation. The natural "facing direction" for 2D things authored
    // facing right at zero rotation (most sprite art convention).
    public Vector2 Right => new(MathF.Cos(Rotation), MathF.Sin(Rotation));

    // Local +Y after rotation. Useful for "top-down facing" conventions where +Y is up
    // and the sprite's nose points along the local Y axis.
    public Vector2 Up => new(-MathF.Sin(Rotation), MathF.Cos(Rotation));

    public Matrix4x4 ToMatrix()
    {
        // T * R * S lifted into a 4x4. Z-row stays identity so 2D content sits on the
        // z=0 plane and composes naturally with 3D camera/view matrices.
        var c = MathF.Cos(Rotation);
        var s = MathF.Sin(Rotation);
        return new Matrix4x4(
            c * Scale.X, -s * Scale.Y, 0.0f, Position.X,
            s * Scale.X,  c * Scale.Y, 0.0f, Position.Y,
            0.0f,         0.0f,         1.0f, 0.0f,
            0.0f,         0.0f,         0.0f, 1.0f);
    }
}
