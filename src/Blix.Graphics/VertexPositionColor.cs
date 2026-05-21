namespace Blix.Graphics;

public readonly record struct VertexPositionColor(GraphicsVector2 Position, GraphicsColor Color)
{
    public static VertexLayout Layout { get; } = new(
        Stride: 6 * sizeof(float),
        Attributes:
        [
            new VertexAttribute(Location: 0, VertexAttributeFormat.Float2, Offset: 0),
            new VertexAttribute(Location: 1, VertexAttributeFormat.Float4, Offset: 2 * sizeof(float))
        ]);

    public static VertexBufferData CreateBufferData(
        IReadOnlyList<VertexPositionColor> vertices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static)
    {
        return new VertexBufferData(
            new VertexBufferDescription(Layout, vertices.Count, usage),
            Pack(vertices));
    }

    public static byte[] Pack(IReadOnlyList<VertexPositionColor> vertices)
    {
        var packed = new byte[vertices.Count * Layout.Stride];
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(packed);

        for (var index = 0; index < vertices.Count; index++)
        {
            var vertex = vertices[index];
            var offset = index * 6;

            floats[offset] = vertex.Position.X;
            floats[offset + 1] = vertex.Position.Y;
            floats[offset + 2] = vertex.Color.Red;
            floats[offset + 3] = vertex.Color.Green;
            floats[offset + 4] = vertex.Color.Blue;
            floats[offset + 5] = vertex.Color.Alpha;
        }

        return packed;
    }
}
