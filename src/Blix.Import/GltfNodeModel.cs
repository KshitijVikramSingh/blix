using System.Numerics;

namespace Blix.Import;

// Node-hierarchy-preserving static glTF import (GltfStaticImporter.ImportNodes).
// Where the flattened Import bakes each node's WORLD transform into its vertices —
// right for a static scene drawn with one identity model matrix — this keeps the
// scene articulated: every node's mesh stays in its own LOCAL space (pivot where the
// author put it) and the node carries its name, local transform, and parent. A
// consumer maps named nodes onto its own rig (e.g. hull -> turret -> barrel
// Transform3Ds) and drives them, instead of getting a pre-posed fused blob.
public sealed record GltfNode(
    string Name,
    int ParentIndex,             // index into GltfNodeModel.Nodes; -1 for a root
    Matrix4x4 LocalTransform,    // relative to the parent (engine row-vector form)
    GltfPrimitive[] Primitives); // node-local-space meshes (empty for transform-only nodes)

public sealed record GltfNodeModel(GltfNode[] Nodes, UnreadAttribute[]? Ignored = null)
{
    /// <summary>Attributes the file declared that this importer did not read.</summary>
    public UnreadAttribute[] IgnoredOrEmpty => Ignored ?? [];

    /// <summary>Each node's world transform: its local composed with every ancestor's, child first.</summary>
    /// <remarks>Row-vector form, like every matrix here: <c>world = local * parent * grandparent ...</c>.</remarks>
    public Matrix4x4[] WorldTransforms()
    {
        var world = new Matrix4x4[Nodes.Length];
        for (var i = 0; i < Nodes.Length; i++)
        {
            world[i] = Nodes[i].LocalTransform;
            for (var p = Nodes[i].ParentIndex; p >= 0; p = Nodes[p].ParentIndex) world[i] *= Nodes[p].LocalTransform;
        }

        return world;
    }

    /// <summary>
    /// The whole model as one mesh per material, every node's primitives moved to world space (then by
    /// <paramref name="root"/>, if given).
    /// </summary>
    /// <remarks>
    /// The arrangement an instanced prop wants: one mesh an instance can place, split only where the
    /// material changes. Materials are grouped by identity, in the order they are first met. See
    /// <see cref="Blix.Assets.MeshDataExtensions.Merge"/> for what a merge keeps.
    /// </remarks>
    public IReadOnlyList<(PbrMaterial? Material, Blix.Assets.MeshData Mesh)> Merged(Matrix4x4? root = null, string name = "merged")
    {
        var world = WorldTransforms();
        var groups = new List<(PbrMaterial? Material, List<(Blix.Assets.MeshData, Matrix4x4)> Parts)>();
        for (var i = 0; i < Nodes.Length; i++)
        {
            var at = root is { } r ? world[i] * r : world[i];
            foreach (var primitive in Nodes[i].Primitives)
            {
                var group = groups.FindIndex(g => ReferenceEquals(g.Material, primitive.Material));
                if (group < 0) { groups.Add((primitive.Material, new())); group = groups.Count - 1; }
                groups[group].Parts.Add((primitive.Mesh, at));
            }
        }

        return groups
            .Select((g, n) => (g.Material, Blix.Assets.MeshDataExtensions.Merge(g.Parts, $"{name}.{g.Material?.Name ?? n.ToString()}")))
            .ToArray();
    }

    // First node whose name matches (ordinal). Null if absent — callers decide
    // whether a missing part is fatal.
    public GltfNode? Find(string name) => Array.Find(Nodes, n => n.Name == name);
}
