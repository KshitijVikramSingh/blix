using System.Numerics;

namespace Blix.Graphics;

public static class GraphicsMatrices
{
    public static Matrix4x4 CreateModel(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        // Column-vector composition: T * R * S applied to a local-space vertex
        // performs scale first, then rotation, then translation - the standard
        // model-matrix interpretation. Swapping the order silently breaks any
        // model that combines non-unit scale with non-zero translation.
        return
            CreateTranslation(position) *
            CreateRotation(rotation) *
            CreateScale(scale);
    }

    public static Matrix4x4 CreateModelCentered(
        Vector3 position,
        Quaternion rotation,
        Vector3 scale,
        Vector3 modelCenter)
    {
        // Same as CreateModel with an extra pre-translation of -modelCenter applied
        // in source space first. After the chain runs, the source mesh's geometric
        // center lands exactly at `position` regardless of where the asset was
        // authored relative to its origin. Use this for OBJ imports where the source
        // wasn't centered (e.g. Suzanne loaded with bounds center at world ~(-2.5, 1.25, 4.1)).
        return
            CreateTranslation(position) *
            CreateRotation(rotation) *
            CreateScale(scale) *
            CreateTranslation(-modelCenter);
    }

    public static Matrix4x4 CreateNormalMatrix(Matrix4x4 model)
    {
        // Normals transform by the inverse-transpose of the model matrix; this is
        // invariant to non-uniform scale, where mat3(model) would skew normals.
        // Returns the full 4x4 — shaders extract the relevant 3x3 via mat3(...).
        if (!Matrix4x4.Invert(model, out var inverse))
        {
            return Matrix4x4.Identity;
        }

        return Matrix4x4.Transpose(inverse);
    }

    public static Matrix4x4 CreateView(Vector3 position, Quaternion rotation)
    {
        var inverseRotation = Quaternion.Inverse(rotation);
        var inversePosition = -position;

        return
            CreateRotation(inverseRotation) *
            CreateTranslation(inversePosition);
    }

    public static Matrix4x4 CreateLookAt(Vector3 eye, Vector3 target, Vector3 up)
    {
        var forward = Vector3.Normalize(target - eye);
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        var actualUp = Vector3.Cross(right, forward);

        return new Matrix4x4(
            right.X, right.Y, right.Z, -Vector3.Dot(right, eye),
            actualUp.X, actualUp.Y, actualUp.Z, -Vector3.Dot(actualUp, eye),
            -forward.X, -forward.Y, -forward.Z, Vector3.Dot(forward, eye),
            0.0f, 0.0f, 0.0f, 1.0f);
    }

    // Off-centre orthographic projection. Useful for screen-space UI overlays where
    // the natural coord system has origin at the top-left of the framebuffer with
    // Y growing down — call with (left=0, right=width, bottom=height, top=0, near=-1, far=1)
    // to map (0,0) onto the top-left of NDC and (width, height) onto the bottom-right.
    public static Matrix4x4 CreateOrthographicOffCenter(float left, float right, float bottom, float top, float nearPlane, float farPlane)
    {
        if (left == right)
        {
            throw new ArgumentException("Orthographic left and right must differ.", nameof(left));
        }

        if (bottom == top)
        {
            throw new ArgumentException("Orthographic bottom and top must differ.", nameof(bottom));
        }

        if (farPlane <= nearPlane)
        {
            throw new ArgumentOutOfRangeException(nameof(farPlane), "Far plane must be greater than the near plane.");
        }

        var rl = right - left;
        var tb = top - bottom;
        var fn = farPlane - nearPlane;

        return new Matrix4x4(
            2.0f / rl, 0.0f, 0.0f, -(right + left) / rl,
            0.0f, 2.0f / tb, 0.0f, -(top + bottom) / tb,
            0.0f, 0.0f, -2.0f / fn, -(farPlane + nearPlane) / fn,
            0.0f, 0.0f, 0.0f, 1.0f);
    }

    public static Matrix4x4 CreateOrthographic(float width, float height, float nearPlane, float farPlane)
    {
        if (width <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Orthographic width must be greater than zero.");
        }

        if (height <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Orthographic height must be greater than zero.");
        }

        if (farPlane <= nearPlane)
        {
            throw new ArgumentOutOfRangeException(nameof(farPlane), "Far plane must be greater than the near plane.");
        }

        var depth = farPlane - nearPlane;

        return new Matrix4x4(
            2.0f / width, 0.0f, 0.0f, 0.0f,
            0.0f, 2.0f / height, 0.0f, 0.0f,
            0.0f, 0.0f, -2.0f / depth, -(farPlane + nearPlane) / depth,
            0.0f, 0.0f, 0.0f, 1.0f);
    }

