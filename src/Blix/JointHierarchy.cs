using System.Numerics;

namespace Blix;

/// <summary>A skin's joints read out of a scene graph: where the skeleton hangs, and each bone's rest and offset.</summary>
/// <remarks>
/// <para>
/// glTF places a skinned vertex by its joints' world transforms alone — <c>sum(w * jointWorld *
/// inverseBind * v)</c> — and those worlds are the scene graph's, non-joint nodes included. A
/// <see cref="Skeleton"/> composes bone worlds from its roots down, so it needs three facts from the
/// graph, and this is the one place that derives them, for the cooked reader and the source importer
/// alike:
/// </para>
/// <list type="bullet">
/// <item><see cref="Placement"/>: the world of the node the first root joint hangs from (identity for a
/// scene root). Not the mesh node's world — glTF ignores that transform for a skinned mesh.</item>
/// <item>Each bone's rest: its joint node's own local transform, which is what it holds when no track
/// moves it.</item>
/// <item>Each bone's offset: the non-joint nodes between it and its parent joint, and for a root whose
/// parent is not the first root's, that parent's world relative to the placement.</item>
/// </list>
/// <para>
/// Non-joint nodes are read at their rest: a clip that animates one is not followed.
/// </para>
/// </remarks>
public sealed class JointHierarchy
{
    private JointHierarchy(Matrix4x4 placement, Bone[] bones)
    {
        Placement = placement;
        Bones = bones;
    }

    /// <summary>Where the skeleton hangs in the scene: goes after every bone world.</summary>
    public Matrix4x4 Placement { get; }

    /// <summary>The bones, named and parented as given, with their rest and offset read from the graph.</summary>
    public IReadOnlyList<Bone> Bones { get; }

    /// <summary>Reads the joints of one skin.</summary>
    /// <param name="names">Each bone's name, in hierarchy order (parents first).</param>
    /// <param name="parents">Each bone's parent bone, or -1 for a root, index for index with <paramref name="names"/>.</param>
    /// <param name="jointNodes">The scene node of each bone, index for index.</param>
    /// <param name="parentOf">A node's parent node, or -1 for a scene root.</param>
    /// <param name="localOf">A node's local transform (row-vector).</param>
    /// <param name="worldOf">A node's world transform (row-vector: <c>local * parentWorld</c>).</param>
    public static JointHierarchy Resolve(
        IReadOnlyList<string> names,
        IReadOnlyList<int> parents,
        IReadOnlyList<int> jointNodes,
        Func<int, int> parentOf,
        Func<int, Matrix4x4> localOf,
        Func<int, Matrix4x4> worldOf)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(jointNodes);
        ArgumentNullException.ThrowIfNull(parentOf);
        ArgumentNullException.ThrowIfNull(localOf);
        ArgumentNullException.ThrowIfNull(worldOf);
        if (names.Count != jointNodes.Count || parents.Count != jointNodes.Count)
        {
            throw new ArgumentException($"{names.Count} name(s) and {parents.Count} parent(s) for {jointNodes.Count} joint node(s).", nameof(jointNodes));
        }

        // The placement: the first root's parent world. Every other root is expressed against it.
        Matrix4x4? placement = null;
        for (var i = 0; i < names.Count && placement is null; i++)
        {
            if (parents[i] < 0) placement = ParentWorld(jointNodes[i]);
        }

        var hangs = placement ?? Matrix4x4.Identity;
        Matrix4x4.Invert(hangs, out var fromPlacement);
        var resolved = new Bone[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            var node = jointNodes[i];
            Matrix4x4 offset;
            if (parents[i] < 0)
            {
                // local * parentWorld = local * offset * placement.
                offset = ParentWorld(node) * fromPlacement;
            }
            else
            {
                // The nodes between this joint and its parent joint, nearest first.
                var parentJoint = jointNodes[parents[i]];
                offset = Matrix4x4.Identity;
                for (var n = parentOf(node); n != parentJoint; n = parentOf(n))
                {
                    if (n < 0)
                    {
                        throw new ArgumentException(
                            $"bone {i} ('{names[i]}') names bone {parents[i]} as its parent, but that joint is not its ancestor.",
                            nameof(parents));
                    }

                    offset *= localOf(n);
                }
            }

            resolved[i] = new Bone(names[i], parents[i], BoneTransform.FromMatrix(localOf(node)), IsIdentity(offset) ? null : offset);
        }

        return new JointHierarchy(hangs, resolved);

        Matrix4x4 ParentWorld(int node) => parentOf(node) is var p and >= 0 ? worldOf(p) : Matrix4x4.Identity;
    }

    private static bool IsIdentity(Matrix4x4 m)
    {
        for (var r = 0; r < 4; r++)
        for (var c = 0; c < 4; c++)
        {
            if (MathF.Abs(m[r, c] - (r == c ? 1f : 0f)) > 1e-6f) return false;
        }

        return true;
    }
}
