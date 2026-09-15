using Blix.Graphics;

namespace Blix.Tools.Preview;

/// <summary>
/// The lab's geometry, built in code.
/// </summary>
/// <remarks>
/// Blix has no primitive builders — no <c>CreateBox</c>, no <c>CreateSphere</c> — and this lab is not the
/// place to invent them. Every demo that needs a cube writes one, which is duplication the engine has so
/// far declined to absorb because a primitive generator is a small API with a large number of opinions in
/// it (winding, UV layout, smoothing, tangents). Noted here as a candidate: if a third consumer wants the
/// same decisions, that is the bar conventions §4 sets.
/// </remarks>
public static class LabGeometry
{
    /// <summary>A unit cube centred on the origin, flat-shaded — 24 vertices, 4 per face.</summary>
    public static (VertexPosition3NormalTexture[] Vertices, ushort[] Indices) Cube(float size = 1f)
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

        var vertices = new VertexPosition3NormalTexture[faces.Length * 4];
        var indices = new ushort[faces.Length * 6];
        for (var f = 0; f < faces.Length; f++)
        {
            var (n, a, b, c, d) = faces[f];
            var v = f * 4;
            vertices[v + 0] = new(a, n, new GraphicsVector2(0, 0));
            vertices[v + 1] = new(b, n, new GraphicsVector2(1, 0));
            vertices[v + 2] = new(c, n, new GraphicsVector2(1, 1));
            vertices[v + 3] = new(d, n, new GraphicsVector2(0, 1));

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
    public static (VertexPosition3NormalTexture[] Vertices, ushort[] Indices) Ground(float extent = 12f)
    {
        var n = new GraphicsVector3(0, 1, 0);
        var vertices = new VertexPosition3NormalTexture[]
        {
            new(new(-extent, 0, -extent), n, new GraphicsVector2(0, 0)),
            new(new(extent, 0, -extent), n, new GraphicsVector2(1, 0)),
            new(new(extent, 0, extent), n, new GraphicsVector2(1, 1)),
            new(new(-extent, 0, extent), n, new GraphicsVector2(0, 1)),
        };
        return (vertices, new ushort[] { 0, 2, 1, 0, 3, 2 });
    }
}
