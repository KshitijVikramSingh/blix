using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Render;

public sealed class DebugDraw
{
    private const int MaxLineCount = 32_000;
    private const int MaxVertexCount = MaxLineCount * 2;

    private const string VertexShaderSource = """
        #version 410 core
        layout (location = 0) in vec3 aPosition;
        layout (location = 1) in vec4 aColor;
        out vec4 vertexColor;
        uniform mat4 uViewProjection;
        void main()
        {
            vertexColor = aColor;
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    private const string FragmentShaderSource = """
        #version 410 core
        in vec4 vertexColor;
        out vec4 fragColor;
        void main()
        {
            fragColor = vertexColor;
        }
        """;

    private readonly IGraphicsDevice device;
    private readonly VertexBufferHandle vertexBuffer;
    private readonly IndexBufferHandle indexBuffer;
    private readonly Material material;
    private readonly byte[] uploadBuffer;
    private int vertexCount;

    public DebugDraw(IGraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;

        var emptyVertices = new VertexPosition3Color[MaxVertexCount];
        var initialData = new VertexBufferData(
            new VertexBufferDescription(VertexPosition3Color.Layout, MaxVertexCount, GraphicsBufferUsage.Dynamic),
            VertexPosition3Color.Pack(emptyVertices));
        vertexBuffer = device.CreateVertexBuffer(initialData, name: "debug.vertices");

        var sequentialIndices = new ushort[MaxVertexCount];
        for (var i = 0; i < MaxVertexCount; i++)
        {
            sequentialIndices[i] = (ushort)i;
        }

        indexBuffer = device.CreateIndexBuffer(sequentialIndices, GraphicsBufferUsage.Static, name: "debug.indices");

        var shader = device.CreateShaderProgram(new ShaderSources(
            VertexShaderSource,
            FragmentShaderSource,
            VertexName: "debugdraw.vert",
            FragmentName: "debugdraw.frag"));

        var pipeline = device.CreatePipeline(
            new PipelineDescription(
                shader,
                VertexPosition3Color.Layout,
                PrimitiveTopology.Lines,
                DepthState.Disabled,
                RasterizerState.NoCulling,
                BlendState.Disabled),
            name: "debug");

        material = new Material("debug", pipeline);
        uploadBuffer = new byte[MaxVertexCount * VertexPosition3Color.Layout.Stride];
    }

    public void Clear()
    {
        vertexCount = 0;
    }

    public void Line(Vector3 a, Vector3 b, GraphicsColor color)
    {
        if (vertexCount + 2 > MaxVertexCount)
        {
            throw new InvalidOperationException($"DebugDraw exceeded line capacity ({MaxLineCount} lines per frame).");
        }

        WriteVertex(vertexCount, a, color);
        WriteVertex(vertexCount + 1, b, color);
        vertexCount += 2;
    }

    public void Aabb(Vector3 min, Vector3 max, GraphicsColor color)
    {
        var c000 = new Vector3(min.X, min.Y, min.Z);
        var c100 = new Vector3(max.X, min.Y, min.Z);
        var c010 = new Vector3(min.X, max.Y, min.Z);
        var c110 = new Vector3(max.X, max.Y, min.Z);
        var c001 = new Vector3(min.X, min.Y, max.Z);
        var c101 = new Vector3(max.X, min.Y, max.Z);
        var c011 = new Vector3(min.X, max.Y, max.Z);
        var c111 = new Vector3(max.X, max.Y, max.Z);

        Line(c000, c100, color);
        Line(c100, c101, color);
        Line(c101, c001, color);
        Line(c001, c000, color);

        Line(c010, c110, color);
        Line(c110, c111, color);
        Line(c111, c011, color);
        Line(c011, c010, color);

        Line(c000, c010, color);
        Line(c100, c110, color);
        Line(c101, c111, color);
        Line(c001, c011, color);
    }

    public void Grid(Vector3 center, float size, int divisions, GraphicsColor color)
    {
        if (size <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Grid size must be positive.");
        }

        if (divisions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(divisions), "Grid divisions must be positive.");
        }

        var half = size * 0.5f;
        var step = size / divisions;

        for (var i = 0; i <= divisions; i++)
        {
            var offset = -half + i * step;
            Line(center + new Vector3(-half, 0.0f, offset), center + new Vector3(half, 0.0f, offset), color);
            Line(center + new Vector3(offset, 0.0f, -half), center + new Vector3(offset, 0.0f, half), color);
        }
    }

    public void Frustum(Matrix4x4 viewProjection, GraphicsColor color)
    {
        if (!Matrix4x4.Invert(viewProjection, out var inverse))
        {
            return;
        }

        Span<Vector3> corners = stackalloc Vector3[8];
        ReadOnlySpan<Vector3> clipCorners =
        [
            new(-1.0f, -1.0f, -1.0f), new(1.0f, -1.0f, -1.0f), new(1.0f, 1.0f, -1.0f), new(-1.0f, 1.0f, -1.0f),
            new(-1.0f, -1.0f, 1.0f), new(1.0f, -1.0f, 1.0f), new(1.0f, 1.0f, 1.0f), new(-1.0f, 1.0f, 1.0f)
        ];

        for (var i = 0; i < 8; i++)
        {
            var clip = new Vector4(clipCorners[i], 1.0f);
            var world = TransformColumnVector(inverse, clip);
            corners[i] = new Vector3(world.X / world.W, world.Y / world.W, world.Z / world.W);
        }

        Line(corners[0], corners[1], color);
        Line(corners[1], corners[2], color);
        Line(corners[2], corners[3], color);
        Line(corners[3], corners[0], color);

        Line(corners[4], corners[5], color);
        Line(corners[5], corners[6], color);
        Line(corners[6], corners[7], color);
        Line(corners[7], corners[4], color);

        Line(corners[0], corners[4], color);
        Line(corners[1], corners[5], color);
        Line(corners[2], corners[6], color);
        Line(corners[3], corners[7], color);
    }

    // Three great circles in the XY, YZ, and XZ planes. Segments is the
    // number of line segments around each circle (24 is a good default).
    public void Sphere(Vector3 center, float radius, GraphicsColor color, int segments)
    {
        if (radius <= 0.0f || segments < 3)
        {
            return;
        }
        CircleStrip(center, Vector3.UnitX * radius, Vector3.UnitY * radius, segments, color);
        CircleStrip(center, Vector3.UnitY * radius, Vector3.UnitZ * radius, segments, color);
        CircleStrip(center, Vector3.UnitX * radius, Vector3.UnitZ * radius, segments, color);
    }

    // Square in the plane perpendicular to `normal`, centered at `center`,
    // edge length = size. Plus a normal stub of length size*0.5 from the
    // center along the normal for direction disambiguation.
    public void Plane(Vector3 center, Vector3 normal, float size, GraphicsColor color)
    {
        if (size <= 0.0f)
        {
            return;
        }
        var n = Vector3.Normalize(normal);
        if (n.LengthSquared() < 1e-6f)
        {
            return;
        }
        // Pick a tangent that isn't parallel to the normal.
        var seed = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(n, seed)) * (size * 0.5f);
        var v = Vector3.Normalize(Vector3.Cross(n, u)) * (size * 0.5f);

        var p00 = center - u - v;
        var p10 = center + u - v;
        var p11 = center + u + v;
        var p01 = center - u + v;
        Line(p00, p10, color);
        Line(p10, p11, color);
        Line(p11, p01, color);
        Line(p01, p00, color);
        Line(center, center + n * (size * 0.5f), color);
    }

    public void Ray(Vector3 origin, Vector3 direction, float length, GraphicsColor color)
    {
        if (direction.LengthSquared() < 1e-12f || length <= 0.0f)
        {
            return;
        }
        var end = origin + Vector3.Normalize(direction) * length;
        DrawArrow(origin, end, color);
    }

    // Capsule = two endcap circles + 4 longitudinal lines connecting them.
    // For a tight visual, we also draw two half-arcs on the endcaps so the
    // hemisphere is visible even with line-only rendering.
    public void Capsule(Vector3 a, Vector3 b, float radius, GraphicsColor color, int segments)
    {
        if (radius <= 0.0f || segments < 3)
        {
            return;
        }
        var axis = b - a;
        var axisLen = axis.Length();
        if (axisLen < 1e-6f)
        {
            Sphere(a, radius, color, segments);
            return;
        }
        var axisUnit = axis / axisLen;
        // Build an orthonormal basis (axisUnit, right, up).
        var seed = MathF.Abs(axisUnit.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var right = Vector3.Normalize(Vector3.Cross(axisUnit, seed)) * radius;
        var up = Vector3.Normalize(Vector3.Cross(axisUnit, right)) * radius;

        CircleStrip(a, right, up, segments, color);
        CircleStrip(b, right, up, segments, color);

        // 4 longitudinal lines (right, -right, up, -up offsets).
        Line(a + right, b + right, color);
        Line(a - right, b - right, color);
        Line(a + up, b + up, color);
        Line(a - up, b - up, color);

        // Hemispherical half-arcs on each cap so the cap is recognizable.
        ArcStrip(a, -axisUnit * radius, right, segments / 2, color);
        ArcStrip(a, -axisUnit * radius, up, segments / 2, color);
        ArcStrip(b, axisUnit * radius, right, segments / 2, color);
        ArcStrip(b, axisUnit * radius, up, segments / 2, color);
    }

    // The Obb transform maps the unit cube [-1,1]^3 into world space.
    // Translation lives in column 4; basis columns 1-3 carry rotation + extents.
    public void Obb(Matrix4x4 transform, GraphicsColor color)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        ReadOnlySpan<Vector3> unit =
        [
            new(-1, -1, -1), new( 1, -1, -1), new( 1,  1, -1), new(-1,  1, -1),
            new(-1, -1,  1), new( 1, -1,  1), new( 1,  1,  1), new(-1,  1,  1),
        ];
        for (var i = 0; i < 8; i++)
        {
            var v = new Vector4(unit[i], 1.0f);
            var w = TransformColumnVector(transform, v);
            // Obb transforms are affine (no projection), so w.W is 1.
            corners[i] = new Vector3(w.X, w.Y, w.Z);
        }
        // 12 edges of the cube.
        Line(corners[0], corners[1], color);
        Line(corners[1], corners[2], color);
        Line(corners[2], corners[3], color);
        Line(corners[3], corners[0], color);
        Line(corners[4], corners[5], color);
        Line(corners[5], corners[6], color);
        Line(corners[6], corners[7], color);
        Line(corners[7], corners[4], color);
        Line(corners[0], corners[4], color);
        Line(corners[1], corners[5], color);
        Line(corners[2], corners[6], color);
        Line(corners[3], corners[7], color);
    }

    // 3D crosshair: three axis-aligned segments through `center`, each
    // running 2*size in length.
    public void Cross(Vector3 center, float size, GraphicsColor color)
    {
        if (size <= 0.0f)
        {
            return;
        }
        Line(center - Vector3.UnitX * size, center + Vector3.UnitX * size, color);
        Line(center - Vector3.UnitY * size, center + Vector3.UnitY * size, color);
        Line(center - Vector3.UnitZ * size, center + Vector3.UnitZ * size, color);
    }

    // Open cone: base circle + lines from apex to each base sample.
    public void Cone(Vector3 apex, Vector3 axis, float length, float halfAngleRad, GraphicsColor color, int segments)
    {
        if (length <= 0.0f || halfAngleRad <= 0.0f || segments < 3)
        {
            return;
        }
        var n = Vector3.Normalize(axis);
        if (n.LengthSquared() < 1e-6f)
        {
            return;
        }
        var baseCenter = apex + n * length;
        var baseRadius = length * MathF.Tan(halfAngleRad);

        var seed = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(n, seed)) * baseRadius;
        var v = Vector3.Normalize(Vector3.Cross(n, u)) * baseRadius;

        var prev = baseCenter + u;
        for (var i = 1; i <= segments; i++)
        {
            var t = (float)i / segments * MathF.Tau;
            var pt = baseCenter + u * MathF.Cos(t) + v * MathF.Sin(t);
            Line(prev, pt, color);
            prev = pt;
        }
        // 4 spokes from apex to cardinal base points — keeps the cone
        // readable without flooding it with one line per base segment.
        Line(apex, baseCenter + u, color);
        Line(apex, baseCenter - u, color);
        Line(apex, baseCenter + v, color);
        Line(apex, baseCenter - v, color);
    }

    public void Arrow(Vector3 from, Vector3 to, GraphicsColor color)
    {
        DrawArrow(from, to, color);
    }

    // Edges is a flat (i0, i1, i0, i1, ...) array. Odd-length input
    // drops the trailing dangling index. Indices out of range silently
    // skip — wireframes are diagnostic output; a malformed edge list
    // shouldn't take down rendering.
    public void MeshWireframe(IReadOnlyList<Vector3> vertices, IReadOnlyList<int> edges, GraphicsColor color)
    {
        var n = vertices.Count;
        var pairCount = edges.Count / 2;
        for (var i = 0; i < pairCount; i++)
        {
            var a = edges[i * 2];
            var b = edges[i * 2 + 1];
            if ((uint)a >= (uint)n || (uint)b >= (uint)n)
            {
                continue;
            }
            Line(vertices[a], vertices[b], color);
        }
    }

    // One line per (position, normal) pair. Mismatched lengths render
    // up to the shorter of the two — same diagnostic-tolerance rule as
    // MeshWireframe.
    public void Normals(IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> normals, float length, GraphicsColor color)
    {
        if (length <= 0.0f)
        {
            return;
        }
        var count = Math.Min(positions.Count, normals.Count);
        for (var i = 0; i < count; i++)
        {
            Line(positions[i], positions[i] + normals[i] * length, color);
        }
    }

    // Shared arrowhead helper for Arrow and Ray.
    private void DrawArrow(Vector3 from, Vector3 to, GraphicsColor color)
    {
        Line(from, to, color);
        var shaft = to - from;
        var shaftLen = shaft.Length();
        if (shaftLen < 1e-6f)
        {
            return;
        }
        var n = shaft / shaftLen;
        var headLength = MathF.Min(shaftLen * 0.2f, 0.5f);
        var headRadius = headLength * 0.5f;
        var seed = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(n, seed)) * headRadius;
        var v = Vector3.Normalize(Vector3.Cross(n, u)) * headRadius;
        var basePos = to - n * headLength;
        Line(to, basePos + u, color);
        Line(to, basePos - u, color);
        Line(to, basePos + v, color);
        Line(to, basePos - v, color);
    }

    private void CircleStrip(Vector3 center, Vector3 axisA, Vector3 axisB, int segments, GraphicsColor color)
    {
        var prev = center + axisA;
        for (var i = 1; i <= segments; i++)
        {
            var t = (float)i / segments * MathF.Tau;
            var pt = center + axisA * MathF.Cos(t) + axisB * MathF.Sin(t);
            Line(prev, pt, color);
            prev = pt;
        }
    }

    // Half-arc from `from` (= center + axisFrom) to `center + axisTo`,
    // following the great-circle path. Used by capsule endcaps.
    private void ArcStrip(Vector3 center, Vector3 axisFrom, Vector3 axisTo, int segments, GraphicsColor color)
    {
        if (segments < 2)
        {
            segments = 2;
        }
        var prev = center + axisFrom;
        for (var i = 1; i <= segments; i++)
        {
            var t = (float)i / segments * (MathF.PI * 0.5f);
            var pt = center + axisFrom * MathF.Cos(t) + axisTo * MathF.Sin(t);
            Line(prev, pt, color);
            prev = pt;
        }
    }

    public void Submit(RenderPassBuilder pass, Matrix4x4 viewProjection)
    {
        ArgumentNullException.ThrowIfNull(pass);

        if (vertexCount == 0)
        {
            return;
        }

        var byteCount = vertexCount * VertexPosition3Color.Layout.Stride;
        device.UpdateVertexBuffer(vertexBuffer, uploadBuffer.AsSpan(0, byteCount));

        var frameMesh = new Mesh("debug", vertexBuffer, indexBuffer, vertexCount, Blix.Geometry.Bounds3.Empty);
        pass.DrawMesh(frameMesh, material, perDrawUniforms:
        [
            new ShaderUniform("uViewProjection", new Matrix4x4Uniform(viewProjection))
        ]);

        vertexCount = 0;
    }

    private void WriteVertex(int index, Vector3 position, GraphicsColor color)
    {
        var floats = MemoryMarshal.Cast<byte, float>(uploadBuffer.AsSpan(index * VertexPosition3Color.Layout.Stride, VertexPosition3Color.Layout.Stride));
        floats[0] = position.X;
        floats[1] = position.Y;
        floats[2] = position.Z;
        floats[3] = color.Red;
        floats[4] = color.Green;
        floats[5] = color.Blue;
        floats[6] = color.Alpha;
    }

    // GraphicsMatrices uses column-vector convention (translation in column 4 / M14).
    // System.Numerics.Vector4.Transform applies row-vector convention, which gives a
    // wrong result on those matrices. Do the M * v multiplication explicitly.
    private static Vector4 TransformColumnVector(Matrix4x4 m, Vector4 v)
    {
        return new Vector4(
            m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z + m.M14 * v.W,
            m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z + m.M24 * v.W,
            m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z + m.M34 * v.W,
            m.M41 * v.X + m.M42 * v.Y + m.M43 * v.Z + m.M44 * v.W);
    }
}
