namespace Blix.Graphics.Primitives;

public static class Icosphere
{
    private const int SubdivisionLevels = 3;

    public static VertexPosition3NormalTexture[] Vertices { get; }

    public static ushort[] Indices { get; }

    static Icosphere()
    {
        var (positions, faces) = BuildSubdividedMesh(SubdivisionLevels);

        Vertices = new VertexPosition3NormalTexture[positions.Count];
        for (var i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            // Equirectangular UV from the unit position. Has a seam at the dateline
            // but is fine for testing - the alternative (chartlets) is heavier than
            // this demo needs.
            var u = 0.5f + MathF.Atan2(p.Z, p.X) / (2.0f * MathF.PI);
            var v = 0.5f - MathF.Asin(p.Y) / MathF.PI;
            // Position and normal coincide for a unit sphere; scale to radius 0.5.
            Vertices[i] = new VertexPosition3NormalTexture(
                new GraphicsVector3(p.X * 0.5f, p.Y * 0.5f, p.Z * 0.5f),
                new GraphicsVector3(p.X, p.Y, p.Z),
                new GraphicsVector2(u, v));
        }

        Indices = new ushort[faces.Count * 3];
        for (var i = 0; i < faces.Count; i++)
        {
            Indices[i * 3 + 0] = (ushort)faces[i].A;
            Indices[i * 3 + 1] = (ushort)faces[i].B;
            Indices[i * 3 + 2] = (ushort)faces[i].C;
        }
    }

    private readonly record struct V3(float X, float Y, float Z);
    private readonly record struct Tri(int A, int B, int C);

    private static (List<V3> Positions, List<Tri> Faces) BuildSubdividedMesh(int subdivisions)
    {
        // Start from a unit icosahedron - 12 verts, 20 faces. Coordinates derived from
        // the golden ratio so all edges have equal length on the unit sphere.
        var t = (1.0f + MathF.Sqrt(5.0f)) / 2.0f;
        var positions = new List<V3>
        {
            Normalize(new V3(-1,  t,  0)), Normalize(new V3( 1,  t,  0)),
            Normalize(new V3(-1, -t,  0)), Normalize(new V3( 1, -t,  0)),
            Normalize(new V3( 0, -1,  t)), Normalize(new V3( 0,  1,  t)),
            Normalize(new V3( 0, -1, -t)), Normalize(new V3( 0,  1, -t)),
            Normalize(new V3( t,  0, -1)), Normalize(new V3( t,  0,  1)),
            Normalize(new V3(-t,  0, -1)), Normalize(new V3(-t,  0,  1)),
        };
        var faces = new List<Tri>
        {
            new(0, 11, 5),  new(0, 5, 1),   new(0, 1, 7),   new(0, 7, 10),  new(0, 10, 11),
            new(1, 5, 9),   new(5, 11, 4),  new(11, 10, 2), new(10, 7, 6),  new(7, 1, 8),
            new(3, 9, 4),   new(3, 4, 2),   new(3, 2, 6),   new(3, 6, 8),   new(3, 8, 9),
            new(4, 9, 5),   new(2, 4, 11),  new(6, 2, 10),  new(8, 6, 7),   new(9, 8, 1),
        };

        // Each subdivision splits every triangle into 4 by introducing midpoint vertices
        // on each edge, projected back to the unit sphere. Cached by edge so adjacent
        // triangles share the new midpoint instead of duplicating it.
        for (var step = 0; step < subdivisions; step++)
        {
            var midpointCache = new Dictionary<(int, int), int>();
            var newFaces = new List<Tri>(faces.Count * 4);
            foreach (var f in faces)
            {
                var a = Midpoint(f.A, f.B, positions, midpointCache);
                var b = Midpoint(f.B, f.C, positions, midpointCache);
                var c = Midpoint(f.C, f.A, positions, midpointCache);
                newFaces.Add(new Tri(f.A, a, c));
                newFaces.Add(new Tri(f.B, b, a));
                newFaces.Add(new Tri(f.C, c, b));
                newFaces.Add(new Tri(a, b, c));
            }
            faces = newFaces;
        }

        return (positions, faces);
    }

    private static int Midpoint(int i, int j, List<V3> positions, Dictionary<(int, int), int> cache)
    {
        var key = i < j ? (i, j) : (j, i);
        if (cache.TryGetValue(key, out var existing))
        {
            return existing;
        }
        var a = positions[i];
        var b = positions[j];
        var mid = Normalize(new V3((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f, (a.Z + b.Z) * 0.5f));
        var index = positions.Count;
        positions.Add(mid);
        cache[key] = index;
        return index;
    }

    private static V3 Normalize(V3 v)
    {
        var len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return new V3(v.X / len, v.Y / len, v.Z / len);
    }
}
