using Blix.Graphics;

namespace Blix;

/// <summary>Device-independent facts derived from skinned vertex data and its skeleton.</summary>
public static class SkinningAnalysis
{
    /// <summary>Which bones carry at least one non-zero vertex weight.</summary>
    public static bool[] FindWeightedBones(
        Skeleton skeleton,
        IReadOnlyList<GltfPrimitive> primitives)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(primitives);

        var weighted = new bool[skeleton.BoneCount];
        foreach (var primitive in primitives)
        {
            var mesh = primitive.Mesh;
            var indexAttribute = Attribute(mesh.Layout, location: 3);
            var weightAttribute = Attribute(mesh.Layout, location: 4);
            if (indexAttribute < 0 || weightAttribute < 0) continue;

            var stride = mesh.Layout.Stride;
            for (var vertex = 0; vertex < mesh.VertexCount; vertex++)
            {
                var at = vertex * stride;
                for (var influence = 0; influence < 4; influence++)
                {
                    var weight = BitConverter.ToSingle(
                        mesh.VertexBytes, at + weightAttribute + (influence * 4));
                    if (weight <= 0f) continue;

                    var bone = (int)BitConverter.ToSingle(
                        mesh.VertexBytes, at + indexAttribute + (influence * 4));
                    if ((uint)bone < (uint)weighted.Length) weighted[bone] = true;
                }
            }
        }

        return weighted;
    }

    /// <summary>Adds every ancestor needed to draw weighted bone chains continuously.</summary>
    public static bool[] IncludeAncestors(Skeleton skeleton, IReadOnlyList<bool> weighted)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(weighted);

        var hierarchy = new bool[skeleton.BoneCount];
        for (var i = 0; i < hierarchy.Length && i < weighted.Count; i++) hierarchy[i] = weighted[i];

        for (var i = hierarchy.Length - 1; i >= 0; i--)
        {
            if (!hierarchy[i]) continue;
            var parent = skeleton.Bones[i].ParentIndex;
            if (parent >= 0) hierarchy[parent] = true;
        }

        return hierarchy;
    }

    private static int Attribute(VertexLayout layout, int location)
    {
        foreach (var attribute in layout.Attributes)
        {
            if (attribute.Location == location) return attribute.Offset;
        }

        return -1;
    }
}
