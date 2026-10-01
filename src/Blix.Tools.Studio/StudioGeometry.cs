using Blix.Graphics;

namespace Blix.Tools.Studio;

/// <summary>
/// Studio's small reference geometry, built in code.
/// </summary>
/// <remarks>
/// Blix has no primitive builders — no <c>CreateBox</c>, no <c>CreateSphere</c> — and Studio is not the
/// place to invent them. Every demo that needs a cube writes one, which is duplication the engine has so
/// far declined to absorb because a primitive generator is a small API with a large number of opinions in
/// it (winding, UV layout, smoothing, tangents). Noted here as a candidate: if a third consumer wants the
/// same decisions, that is the bar conventions §4 sets.
/// <para>
/// Built as the complete static vertex a cooked model reaches Studio in, so one pipeline draws the
/// furniture and the asset. The tangent is exact, since each face is planar with a known UV layout: T
/// runs toward increasing u, and w makes <c>cross(N, T) * w</c> run up the image (decreasing v), as
/// glTF defines it. The colour is white and the second UV set mirrors the first.
/// </para>
/// </remarks>
public static class StudioGeometry
{
    /// <summary>A unit cube centred on the origin, flat-shaded — 24 vertices, 4 per face.</summary>
    public static (VertexPosition3NormalTangentTexture2Color[] Vertices, ushort[] Indices) Cube(float size = 1f)
    {
        var h = size * 0.5f;
        var faces = new (GraphicsVector3 Normal, GraphicsVector3 A, GraphicsVector3 B, GraphicsVector3 C, GraphicsVector3 D)[]
        {
            (new(0, 0, 1), new(-h, -h, h), new(h, -h, h), new(h, h, h), new(-h, h, h)),
            (new(0, 0, -1), new(h, -h, -h), new(-h, -h, -h), new(-h, h, -h), new(h, h, -h)),
            (new(1, 0, 0), new(h, -h, h), new(h, -h, -h), new(h, h, -h), new(h, h, h)),
            (new(-1, 0, 0), new(-h, -h, -h), new(-h, -h, h), new(-h, h, h), new(-h, h, -h)),
            (new(0, 1, 0), new(-h, h, h), new(h, h, h), new(h, h, -h), new(-h, h, -h)),
            (new(0, -1, 0), new(-h, -h, -h), new(h, -h, -h), new(h, -h, h), new(-h, -h, h)),
        };

        var vertices = new VertexPosition3NormalTangentTexture2Color[faces.Length * 4];
        var indices = new ushort[faces.Length * 6];
        for (var f = 0; f < faces.Length; f++)
        {
            var (n, a, b, c, d) = faces[f];
            // u runs a -> b and v runs a -> d.
            var t = Tangent(n, Sub(b, a), Sub(d, a));
            var v = f * 4;
            vertices[v + 0] = Vertex(a, n, t, new GraphicsVector2(0, 0));
            vertices[v + 1] = Vertex(b, n, t, new GraphicsVector2(1, 0));
            vertices[v + 2] = Vertex(c, n, t, new GraphicsVector2(1, 1));
            vertices[v + 3] = Vertex(d, n, t, new GraphicsVector2(0, 1));

            var i = f * 6;
            indices[i + 0] = (ushort)(v + 0);
            indices[i + 1] = (ushort)(v + 1);
            indices[i + 2] = (ushort)(v + 2);
            indices[i + 3] = (ushort)(v + 0);
            indices[i + 4] = (ushort)(v + 2);
            indices[i + 5] = (ushort)(v + 3);
        }

        return (vertices, indices);
    }

    /// <summary>A ground quad in the XZ plane, facing up.</summary>
    public static (VertexPosition3NormalTangentTexture2Color[] Vertices, ushort[] Indices) Ground(float extent = 12f)
    {
        var n = new GraphicsVector3(0, 1, 0);
        // u runs toward +x and v toward +z.
        var t = Tangent(n, new GraphicsVector3(1, 0, 0), new GraphicsVector3(0, 0, 1));
        var vertices = new[]
        {
            Vertex(new(-extent, 0, -extent), n, t, new GraphicsVector2(0, 0)),
            Vertex(new(extent, 0, -extent), n, t, new GraphicsVector2(1, 0)),
            Vertex(new(extent, 0, extent), n, t, new GraphicsVector2(1, 1)),
            Vertex(new(-extent, 0, extent), n, t, new GraphicsVector2(0, 1)),
        };
        return (vertices, new ushort[] { 0, 2, 1, 0, 3, 2 });
    }

    private static VertexPosition3NormalTangentTexture2Color Vertex(
        GraphicsVector3 position, GraphicsVector3 normal, GraphicsVector4 tangent, GraphicsVector2 uv) =>
        new(position, normal, tangent, uv, uv, VertexPosition3NormalTangentTexture2Color.White);

    // A planar face's tangent: T along increasing u; w so that cross(N, T) * w points along
    // decreasing v, which is glTF's bitangent.
    private static GraphicsVector4 Tangent(GraphicsVector3 n, GraphicsVector3 alongU, GraphicsVector3 alongV)
    {
        var length = MathF.Sqrt(Dot(alongU, alongU));
        var t = new GraphicsVector3(alongU.X / length, alongU.Y / length, alongU.Z / length);
        var nxt = new GraphicsVector3(n.Y * t.Z - n.Z * t.Y, n.Z * t.X - n.X * t.Z, n.X * t.Y - n.Y * t.X);
        var w = Dot(nxt, alongV) > 0 ? -1f : 1f;
        return new GraphicsVector4(t.X, t.Y, t.Z, w);
    }

    private static GraphicsVector3 Sub(GraphicsVector3 a, GraphicsVector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static float Dot(GraphicsVector3 a, GraphicsVector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
