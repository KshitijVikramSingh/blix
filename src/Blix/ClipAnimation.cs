namespace Blix;

// Drives an AnimationClip against a target Pose as an IAnimation, so clips can be
// hosted in the same AnimationHost that handles every other animation type.
//
// Two lifecycle modes:
//   - Loop = true  (default): infinite — Sample wraps elapsed time mod Duration and
//     always returns true.
//   - Loop = false: finite — Sample returns false once elapsed >= Duration so the
//     host removes it; the final sample writes Duration (which IFiniteCurve clamps
//     to the last keyframe) so the pose holds the ending state.
//
// StartTime aligns the clip with absolute Time.Total — callers set it to the moment
// the clip should start (typically `time.Total` when adding the animation, possibly
// + a delay). Defaults to 0 so a clip added at startup begins from t=0.
//
// The target Pose is mutated in place. For partial clips (which only touch the
// channels their tracks define), the caller is responsible for resetting the pose
// to a base before all clip-driven animations sample — SkinnedGameObject does this
// in its Update by copying RestPose into Pose before delegating to AnimationHost.
public sealed class ClipAnimation : IAnimation
{
    public AnimationClip Clip { get; init; } = null!;
    public Pose Target { get; init; } = null!;
    public double StartTime { get; init; } = 0.0;
    public bool Loop { get; init; } = true;

    public bool Sample(Time time)
    {
        ArgumentNullException.ThrowIfNull(Clip);
        ArgumentNullException.ThrowIfNull(Target);

        var elapsed = time.Total - StartTime;
        if (elapsed < 0.0)
        {
            // Animation hasn't started yet — leave the target untouched and stay alive.
            return true;
        }

        if (Loop)
        {
            // Clip.Duration could be zero (degenerate single-keyframe clip); guard
            // against divide-by-zero. A zero-duration clip is effectively static.
            var sampleTime = Clip.Duration > 0.0 ? elapsed % Clip.Duration : 0.0;
            Clip.Sample(sampleTime, Target);
            return true;
        }

        if (elapsed >= Clip.Duration)
        {
            // Final sample at Duration — clamps to last keyframe via IFiniteCurve.
            Clip.Sample(Clip.Duration, Target);
            return false;
        }
        Clip.Sample(elapsed, Target);
        return true;
    }
}
