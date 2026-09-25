namespace Blix.Graphics.Primitives;

/// <summary>
/// A flat disc in the x/z plane, facing up, with its radius carried in the texture coordinate.
/// </summary>
/// <remarks>
/// A triangle fan from the centre, wound counter-clockwise seen from above. The texture coordinate's first
/// channel is the distance from the centre as a fraction of the radius — zero at the middle, one at the rim
/// — which is what makes this useful without a texture: anything wanting a radial falloff reads it straight
/// out of the vertex stream instead of reconstructing the instance's centre in a shader.
/// <para>
/// Geometry only, like the rest of this namespace: what a disc is <em>for</em> is the caller's business.
/// An external RTS consumer draws contact shadows with it; a decal, a selection ring or a light pool would use the same mesh.
/// </para>
/// </remarks>
public static class Disc
{
    /// <summary>Segments round the rim. Sixteen is round enough at any size a decal is drawn at.</summary>
    private const int Segments = 16;

    public static VertexPosition3NormalTexture[] Vertices { get; } = BuildVertices();

    public static ushort[] Indices { get; } = BuildIndices();

    private static VertexPosition3NormalTexture[] BuildVertices()
    {
        var vertices = new VertexPosition3NormalTexture[Segments + 1];
        vertices[0] = new VertexPosition3NormalTexture(
            new GraphicsVector3(0f, 0f, 0f),
            new GraphicsVector3(0f, 1f, 0f),
            new GraphicsVector2(0f, 0f));
        for (var i = 0; i < Segments; i++)
        {
            var angle = i / (float)Segments * MathF.Tau;
            vertices[i + 1] = new VertexPosition3NormalTexture(
                new GraphicsVector3(MathF.Cos(angle), 0f, MathF.Sin(angle)),
                new GraphicsVector3(0f, 1f, 0f),
                new GraphicsVector2(1f, i / (float)Segments));
        }

        return vertices;
    }

    private static ushort[] BuildIndices()
    {
        var indices = new ushort[Segments * 3];
        for (var i = 0; i < Segments; i++)
        {
            indices[i * 3] = 0;
            indices[i * 3 + 1] = (ushort)(1 + (i + 1) % Segments);
            indices[i * 3 + 2] = (ushort)(1 + i);
        }

        return indices;
    }
}
