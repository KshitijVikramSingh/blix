using System.Numerics;

namespace Blix.Graphics;

// Engine convention: .NET row-vector form throughout (F-016). Matrices use
// System.Numerics's native layout, where translation lives in M41/M42/M43,
// Vector4.Transform applies as v_row * M, and `M = A * B * C` composed
// left-to-right applies A first, B second, C third.
//
// Backends upload .NET row-major bytes directly to UBO/uniform storage.
// GLSL's std140 column-major reading of those bytes produces the TRANSPOSE
// (column-vector form) in shader-space — that's the correct form for
// GLSL's `M * v_col` multiplication. Net effect: same transformation,
// no manual transposes anywhere.
public static class GraphicsMatrices
{
    public static Matrix4x4 CreateModel(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        // Row-vector composition: v_row * (S * R * T) applies S first, then R,
        // then T — standard model-matrix interpretation. Reversed from the
        // column-vector convention's `T * R * S` because row-vector
        // multiplication applies the LEFTMOST factor first.
        return
            Matrix4x4.CreateScale(scale) *
            Matrix4x4.CreateFromQuaternion(rotation) *
            Matrix4x4.CreateTranslation(position);
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
        //
        // Row-vector convention: leftmost factor applied first. Order:
        // center-shift, then scale, then rotate, then translate to world.
        return
            Matrix4x4.CreateTranslation(-modelCenter) *
            Matrix4x4.CreateScale(scale) *
            Matrix4x4.CreateFromQuaternion(rotation) *
            Matrix4x4.CreateTranslation(position);
    }

    public static Matrix4x4 CreateNormalMatrix(Matrix4x4 model)
    {
        // Normals transform by the inverse-transpose of the model matrix; this is
        // invariant to non-uniform scale, where mat3(model) would skew normals.
        // Returns the full 4x4 — shaders extract the relevant 3x3 via mat3(...).
        //
        // Row-vector convention (F-016): the .NET model matrix is the row-vector
        // form. After upload (direct memcpy + GLSL column-major reading), GLSL
        // sees the TRANSPOSE = column-vector form. So GLSL's column-vector
        // M_glsl = M_dotnet^T. For the normal transform GLSL needs M_glsl^-T
        // = (M_dotnet^T)^-T = M_dotnet^-1. So we just invert — no explicit
        // Transpose needed in .NET space; the upload-time reinterpretation
        // provides it.
        if (!Matrix4x4.Invert(model, out var inverse))
        {
            return Matrix4x4.Identity;
        }
        return inverse;
    }

    public static Matrix4x4 CreateView(Vector3 position, Quaternion rotation)
    {
        // Row-vector view: v_row * T * R = first translate (so camera at origin),
        // then rotate to canonical orientation. Reversed from column-vector
        // form's `R * T` composition.
        var inverseRotation = Quaternion.Inverse(rotation);
        var inversePosition = -position;
        return
            Matrix4x4.CreateTranslation(inversePosition) *
            Matrix4x4.CreateFromQuaternion(inverseRotation);
    }

    public static Matrix4x4 CreateLookAt(Vector3 eye, Vector3 target, Vector3 up)
    {
        // Equivalent to System.Numerics.Matrix4x4.CreateLookAt — produces a
        // row-vector view matrix where v_row * view = camera-local position.
        // System.Numerics uses (cameraPosition - cameraTarget) as the z-axis
        // (camera-backward) which gives standard right-handed view space:
        // camera looks down -Z in view space; origin in front of camera has
        // negative view-z.
        return Matrix4x4.CreateLookAt(eye, target, up);
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

        // Row-vector orthographic: translation lives in last row (M41-M43).
        // Equivalent to transposing the column-vector form (translation in
        // last column).
        return new Matrix4x4(
            2.0f / rl,                0.0f,                     0.0f,                            0.0f,
            0.0f,                     2.0f / tb,                0.0f,                            0.0f,
            0.0f,                     0.0f,                     -2.0f / fn,                      0.0f,
            -(right + left) / rl,     -(top + bottom) / tb,     -(farPlane + nearPlane) / fn,    1.0f);
    }

