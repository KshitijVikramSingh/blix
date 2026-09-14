using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Render;

// A single fullscreen triangle drawn from a dummy vertex buffer. The vertex shader
// synthesizes its three clip-space positions + UVs from gl_VertexIndex (the standard
// fullscreen-triangle trick), so the buffer's CONTENTS are never read — the VB/IB exist
// only to satisfy the bind/draw call. Every post pass (bloom bright/blur, tonemap present,
// debug-view blits) is one such draw; before this they each hand-rolled an identical
// 3-vert dummy VB + {0,1,2} IB + DrawIndexed(indexCount:3).
//
// Like InstancedBatch and ParticleBatch, this owns ONLY geometry. It brings no shader and
// no policy: the CALLER hands in the pipeline (and thus the fragment shader — bright extract,
// Gaussian tap, ACES vs AgX tonemap, whatever), the push-constant bytes, and the texture
// bindings, and they ride straight through to the draw. What the fullscreen pass *means* is
// entirely the caller's; this just gets the triangle on screen.
public sealed class FullscreenPass : IDisposable
{
    /// <summary>The vertex layout a fullscreen pipeline should declare: the dummy buffer's stride, and no attributes.</summary>
    /// <remarks>
    /// <b>A fullscreen vertex shader reads gl_VertexIndex and nothing else</b>, so a pipeline that
    /// declares attributes is promising the shader inputs it does not have. Vulkan permits it and the
    /// validation layers say so every time a device is created — "Vertex attribute at location 0 not
    /// consumed by vertex shader", twice per present pipeline. Harmless, and noise in exactly the stream
    /// an instrument needs to be able to read.
    /// <para>
    /// The stride still has to match the dummy buffer this type binds, which is why the layout belongs
    /// here rather than being something each caller invents: the buffer and the layout that describes it
    /// are one decision, and today every caller restates half of it.
    /// </para>
    /// </remarks>
    public static VertexLayout Layout { get; } =
        new(VertexPosition3NormalTexture.Layout.Stride, Array.Empty<VertexAttribute>());

    private readonly VulkanGraphicsDevice device;
    private readonly VertexBufferHandle vertexBuffer;
    private readonly IndexBufferHandle indexBuffer;
    private bool disposed;

    public FullscreenPass(VulkanGraphicsDevice device, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        this.device = device;

        // Three zeroed vertices: never sampled (the vertex shader reads gl_VertexIndex),
        // but the bound pipeline declares a VertexPosition3NormalTexture layout so the
        // dummy buffer must match that stride. Matches the present.vert pipelines across
        // every demo, so this is a behaviour-preserving extraction.
        var dummyVerts = new VertexPosition3NormalTexture[]
        {
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0)),
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0)),
            new(new GraphicsVector3(0, 0, 0), new GraphicsVector3(0, 0, 1), new GraphicsVector2(0, 0)),
        };
        vertexBuffer = device.CreateVertexBuffer(
            VertexPosition3NormalTexture.CreateBufferData(dummyVerts), $"{name ?? "fullscreen"}.vb");
        indexBuffer = device.CreateIndexBuffer(new ushort[] { 0, 1, 2 }, name: $"{name ?? "fullscreen"}.ib");
    }

    // Record the fullscreen-triangle draw into the given pass scope (a RenderGraph pass or
    // an imperative commandList.Pass — both hand back a RenderPassBuilder). The caller's
    // pipeline + textures + optional push + optional set-0 uniforms are forwarded verbatim;
    // the primitive never inspects them. (Most post passes bind only textures + push; a sky
    // or analytic-background pass also binds a per-frame uniform set.)
    public void Draw(
        RenderPassBuilder pass,
        PipelineHandle pipeline,
        IReadOnlyList<ShaderTextureBinding> textures,
        byte[]? pushConstants = null,
        IReadOnlyList<ShaderUniform>? uniforms = null)
    {
        ArgumentNullException.ThrowIfNull(pass);
        var binds = uniforms ?? Array.Empty<ShaderUniform>();
        if (pushConstants is null)
        {
            pass.DrawIndexed(
                vertexBuffer, indexBuffer, pipeline, indexCount: 3, binds, textures);
        }
        else
        {
            pass.DrawIndexed(
                vertexBuffer, indexBuffer, pipeline, indexCount: 3, binds, textures, pushConstants);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        device.DestroyVertexBuffer(vertexBuffer);
        device.DestroyIndexBuffer(indexBuffer);
    }
}
