using System.Numerics;

namespace Blix;

// The bone hierarchy + the bind-time-constant data needed to convert a Pose into
// a BonePalette ready for GPU skinning.
//
// Bones are stored in a flat array with a strict invariant: each bone's
// `ParentIndex` is either -1 (root) or strictly less than its own index. This
// guarantees that a single forward walk over `Bones` visits every parent before
// any of its children — which is exactly the access pattern ComputeBonePalette
// needs, with no recursion, no sorting, and no per-bone dictionary lookups.
//
// Construction validates the invariant. Importers (glTF / FBX / hand-authored)
// must topo-sort their joint lists before passing them in — glTF's `Skin.Joints`
// is usually pre-sorted; one that isn't gets a single sort pass at import time.
//
// Multiple roots are supported (`ParentIndex == -1` is allowed on more than one
// bone). glTF allows multi-root skins and so do we.
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

    // Reconstruct the local-space rest pose from each bone's inverse-bind matrix.
    //
    // The math: `InverseBindPose` maps a vertex from object-space-at-rest into
    // bone-space-at-rest. Its inverse — `BindWorld[i]` — is the bone's object-space
    // transform at the rest pose. The bone-local rest is that transform expressed
    // in the parent's frame:
    //
    //   LocalRest[root] = BindWorld[root]
    //   LocalRest[child] = inverse(BindWorld[parent]) * BindWorld[child]
    //
    // Decomposing each LocalRest matrix into TRS lets the resulting Pose stay in
    // struct form, consistent with what animation clips later produce.
    //
    // Sanity check: feed the returned Pose back into ComputeBonePalette and every
    // resulting matrix is the identity (because BindWorld * InverseBindPose = I by
    // construction).
    public Pose CreateRestPose()
    {
        var bindWorld = new Matrix4x4[Bones.Length];
        for (var i = 0; i < Bones.Length; i++)
        {
            Matrix4x4.Invert(Bones[i].InverseBindPose, out bindWorld[i]);
        }

        var locals = new BoneTransform[Bones.Length];
        for (var i = 0; i < Bones.Length; i++)
        {
            var p = Bones[i].ParentIndex;
            if (p < 0)
            {
                locals[i] = BoneTransform.FromMatrix(bindWorld[i]);
            }
            else
            {
                Matrix4x4.Invert(bindWorld[p], out var inverseParent);
                // Row-vector composition (F-016): a vertex flows left-to-right.
                // child-local → world via bindWorld[child], then world →
                // parent-local via inverseParent gives child-local → parent-local.
                locals[i] = BoneTransform.FromMatrix(bindWorld[i] * inverseParent);
            }
        }
        return new Pose(locals);
    }

    // Hierarchy walk: for each bone, compose local-space → world-space; then
    // multiply through the inverse-bind matrix to produce the "delta from rest
    // pose" matrix the GPU consumes. Single forward pass over Bones because the
    // hierarchy-order invariant guarantees every parent's world matrix is already
    // filled when a child needs to read it.
    //
    // The scratch worldMatrices array is per-call. A per-frame allocation of one
    // Matrix4x4[] per skinned object is negligible at the kind of object counts
    // we deal with; a poolable variant can land alongside the first profiling
    // evidence that the allocation matters.
    public void ComputeBonePalette(Pose pose, BonePalette outPalette) =>
        ComputeBonePalette(pose, outPalette, null);

    /// <summary>
    /// Computes the skinning palette, and optionally hands back the joint world transforms it
    /// builds on the way.
    /// </summary>
    /// <param name="outJointWorlds">
    /// Filled with each bone's world transform when given, sized to <see cref="BoneCount"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The palette is not the joints' transforms, and that distinction is why this exists.</b>
    /// <c>Matrices[i]</c> is <c>InverseBindPose · world</c> — a map from a REST vertex to its posed
    /// position, which is exactly what a skinned vertex shader wants and exactly the wrong thing to
    /// hand something that has no rest vertices in this skin's space. A knife parented to a hand
    /// has its own mesh and its own offset; what it needs is where the hand IS.
    /// </para>
    /// <para>
    /// <b>And that array was already being built and thrown away.</b> The world transforms are the
    /// intermediate the palette is derived from, computed on every call and dropped one line later,
    /// so this adds no traversal and no arithmetic — it stops discarding a result.
    /// </para>
    /// <para>
    /// <b>It also removes a per-frame allocation.</b> The scratch array was allocated inside this
    /// method on every call — once per skinned body per frame, which in RTSGame is a crowd. A
    /// caller that wants the worlds supplies the array and there is none; a caller that does not
    /// gets the old behaviour, because making everyone provide one to fix an allocation they never
    /// asked about is a worse trade than the allocation.
    /// </para>
    /// </remarks>
    public void ComputeBonePalette(Pose pose, BonePalette outPalette, Matrix4x4[]? outJointWorlds)
    {
        ArgumentNullException.ThrowIfNull(pose);
        ArgumentNullException.ThrowIfNull(outPalette);
        if (pose.BoneCount != Bones.Length)
        {
            throw new ArgumentException(
                $"Pose has {pose.BoneCount} bones; skeleton has {Bones.Length}.",
                nameof(pose));
        }
        if (outPalette.BoneCount != Bones.Length)
        {
            throw new ArgumentException(
                $"BonePalette has {outPalette.BoneCount} matrices; skeleton has {Bones.Length}.",
                nameof(outPalette));
        }

        var worldMatrices = outJointWorlds ?? new Matrix4x4[Bones.Length];
        ComputeBoneWorlds(pose, worldMatrices);

        // Palette maps rest → bone-local (InverseBindPose), then bone-local → posed world.
        for (var i = 0; i < Bones.Length; i++)
        {
            outPalette.Matrices[i] = Bones[i].InverseBindPose * worldMatrices[i];
        }
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

        // Row-vector composition (F-016): local[i] · local[parent] · … · local[root].
        for (var i = 0; i < Bones.Length; i++)
        {
            var localMatrix = pose.Locals[i].ToMatrix();
            var p = Bones[i].ParentIndex;
            outJointWorlds[i] = p < 0 ? localMatrix : localMatrix * outJointWorlds[p];
        }
    }
}
