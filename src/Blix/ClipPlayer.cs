namespace Blix;

// A clip, a clock, and the reset that every consumer was performing by hand.
//
// ── The decision being named ────────────────────────────────────────────────
// Three places in this tree write the same three lines independently:
//
//     pose.CopyFrom(restPose);                 // Runner, Bulwark, RTSGame
//     clip.Sample(time % clip.Duration, pose);
//     skeleton.ComputeBonePalette(pose, palette);
//
// The reset is not tidiness. `AnimationClip.Sample` writes only the channels a clip has
// tracks for, so without it every untracked bone keeps the PREVIOUS frame's value — which
// on a partial clip is a character whose legs are one animation behind its arms, and on a
// full clip is invisible until the day someone authors a partial one. Three consumers, one
// non-obvious decision: that is the bar conventions §4 sets, and this clears it.
//
// The `% Duration` is the second half, and carries the zero-duration guard every consumer
// wrote separately (the Rogue ships seven single-key "pose" clips whose duration is exactly
// 0, so this is a live case rather than a defensive one).
//
// ── What is deliberately NOT here ───────────────────────────────────────────
// The palette. `Skeleton.ComputeBonePalette` is already a named engine call, and a player
// that owned the palette would make blending awkward for no gain — two players feeding
// `PoseBlend.Lerp` into a third pose is the obvious shape, and it is obvious precisely
// because the palette is built once, by whoever draws, from whichever pose won.
//
// Also not here: a state machine, a blend tree, an event track, or a scheduler. Each is a
// decision two consumers would disagree about. This owns a clip and a time.
public sealed class ClipPlayer
{
    /// <summary>The skeleton the rest pose came from — the bone count every pose must match.</summary>
    public Skeleton Skeleton { get; }

    /// <summary>The base every sample starts from. Never mutated by this type.</summary>
    public Pose RestPose { get; }

    /// <summary>The working pose, rewritten by every sample. Feed it to ComputeBonePalette.</summary>
    public Pose Pose { get; }

    /// <summary>The clip being played. Null holds the rest pose.</summary>
    public AnimationClip? Clip
    {
        get => clip;
        set
        {
            if (ReferenceEquals(clip, value)) return;
            clip = value;
            // A time past the new clip's end would clamp to its last keyframe and sit there,
            // which reads as "the clip is broken" rather than "the clip is shorter".
            Time = Wrap(Time, clip?.Duration ?? 0.0);
            Finished = false;
            RootDelta = RootMotion.None;
            Resample();
        }
    }

    /// <summary>Where in the clip the current pose was sampled, always within [0, Duration).</summary>
    public double Time { get; private set; }

    /// <summary>Playback speed multiplier. Negative runs the clip backwards; 0 holds.</summary>
    public float Rate { get; set; } = 1f;

    /// <summary>Wrap at the end (true) or stop on the final keyframe (false).</summary>
    public bool Loop { get; set; } = true;

    /// <summary>Held: <see cref="Advance"/> re-samples but does not move <see cref="Time"/>.</summary>
    public bool Paused { get; set; }

    /// <summary>Which bone's travel <see cref="RootDelta"/> reports. Defaults to the first parentless bone.</summary>
    public int RootBone { get; set; }

    /// <summary>
    /// How far the root bone travelled during the LAST <see cref="Advance"/> — correct across the loop.
    /// </summary>
    /// <remarks>
    /// Zero after a <see cref="ScrubTo"/> or a clip change: a scrub is a jump, not travel, and
    /// integrating one would teleport whatever the delta is driving. That distinction is the reason
    /// this is a property of the advance rather than of the time.
    /// </remarks>
    public RootMotion RootDelta { get; private set; } = RootMotion.None;

    /// <summary>True once a non-looping clip has run past its end.</summary>
    public bool Finished { get; private set; }

    private AnimationClip? clip;

