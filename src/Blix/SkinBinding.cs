using System.Numerics;

namespace Blix;

/// <summary>
/// A skin bound to a pose hierarchy: which of its bones are the skin's joints, and each joint's inverse bind.
/// Everything a palette needs that a <see cref="Skeleton"/> does not have.
/// </summary>
/// <remarks>
/// <para>
/// <b>A skin is not a skeleton.</b> glTF animates nodes; a skin names some of them as its joints and says,
/// per joint, where a vertex sat relative to it when the mesh was bound. Two skins can share joints with
/// different binds, and a node a clip moves need be no skin's joint at all. So the binds live here, per
/// skin, and the hierarchy they index carries none.
/// </para>
/// <para>
/// <b>It keeps the skeleton it indexes.</b> <see cref="Bones"/> are indices into one hierarchy, and an index
/// means a bone only there; without the skeleton they are indices into some hierarchy somewhere. Recording it
/// is the fact this type represents. Its palette takes <see cref="BoneWorlds"/>, which know their skeleton too,
/// so worlds of another rig are refused by identity, not merely by size.
/// </para>
/// </remarks>
public sealed class SkinBinding
{
    private readonly int[] bones;
    private readonly Matrix4x4[] inverseBinds;

    /// <param name="skeleton">The hierarchy the joints are bones of.</param>
    /// <param name="bones">Each joint, in the skin's joint order, as a bone of <paramref name="skeleton"/>.</param>
    /// <param name="inverseBinds">Each joint's inverse bind, index for index with <paramref name="bones"/> (row-vector).</param>
    /// <exception cref="ArgumentException">The counts differ, a joint is not a bone of the skeleton, or a bone is named twice.</exception>
    public SkinBinding(Skeleton skeleton, IReadOnlyList<int> bones, IReadOnlyList<Matrix4x4> inverseBinds)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(bones);
        ArgumentNullException.ThrowIfNull(inverseBinds);
        if (bones.Count != inverseBinds.Count)
        {
            throw new ArgumentException($"{bones.Count} joint(s) and {inverseBinds.Count} inverse bind(s); a joint needs exactly one.", nameof(inverseBinds));
        }

        var seen = new HashSet<int>();
        for (var j = 0; j < bones.Count; j++)
        {
            if ((uint)bones[j] >= (uint)skeleton.BoneCount)
            {
                throw new ArgumentException($"joint {j} is bone {bones[j]}, and the skeleton has {skeleton.BoneCount}.", nameof(bones));
            }

            if (!seen.Add(bones[j]))
            {
                throw new ArgumentException($"joint {j} names bone {bones[j]} ('{skeleton.Bones[bones[j]].Name}') again; a node is one joint of a skin.", nameof(bones));
            }
        }

