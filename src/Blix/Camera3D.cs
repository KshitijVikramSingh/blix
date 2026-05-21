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
        GraphicsMatrices.CreatePerspective(VerticalFieldOfView, aspectRatio, NearPlane, FarPlane);

    public Matrix4x4 GetViewProjection(float aspectRatio) => GetProjection(aspectRatio) * GetView();

    // Build a world-space Ray from a screen-pixel position. Screen coordinates use
    // top-left origin (screen Y grows downward, the OpenTK / window-system convention);
    // the unprojection flips that into NDC's bottom-left origin internally. The
    // returned ray's origin sits on the near plane and the direction points away
    // from the camera through the screen pixel toward the far plane.
    //
    // The view-projection is computed from the camera's current Transform + the
    // viewport's aspect ratio, then inverted. The two NDC z values (-1 = near,
    // +1 = far) get unprojected through the inverse, homogeneous-divided, and
    // subtracted to form the ray direction.
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

        // Screen -> NDC. NDC y is up; screen y is down — flip.
        var ndcX = 2.0f * screenX / viewportWidth - 1.0f;
        var ndcY = 1.0f - 2.0f * screenY / viewportHeight;

        var viewProjection = GetViewProjection(viewportWidth / viewportHeight);
        if (!Matrix4x4.Invert(viewProjection, out var inv))
        {
            // Degenerate matrix (shouldn't happen for normal camera params) — fall back
            // to a forward ray from the camera so callers always get a usable Ray.
            return new Ray(Transform.Position, Transform.Forward);
        }

        var nearH = TransformColumnVector4(inv, new Vector4(ndcX, ndcY, -1.0f, 1.0f));
        var farH  = TransformColumnVector4(inv, new Vector4(ndcX, ndcY,  1.0f, 1.0f));

        var near = new Vector3(nearH.X / nearH.W, nearH.Y / nearH.W, nearH.Z / nearH.W);
        var far  = new Vector3(farH.X  / farH.W,  farH.Y  / farH.W,  farH.Z  / farH.W);

        return new Ray(near, Vector3.Normalize(far - near));
    }

    // Vector4 transform under column-vector convention. Mirrors GraphicsMatrices'
    // TransformPoint / TransformDirection but for w-bearing homogeneous coords
    // (System.Numerics Vector4.Transform uses row-vector convention and would
    // ignore the M14/M24/M34 translation column).
    private static Vector4 TransformColumnVector4(Matrix4x4 m, Vector4 v)
    {
        return new Vector4(
            m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z + m.M14 * v.W,
            m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z + m.M24 * v.W,
            m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z + m.M34 * v.W,
            m.M41 * v.X + m.M42 * v.Y + m.M43 * v.Z + m.M44 * v.W);
    }
}
