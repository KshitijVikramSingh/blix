namespace Blix;

// Overlays a clip's motion *additively* on top of whatever pose Target currently
// holds. The typical use case is layering procedural / supplementary motion on a
// base animation: a "wave hello" clip authored against a rest pose, applied as
// a delta on top of the character's Walk pose so the character can walk *and*
// wave at the same time.
//
// Math: see PoseDelta. Per bone, the additive operation is:
//   target.translation += weight * (clip.translation - rest.translation)
//   target.rotation     = target.rotation * slerp(identity, rest^-1 * clip, weight)
//   target.scale       *= lerp(1, clip.scale / rest.scale, weight)
//
// Lifecycle / ordering with AnimationHost:
//   - Add the base animation (ClipAnimation / BlendedClipAnimation) FIRST.
//     SkinnedGameObject.Update resets the pose to RestPose, then the base
//     animation samples its clip into the pose.
//   - Add this AdditiveClipAnimation SECOND. It reads the current target pose
//     (the base animation's output), layers the delta on top in place.
//
// Weight is mutable: game code drives it for fade-in/out lifecycles, the same
// way it drives BlendedClipAnimation's Weight. The clip itself loops or runs
// once depending on Loop; the additive contribution survives even when the base
// animation changes (since the base is read each frame and the delta only
// modifies it).
public sealed class AdditiveClipAnimation : IAnimation
{
    public AnimationClip Clip { get; init; } = null!;
    public Pose Target { get; init; } = null!;

    // Rest pose the clip was authored against. Required for the delta math --
    // the clip's per-bone values are interpreted as "offsets from this rest."
    // Almost always the SkinnedGameObject.RestPose.
    public Pose RestPose { get; init; } = null!;

    public double StartTime { get; init; } = 0.0;
    public bool Loop { get; init; } = true;

    // Overlay strength in [0, 1]. 0 = invisible (no effect); 1 = fully applied.
    // Game code drives this for fade-in / fade-out behaviour.
    public float Weight { get; set; } = 1.0f;

    private Pose? clipScratch;

    public bool Sample(Time time)
    {
        ArgumentNullException.ThrowIfNull(Clip);
        ArgumentNullException.ThrowIfNull(Target);
        ArgumentNullException.ThrowIfNull(RestPose);

        clipScratch ??= new Pose(Target.BoneCount);

        var elapsed = time.Total - StartTime;
        if (elapsed < 0.0)
        {
            // Pre-start; leave target untouched.
            return true;
        }

        var sampleTime = Loop && Clip.Duration > 0.0 ? elapsed % Clip.Duration : elapsed;

        // Sample clip into scratch starting from rest -- partial-write clip
        // semantics overlay onto a known base, so untouched bones equal rest
        // and produce zero delta.
        clipScratch.CopyFrom(RestPose);
        Clip.Sample(sampleTime, clipScratch);

        var weight = Math.Clamp(Weight, 0.0f, 1.0f);
        for (var i = 0; i < Target.BoneCount; i++)
        {
            Target.Locals[i] = PoseDelta.LayerOnto(
                Target.Locals[i],
                RestPose.Locals[i],
                clipScratch.Locals[i],
                weight);
        }
        return true;
    }
}
