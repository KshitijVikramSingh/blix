using System.Numerics;

namespace Blix;

// Per-bone pose interpolation helpers. The math BlendedClipAnimation (and any
// future blend/crossfade/additive primitive) builds on.
//
// Translation and scale use linear lerp; rotation uses spherical-linear (slerp)
// on the unit-quaternion arc, the same justification SlerpQuaternionCurve gave
// for animation tracks -- componentwise lerp on quaternions produces non-unit
// intermediates and wrong angular rates.
public static class PoseBlend
{
    // Blend two poses into `outPose` at the given weight. weight = 0 produces
    // exactly poseA; weight = 1 produces exactly poseB; values in between mix.
    // All three poses must share BoneCount.
    public static void Lerp(Pose poseA, Pose poseB, float weight, Pose outPose)
    {
        ArgumentNullException.ThrowIfNull(poseA);
        ArgumentNullException.ThrowIfNull(poseB);
        ArgumentNullException.ThrowIfNull(outPose);
        if (poseA.BoneCount != poseB.BoneCount || poseA.BoneCount != outPose.BoneCount)
        {
            throw new ArgumentException(
                $"Pose bone counts mismatch: a={poseA.BoneCount}, b={poseB.BoneCount}, out={outPose.BoneCount}.");
        }

        for (var i = 0; i < outPose.BoneCount; i++)
        {
            outPose.Locals[i] = LerpBone(poseA.Locals[i], poseB.Locals[i], weight);
        }
    }

    // Per-bone interpolation. Public so callers can compose their own pose-math
    // without going through the full-Pose path (e.g., additive layering on a
    // single bone, IK targeting a specific joint).
    public static BoneTransform LerpBone(BoneTransform a, BoneTransform b, float weight)
    {
        return new BoneTransform(
            Vector3.Lerp(a.Translation, b.Translation, weight),
            Quaternion.Slerp(a.Rotation, b.Rotation, weight),
            Vector3.Lerp(a.Scale, b.Scale, weight));
    }
}
