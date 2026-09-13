namespace Blix.Graphics.Primitives;

/// <summary>
/// A flat square lying in the XZ plane, carrying its own distance-to-edge in the texture channel.
/// </summary>
/// <remarks>
/// <b>The square twin of <see cref="Disc"/>, and it exists so one shader can draw both shapes.</b> The decal
/// shader fades on <c>uv.x</c>, treating it as "how far out am I, where one is the edge" — a disc bakes the
/// radius into that channel and this bakes the <em>Chebyshev</em> distance, <c>max(|x|, |z|)</c>, which is one
/// along every edge of a square rather than only at its corners. So the same rim band that hugs a circle hugs
/// a square, with no flag to pass and no second shader to keep in step.
/// <para>
/// <b>Tessellated rather than four corners, and that is the whole trick.</b> Chebyshev distance is not linear
/// across a quad: interpolating from four corner values puts the value 1 at the corners and about 0.7 at the
/// edge midpoints, so a rim band drawn from it would bulge at the corners and vanish along the sides. A grid
/// samples the function often enough that the interpolation between samples is close to it everywhere.
/// </para>
/// </remarks>
public static class SquarePlate
{
    /// <summary>Cells per side. Enough that the Chebyshev interpolation is faithful; a decal needs no more.</summary>
    private const int Cells = 8;

    public static VertexPosition3NormalTexture[] Vertices { get; } = BuildVertices();

    public static ushort[] Indices { get; } = BuildIndices();

    private static VertexPosition3NormalTexture[] BuildVertices()
    {
        var side = Cells + 1;
        var vertices = new VertexPosition3NormalTexture[side * side];
        for (var z = 0; z < side; z++)
        for (var x = 0; x < side; x++)
        {
            // Unit square about the origin, so an instance's scale is its half-extent — the same contract the
            // disc has, where a scale of r gives a circle of radius r.
            var px = x / (float)Cells * 2f - 1f;
            var pz = z / (float)Cells * 2f - 1f;
            vertices[z * side + x] = new VertexPosition3NormalTexture(
                new GraphicsVector3(px, 0f, pz),
                new GraphicsVector3(0f, 1f, 0f),
                new GraphicsVector2(MathF.Max(MathF.Abs(px), MathF.Abs(pz)), 0f));
        }

        return vertices;
    }

    private static ushort[] BuildIndices()
    {
        var side = Cells + 1;
        var indices = new ushort[Cells * Cells * 6];
        var at = 0;
        for (var z = 0; z < Cells; z++)
        for (var x = 0; x < Cells; x++)
        {
            var corner = (ushort)(z * side + x);
            indices[at++] = corner;
            indices[at++] = (ushort)(corner + side);
            indices[at++] = (ushort)(corner + 1);
            indices[at++] = (ushort)(corner + 1);
            indices[at++] = (ushort)(corner + side);
            indices[at++] = (ushort)(corner + side + 1);
        }

        return indices;
    }
}
