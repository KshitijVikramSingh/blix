namespace Blix;

// A snapshot of every bone's local-space transform — the value an animation clip
// produces and the skeleton consumes to compute the GPU bone palette.
//
// Poses carry no reference to their owning Skeleton: a single pose value can be
// produced by one source (an animation clip, hand authoring, blending of two other
// poses) and consumed by any skeleton with the matching bone count. Caller is
// responsible for the count match; Skeleton.ComputeBonePalette validates it at the
// boundary.
//
// SharpGLTF's design embeds the current local transform on each Node directly,
// blurring data and topology. We keep the Pose distinct from the Skeleton —
// separating the *data* (which way is the elbow bent) from the *metadata* (where
// does the elbow sit in the hierarchy). Animations produce poses; skeletons hold
// metadata; the conversion to GPU palettes joins them at one call site.
public sealed class Pose
{
    public BoneTransform[] Locals { get; }

    public int BoneCount => Locals.Length;

    public Pose(int boneCount)
    {
        if (boneCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(boneCount), "Bone count must be non-negative.");
        }
        Locals = new BoneTransform[boneCount];
    }

    public Pose(BoneTransform[] locals)
    {
        ArgumentNullException.ThrowIfNull(locals);
        Locals = locals;
    }

    // Replace this pose's locals with those of `other`. Used to reset the working
    // pose to a base (typically the cached rest pose) before sampling a partial
    // clip on top — clips only touch the channels they explicitly animate, so the
    // base provides the values for everything else.
    public void CopyFrom(Pose other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.BoneCount != BoneCount)
        {
            throw new ArgumentException(
                $"Cannot copy from pose with {other.BoneCount} bones into pose with {BoneCount} bones.",
                nameof(other));
        }
        Array.Copy(other.Locals, Locals, BoneCount);
    }
}
