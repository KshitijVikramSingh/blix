using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Assets;

// One LOD level's index buffer (over the mesh's shared vertex buffer).
// Exactly one of the two arrays is set, matching MeshData.IndexFormat.
public sealed record MeshLod(ushort[]? Indices16, uint[]? Indices32)
{
    public int IndexCount => Indices32?.Length ?? Indices16!.Length;
}

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
