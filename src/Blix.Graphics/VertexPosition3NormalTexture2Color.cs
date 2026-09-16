namespace Blix.Graphics;

/// <summary>
/// Position, normal, TWO texture coordinates and a packed vertex colour — 44 bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>A second UV set is per-vertex data, so there is no way to carry it but a wider vertex.</b>
/// glTF lets every texture on a material name the set it samples, and a material that names set 1
/// while the mesh only uploads set 0 does not fail — it samples the wrong coordinates and produces a
/// picture that is merely wrong.
/// </para>
/// <para>
/// <b>One layout rather than a second variant.</b> The studio's static pipeline declares this and
/// anything without a second set gets its first one copied into it, exactly as a mesh without
/// <c>COLOR_0</c> gets white. That is what keeps a stage that now handles colour, cutouts, blending
/// and two UV sets down to one static pipeline plus one for blending.
/// </para>
/// <para>
/// <b>Full-width floats, unlike the colour.</b> The colour packed to four bytes because it is
/// greyscale occlusion multiplied into albedo, where a 1/255 step is invisible. A texture coordinate
/// is an address, and quantising an address moves the sample.
/// </para>
/// </remarks>
public readonly record struct VertexPosition3NormalTexture2Color(
    GraphicsVector3 Position,
    GraphicsVector3 Normal,
    GraphicsVector2 TextureCoordinate,
    GraphicsVector2 TextureCoordinate1,
    uint PackedColor)
{
    /// <summary>Opaque white — what a vertex with no authored colour means.</summary>
    public const uint White = VertexPosition3NormalTextureColor.White;

    public static VertexLayout Layout { get; } = new(
        Stride: 10 * sizeof(float) + sizeof(uint),
        Attributes:
        [
            new VertexAttribute(Location: 0, VertexAttributeFormat.Float3, Offset: 0),
            new VertexAttribute(Location: 1, VertexAttributeFormat.Float3, Offset: 3 * sizeof(float)),
            new VertexAttribute(Location: 2, VertexAttributeFormat.Float2, Offset: 6 * sizeof(float)),
            new VertexAttribute(Location: 3, VertexAttributeFormat.Float2, Offset: 8 * sizeof(float)),
            new VertexAttribute(Location: 4, VertexAttributeFormat.UByte4Norm, Offset: 10 * sizeof(float)),
        ]);

    public static VertexBufferData CreateBufferData(
        IReadOnlyList<VertexPosition3NormalTexture2Color> vertices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static)
        => new(new VertexBufferDescription(Layout, vertices.Count, usage), Pack(vertices));

    public static byte[] Pack(IReadOnlyList<VertexPosition3NormalTexture2Color> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        var packed = new byte[vertices.Count * Layout.Stride];
        for (var i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            var at = i * Layout.Stride;
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                packed.AsSpan(at, 10 * sizeof(float)));
            floats[0] = v.Position.X; floats[1] = v.Position.Y; floats[2] = v.Position.Z;
            floats[3] = v.Normal.X; floats[4] = v.Normal.Y; floats[5] = v.Normal.Z;
            floats[6] = v.TextureCoordinate.X; floats[7] = v.TextureCoordinate.Y;
            floats[8] = v.TextureCoordinate1.X; floats[9] = v.TextureCoordinate1.Y;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                packed.AsSpan(at + 10 * sizeof(float), sizeof(uint)), v.PackedColor);
        }

        return packed;
    }

    /// <summary>Widens vertices that carry one UV set and no colour: set 1 mirrors set 0, colour is white.</summary>
    /// <remarks>
    /// <b>Mirroring rather than zeroing the second set.</b> A material naming set 1 on a mesh that
    /// has only set 0 is malformed, and sampling (0,0) would put the whole surface on one texel —
    /// a flat wash that reads as a broken texture rather than as a broken asset. The first set is
    /// the only defensible guess.
    /// </remarks>
    public static VertexPosition3NormalTexture2Color[] From(
        IReadOnlyList<VertexPosition3NormalTexture> vertices, uint colour = White)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        var widened = new VertexPosition3NormalTexture2Color[vertices.Count];
        for (var i = 0; i < vertices.Count; i++)
        {
            widened[i] = new VertexPosition3NormalTexture2Color(
                vertices[i].Position, vertices[i].Normal,
                vertices[i].TextureCoordinate, vertices[i].TextureCoordinate, colour);
        }

        return widened;
    }
}
