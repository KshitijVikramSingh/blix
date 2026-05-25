using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Render;

namespace Blix.Runtime.Silk;

// Vulkan-native debug-line renderer used by Window to translate
// debug.Draw.* commands into a real Pass on the swapchain. Mirrors the
// shape of Blix.Render.DebugDraw but bypasses the GL-only
// CreateShaderProgram(ShaderSources) path — pre-compiled SPIR-V from the
// library's Shaders/ output dir feeds straight into the Vulkan backend's
// CreateShaderProgramFromSpv.
//
// Lines render WITHOUT depth test or write so they're always visible —
// the right default for debug overlays. If depth-tested debug becomes
// useful, expose a DepthState toggle on construction.
public sealed class VkLineDrawer : IDisposable
{
    private const int MaxLineCount = 4_000;
    private const int MaxVertexCount = MaxLineCount * 2;
    private const int StrideBytes = 28; // matches VertexPosition3Color.Layout.Stride (3 floats pos + 4 floats color)

    private readonly VulkanGraphicsDevice device;
    private readonly VertexBufferHandle vertexBuffer;
    private readonly IndexBufferHandle indexBuffer;
    private readonly ShaderProgramHandle shader;
    private readonly PipelineHandle pipeline;
    private readonly byte[] uploadBuffer;
    private int vertexCount;
    private bool disposed;

    public VkLineDrawer(VulkanGraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;

        // Pre-allocate the dynamic vertex buffer at the max size we expect.
        // Vulkan-backend's UpdateVertexBuffer memcpys into the host-visible
        // buffer per frame; cheap as long as MaxVertexCount stays modest.
        var emptyVertices = new VertexPosition3Color[MaxVertexCount];
        var initialData = new VertexBufferData(
            new VertexBufferDescription(VertexPosition3Color.Layout, MaxVertexCount, GraphicsBufferUsage.Dynamic),
            VertexPosition3Color.Pack(emptyVertices));
        vertexBuffer = device.CreateVertexBuffer(initialData, name: "debugline.vb");

        // Pre-baked sequential index buffer — every two consecutive vertices
        // form one line segment. No reuse, but simpler than tracking pairs.
        var indices = new ushort[MaxVertexCount];
        for (var i = 0; i < MaxVertexCount; i++) indices[i] = (ushort)i;
        indexBuffer = device.CreateIndexBuffer(indices, GraphicsBufferUsage.Static, name: "debugline.ib");

        var shaderDir = Path.Combine(AppContext.BaseDirectory, "Shaders");
        var vertSpv = File.ReadAllBytes(Path.Combine(shaderDir, "debugline.vert.spv"));
        var fragSpv = File.ReadAllBytes(Path.Combine(shaderDir, "debugline.frag.spv"));
        var uniformLayout = new UniformBlockLayout(
            TotalSize: 64,
            Members: new[] { new UniformBlockMember("uViewProjection", Offset: 0, Size: 64) });
        var lineInterface = new ShaderInterface(new[]
        {
            new DescriptorSetSlot(
                Set: 0,
                Binding: 0,
                Type: ShaderResourceType.UniformBuffer,
                Stages: ShaderStages.Vertex | ShaderStages.Fragment,
                BlockLayout: uniformLayout),
        });
        shader = device.CreateShaderProgramFromSpv(vertSpv, fragSpv, lineInterface, "debugline");

        pipeline = device.CreatePipeline(new PipelineDescription(
            shader,
            VertexPosition3Color.Layout,
            PrimitiveTopology.Lines,
            DepthState.Disabled,
            RasterizerState.NoCulling,
            BlendState.AlphaBlend), "debugline");

        uploadBuffer = new byte[MaxVertexCount * StrideBytes];
    }

    public bool HasLines => vertexCount > 0;

    public void Clear() => vertexCount = 0;

    public void Line(Vector3 a, Vector3 b, GraphicsColor color)
    {
        if (vertexCount + 2 > MaxVertexCount) return; // silently drop on overflow — debug draw shouldn't crash
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
        // Bottom and top rings
        Line(c000, c100, color); Line(c100, c101, color); Line(c101, c001, color); Line(c001, c000, color);
        Line(c010, c110, color); Line(c110, c111, color); Line(c111, c011, color); Line(c011, c010, color);
        // Vertical posts
        Line(c000, c010, color); Line(c100, c110, color); Line(c101, c111, color); Line(c001, c011, color);
    }

