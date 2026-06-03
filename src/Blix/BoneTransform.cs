using System.Numerics;
using Blix.Graphics;

namespace Blix;

// Per-bone local-space transform — the (Translation, Rotation, Scale) form animation
// clips produce and the skeleton's hierarchy walk consumes. Struct-valued so a Pose's
// array-of-N-bone-locals is N×stride contiguous bytes with zero heap allocation per
// entry — Transform3D is a class, which would be one heap object per bone.
//
// Mirrors SharpGLTF.Transforms.AffineTransform and the glTF 2.0 spec's TRS form.
// Uses `Translation` (rather than the `Position` we use on Transform3D) to mark the
// bone-local domain — when reading code, `Translation` says "this is a bone-local
// transform inside a skeletal pose" without needing the surrounding context.
public readonly record struct BoneTransform(
    Vector3 Translation,
    Quaternion Rotation,
    Vector3 Scale)
{
    public static BoneTransform Identity { get; } =
        new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    // Build the 4x4 model matrix for this local transform under the engine's
    // row-vector convention (F-016): Scale * Rotation * Translation, applied to a
    // local vertex left-to-right as scale-first, then rotate, then translate.
    // Same composition GraphicsMatrices.CreateModel / Transform3D use.
    public Matrix4x4 ToMatrix() =>
        GraphicsMatrices.CreateModel(Translation, Rotation, Scale);

    // Decompose an affine matrix (in the engine's row-vector convention) into TRS
    // form. Used by Skeleton.CreateRestPose when reconstructing local rest
    // transforms from inverse-bind matrices.
    //
    // F-016: engine matrices are System.Numerics row-vector form (translation in
    // M41/M42/M43), matching System.Numerics's Matrix4x4.Decompose. No transpose
    // needed — pass through directly.
    //
    // Degenerate inputs that Decompose can't handle (zero scale, non-affine,
    // perspective row) fall back to Identity rather than throwing — bad input
    // shouldn't crash construction.
    public static BoneTransform FromMatrix(Matrix4x4 m)
    {
        return Matrix4x4.Decompose(m, out var scale, out var rotation, out var translation)
            ? new BoneTransform(translation, rotation, scale)
            : Identity;
    }
}
