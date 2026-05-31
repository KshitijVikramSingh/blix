using System.Numerics;

namespace Blix.Geometry;

// View-frustum extracted from a view-projection matrix via Gribb-Hartmann.
// Six planes (left/right/bottom/top/near/far) stored as Vector4 where xyz
// is the plane normal and w is the signed distance to origin -- a point P
// is on the positive (inside-frustum) side when `n.x*P.x + n.y*P.y +
// n.z*P.z + n.w >= 0`. Used by the renderer to skip drawing submeshes
// whose AABB is fully outside the camera or cascade frustum.
//
// Plane derivation (Vulkan/D3D clip convention: z in [0, 1], which the Blix
// projection produces). Callers pass the TRANSPOSE of the row-vector clip
// matrix so the planes fall out of its rows (Mij = row i, col j):
//   clip.x = row1(vp) . world
//   clip.w = row4(vp) . world
// And each plane is a sum/difference of rows:
//   left   = row4 + row1   (clip.w + clip.x >= 0)
//   right  = row4 - row1   (clip.w - clip.x >= 0)
//   bottom = row4 + row2
//   top    = row4 - row2
//   near   = row3          (clip.z >= 0 for [0,1] depth; GL [-1,1] would be row4 + row3)
//   far    = row4 - row3
//
// Row k components in Blix naming: (Mk1, Mk2, Mk3, Mk4).
public readonly struct Frustum
{
    private readonly Vector4 left, right, bottom, top, near, far;

    private Frustum(
        Vector4 left, Vector4 right,
        Vector4 bottom, Vector4 top,
        Vector4 near, Vector4 far)
    {
        this.left = left;
        this.right = right;
        this.bottom = bottom;
        this.top = top;
        this.near = near;
        this.far = far;
    }

    public static Frustum FromViewProjection(Matrix4x4 vp)
    {
        var leftP   = new Vector4(vp.M41 + vp.M11, vp.M42 + vp.M12, vp.M43 + vp.M13, vp.M44 + vp.M14);
        var rightP  = new Vector4(vp.M41 - vp.M11, vp.M42 - vp.M12, vp.M43 - vp.M13, vp.M44 - vp.M14);
        var bottomP = new Vector4(vp.M41 + vp.M21, vp.M42 + vp.M22, vp.M43 + vp.M23, vp.M44 + vp.M24);
        var topP    = new Vector4(vp.M41 - vp.M21, vp.M42 - vp.M22, vp.M43 - vp.M23, vp.M44 - vp.M24);
        var nearP   = new Vector4(vp.M31, vp.M32, vp.M33, vp.M34);
        var farP    = new Vector4(vp.M41 - vp.M31, vp.M42 - vp.M32, vp.M43 - vp.M33, vp.M44 - vp.M34);
        return new Frustum(
            NormalizePlane(leftP),
            NormalizePlane(rightP),
            NormalizePlane(bottomP),
            NormalizePlane(topP),
            NormalizePlane(nearP),
            NormalizePlane(farP));
    }

    // Conservative AABB-vs-frustum test. Picks the AABB corner that
    // maximises signed distance from each plane (the "p-vertex"); if
    // that corner is still behind any plane, the whole AABB is outside
    // the frustum. False positives are possible (an AABB that straddles
    // a corner of the frustum is reported as intersecting) but never
    // false negatives -- safe to use for culling.
    public bool Intersects(Bounds3 b, float margin = 0.0f)
    {
        return InsidePlane(left,   b, margin)
            && InsidePlane(right,  b, margin)
            && InsidePlane(bottom, b, margin)
            && InsidePlane(top,    b, margin)
            && InsidePlane(near,   b, margin)
            && InsidePlane(far,    b, margin);
    }

    // `margin` shifts the plane outward by that many world units, expanding
    // the effective frustum volume. Used to gracefully handle AABBs near
    // grazing-plane intersection where floating-point error or under-tight
    // bounds can spuriously reject visible geometry; pass 0 for the
    // canonical conservative test.
    private static bool InsidePlane(Vector4 plane, Bounds3 b, float margin)
    {
        var px = plane.X > 0 ? b.Max.X : b.Min.X;
        var py = plane.Y > 0 ? b.Max.Y : b.Min.Y;
        var pz = plane.Z > 0 ? b.Max.Z : b.Min.Z;
        return plane.X * px + plane.Y * py + plane.Z * pz + plane.W + margin >= 0.0f;
    }

    private static Vector4 NormalizePlane(Vector4 plane)
    {
        var len = MathF.Sqrt(plane.X * plane.X + plane.Y * plane.Y + plane.Z * plane.Z);
        return len > 1e-6f ? plane / len : plane;
    }
}