    public ClipPlayer(Skeleton skeleton, AnimationClip? clip = null)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        Skeleton = skeleton;
        RestPose = skeleton.CreateRestPose();
        Pose = skeleton.CreateRestPose();
        RootBone = RootMotion.DefaultRootBone(skeleton);
        this.clip = clip;
        Resample();
    }

    /// <summary>The current clip's length, or 0 when there is no clip.</summary>
    public double Duration => clip?.Duration ?? 0.0;

    /// <summary>Where the clip sits in its cycle, in [0, 1]. 0 when there is no length to sit in.</summary>
    public float Phase => Duration > 0.0 ? (float)(Time / Duration) : 0f;

    /// <summary>
    /// Move time on by <paramref name="delta"/> seconds (scaled by <see cref="Rate"/>), re-sample, and
    /// report the root's travel.
    /// </summary>
    /// <returns>False once a non-looping clip has finished — the caller's cue to switch clips.</returns>
    /// <remarks>
    /// <b>Every loop crossed contributes its own travel.</b> The naive form samples the root at the old
    /// and new times and subtracts, which is right until the new time has wrapped — then it reports a
    /// jump backwards across the entire cycle, once per loop, forever. Walking the step in pieces that
    /// each stay inside one pass of the clip makes the wrap a non-event: a 0.8 s clip advanced by 2 s
    /// yields two whole cycles plus a remainder, summed.
    /// <para>
    /// Reverse playback takes the mirrored path rather than a negated forward one, because the pieces
    /// are bounded by the clip's start instead of its end.
    /// </para>
    /// </remarks>
    public bool Advance(double delta)
    {
        RootDelta = RootMotion.None;
        if (clip is null)
        {
            Resample();
            return false;
        }

        var duration = clip.Duration;
        if (Paused || Rate == 0f || !double.IsFinite(delta))
        {
            Resample();
            return !Finished;
        }

        var step = delta * Rate;

        // The root's rest transform is what an unanimated channel falls back to, so every travel
        // measurement below needs it. Hoisted once rather than fetched in each branch.
        var rest = RestPose.Locals[RootBone];
        if (duration <= 0.0)
        {
            // A single-keyframe "pose" clip. There is nowhere to advance to; holding it is the
            // whole behaviour, and a non-looping one is finished the moment it starts.
            Time = 0.0;
            Finished = !Loop;
            Resample();
            return !Finished;
        }

        if (!Loop)
        {
            // <b>"Finished" means the terminal boundary IN THE DIRECTION OF TRAVEL, not the
            // chronological end.</b> A negative Rate is a supported way to play a clip, so a
            // one-shot run backwards ends at t=0 as surely as a forward one ends at Duration —
            // and a caller watching the return value to switch clips has to be told either way.
            //
            // This used to set Finished only at the end: reverse clamped to zero, returned true
            // forever, and a lifecycle driven by it hung on a clip that had visibly stopped. The
            // asymmetry was invisible because nothing in the tree played a one-shot backwards yet.
            var target = Time + step;
            var forward = step > 0.0;
            var boundary = forward ? duration : 0.0;

            if (forward ? target >= duration : target <= 0.0)
            {
                RootDelta = RootMotion.Between(clip, RootBone, rest, Time, boundary);
                Time = boundary;
                Finished = true;
                Resample();
                return false;
            }

            RootDelta = RootMotion.Between(clip, RootBone, rest, Time, target);
            Time = target;
            Resample();
            return true;
        }

        var motion = RootMotion.None;
        var cursor = Time;
        var remaining = step;

        if (remaining > 0.0)
        {
            while (remaining > 0.0)
            {
                var room = duration - cursor;
                if (remaining < room)
                {
                    motion = motion.Then(RootMotion.Between(clip, RootBone, rest, cursor, cursor + remaining));
                    cursor += remaining;
                    break;
                }

                motion = motion.Then(RootMotion.Between(clip, RootBone, rest, cursor, duration));
                remaining -= room;
                cursor = 0.0;
            }
        }
        else
        {
            while (remaining < 0.0)
            {
                if (cursor <= 0.0)
                {
                    // Standing on the start of the cycle with distance left to travel backwards:
                    // step over the seam to the end and keep going. No travel is accrued by the
                    // move itself — the seam is the same instant seen from both sides.
                    cursor = duration;
                    continue;
                }

                if (-remaining < cursor)
                {
                    motion = motion.Then(RootMotion.Between(clip, RootBone, rest, cursor, cursor + remaining));
                    cursor += remaining;
                    break;
                }

                motion = motion.Then(RootMotion.Between(clip, RootBone, rest, cursor, 0.0));
                remaining += cursor;
                cursor = 0.0;
            }
        }

        Time = Wrap(cursor, duration);
        RootDelta = motion;
        Resample();
        return true;
    }

    /// <summary>
    /// Jump to an absolute clip time and re-sample. <see cref="RootDelta"/> is cleared — a scrub is not travel.
    /// </summary>
    public void ScrubTo(double clipTime)
    {
        Time = Wrap(clipTime, Duration);
        RootDelta = RootMotion.None;
        Finished = false;
        Resample();
    }

    /// <summary>Advance by a fixed number of seconds regardless of <see cref="Paused"/> — one frame of a scrub.</summary>
    /// <remarks>
    /// Steps DO accrue root travel: stepping through a walk frame by frame and watching the delta add
    /// up is exactly how you find out whether the clip's travel is smooth, which is the question a
    /// step button exists to answer.
    /// </remarks>
    public void Step(double seconds)
    {
        var wasPaused = Paused;
        var rate = Rate;
        Paused = false;
        Rate = 1f;
        Advance(seconds);
        Rate = rate;
        Paused = wasPaused;
    }

    /// <summary>Rewrite <see cref="Pose"/> from the rest pose and the clip at the current time.</summary>
    /// <remarks>
    /// Public because a caller that mutated the pose after a sample (an IK pass, a hand-authored
    /// override) needs a way back to what the clip actually says without moving the clock.
    /// </remarks>
    public void Resample()
    {
        Pose.CopyFrom(RestPose);
        clip?.Sample(Time, Pose);
    }

    /// <summary>Time folded into [0, duration), with the zero-duration case answering 0.</summary>
    public static double Wrap(double time, double duration)
    {
        if (duration <= 0.0 || !double.IsFinite(time)) return 0.0;
        var wrapped = time % duration;
        return wrapped < 0.0 ? wrapped + duration : wrapped;
    }
}
