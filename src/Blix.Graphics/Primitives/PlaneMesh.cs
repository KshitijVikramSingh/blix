namespace Blix.Graphics.Primitives;

public static class PlaneMesh
{
    public static VertexPosition3NormalTexture[] Vertices { get; } = BuildVertices();

    // CCW winding viewed from +Y (above), matching the vertex normals. The reversed
    // order makes the geometric front face the top of the plane; back-face culling
    // then keeps the plane visible from above and hides it from below.
    public static ushort[] Indices { get; } =
    [
        0, 2, 1,
        0, 3, 2
    ];

    private static VertexPosition3NormalTexture[] BuildVertices()
    {
        var up = new GraphicsVector3(0.0f, 1.0f, 0.0f);

        return
        [
            new(new GraphicsVector3(-0.5f, 0.0f, -0.5f), up, new GraphicsVector2(0.0f, 0.0f)),
            new(new GraphicsVector3(0.5f, 0.0f, -0.5f), up, new GraphicsVector2(1.0f, 0.0f)),
            new(new GraphicsVector3(0.5f, 0.0f, 0.5f), up, new GraphicsVector2(1.0f, 1.0f)),
            new(new GraphicsVector3(-0.5f, 0.0f, 0.5f), up, new GraphicsVector2(0.0f, 1.0f))
        ];
    }
}
