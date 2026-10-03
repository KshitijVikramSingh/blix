using System.Numerics;
using Blix.Assets;

namespace Blix.Recipes;

/// <summary>A primitive's LOD levels split into clusters, each level's index list reordered so a cluster is one run.</summary>
/// <remarks>
/// <para>
/// <b>Clusters as index runs, not meshlet arrays.</b> meshoptimizer builds meshlets as a local vertex list plus
/// byte triangles, which is what a mesh shader reads; this Mac has no mesh shaders. Mapped back through their
/// vertex lists, a level's meshlets become the same triangles in a new order, so a cluster is a sub-range of
/// the index buffer every renderer already uploads, and drawing, culling or tracing one needs nothing else.
/// </para>
/// <para>
/// <b>Sizes.</b> At most <see cref="MaxVertices"/> vertices and <see cref="MaxTriangles"/> triangles, the sizes
/// meshlet culling is usually measured at; the cone weight trades a little cluster compactness for normal cones
/// tight enough to cull back-facing clusters. Both are where the measurement of cluster culling (stage 1d)
/// starts, not where it must end.
/// </para>
/// </remarks>
internal static class MeshClusters
{
    public const int MaxVertices = 64;
    public const int MaxTriangles = 124;
    public const float ConeWeight = 0.25f;

    /// <summary>Every level of <paramref name="lods"/>, clustered, over <paramref name="mesh"/>'s vertices.</summary>
    /// <exception cref="InvalidOperationException">The meshoptimizer native is not beside the cook.</exception>
    public static IReadOnlyList<BlixMeshLod> Clustered(IReadOnlyList<BlixMeshLod> lods, MeshData mesh)
    {
        if (!MeshoptNative.Available)
        {
            throw new InvalidOperationException(
                $"Clustering needs the meshoptimizer native, and {MeshoptNative.FileName} is not beside the cook. "
                + "It is built by the BuildMeshopt target in Blix.Recipes.csproj.");
        }

        if (mesh.VertexCount == 0) return lods;
        var stride = mesh.Layout.Stride;
        var position = Blix.Graphics.VertexSemantics.Of(mesh.Layout)?.Position ?? 0;
        var positions = new float[mesh.VertexCount * 3];
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var at = v * stride + position;
            positions[v * 3 + 0] = BitConverter.ToSingle(mesh.VertexBytes, at);
            positions[v * 3 + 1] = BitConverter.ToSingle(mesh.VertexBytes, at + 4);
            positions[v * 3 + 2] = BitConverter.ToSingle(mesh.VertexBytes, at + 8);
        }

        return lods.Select(lod => Clustered(lod, positions, mesh.VertexCount)).ToArray();
    }

    private static BlixMeshLod Clustered(BlixMeshLod lod, float[] positions, int vertexCount)
    {
        var indices = lod.Indices32 ?? Array.ConvertAll(lod.Indices16!, i => (uint)i);
        if (indices.Length == 0) return lod;

        var (reordered, runs) = MeshoptNative.BuildClusters(indices, positions, vertexCount, MaxVertices, MaxTriangles, ConeWeight);
        var clusters = new MeshCluster[runs.Length];
        for (var c = 0; c < runs.Length; c++)
        {
            var (first, count) = runs[c];
            var run = reordered.AsSpan(first, count);
            var bounds = MeshoptNative.Bounds(run, positions, vertexCount);
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var i in run)
            {
                var p = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            clusters[c] = new MeshCluster(first, count, bounds.Center, bounds.Radius, min, max,
                bounds.ConeApex, bounds.ConeAxis, bounds.ConeCutoff);
        }

        return lod.Indices32 is not null
            ? lod with { Indices32 = reordered, Clusters = clusters }
            : lod with { Indices16 = Array.ConvertAll(reordered, i => (ushort)i), Clusters = clusters };
    }
}
