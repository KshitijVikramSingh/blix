using System.Numerics;
using Blix;
using SharpGLTF.Schema2;

namespace Blix.Recipes;

/// <summary>A glTF skin's joints as the cook records them: parent-first bones, and the order that came from.</summary>
internal static class GltfSkeleton
{
    /// <summary>
    /// A skin's joints in parent-first order: as bones (rest and offset from the scene graph), their inverse
    /// binds index for index, the source-joint-to-bone remap, and where the joints hang.
    /// </summary>
    /// <remarks>
    /// Inverse binds pass through untransposed: SharpGLTF returns System.Numerics row-vector matrices, which
    /// is exactly the engine's convention (F-016).
    /// </remarks>
    public static (Bone[] Bones, Matrix4x4[] InverseBinds, int[] OldToNew, Matrix4x4 Placement) Build(Skin skin)
    {
        var joints = skin.Joints;
        var ibmList = skin.InverseBindMatrices;
        var n = joints.Count;
        // No inverseBindMatrices accessor means each is the identity (glTF 2.0 §5.27): the joints were
        // bound where they stand.
        if (ibmList.Count == 0) ibmList = Enumerable.Repeat(Matrix4x4.Identity, n).ToArray();
        // glTF 2.0 §5.27: the accessor MUST have at least as many elements as there are joints; the
        // joints consume the first n, in order, and any beyond are legal and unread.
        if (ibmList.Count < n)
        {
            throw new InvalidOperationException(
                $"Skin has {n} joints but {ibmList.Count} inverse-bind matrices; glTF requires at least one per joint.");
        }

        var jointToIndex = new Dictionary<Node, int>();
        for (var i = 0; i < n; i++) jointToIndex[joints[i]] = i;

        // Each joint's parent in the joints list: up the VisualParent chain to the next joint, skipping
        // non-joint ancestors (an armature root that is not itself a joint, say).
        var parentOld = new int[n];
        for (var i = 0; i < n; i++)
        {
            var p = joints[i].VisualParent;
            while (p is not null && !jointToIndex.ContainsKey(p))
            {
                p = p.VisualParent;
            }
            parentOld[i] = p is null ? -1 : jointToIndex[p];
        }

        // Topological sort: Visit places a joint's parent before the joint, so every parent precedes its
        // children.
        var visited = new bool[n];
        var orderNewToOld = new List<int>(n);
        void Visit(int i)
        {
            if (visited[i]) return;
            visited[i] = true;
            if (parentOld[i] >= 0) Visit(parentOld[i]);
            orderNewToOld.Add(i);
        }
        for (var i = 0; i < n; i++) Visit(i);

        var oldToNew = new int[n];
        for (var newIdx = 0; newIdx < orderNewToOld.Count; newIdx++)
        {
            oldToNew[orderNewToOld[newIdx]] = newIdx;
        }

        var names = new string[n];
        var parents = new int[n];
        var inverseBinds = new Matrix4x4[n];
        var jointNodes = new int[n];
        for (var newIdx = 0; newIdx < n; newIdx++)
        {
            var oldIdx = orderNewToOld[newIdx];
            names[newIdx] = joints[oldIdx].Name ?? $"bone_{newIdx}";
            parents[newIdx] = parentOld[oldIdx] >= 0 ? oldToNew[parentOld[oldIdx]] : -1;
            inverseBinds[newIdx] = ibmList[oldIdx];
            jointNodes[newIdx] = joints[oldIdx].LogicalIndex;
        }

        // Rest, offset and placement from the scene graph, as the cooked reader derives them: one rule, in the engine.
        var nodes = skin.LogicalParent.LogicalNodes;
        var hierarchy = JointHierarchy.Resolve(
            names, parents, jointNodes,
            j => nodes[j].VisualParent?.LogicalIndex ?? -1,
            j => nodes[j].LocalMatrix,
            j => nodes[j].WorldMatrix);
        return (hierarchy.Bones.ToArray(), inverseBinds, oldToNew, hierarchy.Placement);
    }
}
