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
