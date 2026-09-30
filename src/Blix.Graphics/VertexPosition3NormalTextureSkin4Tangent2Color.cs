namespace Blix.Graphics;

/// <summary>
/// Everything Blix reads from a skinned glTF vertex — the 80-byte skinned layout followed by a second
/// texture coordinate and a packed colour, 92 bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cooked skinned vertex.</b> A cooked mesh carries the whole vertex so no load has to reopen
/// the source; a loader repacks it into the layout its pipeline declares. The first 80 bytes are
/// exactly <see cref="VertexPosition3NormalTextureSkin4Tangent"/>, so that repack is a prefix.
/// </para>
/// <para>
/// Bone indices address the owning skin's bones in their parent-first order. The tangent is the
/// authored or MikkTSpace frame; the second set mirrors the first and the colour is white where the
/// source authored neither.
/// </para>
/// </remarks>
public readonly record struct VertexPosition3NormalTextureSkin4Tangent2Color(
    GraphicsVector3 Position,
    GraphicsVector3 Normal,
    GraphicsVector2 TextureCoordinate,
    GraphicsVector4 BoneIndices,
    GraphicsVector4 BoneWeights,
    GraphicsVector4 Tangent,
    GraphicsVector2 TextureCoordinate1,
    uint PackedColor)
{
    public static VertexLayout Layout { get; } = new(
        Stride: 22 * sizeof(float) + sizeof(uint),
        Attributes:
        [
            new VertexAttribute(Location: 0, VertexAttributeFormat.Float3, Offset: 0),
            new VertexAttribute(Location: 1, VertexAttributeFormat.Float3, Offset: 3 * sizeof(float)),
            new VertexAttribute(Location: 2, VertexAttributeFormat.Float2, Offset: 6 * sizeof(float)),
            new VertexAttribute(Location: 3, VertexAttributeFormat.Float4, Offset: 8 * sizeof(float)),
            new VertexAttribute(Location: 4, VertexAttributeFormat.Float4, Offset: 12 * sizeof(float)),
            new VertexAttribute(Location: 5, VertexAttributeFormat.Float4, Offset: 16 * sizeof(float)),
            new VertexAttribute(Location: 6, VertexAttributeFormat.Float2, Offset: 20 * sizeof(float)),
            new VertexAttribute(Location: 7, VertexAttributeFormat.UByte4Norm, Offset: 22 * sizeof(float)),
        ]);

    public static byte[] Pack(IReadOnlyList<VertexPosition3NormalTextureSkin4Tangent2Color> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        var packed = new byte[vertices.Count * Layout.Stride];
        for (var i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            var at = i * Layout.Stride;
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(packed.AsSpan(at, 22 * sizeof(float)));
            floats[0] = v.Position.X; floats[1] = v.Position.Y; floats[2] = v.Position.Z;
            floats[3] = v.Normal.X; floats[4] = v.Normal.Y; floats[5] = v.Normal.Z;
            floats[6] = v.TextureCoordinate.X; floats[7] = v.TextureCoordinate.Y;
            floats[8] = v.BoneIndices.X; floats[9] = v.BoneIndices.Y; floats[10] = v.BoneIndices.Z; floats[11] = v.BoneIndices.W;
            floats[12] = v.BoneWeights.X; floats[13] = v.BoneWeights.Y; floats[14] = v.BoneWeights.Z; floats[15] = v.BoneWeights.W;
            floats[16] = v.Tangent.X; floats[17] = v.Tangent.Y; floats[18] = v.Tangent.Z; floats[19] = v.Tangent.W;
            floats[20] = v.TextureCoordinate1.X; floats[21] = v.TextureCoordinate1.Y;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                packed.AsSpan(at + 22 * sizeof(float), sizeof(uint)), v.PackedColor);
        }

        return packed;
    }
}
