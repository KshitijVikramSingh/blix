namespace Blix.Graphics.Primitives;

public static class Torus
{
    private const int MajorSegments = 32;
    private const int MinorSegments = 16;
    private const float MajorRadius = 0.40f;
    private const float MinorRadius = 0.15f;

    public static VertexPosition3NormalTexture[] Vertices { get; } = BuildVertices();

    public static ushort[] Indices { get; } = BuildIndices();

    private static VertexPosition3NormalTexture[] BuildVertices()
    {
        var rowSize = MinorSegments + 1;
        var vertices = new VertexPosition3NormalTexture[(MajorSegments + 1) * rowSize];

        for (var major = 0; major <= MajorSegments; major++)
        {
            var u = 2.0f * MathF.PI * major / MajorSegments;
            var cosU = MathF.Cos(u);
            var sinU = MathF.Sin(u);

            for (var minor = 0; minor <= MinorSegments; minor++)
            {
                var v = 2.0f * MathF.PI * minor / MinorSegments;
                var cosV = MathF.Cos(v);
                var sinV = MathF.Sin(v);

                var px = (MajorRadius + MinorRadius * cosV) * cosU;
                var py = MinorRadius * sinV;
                var pz = (MajorRadius + MinorRadius * cosV) * sinU;

                // Outward-facing normal at the tube surface: derivative of the tube's
                // radial direction. Stays smooth around both axes so the Poisson-disk
                // soft shadows have a clean per-vertex normal to interpolate.
                var nx = cosV * cosU;
                var ny = sinV;
                var nz = cosV * sinU;

                vertices[major * rowSize + minor] = new VertexPosition3NormalTexture(
                    new GraphicsVector3(px, py, pz),
                    new GraphicsVector3(nx, ny, nz),
                    new GraphicsVector2((float)major / MajorSegments, (float)minor / MinorSegments));
            }
        }

        return vertices;
    }

    private static ushort[] BuildIndices()
    {
        var rowSize = MinorSegments + 1;
        var indices = new ushort[MajorSegments * MinorSegments * 6];
        var write = 0;

        for (var major = 0; major < MajorSegments; major++)
        {
            for (var minor = 0; minor < MinorSegments; minor++)
            {
                var a = (ushort)(major * rowSize + minor);
                var b = (ushort)(major * rowSize + minor + 1);
                var c = (ushort)((major + 1) * rowSize + minor);
                var d = (ushort)((major + 1) * rowSize + minor + 1);

                // CCW from outside the tube, matching the outward-pointing normals.
                // Verified at (u=0, v=0): cross((b - a), (c - a)) points in +X, which is
                // the outward radial direction at that vertex.
                indices[write++] = a;
                indices[write++] = b;
                indices[write++] = c;

                indices[write++] = b;
                indices[write++] = d;
                indices[write++] = c;
            }
        }

        return indices;
    }
}
