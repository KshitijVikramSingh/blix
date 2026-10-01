using System.Numerics;

namespace Blix;

/// <summary>A skeleton's bone worlds under a pose: matrices that know whose bones they are.</summary>
/// <remarks>
/// <para>
/// <b>Why not a <c>Matrix4x4[]</c>.</b> A world array is indexed by bone, and a bone index means a bone only in
/// the skeleton that composed it. A raw array forgets which that was, so a palette could be built from another
/// same-sized rig's worlds and nothing could say so. These are written only by <see cref="Compute"/>, from
/// <see cref="Skeleton"/>, so a <see cref="SkinBinding"/> can refuse worlds of any skeleton but its own.
/// </para>
/// <para>
/// The limit one level down: a <see cref="Pose"/> is indexed locals with no skeleton, so a same-sized pose from
/// another rig still composes here. What this guarantees is that the worlds are this skeleton's composition.
/// </para>
/// </remarks>
public sealed class BoneWorlds
{
    private readonly Matrix4x4[] matrices;

    public BoneWorlds(Skeleton skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        Skeleton = skeleton;
        matrices = new Matrix4x4[skeleton.BoneCount];
    }

    /// <summary>The skeleton whose bones these are.</summary>
    public Skeleton Skeleton { get; }

    /// <summary>One per bone of <see cref="Skeleton"/>.</summary>
    public int Count => matrices.Length;

    /// <summary>Bone <paramref name="bone"/>'s world (row-vector), as of the last <see cref="Compute"/>.</summary>
    public Matrix4x4 this[int bone] => matrices[bone];

    /// <summary>Every bone's world, read-only: only <see cref="Compute"/> writes them.</summary>
    public ReadOnlySpan<Matrix4x4> AsSpan() => matrices;

    /// <summary>Composes every bone's world under <paramref name="pose"/> (<see cref="Skeleton.ComputeBoneWorlds"/>).</summary>
    public void Compute(Pose pose) => Skeleton.ComputeBoneWorlds(pose, matrices);
}
