namespace Blix.Graphics.Primitives;

public static class CapsuleMesh
{
    private const int RadialSegments = 24;
    // CapRings: rings of latitude inside each hemisphere (not counting the seam with
    // the cylinder or the pole). 4 -> 5 ring positions per hemisphere counting the pole.
    private const int CapRings = 5;
    private const float Radius = 0.30f;
    private const float CylinderHalfHeight = 0.30f;

    public static VertexPosition3NormalTexture[] Vertices { get; } = BuildVertices();

    public static ushort[] Indices { get; } = BuildIndices();

    private const int RingVertCount = RadialSegments + 1;
    // Top hemisphere: pole + CapRings rings (last one is the equator at +CylinderHalfHeight)
    // Bottom mirror. Plus the two cylinder seam rings would be duplicates so we reuse
    // the bottom edge of the top hemisphere as the top of the cylinder, and vice versa.
    private const int TopRingsStart = 0;          // CapRings rings (poles to equator)
    private const int BottomRingsStart = TopRingsStart + CapRings * RingVertCount;
    private const int TotalVertices = BottomRingsStart + CapRings * RingVertCount;

    private static VertexPosition3NormalTexture[] BuildVertices()
    {
        var vertices = new VertexPosition3NormalTexture[TotalVertices];

        // Top hemisphere: latitudes from theta=0 (pole, +Y) to theta=pi/2 (equator).
        // CapRings rings; first is the pole (duplicated around the rim for UV continuity).
        for (var lat = 0; lat < CapRings; lat++)
        {
            var theta = (MathF.PI / 2.0f) * lat / (CapRings - 1);
            var sinT = MathF.Sin(theta);
            var cosT = MathF.Cos(theta);
            for (var i = 0; i <= RadialSegments; i++)
            {
                var phi = 2.0f * MathF.PI * i / RadialSegments;
                var nx = sinT * MathF.Cos(phi);
                var ny = cosT;
                var nz = sinT * MathF.Sin(phi);
                var px = nx * Radius;
                var py = ny * Radius + CylinderHalfHeight;
                var pz = nz * Radius;
                vertices[TopRingsStart + lat * RingVertCount + i] = new VertexPosition3NormalTexture(
                    new GraphicsVector3(px, py, pz),
                    new GraphicsVector3(nx, ny, nz),
                    new GraphicsVector2((float)i / RadialSegments, 1.0f - (float)lat / (CapRings - 1) * 0.5f));
            }
        }

        // Bottom hemisphere: theta from pi/2 (equator) to pi (south pole).
        for (var lat = 0; lat < CapRings; lat++)
        {
            var theta = MathF.PI / 2.0f + (MathF.PI / 2.0f) * lat / (CapRings - 1);
            var sinT = MathF.Sin(theta);
            var cosT = MathF.Cos(theta);
            for (var i = 0; i <= RadialSegments; i++)
            {
                var phi = 2.0f * MathF.PI * i / RadialSegments;
                var nx = sinT * MathF.Cos(phi);
                var ny = cosT;
                var nz = sinT * MathF.Sin(phi);
                var px = nx * Radius;
                var py = ny * Radius - CylinderHalfHeight;
                var pz = nz * Radius;
                vertices[BottomRingsStart + lat * RingVertCount + i] = new VertexPosition3NormalTexture(
                    new GraphicsVector3(px, py, pz),
                    new GraphicsVector3(nx, ny, nz),
                    new GraphicsVector2((float)i / RadialSegments, 0.5f - (float)lat / (CapRings - 1) * 0.5f));
            }
        }

        return vertices;
    }

    private static ushort[] BuildIndices()
    {
        // Quads between every adjacent pair of latitude rings, top hemisphere + cylinder
        // span (equator of top to equator of bottom) + bottom hemisphere. The "equator"
        // of each hemisphere is the cylinder seam.
        var quadRows = (CapRings - 1) + 1 + (CapRings - 1);
        var indices = new ushort[quadRows * RadialSegments * 6];
        var write = 0;

        for (var lat = 0; lat < CapRings - 1; lat++)
        {
            WriteQuadRing(indices, ref write,
                TopRingsStart + lat * RingVertCount,
                TopRingsStart + (lat + 1) * RingVertCount);
        }
        // Cylinder seam: bottom row of top hemisphere is the top of the cylinder;
        // top row of bottom hemisphere is the bottom of the cylinder.
        WriteQuadRing(indices, ref write,
            TopRingsStart + (CapRings - 1) * RingVertCount,
            BottomRingsStart + 0 * RingVertCount);
        for (var lat = 0; lat < CapRings - 1; lat++)
        {
            WriteQuadRing(indices, ref write,
                BottomRingsStart + lat * RingVertCount,
                BottomRingsStart + (lat + 1) * RingVertCount);
        }
        return indices;
    }

    private static void WriteQuadRing(ushort[] indices, ref int write, int topStart, int bottomStart)
    {
        for (var i = 0; i < RadialSegments; i++)
        {
            var a = (ushort)(topStart + i);
            var b = (ushort)(topStart + i + 1);
            var c = (ushort)(bottomStart + i);
            var d = (ushort)(bottomStart + i + 1);
            // CCW from outside. The previous (a,c,b)/(b,c,d) wound the triangles
            // inside-out, so back-face culling showed only the cavity instead of the
            // exterior. Verified at the equator (phi=0, theta=pi/2): cross((b-a), (c-a))
            // points in +X which is the outward radial direction at that vertex.
            indices[write++] = a;
            indices[write++] = b;
            indices[write++] = c;
            indices[write++] = b;
            indices[write++] = d;
            indices[write++] = c;
        }
    }
}
