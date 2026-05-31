using System.Numerics;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix;

// Camera3D composes a Transform3D rather than embedding pose fields. Position, rotation,
// facing-direction accessors, and LookAt all live on `Transform`; the camera adds only
// projection state (FoV, near/far). The composition makes pose a uniform animation
// target across cameras and GameObjects.
public sealed class Camera3D
{
    public Transform3D Transform { get; init; } = new();

    public float VerticalFieldOfView { get; set; } = MathF.PI / 3.0f;

    public float NearPlane { get; set; } = 0.1f;

    public float FarPlane { get; set; } = 100.0f;

    public Matrix4x4 GetView() =>
        GraphicsMatrices.CreateView(Transform.Position, Transform.Rotation);

    public Matrix4x4 GetProjection(float aspectRatio) =>
        GraphicsMatrices.CreatePerspectiveVulkan(VerticalFieldOfView, aspectRatio, NearPlane, FarPlane);

    public Matrix4x4 GetViewProjection(float aspectRatio) => GetView() * GetProjection(aspectRatio);

    // Build a world-space Ray from a screen-pixel position. Screen coordinates use
    // top-left origin (screen Y grows downward, the window-system convention),
    // which matches Vulkan's Y-down NDC, so the screen→NDC map needs no Y flip.
    // The returned ray's origin sits on the near plane and the direction points
    // away from the camera through the screen pixel toward the far plane.
    //
    // The view-projection is computed from the camera's current Transform + the
    // viewport's aspect ratio, then inverted. The two Vulkan NDC z values
    // (0 = near, 1 = far) get unprojected through the inverse, homogeneous-
    // divided, and subtracted to form the ray direction.
    public Ray ScreenPointToRay(float screenX, float screenY, float viewportWidth, float viewportHeight)
    {
        if (viewportWidth <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportWidth), "Viewport width must be positive.");
        }
        if (viewportHeight <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(viewportHeight), "Viewport height must be positive.");
        }

        // Screen -> Vulkan NDC. NDC y points down, same as screen y — no flip.
        var ndcX = 2.0f * screenX / viewportWidth - 1.0f;
        var ndcY = 2.0f * screenY / viewportHeight - 1.0f;

        var viewProjection = GetViewProjection(viewportWidth / viewportHeight);
        if (!Matrix4x4.Invert(viewProjection, out var inv))
        {
            // Degenerate matrix (shouldn't happen for normal camera params) — fall back
            // to a forward ray from the camera so callers always get a usable Ray.
            return new Ray(Transform.Position, Transform.Forward);
        }

        // Engine row-vector form: Vector4.Transform applies v_row * M, which is
        // exactly what we need for unprojecting NDC through inv(viewProj).
        // Vulkan clip depth is [0, 1]: near = 0, far = 1.
        var nearH = Vector4.Transform(new Vector4(ndcX, ndcY, 0.0f, 1.0f), inv);
        var farH  = Vector4.Transform(new Vector4(ndcX, ndcY, 1.0f, 1.0f), inv);

        var near = new Vector3(nearH.X / nearH.W, nearH.Y / nearH.W, nearH.Z / nearH.W);
        var far  = new Vector3(farH.X  / farH.W,  farH.Y  / farH.W,  farH.Z  / farH.W);

        return new Ray(near, Vector3.Normalize(far - near));
    }
}
