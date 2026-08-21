namespace Blix.Graphics.Primitives;

/// <summary>
/// A twenty-triangle sphere: the cheapest shape that still reads as a rounded mass.
/// </summary>
/// <remarks>
/// The unsubdivided icosahedron, radius 0.5, wound counter-clockwise from outside, with flat per-face
/// normals so it facets rather than pretending to be smooth. <see cref="Icosphere"/> is the same shape
/// subdivided three times — 1,280 triangles — which is the right answer for a canopy you are standing under
/// and sixty times too many for one on a horizon.
/// <para>
/// It exists because distant foliage cannot be decimated. A tree model is a few thousand disconnected leaf
/// cards, and an edge-collapse simplifier has nothing to collapse: measured through the cook tool, a
/// 4,345-triangle tree reduced to 3,975 and stopped. Distant trees therefore need a <em>substitute</em>
/// rather than a reduction, and the substitute wants to be a mass with a silhouette, not a pair of crossed
/// quads — which from above is a shard.
/// </para>
/// </remarks>
public static class Icosahedron
{
    public static VertexPosition3NormalTexture[] Vertices { get; }

    public static ushort[] Indices { get; }

    static Icosahedron()
    {
        // The twelve vertices of an icosahedron, from the golden ratio.
        var phi = (1f + MathF.Sqrt(5f)) * 0.5f;
        var corners = new[]
        {
            new GraphicsVector3(-1f, phi, 0f), new GraphicsVector3(1f, phi, 0f),
            new GraphicsVector3(-1f, -phi, 0f), new GraphicsVector3(1f, -phi, 0f),
            new GraphicsVector3(0f, -1f, phi), new GraphicsVector3(0f, 1f, phi),
            new GraphicsVector3(0f, -1f, -phi), new GraphicsVector3(0f, 1f, -phi),
            new GraphicsVector3(phi, 0f, -1f), new GraphicsVector3(phi, 0f, 1f),
            new GraphicsVector3(-phi, 0f, -1f), new GraphicsVector3(-phi, 0f, 1f),
        };

        var faces = new[]
        {
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
            1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
            4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        };

        // Unshared vertices, three per face, so each face carries its own normal and the shape facets.
        var vertices = new VertexPosition3NormalTexture[faces.Length];
        var indices = new ushort[faces.Length];
        for (var f = 0; f < faces.Length; f += 3)
        {
            var a = Normalise(corners[faces[f]]);
            var b = Normalise(corners[faces[f + 1]]);
            var c = Normalise(corners[faces[f + 2]]);
            var normal = Normalise(new GraphicsVector3(
                (a.X + b.X + c.X) / 3f, (a.Y + b.Y + c.Y) / 3f, (a.Z + b.Z + c.Z) / 3f));
            vertices[f] = new VertexPosition3NormalTexture(Half(a), normal, new GraphicsVector2(0f, 0f));
            vertices[f + 1] = new VertexPosition3NormalTexture(Half(b), normal, new GraphicsVector2(1f, 0f));
            vertices[f + 2] = new VertexPosition3NormalTexture(Half(c), normal, new GraphicsVector2(0f, 1f));
            indices[f] = (ushort)f;
            indices[f + 1] = (ushort)(f + 1);
            indices[f + 2] = (ushort)(f + 2);
        }

        Vertices = vertices;
        Indices = indices;
    }

    private static GraphicsVector3 Normalise(GraphicsVector3 v)
    {
        var length = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return length <= 0f ? v : new GraphicsVector3(v.X / length, v.Y / length, v.Z / length);
    }

    private static GraphicsVector3 Half(GraphicsVector3 v) => new(v.X * 0.5f, v.Y * 0.5f, v.Z * 0.5f);
}