        Skeleton = skeleton;
        this.bones = bones.ToArray();
        this.inverseBinds = inverseBinds.ToArray();
    }

    /// <summary>A skin whose joints are its skeleton's bones, in order: joint i is bone i.</summary>
    public static SkinBinding Direct(Skeleton skeleton, IReadOnlyList<Matrix4x4> inverseBinds)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        return new SkinBinding(skeleton, Enumerable.Range(0, skeleton.BoneCount).ToArray(), inverseBinds);
    }

    /// <summary>The hierarchy <see cref="Bones"/> index.</summary>
    public Skeleton Skeleton { get; }

    /// <summary>Each joint as a bone of <see cref="Skeleton"/>, in the skin's joint order: what a vertex's joint index means.</summary>
    public IReadOnlyList<int> Bones => bones;

    /// <summary>Each joint's inverse bind (row-vector): object space at bind to the joint's space.</summary>
    public IReadOnlyList<Matrix4x4> InverseBinds => inverseBinds;

    /// <summary>How many joints the skin has: the palette's length.</summary>
    public int JointCount => bones.Length;

    /// <summary>Each joint's parent joint: its nearest ancestor bone that is also a joint of this skin, or -1.</summary>
    /// <remarks>The skin's joints as a tree of their own, for reporting and analysis over joint indices.</remarks>
    public int[] JointParents()
    {
        var jointOfBone = new int[Skeleton.BoneCount];
        Array.Fill(jointOfBone, -1);
        for (var j = 0; j < bones.Length; j++) jointOfBone[bones[j]] = j;

        var parents = new int[bones.Length];
        for (var j = 0; j < bones.Length; j++)
        {
            var b = Skeleton.Bones[bones[j]].ParentIndex;
            while (b >= 0 && jointOfBone[b] < 0) b = Skeleton.Bones[b].ParentIndex;
            parents[j] = b < 0 ? -1 : jointOfBone[b];
        }

        return parents;
    }

    /// <summary>Each joint's world at bind: the inverse of its inverse bind.</summary>
    /// <exception cref="InvalidOperationException">An inverse bind is singular.</exception>
    /// <remarks>
    /// Matrices in the skin's binding space, not a <see cref="Pose"/>: a pose is a hierarchy's locals, and these
    /// are what the binds imply about where each joint was. Comparing them with the hierarchy's worlds at rest
    /// says whether a file's rest is its bind (it need not be).
    /// </remarks>
    public Matrix4x4[] JointBindWorlds()
    {
        var worlds = new Matrix4x4[inverseBinds.Length];
        for (var j = 0; j < worlds.Length; j++)
        {
            // A diagnostic primitive says which fact it could not derive, rather than handing back a matrix of NaNs.
            if (!Matrix4x4.Invert(inverseBinds[j], out worlds[j]))
            {
                throw new InvalidOperationException(
                    $"the inverse bind of joint {j} ('{Skeleton.Bones[bones[j]].Name}') is not invertible, so it implies no bind world.");
            }
        }
        return worlds;
    }

    /// <summary>Writes the skin's palette from its skeleton's bone worlds: <c>inverseBind[j] · world[bones[j]] · post</c>.</summary>
    /// <param name="worlds">Bone worlds of <see cref="Skeleton"/> itself, not of another rig, however alike.</param>
    /// <param name="post">What goes after every world: the hierarchy's placement, then the body's.</param>
    /// <param name="palette">One matrix per joint.</param>
    /// <exception cref="ArgumentException">The worlds are another skeleton's, or the palette is not one per joint.</exception>
    public void ComputePalette(BoneWorlds worlds, Matrix4x4 post, Span<Matrix4x4> palette)
    {
        RequireOwn(worlds, nameof(worlds));
        if (palette.Length != bones.Length)
        {
            throw new ArgumentException($"a palette of {palette.Length} for a {bones.Length}-joint skin.", nameof(palette));
        }

        var w = worlds.AsSpan();
        for (var j = 0; j < bones.Length; j++) palette[j] = inverseBinds[j] * w[bones[j]] * post;
    }

    /// <summary>Poses the skeleton and writes the palette in one call: its bone worlds, then <see cref="ComputePalette(BoneWorlds, Matrix4x4, Span{Matrix4x4})"/>.</summary>
    /// <param name="worlds">Receives the bone worlds when given (this skin's skeleton's); a scratch set otherwise.</param>
    public void ComputePalette(Pose pose, BonePalette palette, BoneWorlds? worlds = null)
    {
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(palette);
        if (worlds is not null) RequireOwn(worlds, nameof(worlds));
        var w = worlds ?? new BoneWorlds(Skeleton);
        w.Compute(pose);
        ComputePalette(w, Matrix4x4.Identity, palette.Matrices);
    }

    private void RequireOwn(BoneWorlds worlds, string paramName)
    {
        ArgumentNullException.ThrowIfNull(worlds, paramName);
        if (!ReferenceEquals(worlds.Skeleton, Skeleton))
        {
            throw new ArgumentException(
                $"the bone worlds are another skeleton's ({worlds.Skeleton.BoneCount} bones); a skin's joints are bones of its own skeleton only.",
                paramName);
        }
    }
}
