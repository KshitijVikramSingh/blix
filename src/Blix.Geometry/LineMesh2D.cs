namespace Blix.Geometry;

// 2D analogue of TriangleMesh3D: static line-segment soup for level geometry
// (platforms, walls, slopes). Segments are stored in world space — moving level
// geometry rebuilds the mesh when the transform changes, same convention as the
// 3D triangle mesh.
//
// Bounds are aggregated at construction so every intersection query can early-
// out against a single AABB before touching individual segments.
public sealed class LineMesh2D
{
    public LineMesh2D(Segment2D[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        Segments = segments;
        Bounds = ComputeBounds(segments);
    }

    public LineMesh2D(IEnumerable<Segment2D> segments)
        : this(segments?.ToArray() ?? throw new ArgumentNullException(nameof(segments)))
    {
    }

    public Segment2D[] Segments { get; }

    public Bounds2 Bounds { get; }

    public int Count => Segments.Length;

    private static Bounds2 ComputeBounds(Segment2D[] segments)
    {
        if (segments.Length == 0)
        {
            return Bounds2.Empty;
        }
        var bounds = segments[0].Bounds;
        for (var i = 1; i < segments.Length; i++)
        {
            var b = segments[i].Bounds;
            bounds = new Bounds2(
                System.Numerics.Vector2.Min(bounds.Min, b.Min),
                System.Numerics.Vector2.Max(bounds.Max, b.Max));
        }
        return bounds;
    }
}
