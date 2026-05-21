using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Render;

public sealed record Mesh(
    string Name,
    VertexBufferHandle VertexBuffer,
    IndexBufferHandle IndexBuffer,
    int IndexCount,
    Bounds3 Bounds);
