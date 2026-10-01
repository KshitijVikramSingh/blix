using System.Numerics;
using Blix.Assets;
using Blix.Graphics;

namespace Blix;

/// <summary>Device-independent facts derived from skinned vertex data and its skeleton.</summary>
public static class SkinningAnalysis
{
    /// <summary>Which bones carry at least one non-zero vertex weight.</summary>
    public static bool[] FindWeightedBones(
        Skeleton skeleton,
        IEnumerable<MeshData> meshes)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(meshes);

        var weighted = new bool[skeleton.BoneCount];
        foreach (var mesh in meshes)
        {
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

    /// <summary>
    /// Where <paramref name="mesh"/>'s vertices land under <paramref name="palette"/> (a skin's palette at
    /// rest), widened into <paramref name="min"/> and <paramref name="max"/>.
    /// </summary>
    /// <remarks>
    /// The skin sum itself — <c>sum(w * v * palette)</c> at the rest palette — rather than the mesh-space
    /// vertices under one transform: a file's rest pose need not be its bind pose, and only the skin
    /// knows where the rest pose puts them. A vertex with no weight is left out: glTF gives it no place.
    /// </remarks>
    public static void AccumulateRestBounds(
        IReadOnlyList<Matrix4x4> palette, MeshData mesh, ref Vector3 min, ref Vector3 max)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(mesh);
        var indexAttribute = Attribute(mesh.Layout, location: 3);
        var weightAttribute = Attribute(mesh.Layout, location: 4);

        var stride = mesh.Layout.Stride;
        for (var vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            var at = vertex * stride;
            var p = new Vector3(
                BitConverter.ToSingle(mesh.VertexBytes, at),
                BitConverter.ToSingle(mesh.VertexBytes, at + 4),
                BitConverter.ToSingle(mesh.VertexBytes, at + 8));
            var skinned = Vector3.Zero;
            var total = 0f;
            for (var influence = 0; indexAttribute >= 0 && weightAttribute >= 0 && influence < 4; influence++)
            {
                var weight = BitConverter.ToSingle(mesh.VertexBytes, at + weightAttribute + (influence * 4));
                if (weight <= 0f) continue;
                var bone = (int)BitConverter.ToSingle(mesh.VertexBytes, at + indexAttribute + (influence * 4));
                if ((uint)bone >= (uint)palette.Count) continue;
                skinned += Vector3.Transform(p, palette[bone]) * weight;
                total += weight;
            }

            if (total <= 0f) continue;
            var world = skinned / total;
            min = Vector3.Min(min, world);
            max = Vector3.Max(max, world);
        }
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
