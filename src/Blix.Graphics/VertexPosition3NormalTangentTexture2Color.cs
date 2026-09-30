namespace Blix.Graphics;

/// <summary>
/// Everything Blix reads from a static glTF vertex: position, normal, tangent, two texture
/// coordinates and a packed colour — 60 bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cooked static vertex.</b> A cooked mesh carries the whole vertex so the engine never has
/// to reopen a source to learn what its cook left out; a loader repacks it into the layout a caller's
/// pipeline declares. The tangent is authored or MikkTSpace's, never absent; the second set mirrors
/// the first and the colour is white where the source authored neither, as glTF's defaults say.
/// </para>
/// <para>
/// Tangent w is the bitangent's handedness, <c>cross(normal, tangent.xyz) * w</c>, as glTF defines it.
/// </para>
/// </remarks>
public readonly record struct VertexPosition3NormalTangentTexture2Color(
    GraphicsVector3 Position,
    GraphicsVector3 Normal,
    GraphicsVector4 Tangent,
    GraphicsVector2 TextureCoordinate,
    GraphicsVector2 TextureCoordinate1,
    uint PackedColor)
{
    /// <summary>Opaque white — what a vertex with no authored colour means.</summary>
    public const uint White = VertexPosition3NormalTextureColor.White;

    public static VertexLayout Layout { get; } = new(
        Stride: 14 * sizeof(float) + sizeof(uint),
        Attributes:
        [
            new VertexAttribute(Location: 0, VertexAttributeFormat.Float3, Offset: 0),
            new VertexAttribute(Location: 1, VertexAttributeFormat.Float3, Offset: 3 * sizeof(float)),
            new VertexAttribute(Location: 2, VertexAttributeFormat.Float4, Offset: 6 * sizeof(float)),
            new VertexAttribute(Location: 3, VertexAttributeFormat.Float2, Offset: 10 * sizeof(float)),
            new VertexAttribute(Location: 4, VertexAttributeFormat.Float2, Offset: 12 * sizeof(float)),
            new VertexAttribute(Location: 5, VertexAttributeFormat.UByte4Norm, Offset: 14 * sizeof(float)),
        ]);

    public static VertexBufferData CreateBufferData(
        IReadOnlyList<VertexPosition3NormalTangentTexture2Color> vertices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        return new VertexBufferData(new VertexBufferDescription(Layout, vertices.Count, usage), Pack(vertices));
    }

    public static byte[] Pack(IReadOnlyList<VertexPosition3NormalTangentTexture2Color> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        var packed = new byte[vertices.Count * Layout.Stride];
        for (var i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            var at = i * Layout.Stride;
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                packed.AsSpan(at, 14 * sizeof(float)));
            floats[0] = v.Position.X; floats[1] = v.Position.Y; floats[2] = v.Position.Z;
            floats[3] = v.Normal.X; floats[4] = v.Normal.Y; floats[5] = v.Normal.Z;
            floats[6] = v.Tangent.X; floats[7] = v.Tangent.Y; floats[8] = v.Tangent.Z; floats[9] = v.Tangent.W;
            floats[10] = v.TextureCoordinate.X; floats[11] = v.TextureCoordinate.Y;
            floats[12] = v.TextureCoordinate1.X; floats[13] = v.TextureCoordinate1.Y;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                packed.AsSpan(at + 14 * sizeof(float), sizeof(uint)), v.PackedColor);
        }

        return packed;
    }
}