    // Vulkan-NDC off-center orthographic for screen-space 2D (SpriteBatch). vs
    // the GL CreateOrthographicOffCenter: +Y points DOWN in clip space (top of
    // screen -> NDC -1) and depth maps to [0, 1] instead of [-1, 1]. Called with
    // (0, width, height, 0, ...) it reproduces the ImGui scale/translate mapping
    // (x*2/w - 1, y*2/h - 1) that renders right-side-up on this backend.
    // Row-vector form (translation in last row), written raw to a push constant
    // / UBO; GLSL reads it column-major so `mat * v_col` == `v_row * mat`.
    public static Matrix4x4 CreateOrthographicOffCenterVulkan(float left, float right, float bottom, float top, float nearPlane, float farPlane)
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
        var bt = bottom - top;   // Y-down: divide by (bottom - top), not (top - bottom)
        var fn = farPlane - nearPlane;

        return new Matrix4x4(
            2.0f / rl,                0.0f,                     0.0f,               0.0f,
            0.0f,                     2.0f / bt,                0.0f,               0.0f,
            0.0f,                     0.0f,                     1.0f / fn,          0.0f,
            -(right + left) / rl,     -(top + bottom) / bt,     -nearPlane / fn,    1.0f);
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

        // Row-vector orthographic centered at origin: z-translation lives in last row (M43).
        return new Matrix4x4(
            2.0f / width, 0.0f,          0.0f,                          0.0f,
            0.0f,         2.0f / height, 0.0f,                          0.0f,
            0.0f,         0.0f,          -2.0f / depth,                 0.0f,
            0.0f,         0.0f,          -(farPlane + nearPlane) / depth, 1.0f);
    }

    // Symmetric Vulkan-NDC orthographic centred on the origin: x/y map
    // [-width/2, width/2] × [-height/2, height/2] onto [-1, 1] with +Y DOWN
    // (top of the world slab → NDC -1, matching CreatePerspectiveVulkan's
    // Y-flip), and view-space depth maps to [0, 1] (near → 0, far → 1) rather
    // than GL's [-1, 1]. This is the shadow-cascade / spot-light projection:
    // pair it with CreateLookAt to build a light's view-projection. Row-vector
    // form (z-translation in M43); written raw to a UBO and read column-major
    // by GLSL so `mat * v_col` == `v_row * mat`.
    public static Matrix4x4 CreateOrthographicVulkan(float width, float height, float nearPlane, float farPlane)
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

        var fn = farPlane - nearPlane;

        return new Matrix4x4(
            2.0f / width, 0.0f,           0.0f,                0.0f,
            0.0f,        -2.0f / height,  0.0f,                0.0f,
            0.0f,         0.0f,          -1.0f / fn,           0.0f,
            0.0f,         0.0f,          -nearPlane / fn,      1.0f);
    }

    // Vulkan-NDC perspective: +Y points DOWN in clip space (the projection
    // flips Y so screen Y matches framebuffer Y-down convention) and depth
    // maps to [0, 1] (the Vulkan/D3D convention, not OpenGL's [-1, 1]). Built
    // directly in the form the Vulkan backend's UBO-write path expects —
    // System.Numerics row-major bytes, no transpose at upload time. Math
    // (row, col) lives at M[col+1, row+1] in .NET field naming.
    //
    // Returns the camera's view-space → Vulkan-clip transform applied as
    // `clip_row = view_row * proj` in .NET (row-vector convention).
    public static Matrix4x4 CreatePerspectiveVulkan(
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
            focalLength / aspectRatio, 0.0f, 0.0f,                       0.0f,
            0.0f,                     -focalLength, 0.0f,                0.0f,
            0.0f,                      0.0f,        farPlane / depth,   -1.0f,
            0.0f,                      0.0f,        (nearPlane * farPlane) / depth, 0.0f);
    }

    // View-projection for a directional (sun) shadow map. The light eye sits up the
    // `sunDirToward` direction (the direction TOWARD the sun) at `distance`, looks at the
    // origin, and a square Vulkan-NDC orthographic of side `orthoExtent` covers the scene
    // footprint. `orthoExtent`/`distance`/planes are scene tuning the caller supplies; the
    // construction (eye placement, up-vector degeneracy guard, ortho) is the shared
    // technique — pair it with the GLSL blix_sun_shadow() lookup (Blix.Shaders/shadow.glsl).
    public static Matrix4x4 SunShadowViewProjection(
        Vector3 sunDirToward, float distance, float orthoExtent, float nearPlane, float farPlane) =>
        SunShadowViewProjection(sunDirToward, Vector3.Zero, distance, orthoExtent, nearPlane, farPlane);

    // As above, but the light looks at `center` rather than the origin. A scene larger than
    // one crisp shadow map can cover has to spend its texels where the camera is looking:
    // a 2048 map over a 600 m world is 0.3 m per texel and every shadow edge is a staircase,
    // while the same map over a 180 m box around the camera focus is 11 texels per metre.
    // The caller is then responsible for moving `center` with the view.
    public static Matrix4x4 SunShadowViewProjection(
        Vector3 sunDirToward,
        Vector3 center,
        float distance,
        float orthoExtent,
        float nearPlane,
        float farPlane)
    {
        var dir = Vector3.Normalize(sunDirToward);
        var eye = center + dir * distance;
        var up = MathF.Abs(dir.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var view = CreateLookAt(eye, center, up);
        var ortho = CreateOrthographicVulkan(orthoExtent, orthoExtent, nearPlane, farPlane);
        return view * ortho;
    }

    /// <summary>
    /// The eight world-space corners of one slice of a perspective view frustum.
    /// </summary>
    /// <remarks>
    /// Near four first, then far four, each as (−right −up), (+right −up), (−right +up), (+right +up).
    /// Split out from the fit below because a caller that already has its corners — a clipped frustum,
    /// a band of ground the camera can actually see — should be able to fit a cascade to those rather
    /// than to the ones this would compute.
    /// </remarks>
    public static void FrustumSliceCorners(
        Vector3 eye, Vector3 forward, Vector3 right, Vector3 up,
        float verticalFieldOfView, float aspect, float near, float far, Span<Vector3> corners)
    {
        if (corners.Length < 8) throw new ArgumentException("needs room for eight corners", nameof(corners));

        var tall = MathF.Tan(verticalFieldOfView * 0.5f);
        var wide = tall * aspect;
        var at = 0;
        for (var slice = 0; slice < 2; slice++)
        {
            var away = slice == 0 ? near : far;
            var middle = eye + forward * away;
            var h = away * tall;
            var w = away * wide;
            corners[at++] = middle - right * w - up * h;
            corners[at++] = middle + right * w - up * h;
            corners[at++] = middle - right * w + up * h;
            corners[at++] = middle + right * w + up * h;
        }
    }

    /// <summary>
    /// Fits one shadow cascade's view-projection around a slice of the camera's frustum.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A bounding SPHERE, not a box.</b> A box fitted to the corners changes size as the camera
    /// turns, and a shadow map whose world extent changes every frame crawls. The sphere through the
    /// same corners is rotation-invariant, so the box derived from it is too — it costs some texels
    /// and buys a still image.
    /// </para>
    /// <para>
    /// <b>Snapped on the LIGHT's axes, which is the grid the texels are actually on.</b> Snapping the
    /// centre in world XZ — the obvious version — snaps to a grid the texels are not aligned with
    /// unless the sun happens to be axis-aligned, so the shimmer it is meant to stop only partly
    /// stops. Learned in the external RTSGame consumer, which still carries its own copy of this: converting it is a job
    /// of its own and not one to do while it is working.
    /// </para>
    /// </remarks>
    /// <param name="padding">
    /// Extra metres on the box side, for casters standing OUTSIDE the slice that still shadow into
    /// it. Zero is wrong in any scene with tall geometry: the fit only sees what the camera sees.
    /// </param>
    /// <param name="side">The box's width in metres, so a caller can report metres per texel.</param>
    public static Matrix4x4 FitCascadeViewProjection(
        Vector3 sunDirToward, ReadOnlySpan<Vector3> corners, int mapSize,
        float padding, float sunDistance, out float side, out Vector3 center)
    {
        if (corners.Length < 8) throw new ArgumentException("needs eight corners", nameof(corners));
        if (mapSize <= 0) throw new ArgumentOutOfRangeException(nameof(mapSize));

        center = Vector3.Zero;
        for (var i = 0; i < 8; i++) center += corners[i];
        center /= 8f;

        var radius = 0f;
        for (var i = 0; i < 8; i++) radius = MathF.Max(radius, Vector3.Distance(corners[i], center));

        // Ceiling so a camera creeping forward does not resize the box by a fraction of a texel
        // every frame, which is the same crawl the snapping below exists to stop.
        radius = MathF.Ceiling(radius);
        side = radius * 2f + padding;

        var toLight = Vector3.Normalize(sunDirToward);
        var up = MathF.Abs(toLight.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
        var forward = -toLight;
        var axisRight = Vector3.Normalize(Vector3.Cross(up, forward));
        var axisUp = Vector3.Cross(forward, axisRight);

        var texel = side / mapSize;
        center =
            axisRight * (MathF.Round(Vector3.Dot(center, axisRight) / texel) * texel) +
            axisUp * (MathF.Round(Vector3.Dot(center, axisUp) / texel) * texel) +
            forward * Vector3.Dot(center, forward);

        var away = radius + sunDistance;
        return SunShadowViewProjection(sunDirToward, center, away, side, 0.05f, away * 2f + side);
    }

    /// <summary>
    /// Practical split scheme: the distances at which one cascade hands over to the next.
    /// </summary>
    /// <remarks>
    /// Blends the logarithmic distribution (which matches how perspective compresses distance, and
    /// starves the far cascades) with the uniform one (which wastes the near cascade on air).
    /// <paramref name="lambda"/> 0 is fully uniform, 1 fully logarithmic; ~0.7 is the usual answer.
    /// Writes one far bound per cascade, ending at <paramref name="far"/>.
    /// </remarks>
    public static void CascadeSplits(float near, float far, float lambda, Span<float> splits)
    {
        var n = splits.Length;
        if (n == 0) return;
        near = MathF.Max(near, 0.001f);
        lambda = Math.Clamp(lambda, 0f, 1f);

        for (var i = 0; i < n; i++)
        {
            var t = (i + 1) / (float)n;
            var log = near * MathF.Pow(far / near, t);
            var uniform = near + (far - near) * t;
            splits[i] = uniform + lambda * (log - uniform);
        }

        splits[n - 1] = far;
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

    // Apply a row-vector matrix to a point (w = 1). Equivalent to
    // System.Numerics.Vector4.Transform(new Vector4(p, 1), m) followed by a
    // perspective divide, but returns a Vector3 directly. Use this when the
    // matrix may project (perspective) — for affine xforms,
    // Vector4.Transform with w=1 is sufficient.
    public static Vector3 TransformPoint(Matrix4x4 m, Vector3 p)
    {
        // Row-vector convention: result_j = sum_i v_i * M[i, j].
        var x = p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41;
        var y = p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42;
        var z = p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43;
        var w = p.X * m.M14 + p.Y * m.M24 + p.Z * m.M34 + m.M44;
        if (w == 0.0f) w = 1.0f;   // affine xforms always have w = 1; defensive
        return new Vector3(x / w, y / w, z / w);
    }

    // Apply a row-vector matrix to a direction (w = 0). Translation columns
    // (M41-M43) are ignored — only the rotation/scale 3x3 contributes.
    // Use for normals (after compensating for non-uniform scale via the
    // inverse-transpose normal matrix from CreateNormalMatrix) or any
    // vector that shouldn't be translated.
    public static Vector3 TransformDirection(Matrix4x4 m, Vector3 d)
    {
        return new Vector3(
            d.X * m.M11 + d.Y * m.M21 + d.Z * m.M31,
            d.X * m.M12 + d.Y * m.M22 + d.Z * m.M32,
            d.X * m.M13 + d.Y * m.M23 + d.Z * m.M33);
    }
}
