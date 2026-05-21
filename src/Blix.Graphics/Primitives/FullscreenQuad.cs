namespace Blix.Graphics.Primitives;

public static class FullscreenQuad
{
    public static VertexPositionTexture[] Vertices { get; } =
    [
        new VertexPositionTexture(new GraphicsVector2(-1.0f, -1.0f), new GraphicsVector2(0.0f, 0.0f)),
        new VertexPositionTexture(new GraphicsVector2(1.0f, -1.0f), new GraphicsVector2(1.0f, 0.0f)),
        new VertexPositionTexture(new GraphicsVector2(1.0f, 1.0f), new GraphicsVector2(1.0f, 1.0f)),
        new VertexPositionTexture(new GraphicsVector2(-1.0f, 1.0f), new GraphicsVector2(0.0f, 1.0f))
    ];

    public static ushort[] Indices { get; } =
    [
        0, 1, 2,
        0, 2, 3
    ];
}
