namespace Blix;

// Blends two AnimationClips into a target Pose at a runtime-controllable Weight.
// IAnimation contract so it lives in the same AnimationHost machinery as
// ClipAnimation -- one entry in `host.AddAnimation(...)`.
//
// Per-frame math:
//   1. Sample ClipA at its current time into a private scratchA pose (reset to
//      RestPose first, so partial clips overlay on a known base).
//   2. Sample ClipB similarly into scratchB.
//   3. PoseBlend.Lerp(scratchA, scratchB, Weight, Target).
//
// Two scratch poses allocated lazily at first Sample. Reused frame-to-frame.
//
// Weight is mutable: game code drives it per frame for crossfade behaviour. The
// engine deliberately doesn't carry a built-in crossfade curve here -- a curve
// of any shape (linear, eased, manual scrub from a UI slider) is a one-liner
// in OnUpdate. CrossfadeClipAnimation could land as a sibling if a
// "self-completing crossfade" lifecycle proves useful.
//
// Loop semantics match ClipAnimation: each clip's local time wraps independently
// against its own Duration when Loop = true. This means clips of different
// lengths blend correctly without phase-locking -- a 1.2s Walk and a 0.8s Run
// each cycle at their authored speeds.
public sealed class BlendedClipAnimation : IAnimation
{
    public AnimationClip ClipA { get; init; } = null!;
    public AnimationClip ClipB { get; init; } = null!;
    public Pose Target { get; init; } = null!;

    // Base pose copied into scratchA / scratchB before each clip samples. Almost
    // always the rest pose for the skeleton driving Target -- SkinnedGameObject's
    // RestPose is the natural choice. Required because clip.Sample is
    // partial-write: bones not animated by the clip retain whatever scratchA /
    // scratchB held last frame unless reset.
    public Pose RestPose { get; init; } = null!;

    public double StartTime { get; init; } = 0.0;
    public bool Loop { get; init; } = true;

    // Blend weight in [0, 1]. 0 = pure ClipA, 1 = pure ClipB, 0.5 = even mix.
    // Mutable; game code drives it per frame for crossfading or manual scrubbing.
    public float Weight { get; set; } = 0.5f;

    private Pose? scratchA;
    private Pose? scratchB;

    public bool Sample(Time time)
    {
        ArgumentNullException.ThrowIfNull(ClipA);
        ArgumentNullException.ThrowIfNull(ClipB);
        ArgumentNullException.ThrowIfNull(Target);
        ArgumentNullException.ThrowIfNull(RestPose);

        // Lazy scratch allocation matches the BonePalette / Pose lifecycle on
        // SkinnedGameObject: one allocation at first Sample, reused forever.
        scratchA ??= new Pose(Target.BoneCount);
        scratchB ??= new Pose(Target.BoneCount);

        var elapsed = time.Total - StartTime;
        if (elapsed < 0.0)
        {
            // Animation hasn't started yet; leave Target untouched. Same shape
            // as ClipAnimation's pre-start handling.
            return true;
        }

        var sampleTimeA = Loop && ClipA.Duration > 0.0 ? elapsed % ClipA.Duration : elapsed;
        var sampleTimeB = Loop && ClipB.Duration > 0.0 ? elapsed % ClipB.Duration : elapsed;

        scratchA.CopyFrom(RestPose);
        ClipA.Sample(sampleTimeA, scratchA);

        scratchB.CopyFrom(RestPose);
        ClipB.Sample(sampleTimeB, scratchB);

        var w = Math.Clamp(Weight, 0.0f, 1.0f);
        PoseBlend.Lerp(scratchA, scratchB, w, Target);

        return true;
    }
}
