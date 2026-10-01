using System.Numerics;

namespace Blix;

// A pose hierarchy: the bones a clip, a pose stack and a mask index, and nothing about skinning. What a
// skin binds to it (which bones are its joints, and their inverse binds) is a SkinBinding over it.
//
// Bones are stored in a flat array with a strict invariant: each bone's `ParentIndex` is either -1 (root)
// or strictly less than its own index. So a single forward walk over `Bones` visits every parent before
// any of its children, which is the access pattern ComputeBoneWorlds needs, with no recursion, no sorting
// and no per-bone lookups. Construction validates it; importers topo-sort their joints first.
//
// Multiple roots are supported (`ParentIndex == -1` on more than one bone): a hierarchy can hold several skins,
// and nodes a clip moves that hang from no joint. That is not a claim about glTF skins, whose joints MUST share a
// common root (glTF 2.0 §5.27); Blix reads one that does not only through a named validator fallback
// (MeshRecipe.ValidatorFallbacks), which is a declared leniency, not conformance.
public sealed class Skeleton
{
    public Bone[] Bones { get; }

    public int BoneCount => Bones.Length;

    public Skeleton(Bone[] bones)
    {
        ArgumentNullException.ThrowIfNull(bones);
        for (var i = 0; i < bones.Length; i++)
        {
            var p = bones[i].ParentIndex;
            if (p < -1 || p >= i)
            {
                throw new ArgumentException(
                    $"Bone {i} ('{bones[i].Name}') has ParentIndex {p}; expected -1 (root) " +
                    $"or an index strictly less than {i} (hierarchy order required).",
                    nameof(bones));
            }
        }
        Bones = bones;
    }

    /// <summary>The rest pose: each bone's authored <see cref="Bone.Rest"/>.</summary>
    public Pose CreateRestPose()
    {
        var locals = new BoneTransform[Bones.Length];
        for (var i = 0; i < Bones.Length; i++) locals[i] = Bones[i].Rest;
        return new Pose(locals);
    }

    /// <summary>Computes each joint's object-space transform under <paramref name="pose"/>.</summary>
    /// <remarks>
    /// These are joint transforms for attachments and inspection, not skinning matrices. The
    /// hierarchy-order invariant makes this a single forward walk.
    /// </remarks>
    public void ComputeBoneWorlds(Pose pose, Matrix4x4[] outJointWorlds)
    {
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(outJointWorlds);
        if (pose.BoneCount != Bones.Length)
        {
            throw new ArgumentException(
                $"Pose has {pose.BoneCount} bones; skeleton has {Bones.Length}.", nameof(pose));
        }
        if (outJointWorlds.Length != Bones.Length)
        {
            throw new ArgumentException(
                $"Joint-world array has {outJointWorlds.Length} matrices; skeleton has {Bones.Length}.",
                nameof(outJointWorlds));
        }

        // Row-vector composition (F-016): local[i] · offset[i] · world[parent], the offset being the
        // non-joint nodes in between (identity for most skeletons).
        for (var i = 0; i < Bones.Length; i++)
        {
            var localMatrix = pose.Locals[i].ToMatrix();
            if (Bones[i].Offset is { } offset) localMatrix *= offset;
            var p = Bones[i].ParentIndex;
            outJointWorlds[i] = p < 0 ? localMatrix : localMatrix * outJointWorlds[p];
        }
    }
}
