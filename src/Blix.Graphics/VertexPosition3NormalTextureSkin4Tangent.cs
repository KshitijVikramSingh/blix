namespace Blix.Graphics;

// Skinned-mesh vertex layout with explicit per-vertex tangent: position + normal +
// texcoord + bone indices/weights (the existing skinned layout) plus a vec4 tangent
// at location 5. The W component of the tangent is glTF's bitangent-sign convention
// (+1 or -1) -- the bitangent is reconstructed in the vertex shader as
// `cross(N, T.xyz) * T.w`. A tangent of (0, 0, 0, 0) signals "no tangent available";
// shaders fall back to dFdx/dFdy synthesis (or just zero, depending on caller).
//
// Stride grows from 16 floats (Skin4 baseline) to 20 floats. Backwards-compatible:
// shaders that don't declare an `aTangent` attribute simply ignore it; the
// per-attribute strides come from the layout, not the vertex shader.
public readonly record struct VertexPosition3NormalTextureSkin4Tangent(
    GraphicsVector3 Position,
    GraphicsVector3 Normal,
    GraphicsVector2 TextureCoordinate,
    GraphicsVector4 BoneIndices,
    GraphicsVector4 BoneWeights,
    GraphicsVector4 Tangent)
{
    public static VertexLayout Layout { get; } = new(
        Stride: 20 * sizeof(float),
        Attributes:
        [
            new VertexAttribute(Location: 0, VertexAttributeFormat.Float3, Offset: 0),
            new VertexAttribute(Location: 1, VertexAttributeFormat.Float3, Offset: 3 * sizeof(float)),
            new VertexAttribute(Location: 2, VertexAttributeFormat.Float2, Offset: 6 * sizeof(float)),
            new VertexAttribute(Location: 3, VertexAttributeFormat.Float4, Offset: 8 * sizeof(float)),
            new VertexAttribute(Location: 4, VertexAttributeFormat.Float4, Offset: 12 * sizeof(float)),
            new VertexAttribute(Location: 5, VertexAttributeFormat.Float4, Offset: 16 * sizeof(float))
        ]);

    public static VertexBufferData CreateBufferData(
        IReadOnlyList<VertexPosition3NormalTextureSkin4Tangent> vertices,
        GraphicsBufferUsage usage = GraphicsBufferUsage.Static)
    {
        return new VertexBufferData(
            new VertexBufferDescription(Layout, vertices.Count, usage),
            Pack(vertices));
    }

    public static byte[] Pack(IReadOnlyList<VertexPosition3NormalTextureSkin4Tangent> vertices)
    {
        var packed = new byte[vertices.Count * Layout.Stride];
        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(packed);

        for (var index = 0; index < vertices.Count; index++)
        {
            var v = vertices[index];
            var offset = index * 20;

            floats[offset]      = v.Position.X;
            floats[offset + 1]  = v.Position.Y;
            floats[offset + 2]  = v.Position.Z;
            floats[offset + 3]  = v.Normal.X;
            floats[offset + 4]  = v.Normal.Y;
            floats[offset + 5]  = v.Normal.Z;
            floats[offset + 6]  = v.TextureCoordinate.X;
            floats[offset + 7]  = v.TextureCoordinate.Y;
            floats[offset + 8]  = v.BoneIndices.X;
            floats[offset + 9]  = v.BoneIndices.Y;
            floats[offset + 10] = v.BoneIndices.Z;
            floats[offset + 11] = v.BoneIndices.W;
            floats[offset + 12] = v.BoneWeights.X;
            floats[offset + 13] = v.BoneWeights.Y;
            floats[offset + 14] = v.BoneWeights.Z;
            floats[offset + 15] = v.BoneWeights.W;
            floats[offset + 16] = v.Tangent.X;
            floats[offset + 17] = v.Tangent.Y;
            floats[offset + 18] = v.Tangent.Z;
            floats[offset + 19] = v.Tangent.W;
        }

        return packed;
    }
}