    public void Cross(Vector3 center, float size, GraphicsColor color)
    {
        if (size <= 0f) return;
        Line(center - Vector3.UnitX * size, center + Vector3.UnitX * size, color);
        Line(center - Vector3.UnitY * size, center + Vector3.UnitY * size, color);
        Line(center - Vector3.UnitZ * size, center + Vector3.UnitZ * size, color);
    }

    public void Arrow(Vector3 from, Vector3 to, GraphicsColor color)
    {
        Line(from, to, color);
        var shaft = to - from;
        var len = shaft.Length();
        if (len < 1e-6f) return;
        var n = shaft / len;
        var headLen = MathF.Min(len * 0.2f, 0.5f);
        var headRadius = headLen * 0.5f;
        var seed = MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(n, seed)) * headRadius;
        var v = Vector3.Normalize(Vector3.Cross(n, u)) * headRadius;
        var basePos = to - n * headLen;
        Line(to, basePos + u, color);
        Line(to, basePos - u, color);
        Line(to, basePos + v, color);
        Line(to, basePos - v, color);
    }

    // OBB transform maps the unit cube [-1,1]^3 into world space. Translation in
    // column 4. Used to draw the actual world-space wireframe of a rotated mesh.
    public void Obb(Matrix4x4 transform, GraphicsColor color)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        ReadOnlySpan<Vector3> unit = stackalloc Vector3[]
        {
            new(-1, -1, -1), new( 1, -1, -1), new( 1,  1, -1), new(-1,  1, -1),
            new(-1, -1,  1), new( 1, -1,  1), new( 1,  1,  1), new(-1,  1,  1),
        };
        for (var i = 0; i < 8; i++)
        {
            var v = new Vector4(unit[i], 1f);
            // .NET Matrix4x4 stores row-vector form. For a transform built by
            // composing CreateRotation*/CreateScale/CreateTranslation, the
            // correct multiplication is `localPos * transform` (row-vector).
            var w = Vector4.Transform(v, transform);
            corners[i] = new Vector3(w.X, w.Y, w.Z);
        }
        // 12 edges
        Line(corners[0], corners[1], color); Line(corners[1], corners[2], color);
        Line(corners[2], corners[3], color); Line(corners[3], corners[0], color);
        Line(corners[4], corners[5], color); Line(corners[5], corners[6], color);
        Line(corners[6], corners[7], color); Line(corners[7], corners[4], color);
        Line(corners[0], corners[4], color); Line(corners[1], corners[5], color);
        Line(corners[2], corners[6], color); Line(corners[3], corners[7], color);
    }

    public void Submit(RenderPassBuilder pass, Matrix4x4 viewProjection)
    {
        if (vertexCount == 0) return;
        var byteCount = vertexCount * StrideBytes;
        device.UpdateVertexBuffer(vertexBuffer, uploadBuffer.AsSpan(0, byteCount));
        pass.DrawIndexed(
            vertexBuffer: vertexBuffer,
            indexBuffer: indexBuffer,
            pipeline: pipeline,
            indexCount: vertexCount,
            uniforms: new[] { new ShaderUniform("uViewProjection", new Matrix4x4Uniform(viewProjection)) },
            textures: Array.Empty<ShaderTextureBinding>());
        vertexCount = 0;
    }

    private void WriteVertex(int index, Vector3 position, GraphicsColor color)
    {
        var floats = MemoryMarshal.Cast<byte, float>(uploadBuffer.AsSpan(index * StrideBytes, StrideBytes));
        floats[0] = position.X;
        floats[1] = position.Y;
        floats[2] = position.Z;
        floats[3] = color.Red;
        floats[4] = color.Green;
        floats[5] = color.Blue;
        floats[6] = color.Alpha;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        device.DestroyPipeline(pipeline);
        device.DestroyShaderProgram(shader);
        device.DestroyVertexBuffer(vertexBuffer);
        device.DestroyIndexBuffer(indexBuffer);
    }
}
