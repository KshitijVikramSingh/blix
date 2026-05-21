namespace Blix.Graphics;

public readonly record struct VertexPosition3TextureColor(
    GraphicsVector3 Position,
    GraphicsVector2 TextureCoordinate,
    GraphicsColor Color)
{
    public static VertexLayout Layout { get; } = new(
        Stride: 9 * sizeof(float),
        Attributes:
        [
            new VertexAttribute(Location: 0, VertexAttributeFormat.Float3, Offset: 0),
            new VertexAttribute(Location: 1, VertexAttributeFormat.Float2, Offset: 3 * sizeof(float)),
            new VertexAttribute(Location: 2, VertexAttributeFormat.Float4, Offset: 5 * sizeof(float))
        ]);

    public static VertexBufferData CreateBufferData(
        IReadOnlyList<VertexPosition3TextureColor> vertices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static)
    {
        return new VertexBufferData(
            new VertexBufferDescription(Layout, vertices.Count, usage),
            Pack(vertices));
    }

    public static byte[] Pack(IReadOnlyList<VertexPosition3TextureColor> vertices)
    {
        var packed = new byte[vertices.Count * Layout.Stride];
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(packed);

        for (var index = 0; index < vertices.Count; index++)
        {
            var vertex = vertices[index];
            var offset = index * 9;

            floats[offset] = vertex.Position.X;
            floats[offset + 1] = vertex.Position.Y;
            floats[offset + 2] = vertex.Position.Z;
            floats[offset + 3] = vertex.TextureCoordinate.X;
            floats[offset + 4] = vertex.TextureCoordinate.Y;
            floats[offset + 5] = vertex.Color.Red;
            floats[offset + 6] = vertex.Color.Green;
            floats[offset + 7] = vertex.Color.Blue;
            floats[offset + 8] = vertex.Color.Alpha;
        }

        return packed;
    }
}
