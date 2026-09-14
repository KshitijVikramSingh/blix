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
    /// <summary>
    /// Blend two poses through a mask: bone <c>i</c> mixes at <c>weight × mask[i]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>An unmasked bone is not blended at zero — it is not blended at all</b>, and the honest
    /// reason is COST rather than precision. A mask covering a quarter of a rig does a quarter of the
    /// work, which is what makes running several layers affordable.
    /// </para>
    /// <para>
    /// <b>The precision argument was checked and is not true today.</b> The first version of this
    /// claimed a slerp at weight 0 perturbs a bone's low bits, so skipping it was what made "the legs
    /// are untouched" exact rather than approximate. Deleting the shortcut turns no test red — not
    /// with identity rotations and not with a pair 150° apart, which puts <c>Quaternion.Slerp</c> on
    /// its trigonometric branch. Both it and <c>Vector3.Lerp</c> return their input exactly at 0 and
    /// at 1. The guarantee is real; this is not currently what provides it.
    /// </para>
    /// <para>
    /// It is still worth having, because it makes the guarantee independent of what
    /// <see cref="LerpBone"/> does later — but that is a different and much weaker claim than the one
    /// this comment used to make.
    /// </para>
    /// </remarks>
    public static void Lerp(Pose poseA, Pose poseB, float weight, BoneMask mask, Pose outPose)
    {
        ArgumentNullException.ThrowIfNull(poseA);
        ArgumentNullException.ThrowIfNull(poseB);
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(outPose);

        if (poseA.BoneCount != poseB.BoneCount || poseA.BoneCount != outPose.BoneCount)
        {
            throw new ArgumentException(
                $"Pose bone counts differ: {poseA.BoneCount}, {poseB.BoneCount}, {outPose.BoneCount}.");
        }

        if (mask.BoneCount != poseA.BoneCount)
        {
            throw new ArgumentException(
                $"The mask covers {mask.BoneCount} bones and these poses have {poseA.BoneCount}. " +
                "A mask belongs to the skeleton it was built from.",
                nameof(mask));
        }

        for (var i = 0; i < poseA.BoneCount; i++)
        {
            var w = weight * mask[i];

            if (w <= 0f) { outPose.Locals[i] = poseA.Locals[i]; continue; }
            if (w >= 1f) { outPose.Locals[i] = poseB.Locals[i]; continue; }

            outPose.Locals[i] = LerpBone(poseA.Locals[i], poseB.Locals[i], w);
        }
    }

    public static BoneTransform LerpBone(BoneTransform a, BoneTransform b, float weight)
    {
        return new BoneTransform(
            Vector3.Lerp(a.Translation, b.Translation, weight),
            Quaternion.Slerp(a.Rotation, b.Rotation, weight),
            Vector3.Lerp(a.Scale, b.Scale, weight));
    }
}
