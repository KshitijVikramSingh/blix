using System.Numerics;

namespace Blix.Geometry;

// Static triangle-soup collider. Vertices are stored in WORLD SPACE, baked at
// construction time. Mesh that moves (a rotating platform, an animated door) needs
// to rebuild its TriangleMesh3D when its transform changes — for now the engine
// doesn't carry a per-mesh transform reference because the user-facing case (static
// level geometry) doesn't need it. A future "transformed triangle mesh" that stores
// local triangles + a Transform3D can land alongside the first moving-collider use.
//
// Bounds are aggregated at construction so every intersection query can early-out
// against a single AABB before touching individual triangles.
public sealed class TriangleMesh3D
{
    public TriangleMesh3D(Triangle[] triangles)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        Triangles = triangles;
        Bounds = ComputeBounds(triangles);
    }

    // Convenience: build from any sequence (LINQ result, list, etc.). Copies into an
    // array internally so the mesh owns its storage.
    public TriangleMesh3D(IEnumerable<Triangle> triangles)
        : this(triangles?.ToArray() ?? throw new ArgumentNullException(nameof(triangles)))
    {
    }

    public Triangle[] Triangles { get; }

    public Bounds3 Bounds { get; }

    public int Count => Triangles.Length;

    // Build a triangle mesh from a positions + indices pair (the natural shape for
    // procedural primitives and stripped-down mesh imports). Indices count must be
    // a multiple of 3. Caller is responsible for putting positions in world space —
    // this factory doesn't take a transform, it just packs the triangles.
    public static TriangleMesh3D FromIndexed(IReadOnlyList<Vector3> positions, IReadOnlyList<ushort> indices)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Count % 3 != 0)
        {
            throw new ArgumentException(
                $"Indices count ({indices.Count}) is not a multiple of 3 — can't form a triangle list.",
                nameof(indices));
        }

        var triangles = new Triangle[indices.Count / 3];
        for (var i = 0; i < triangles.Length; i++)
        {
            triangles[i] = new Triangle(
                positions[indices[i * 3]],
                positions[indices[i * 3 + 1]],
                positions[indices[i * 3 + 2]]);
        }
        return new TriangleMesh3D(triangles);
    }

    private static Bounds3 ComputeBounds(Triangle[] triangles)
    {
        if (triangles.Length == 0) return Bounds3.Empty;
        var min = triangles[0].V0;
        var max = triangles[0].V0;
        foreach (var tri in triangles)
        {
            min = Vector3.Min(min, Vector3.Min(tri.V0, Vector3.Min(tri.V1, tri.V2)));
            max = Vector3.Max(max, Vector3.Max(tri.V0, Vector3.Max(tri.V1, tri.V2)));
        }
        return new Bounds3(min, max);
    }
}
