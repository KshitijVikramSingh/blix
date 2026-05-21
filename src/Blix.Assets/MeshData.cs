using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Assets;

public sealed record MeshData(
    string Name,
    byte[] VertexBytes,
    ushort[] Indices,
    VertexLayout Layout,
    Bounds3 Bounds)
{
    public int VertexCount => Layout.Stride == 0 ? 0 : VertexBytes.Length / Layout.Stride;
}
