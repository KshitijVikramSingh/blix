using System.Numerics;
using Blix.Assets;
using SharpGLTF.Schema2;

namespace Blix.Recipes;

/// <summary>A glTF skin as the cook writes it: its joints parent-first, each with its inverse bind and its node.</summary>
/// <remarks>
/// Only the order and the file's own facts. Rest, offset and placement are runtime facts derived from the cooked
/// node graph when a model is read (<see cref="Blix.JointHierarchy"/>, in <c>ModelData</c>), so the cook does not
/// derive them.
/// </remarks>
internal static class GltfSkin
{
    /// <summary>
    /// <paramref name="skin"/>'s joints in parent-first order, and the source-joint-to-bone remap the skinned
    /// vertices are written through.
    /// </summary>
    /// <param name="nodeOfLogical">Each glTF logical node's index in the cooked node table.</param>
    /// <remarks>
    /// Inverse binds pass through untransposed: SharpGLTF returns System.Numerics row-vector matrices, which
    /// is exactly the engine's convention (F-016).
    /// </remarks>
    public static (BlixMeshSkin Skin, int[] OldToNew) Cook(Skin skin, int[] nodeOfLogical)
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

        var bones = new BlixMeshBone[n];
        for (var newIdx = 0; newIdx < n; newIdx++)
        {
            var oldIdx = orderNewToOld[newIdx];
            bones[newIdx] = new BlixMeshBone(
                joints[oldIdx].Name ?? $"bone_{newIdx}",
                parentOld[oldIdx] >= 0 ? oldToNew[parentOld[oldIdx]] : -1,
                ibmList[oldIdx],
                nodeOfLogical[joints[oldIdx].LogicalIndex]);
        }

        return (new BlixMeshSkin(bones), oldToNew);
    }
}
