using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Assets;

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
    uint[]? Indices32 = null)
{
    public int VertexCount => Layout.Stride == 0 ? 0 : VertexBytes.Length / Layout.Stride;

    public IndexFormat IndexFormat => Indices32 is null ? IndexFormat.UInt16 : IndexFormat.UInt32;

    public int IndexCount => Indices32?.Length ?? Indices.Length;
}