    public static Matrix4x4 CreatePerspective(
        float verticalFieldOfView,
        float aspectRatio,
        float nearPlane,
        float farPlane)
    {
        if (verticalFieldOfView <= 0.0f || verticalFieldOfView >= MathF.PI)
        {
            throw new ArgumentOutOfRangeException(nameof(verticalFieldOfView), "Field of view must be between 0 and PI radians.");
        }

        if (aspectRatio <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(aspectRatio), "Aspect ratio must be greater than zero.");
        }

        if (nearPlane <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(nearPlane), "Near plane must be greater than zero.");
        }

        if (farPlane <= nearPlane)
        {
            throw new ArgumentOutOfRangeException(nameof(farPlane), "Far plane must be greater than the near plane.");
        }

        var focalLength = 1.0f / MathF.Tan(verticalFieldOfView * 0.5f);
        var depth = nearPlane - farPlane;

        return new Matrix4x4(
            focalLength / aspectRatio, 0.0f, 0.0f, 0.0f,
            0.0f, focalLength, 0.0f, 0.0f,
            0.0f, 0.0f, (farPlane + nearPlane) / depth, (2.0f * farPlane * nearPlane) / depth,
            0.0f, 0.0f, -1.0f, 0.0f);
    }

    private static Matrix4x4 CreateTranslation(Vector3 position)
    {
        return new Matrix4x4(
            1.0f, 0.0f, 0.0f, position.X,
            0.0f, 1.0f, 0.0f, position.Y,
            0.0f, 0.0f, 1.0f, position.Z,
            0.0f, 0.0f, 0.0f, 1.0f);
    }

    private static Matrix4x4 CreateScale(Vector3 scale)
    {
        return new Matrix4x4(
            scale.X, 0.0f, 0.0f, 0.0f,
            0.0f, scale.Y, 0.0f, 0.0f,
            0.0f, 0.0f, scale.Z, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f);
    }

    // Apply this column-vector matrix to a point (w = 1). The engine builds matrices
    // in column-vector form (translation in M14/M24/M34) but System.Numerics
    // Vector3.Transform treats them as row-vector — it ignores the M14/M24/M34
    // translation column entirely. This helper does the math manually so callers can
    // transform points by engine matrices without that footgun.
    public static Vector3 TransformPoint(Matrix4x4 m, Vector3 p)
    {
        var w = m.M41 * p.X + m.M42 * p.Y + m.M43 * p.Z + m.M44;
        if (w == 0.0f) w = 1.0f;   // affine xforms always have w = 1; defensive
        return new Vector3(
            (m.M11 * p.X + m.M12 * p.Y + m.M13 * p.Z + m.M14) / w,
            (m.M21 * p.X + m.M22 * p.Y + m.M23 * p.Z + m.M24) / w,
            (m.M31 * p.X + m.M32 * p.Y + m.M33 * p.Z + m.M34) / w);
    }

    // Apply this column-vector matrix to a direction (w = 0) — translation rows are
    // ignored; only rotation/scale carry. Use for normals (after compensating for
    // non-uniform scale via the inverse-transpose normal matrix) or any vector that
    // shouldn't be translated.
    public static Vector3 TransformDirection(Matrix4x4 m, Vector3 d)
    {
        return new Vector3(
            m.M11 * d.X + m.M12 * d.Y + m.M13 * d.Z,
            m.M21 * d.X + m.M22 * d.Y + m.M23 * d.Z,
            m.M31 * d.X + m.M32 * d.Y + m.M33 * d.Z);
    }

    private static Matrix4x4 CreateRotation(Quaternion rotation)
    {
        rotation = Quaternion.Normalize(rotation);

        var xx = rotation.X * rotation.X;
        var yy = rotation.Y * rotation.Y;
        var zz = rotation.Z * rotation.Z;
        var xy = rotation.X * rotation.Y;
        var xz = rotation.X * rotation.Z;
        var yz = rotation.Y * rotation.Z;
        var wx = rotation.W * rotation.X;
        var wy = rotation.W * rotation.Y;
        var wz = rotation.W * rotation.Z;

        return new Matrix4x4(
            1.0f - 2.0f * (yy + zz), 2.0f * (xy - wz), 2.0f * (xz + wy), 0.0f,
            2.0f * (xy + wz), 1.0f - 2.0f * (xx + zz), 2.0f * (yz - wx), 0.0f,
            2.0f * (xz - wy), 2.0f * (yz + wx), 1.0f - 2.0f * (xx + yy), 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f);
    }
}
