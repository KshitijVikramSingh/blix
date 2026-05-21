namespace Blix.Graphics.Primitives;

public static class Cylinder
{
    private const int Segments = 32;
    private const float Radius = 0.40f;
    private const float HalfHeight = 0.50f;

    public static VertexPosition3NormalTexture[] Vertices { get; } = BuildVertices();

    public static ushort[] Indices { get; } = BuildIndices();

    // Side wall has duplicated seam (Segments + 1 verts per ring) for clean UV wrap.
    // The two caps are fans around a center vertex with their own rims so the cap
    // normals stay flat (+Y / -Y) rather than averaging with the side's radial normals.
    private const int SideRing = Segments + 1;
    private const int SideVertexCount = SideRing * 2;
    private const int TopCenterIndex = SideVertexCount;
    private const int TopRimStart = SideVertexCount + 1;
    private const int BottomCenterIndex = SideVertexCount + 1 + Segments;
    private const int BottomRimStart = BottomCenterIndex + 1;
    private const int TotalVertexCount = BottomRimStart + Segments;

    private static VertexPosition3NormalTexture[] BuildVertices()
    {
        var vertices = new VertexPosition3NormalTexture[TotalVertexCount];

        // Side wall: two rings (top and bottom) with outward-pointing radial normals.
        for (var i = 0; i <= Segments; i++)
        {
            var u = 2.0f * MathF.PI * i / Segments;
            var cos = MathF.Cos(u);
            var sin = MathF.Sin(u);
            var nx = cos;
            var nz = sin;
            var uTex = (float)i / Segments;

            vertices[i] = new VertexPosition3NormalTexture(
                new GraphicsVector3(Radius * cos, HalfHeight, Radius * sin),
                new GraphicsVector3(nx, 0.0f, nz),
                new GraphicsVector2(uTex, 0.0f));
            vertices[SideRing + i] = new VertexPosition3NormalTexture(
                new GraphicsVector3(Radius * cos, -HalfHeight, Radius * sin),
                new GraphicsVector3(nx, 0.0f, nz),
                new GraphicsVector2(uTex, 1.0f));
        }

        // Top cap fan: center vertex + Segments rim vertices (not Segments+1; the fan
        // wraps via index modulo).
        vertices[TopCenterIndex] = new VertexPosition3NormalTexture(
            new GraphicsVector3(0.0f, HalfHeight, 0.0f),
            new GraphicsVector3(0.0f, 1.0f, 0.0f),
            new GraphicsVector2(0.5f, 0.5f));
        for (var i = 0; i < Segments; i++)
        {
            var u = 2.0f * MathF.PI * i / Segments;
            var cos = MathF.Cos(u);
            var sin = MathF.Sin(u);
            vertices[TopRimStart + i] = new VertexPosition3NormalTexture(
                new GraphicsVector3(Radius * cos, HalfHeight, Radius * sin),
                new GraphicsVector3(0.0f, 1.0f, 0.0f),
                new GraphicsVector2(cos * 0.5f + 0.5f, sin * 0.5f + 0.5f));
        }

        vertices[BottomCenterIndex] = new VertexPosition3NormalTexture(
            new GraphicsVector3(0.0f, -HalfHeight, 0.0f),
            new GraphicsVector3(0.0f, -1.0f, 0.0f),
            new GraphicsVector2(0.5f, 0.5f));
        for (var i = 0; i < Segments; i++)
        {
            var u = 2.0f * MathF.PI * i / Segments;
            var cos = MathF.Cos(u);
            var sin = MathF.Sin(u);
            vertices[BottomRimStart + i] = new VertexPosition3NormalTexture(
                new GraphicsVector3(Radius * cos, -HalfHeight, Radius * sin),
                new GraphicsVector3(0.0f, -1.0f, 0.0f),
                new GraphicsVector2(cos * 0.5f + 0.5f, sin * 0.5f + 0.5f));
        }

        return vertices;
    }

    private static ushort[] BuildIndices()
    {
        var sideTriCount = Segments * 6;
        var capTriCount = Segments * 3;
        var indices = new ushort[sideTriCount + capTriCount * 2];
        var write = 0;

        for (var i = 0; i < Segments; i++)
        {
            var topL = (ushort)i;
            var topR = (ushort)(i + 1);
            var botL = (ushort)(SideRing + i);
            var botR = (ushort)(SideRing + i + 1);

            // CCW viewed from outside (cross((topR - topL), (botL - topL)) points
            // radially outward at angle 0).
            indices[write++] = topL;
            indices[write++] = topR;
            indices[write++] = botL;

            indices[write++] = topR;
            indices[write++] = botR;
            indices[write++] = botL;
        }

        for (var i = 0; i < Segments; i++)
        {
            var rimA = (ushort)(TopRimStart + i);
            var rimB = (ushort)(TopRimStart + (i + 1) % Segments);
            // Top cap viewed from above (+Y): CCW order is center -> rimB -> rimA so
            // the front-normal (cross of the first two edges) comes out +Y.
            indices[write++] = (ushort)TopCenterIndex;
            indices[write++] = rimB;
            indices[write++] = rimA;
        }

        for (var i = 0; i < Segments; i++)
        {
            var rimA = (ushort)(BottomRimStart + i);
            var rimB = (ushort)(BottomRimStart + (i + 1) % Segments);
            // Bottom cap viewed from below (-Y): CCW from that side is center -> rimA
            // -> rimB.
            indices[write++] = (ushort)BottomCenterIndex;
            indices[write++] = rimA;
            indices[write++] = rimB;
        }

        return indices;
    }
}
