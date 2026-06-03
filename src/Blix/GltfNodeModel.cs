using System.Numerics;

namespace Blix;

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

public sealed record GltfNodeModel(GltfNode[] Nodes)
{
    // First node whose name matches (ordinal). Null if absent — callers decide
    // whether a missing part is fatal.
    public GltfNode? Find(string name) => Array.Find(Nodes, n => n.Name == name);
}
