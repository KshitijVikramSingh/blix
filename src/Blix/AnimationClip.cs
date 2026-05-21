namespace Blix;

// Per-bone PRS tracks. Each channel is optional — a bone could be rotation-animated
// only (the typical case for skeletal "swing this joint" clips), or position+rotation
// (root motion), or all three (rare; usually for procedurally-authored clips).
//
// Mirrors glTF's per-channel structure: glTF stores animation channels separately,
// each targeting one of {translation, rotation, scale} on one node. Importing into a
// BoneTrack groups the channels by target bone — one BoneTrack per bone touched by
// the clip, with the channels the clip provides populated and the rest left null.
public sealed class BoneTrack
{
    public int BoneIndex { get; init; }
    public IFiniteCurve<System.Numerics.Vector3>?    Translation { get; init; }
    public IFiniteCurve<System.Numerics.Quaternion>? Rotation    { get; init; }
    public IFiniteCurve<System.Numerics.Vector3>?    Scale       { get; init; }
}

// A finite skeletal animation: a collection of per-bone tracks plus a duration.
// Sampling at time t writes into the channels each track defines, leaving other
// bones and other channels untouched.
//
// The "leave untouched" semantics is what makes partial clips work — a clip that only
// animates the legs can be sampled into a pose that's already at rest (or already
// holding an upper-body clip's result), and only the leg bones change. The caller is
// responsible for resetting the pose before sampling a full clip; the simplest
// pattern is `chainPose.CopyFrom(chainRestPose); clip.Sample(t, chainPose)` each
// frame.
//
// Duration is the maximum end-time across every channel of every track. Sampling
// past Duration clamps each channel to its last keyframe (the IFiniteCurve contract);
// game code is responsible for time wrapping if it wants looped playback, or for
// disposing the clip when it's done if it doesn't.
public sealed class AnimationClip
{
    public string Name { get; }
    public BoneTrack[] Tracks { get; }
    public double Duration { get; }

    public AnimationClip(string name, BoneTrack[] tracks)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(tracks);
        Name = name;
        Tracks = tracks;
        Duration = ComputeDuration(tracks);
    }

    // Write the clip's values at time `time` into `outPose`. Only the bone indices
    // and channels the tracks explicitly define are modified; other bones and other
    // channels of touched bones keep their existing values in outPose.
    //
    // Caller is responsible for ensuring outPose.BoneCount accommodates every
    // track's BoneIndex; out-of-range indices throw at access time.
    public void Sample(double time, Pose outPose)
    {
        ArgumentNullException.ThrowIfNull(outPose);
        foreach (var track in Tracks)
        {
            if (track.BoneIndex < 0 || track.BoneIndex >= outPose.BoneCount)
            {
                throw new ArgumentException(
                    $"Track targets bone {track.BoneIndex}, but pose has {outPose.BoneCount} bones.");
            }
            var local = outPose.Locals[track.BoneIndex];
            if (track.Translation is not null)
            {
                local = local with { Translation = track.Translation.Evaluate(time) };
            }
            if (track.Rotation is not null)
            {
                local = local with { Rotation = track.Rotation.Evaluate(time) };
            }
            if (track.Scale is not null)
            {
                local = local with { Scale = track.Scale.Evaluate(time) };
            }
            outPose.Locals[track.BoneIndex] = local;
        }
    }

    private static double ComputeDuration(BoneTrack[] tracks)
    {
        var max = 0.0;
        foreach (var t in tracks)
        {
            if (t.Translation is { } tp && tp.Duration > max) max = tp.Duration;
            if (t.Rotation    is { } rp && rp.Duration > max) max = rp.Duration;
            if (t.Scale       is { } sp && sp.Duration > max) max = sp.Duration;
        }
        return max;
    }
}
