namespace Blix.Graphics;

/// <summary>
/// Position, normal, texture coordinate and a packed vertex colour — 36 bytes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exists because 49 primitives in this tree ship a <c>COLOR_0</c> channel that was thrown
/// away at import.</b> Sampled from the content it is not colour at all: greyscale, 0.0 to 1.0,
/// 152 distinct values on one tree trunk and 51 on a blade of grass, with the same tree's leaf card
/// uniformly white. That is <b>baked ambient occlusion</b> — a blade dark where it meets the
/// ground, bark dark in its crevices — and it was being discarded on every piece of scatter RTSGame
/// draws.
/// </para>
/// <para>
/// <b>Four bytes, not sixteen.</b> UByte4Norm, the same packing the ImGui vertex already uses.
/// <b>This does discard levels and it was measured rather than waved away:</b> the kit authors
/// COLOR_0 as float (21 primitives) and normalised ushort (12), never as bytes, and quantising
/// CommonTree_1's trunk takes it from 152 distinct authored values to 82. That is the right trade
/// here and only here — the channel is greyscale occlusion multiplied into albedo, where a 1/255
/// step is not visible on any surface, and the alternative is 12 more bytes a vertex on exactly the
/// meshes drawn thousands of times a frame. A channel carrying real colour would deserve the
/// question again.
/// </para>
/// <para>
/// <b>A separate type rather than a wider <see cref="VertexPosition3NormalTexture"/>.</b> Thirty
/// files name that layout and six applications pin it in a pipeline they created; widening it would
/// make every one of them read a 32-byte stride across 36-byte vertices, which is not a compile
/// error and not a crash — it is a mesh that comes out wrong.
/// </para>
/// </remarks>
public readonly record struct VertexPosition3NormalTextureColor(
    GraphicsVector3 Position,
    GraphicsVector3 Normal,
    GraphicsVector2 TextureCoordinate,
    uint PackedColor)
{
    /// <summary>Opaque white — what a vertex with no authored colour means.</summary>
    /// <remarks>
    /// White rather than zero, because this channel multiplies. A missing colour has to be the
    /// identity or an asset that never had one would render black, which is the loudest possible
    /// way to get a default wrong.
    /// </remarks>
    public const uint White = 0xFFFFFFFFu;

    public static VertexLayout Layout { get; } = new(
        Stride: 8 * sizeof(float) + sizeof(uint),
        Attributes:
        [
            new VertexAttribute(Location: 0, VertexAttributeFormat.Float3, Offset: 0),
            new VertexAttribute(Location: 1, VertexAttributeFormat.Float3, Offset: 3 * sizeof(float)),
            new VertexAttribute(Location: 2, VertexAttributeFormat.Float2, Offset: 6 * sizeof(float)),
            new VertexAttribute(Location: 3, VertexAttributeFormat.UByte4Norm, Offset: 8 * sizeof(float)),
        ]);

    /// <summary>Packs RGBA in 0..1 into the wire order the UByte4Norm attribute reads.</summary>
    public static uint Pack(float r, float g, float b, float a) =>
        (uint)(Math.Clamp(r, 0f, 1f) * 255f + 0.5f)
        | ((uint)(Math.Clamp(g, 0f, 1f) * 255f + 0.5f) << 8)
        | ((uint)(Math.Clamp(b, 0f, 1f) * 255f + 0.5f) << 16)
        | ((uint)(Math.Clamp(a, 0f, 1f) * 255f + 0.5f) << 24);

    public static VertexBufferData CreateBufferData(
        IReadOnlyList<VertexPosition3NormalTextureColor> vertices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static)
    {
        return new VertexBufferData(
            new VertexBufferDescription(Layout, vertices.Count, usage),
            Pack(vertices));
    }

    public static byte[] Pack(IReadOnlyList<VertexPosition3NormalTextureColor> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        var packed = new byte[vertices.Count * Layout.Stride];
        for (var i = 0; i < vertices.Count; i++)
        {
            var v = vertices[i];
            var at = i * Layout.Stride;
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
                packed.AsSpan(at, 8 * sizeof(float)));
            floats[0] = v.Position.X; floats[1] = v.Position.Y; floats[2] = v.Position.Z;
            floats[3] = v.Normal.X; floats[4] = v.Normal.Y; floats[5] = v.Normal.Z;
            floats[6] = v.TextureCoordinate.X; floats[7] = v.TextureCoordinate.Y;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                packed.AsSpan(at + 8 * sizeof(float), sizeof(uint)), v.PackedColor);
        }

        return packed;
    }

    /// <summary>Widens vertices that have no colour of their own, which all become white.</summary>
    public static VertexPosition3NormalTextureColor[] From(
        IReadOnlyList<VertexPosition3NormalTexture> vertices, uint colour = White)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        var widened = new VertexPosition3NormalTextureColor[vertices.Count];
        for (var i = 0; i < vertices.Count; i++)
        {
            widened[i] = new VertexPosition3NormalTextureColor(
                vertices[i].Position, vertices[i].Normal, vertices[i].TextureCoordinate, colour);
        }

        return widened;
    }
}
