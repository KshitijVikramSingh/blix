namespace Blix.Graphics.Primitives;

// Unit cube centered at the origin, half-extent 0.5 (edge length 1.0). 24
// vertices (4 per face) so each face carries its own flat outward normal and a
// full 0..1 UV; 36 indices. Triangles are wound CCW viewed from outside (same
// convention as Cylinder / glTF), so they survive RasterizerState.BackFaceCulling
// with FrontFace.CounterClockwise under the engine's Y-flipped Vulkan projection.
public static class Cube
{
    public static VertexPosition3NormalTexture[] Vertices { get; } = BuildVertices();

    public static ushort[] Indices { get; } = BuildIndices();

    private static VertexPosition3NormalTexture[] BuildVertices()
    {
        const float h = 0.5f;

        VertexPosition3NormalTexture V(float x, float y, float z, float nx, float ny, float nz, float u, float v) =>
            new(new GraphicsVector3(x, y, z), new GraphicsVector3(nx, ny, nz), new GraphicsVector2(u, v));

        return new[]
        {
            // +Z (front) — outward normal (0,0,1), CCW from +Z
            V(-h, -h,  h, 0, 0, 1, 0, 1), V( h, -h,  h, 0, 0, 1, 1, 1),
            V( h,  h,  h, 0, 0, 1, 1, 0), V(-h,  h,  h, 0, 0, 1, 0, 0),
            // -Z (back) — outward normal (0,0,-1), CCW from -Z
            V( h, -h, -h, 0, 0, -1, 0, 1), V(-h, -h, -h, 0, 0, -1, 1, 1),
            V(-h,  h, -h, 0, 0, -1, 1, 0), V( h,  h, -h, 0, 0, -1, 0, 0),
            // +X (right) — outward normal (1,0,0), CCW from +X
            V( h, -h,  h, 1, 0, 0, 0, 1), V( h, -h, -h, 1, 0, 0, 1, 1),
            V( h,  h, -h, 1, 0, 0, 1, 0), V( h,  h,  h, 1, 0, 0, 0, 0),
            // -X (left) — outward normal (-1,0,0), CCW from -X
            V(-h, -h, -h, -1, 0, 0, 0, 1), V(-h, -h,  h, -1, 0, 0, 1, 1),
            V(-h,  h,  h, -1, 0, 0, 1, 0), V(-h,  h, -h, -1, 0, 0, 0, 0),
            // +Y (top) — outward normal (0,1,0), CCW from +Y
            V(-h,  h,  h, 0, 1, 0, 0, 1), V( h,  h,  h, 0, 1, 0, 1, 1),
            V( h,  h, -h, 0, 1, 0, 1, 0), V(-h,  h, -h, 0, 1, 0, 0, 0),
            // -Y (bottom) — outward normal (0,-1,0), CCW from -Y
            V(-h, -h, -h, 0, -1, 0, 0, 1), V( h, -h, -h, 0, -1, 0, 1, 1),
            V( h, -h,  h, 0, -1, 0, 1, 0), V(-h, -h,  h, 0, -1, 0, 0, 0),
        };
    }

    private static ushort[] BuildIndices()
    {
        var indices = new ushort[36];
        var write = 0;
        for (var face = 0; face < 6; face++)
        {
            var b = (ushort)(face * 4);
            indices[write++] = b;
            indices[write++] = (ushort)(b + 1);
            indices[write++] = (ushort)(b + 2);
            indices[write++] = b;
            indices[write++] = (ushort)(b + 2);
            indices[write++] = (ushort)(b + 3);
        }
        return indices;
    }
}
