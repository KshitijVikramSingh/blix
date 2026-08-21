namespace Blix.Graphics.Primitives;

/// <summary>
/// Two intersecting vertical quads, double-sided: the cheapest thing that reads as a plant.
/// </summary>
/// <remarks>
/// Eight triangles, standing on the origin and reaching up to one, a unit across. Both quads are emitted
/// with each winding so the mesh survives back-face culling without needing a pipeline of its own — four
/// triangles would do if it were drawn double-sided, and paying four more is cheaper than a second pipeline.
/// <para>
/// Every normal points straight up. That is wrong for a quad and right for a plant: the alternative is two
/// planes whose facing flips as the camera crosses them, which makes a field of distant trees flicker as
/// you pan. Upward normals under the foliage terminator wrap give a soft, stable, slightly translucent read
/// — which is what a tree looks like at the distance anything is drawn this way.
/// </para>
/// <para>
/// Geometry only. What it stands in for is the caller's business: RTSGame uses it as the far level of detail
/// for trees, where a full model is six thousand triangles and there are nine thousand of them in view.
/// </para>
/// </remarks>
public static class FoliageCross
{
    public static VertexPosition3NormalTexture[] Vertices { get; } = BuildVertices();

    public static ushort[] Indices { get; } = BuildIndices();

    private static VertexPosition3NormalTexture[] BuildVertices()
    {
        var vertices = new VertexPosition3NormalTexture[8];
        var up = new GraphicsVector3(0f, 1f, 0f);

        VertexPosition3NormalTexture V(float x, float y, float z, float u, float v) =>
            new(new GraphicsVector3(x, y, z), up, new GraphicsVector2(u, v));

        // One quad across x, one across z.
        vertices[0] = V(-0.5f, 0f, 0f, 0f, 0f);
        vertices[1] = V(0.5f, 0f, 0f, 1f, 0f);
        vertices[2] = V(0.5f, 1f, 0f, 1f, 1f);
        vertices[3] = V(-0.5f, 1f, 0f, 0f, 1f);
        vertices[4] = V(0f, 0f, -0.5f, 0f, 0f);
        vertices[5] = V(0f, 0f, 0.5f, 1f, 0f);
        vertices[6] = V(0f, 1f, 0.5f, 1f, 1f);
        vertices[7] = V(0f, 1f, -0.5f, 0f, 1f);
        return vertices;
    }

    private static ushort[] BuildIndices() => new ushort[]
    {
        // Each quad twice, wound both ways, so neither side is culled.
        0, 1, 2, 0, 2, 3,
        0, 2, 1, 0, 3, 2,
        4, 5, 6, 4, 6, 7,
        4, 6, 5, 4, 7, 6,
    };
}
