namespace Blix.Graphics;

public sealed record VertexBufferDescription(
    VertexLayout Layout,
    int VertexCount,
    GraphicsBufferUsage Usage);

public sealed record VertexBufferData(VertexBufferDescription Description, byte[] Bytes);
