namespace Blix.Graphics;

// Position + normal + tangent + UV. The tangent is glTF's vec4 convention:
// xyz = tangent direction, w = handedness sign for the bitangent
// (bitangent = cross(normal, tangent.xyz) * w). Used by renderers that want a
// real per-vertex TBN for normal mapping instead of synthesizing one from
// screen-space derivatives. 48-byte stride; locations match the lit shader
// (0 = position, 1 = normal, 2 = tangent, 3 = uv).
public readonly record struct VertexPosition3NormalTangentTexture(
    GraphicsVector3 Position,
    GraphicsVector3 Normal,
    GraphicsVector4 Tangent,
    GraphicsVector2 TextureCoordinate)
{
    private const int FloatsPerVertex = 12; // 3 + 3 + 4 + 2

    public static VertexLayout Layout { get; } = new(
        Stride: FloatsPerVertex * sizeof(float),
        Attributes:
        [
            new VertexAttribute(Location: 0, VertexAttributeFormat.Float3, Offset: 0),
            new VertexAttribute(Location: 1, VertexAttributeFormat.Float3, Offset: 3 * sizeof(float)),
            new VertexAttribute(Location: 2, VertexAttributeFormat.Float4, Offset: 6 * sizeof(float)),
            new VertexAttribute(Location: 3, VertexAttributeFormat.Float2, Offset: 10 * sizeof(float))
        ]);

    public static VertexBufferData CreateBufferData(
        IReadOnlyList<VertexPosition3NormalTangentTexture> vertices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static)
    {
        return new VertexBufferData(
            new VertexBufferDescription(Layout, vertices.Count, usage),
            Pack(vertices));
    }

    public static byte[] Pack(IReadOnlyList<VertexPosition3NormalTangentTexture> vertices)
    {
        var packed = new byte[vertices.Count * Layout.Stride];
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(packed);

        for (var index = 0; index < vertices.Count; index++)
        {
            var vertex = vertices[index];
            var offset = index * FloatsPerVertex;

            floats[offset] = vertex.Position.X;
            floats[offset + 1] = vertex.Position.Y;
            floats[offset + 2] = vertex.Position.Z;
            floats[offset + 3] = vertex.Normal.X;
            floats[offset + 4] = vertex.Normal.Y;
            floats[offset + 5] = vertex.Normal.Z;
            floats[offset + 6] = vertex.Tangent.X;
            floats[offset + 7] = vertex.Tangent.Y;
            floats[offset + 8] = vertex.Tangent.Z;
            floats[offset + 9] = vertex.Tangent.W;
            floats[offset + 10] = vertex.TextureCoordinate.X;
            floats[offset + 11] = vertex.TextureCoordinate.Y;
        }

        return packed;
    }
}
