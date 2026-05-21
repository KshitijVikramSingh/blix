namespace Blix.Graphics;

public readonly record struct VertexPositionTexture(GraphicsVector2 Position, GraphicsVector2 TextureCoordinate)
{
    public static VertexLayout Layout { get; } = new(
        Stride: 4 * sizeof(float),
        Attributes:
        [
            new VertexAttribute(Location: 0, VertexAttributeFormat.Float2, Offset: 0),
            new VertexAttribute(Location: 1, VertexAttributeFormat.Float2, Offset: 2 * sizeof(float))
        ]);

    public static VertexBufferData CreateBufferData(
        IReadOnlyList<VertexPositionTexture> vertices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static)
    {
        return new VertexBufferData(
            new VertexBufferDescription(Layout, vertices.Count, usage),
            Pack(vertices));
    }

    public static byte[] Pack(IReadOnlyList<VertexPositionTexture> vertices)
    {
        var packed = new byte[vertices.Count * Layout.Stride];
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(packed);

        for (var index = 0; index < vertices.Count; index++)
        {
            var vertex = vertices[index];
            var offset = index * 4;

            floats[offset] = vertex.Position.X;
            floats[offset + 1] = vertex.Position.Y;
            floats[offset + 2] = vertex.TextureCoordinate.X;
            floats[offset + 3] = vertex.TextureCoordinate.Y;
        }

        return packed;
    }
}
