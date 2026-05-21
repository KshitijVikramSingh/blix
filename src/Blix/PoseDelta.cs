using System.Numerics;

namespace Blix;

// Delta-pose math for additive layering. Given a clip's animated pose (relative
// to a rest pose), compute the per-bone "delta" -- the offset that needs to be
// applied on top of any other base pose to layer the clip's motion additively.
//
// Translation delta is a vector difference (current - rest).
// Rotation delta is the rotation from rest to current: inverse(rest) * current.
// Scale delta is the componentwise ratio (current / rest).
//
// Applying a delta on top of a base pose:
//   target.translation += delta.translation (lerped to zero by weight)
//   target.rotation     = base * slerp(identity, delta.rotation, weight)
//   target.scale       *= lerp(one, delta.scale, weight)
//
// The "weight" parameter scales the delta from identity (no effect, weight=0) to
// full application (weight=1), letting AdditiveClipAnimation fade overlays in
// and out.
//
// Convention: bone transforms are in glTF local space (the bone's parent's
// frame), so the rotation delta is also in that local frame. Composes correctly
// when the base pose is something different from the rest pose (e.g., a Walk
// animation's pose with a "wave hello" overlay applied to the arm bone).
public static class PoseDelta
{
    // Layer one clip-sampled bone on top of an existing target bone, relative to
    // the rest-pose bone the clip was authored against. weight scales the delta
    // toward identity (weight=0 leaves target unchanged; weight=1 applies fully).
    //
    // Returns the new target value; assign back into target.Locals[i] to write.
    public static BoneTransform LayerOnto(BoneTransform target, BoneTransform rest, BoneTransform clip, float weight)
    {
        weight = Math.Clamp(weight, 0.0f, 1.0f);

        // Translation: linear offset from rest, scaled by weight.
        var deltaTranslation = (clip.Translation - rest.Translation) * weight;

        // Rotation: rotation from rest to clip, slerped from identity by weight.
        // Composed onto target (post-multiply: applied in target's local frame).
        var fullDeltaRotation = Quaternion.Inverse(rest.Rotation) * clip.Rotation;
        var deltaRotation = Quaternion.Slerp(Quaternion.Identity, fullDeltaRotation, weight);

        // Scale: componentwise ratio, lerped from (1, 1, 1) by weight. SafeDivide
        // guards against rest scale being zero on any component (rare; produces 1
        // for the ratio so the delta has no effect on that axis).
        var deltaScaleRatio = new Vector3(
            rest.Scale.X != 0.0f ? clip.Scale.X / rest.Scale.X : 1.0f,
            rest.Scale.Y != 0.0f ? clip.Scale.Y / rest.Scale.Y : 1.0f,
            rest.Scale.Z != 0.0f ? clip.Scale.Z / rest.Scale.Z : 1.0f);
        var deltaScale = Vector3.Lerp(Vector3.One, deltaScaleRatio, weight);

        return new BoneTransform(
            target.Translation + deltaTranslation,
            target.Rotation * deltaRotation,
            target.Scale * deltaScale);
    }
}
