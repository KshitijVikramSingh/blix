using System.Numerics;
using Blix.Graphics;

namespace Blix;

// Camera2D composes a Transform2D for its pose. Zoom, NearDepth, and FarDepth describe
// the orthographic projection and stay on the camera — they are not pose. Transform2D's
// Scale is unused for a camera but left at its default (1,1); the type fits the pose
// concept without forcing a separate "pose-without-scale" sibling type.
public sealed class Camera2D
{
    public Transform2D Transform { get; init; } = new();

    public float Zoom { get; set; } = 1.0f;

    public float NearDepth { get; set; } = -1.0f;

    public float FarDepth { get; set; } = 1.0f;

    public Matrix4x4 GetView()
    {
        // F-016: row-vector convention. V = T_inv * R_inv, applied as v_row * V
        // translates first then rotates. 2D rotation around Z: positive angle
        // rotates +X toward +Y, so the view rotation uses -Rotation.
        // Translation lives in last row (M41, M42).
        var c = MathF.Cos(-Transform.Rotation);
        var s = MathF.Sin(-Transform.Rotation);
        var tx = -Transform.Position.X;
        var ty = -Transform.Position.Y;

        return new Matrix4x4(
            c,                       s,                       0.0f, 0.0f,
            -s,                      c,                       0.0f, 0.0f,
            0.0f,                    0.0f,                    1.0f, 0.0f,
            tx * c + ty * (-s),      tx * s + ty * c,         0.0f, 1.0f);
    }

    public Matrix4x4 GetProjection(float viewportWidth, float viewportHeight)
    {
        if (viewportWidth <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportWidth), "Viewport width must be positive.");
        }

        if (viewportHeight <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportHeight), "Viewport height must be positive.");
        }

        if (Zoom <= 0.0f)
        {
            throw new InvalidOperationException("Camera2D.Zoom must be positive.");
        }

        return GraphicsMatrices.CreateOrthographic(
            viewportWidth / Zoom,
            viewportHeight / Zoom,
            NearDepth,
            FarDepth);
    }

    public Matrix4x4 GetViewProjection(float viewportWidth, float viewportHeight) =>
        GetView() * GetProjection(viewportWidth, viewportHeight);
}
