using System.Numerics;

namespace Blix;

// How far a clip's root bone TRAVELLED over an interval, rather than where it is.
//
// Nothing in the tree produced this before. The only consumer that looked at the root at
// all — the external RTSGame consumer's StripRootMotion — reverts it to rest, which throws the travel away: the
// simulation owns where a body is and a clip is not allowed an opinion. That is the right
// call for an RTS and it means root-driven locomotion was *absent*, not merely unextracted.
// A delta is the missing half: the clip still does not move anything, but it can now say
// how far it would have.
//
// ── The space the numbers are in ────────────────────────────────────────────
// Translation is in the root's PARENT frame — model space, for a bone with no parent —
// which is the frame the clip's own translation track is authored in. A caller driving a
// character rotates it into world by the character's world rotation; the delta itself takes
// no view on that, because a delta that had already chosen a world frame could not be
// summed across a loop.
//
// Rotation is the turn from the earlier sample to the later one, composed as
// `later = earlier * delta` — the same order PoseDelta.LayerOnto uses, so the two compose
// without a convention argument.
//
// ── The loop boundary ───────────────────────────────────────────────────────
// This is the part every implementation gets wrong. Sampling the root at t0 and t1 and
// subtracting is right until t1 wraps past the clip's end, at which point the subtraction
// reports a jump BACKWARDS across the whole cycle — a walk that lurches once per loop.
// <see cref="AcrossLoop"/> is the fix: travel to the end of the cycle, then travel from the
// start, added. Both halves are in the same frame, so plain addition is exact.
public readonly record struct RootMotion(Vector3 Translation, Quaternion Rotation)
{
    /// <summary>No travel: the identity of <see cref="Then"/>.</summary>
    public static RootMotion None { get; } = new(Vector3.Zero, Quaternion.Identity);

    /// <summary>This delta followed by <paramref name="next"/>, as one delta.</summary>
    /// <remarks>
    /// Translations add rather than compose through the rotation, because both are expressed in the
    /// same parent frame — the frame does not turn between the two samples, only the bone inside it
    /// does. Rotations compose in the `earlier * later` order the record documents.
    /// </remarks>
    public RootMotion Then(RootMotion next) =>
        new(Translation + next.Translation, Rotation * next.Rotation);

    /// <summary>Distance travelled, for a readout that wants one number.</summary>
    public float Distance => Translation.Length();

    /// <summary>
    /// The root bone's local transform at <paramref name="time"/>, reading the clip's tracks directly.
    /// </summary>
    /// <remarks>
    /// Deliberately not `clip.Sample` into a scratch Pose: that costs every bone in the skeleton to
    /// answer a question about one, and a root-motion probe samples a clip dozens of times. Channels
    /// the clip does not animate fall back to <paramref name="rest"/>, which is the same
    /// partial-write contract <see cref="AnimationClip.Sample"/> honours — so a clip with no root
    /// track yields rest at both ends and a delta of exactly zero.
    /// </remarks>
    public static BoneTransform RootAt(AnimationClip clip, int rootBone, BoneTransform rest, double time)
    {
        ArgumentNullException.ThrowIfNull(clip);
        foreach (var track in clip.Tracks)
        {
            if (track.BoneIndex != rootBone) continue;
            return new BoneTransform(
                track.Translation?.Evaluate(time) ?? rest.Translation,
                track.Rotation?.Evaluate(time) ?? rest.Rotation,
                track.Scale?.Evaluate(time) ?? rest.Scale);
        }

        return rest;
    }

    /// <summary>Travel from <paramref name="from"/> to <paramref name="to"/> within one pass of the clip.</summary>
    public static RootMotion Between(
        AnimationClip clip, int rootBone, BoneTransform rest, double from, double to)
    {
        var a = RootAt(clip, rootBone, rest, from);
        var b = RootAt(clip, rootBone, rest, to);
        return new RootMotion(
            b.Translation - a.Translation,
            Quaternion.Inverse(a.Rotation) * b.Rotation);
    }

    /// <summary>
    /// Travel from <paramref name="from"/> to <paramref name="to"/> across a loop boundary: to the end
    /// of the cycle, then from its start.
    /// </summary>
    /// <remarks>
    /// The whole reason this type exists. `Between(from, to)` with to &lt; from reports the cycle
    /// running backwards; this reports what the body actually did. A clip authored in place (root
    /// returns to where it started) gives ≈ zero here, and a clip with real travel gives exactly one
    /// cycle's worth — both correct, from the same expression.
    /// </remarks>
    public static RootMotion AcrossLoop(
        AnimationClip clip, int rootBone, BoneTransform rest, double from, double to, double duration)
    {
        if (duration <= 0.0) return None;
        return Between(clip, rootBone, rest, from, duration)
            .Then(Between(clip, rootBone, rest, 0.0, to));
    }

    /// <summary>The travel of one whole cycle — what a looping clip adds per pass.</summary>
    public static RootMotion PerCycle(AnimationClip clip, int rootBone, BoneTransform rest) =>
        clip.Duration <= 0.0 ? None : Between(clip, rootBone, rest, 0.0, clip.Duration);

    /// <summary>
    /// Reverts every root bone in <paramref name="pose"/> to its rest transform — the other half of taking a delta.
    /// </summary>
    /// <remarks>
    /// <b>Taking the travel and leaving it in the pose applies it twice.</b> A clip that walks its root
    /// forward already moves the mesh; a caller that also drives its object transform by the delta gets a
    /// body travelling at double speed and snapping back once per loop. Whoever consumes a delta owes the
    /// pose this call.
    /// <para>
    /// Every parentless bone, not just the first — glTF permits a multi-root skin, and stripping one root
    /// of several leaves the others still travelling.
    /// </para>
    /// <para>
    /// The cost is a clip that turns in place, whose turn now goes nowhere; the turn belongs in the
    /// steering layer. The external RTSGame consumer carries a private copy of exactly this, written before
    /// there was anywhere to put it, and should fold in the next time that game is touched under its own
    /// gate — a mechanical swap is still a change to a simulation that verifies in years, not seconds.
    /// </para>
    /// </remarks>
    public static void Strip(Skeleton skeleton, Pose pose, Pose rest)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(rest);

        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            if (skeleton.Bones[i].ParentIndex >= 0) continue;
            // Position, orientation and scale: a root that kept its animated rotation would still
            // turn the whole body, which is travel by another name.
            pose.Locals[i] = rest.Locals[i];
        }
    }

    /// <summary>
    /// The first bone with no parent — the default root a clip's travel is read from.
    /// </summary>
    /// <remarks>
    /// Same rule <c>StripRootMotion</c> uses to decide what to revert, so the two agree about which
    /// bone is "the root". A multi-root skin (glTF permits one) has more than one candidate; the
    /// first is a default, and a caller that knows better sets the index itself.
    /// </remarks>
    public static int DefaultRootBone(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            if (skeleton.Bones[i].ParentIndex < 0) return i;
        }

        return 0;
    }
}
