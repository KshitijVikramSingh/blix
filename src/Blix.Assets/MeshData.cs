using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Assets;

// One LOD level's index buffer (over the mesh's shared vertex buffer), plus
// the world-space geometric error decimating to it introduced (0 for LOD0).
// Exactly one of the two arrays is set, matching MeshData.IndexFormat.
// Clusters, when the cook made them, tile the index list: see MeshCluster.
public sealed record MeshLod(ushort[]? Indices16, uint[]? Indices32, float Error = 0f, IReadOnlyList<MeshCluster>? Clusters = null)
{
    public int IndexCount => Indices32?.Length ?? Indices16!.Length;
}

/// <summary>A cluster of one LOD level's triangles: a contiguous run of its index list, and where that run is.</summary>
/// <remarks>
/// <para>
/// The cook splits each level into clusters of at most a few dozen vertices and about a hundred triangles
/// (meshoptimizer's meshlets) and reorders the level's index list so each cluster's triangles are one run,
/// so a cluster is drawn as a sub-range of the buffers already uploaded. The runs tile the list exactly, in
/// order; the triangles are the level's own, each with its winding.
/// </para>
/// <para>
/// Bounds are mesh space. <see cref="Center"/> and <see cref="Radius"/> bound every vertex of the cluster,
/// as do <see cref="Min"/> and <see cref="Max"/>. The normal cone is meshoptimizer's: seen from a point
/// <c>p</c>, every triangle faces away when <c>dot(normalize(ConeApex - p), ConeAxis) &gt;= ConeCutoff</c>;
/// a cutoff of 1 or more means the cone says nothing (the cluster faces too many ways to cull).
/// </para>
/// </remarks>
public readonly record struct MeshCluster(
    int FirstIndex, int IndexCount,
    System.Numerics.Vector3 Center, float Radius,
    System.Numerics.Vector3 Min, System.Numerics.Vector3 Max,
    System.Numerics.Vector3 ConeApex, System.Numerics.Vector3 ConeAxis, float ConeCutoff);

public sealed record MeshData(
    string Name,
    byte[] VertexBytes,
    ushort[] Indices,
    VertexLayout Layout,
    Bounds3 Bounds,
    // 32-bit index data. When non-null this is the authoritative index
    // buffer and `Indices` is empty -- consumers branch on IndexFormat to
    // decide which array + which CreateIndexBuffer overload to use. Used
    // by glTF assets whose primitives exceed 65535 vertices (Khronos
    // Sponza Modern's curtains pack is the canonical case).
    uint[]? Indices32 = null,
    // Full LOD chain (Lods[0] == the Indices/Indices32 above; coarser after).
    // Null for the runtime glTF-import path (single detail level); populated
    // from the cooked .blixmesh. Consumers that don't do LOD ignore it.
    IReadOnlyList<MeshLod>? Lods = null)
{
    public int VertexCount => Layout.Stride == 0 ? 0 : VertexBytes.Length / Layout.Stride;

    public IndexFormat IndexFormat => Indices32 is null ? IndexFormat.UInt16 : IndexFormat.UInt32;

    public int IndexCount => Indices32?.Length ?? Indices.Length;
}
