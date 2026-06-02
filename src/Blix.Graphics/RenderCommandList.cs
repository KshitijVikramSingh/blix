namespace Blix.Graphics;

public sealed class RenderCommandList
{
    private readonly List<RenderPass> passes = [];
    private readonly IFrameRecorder? recorder;

    // The optional recorder is invoked synchronously around each Pass and
    // each DrawIndexed call. Null is the default for headless paths and
    // for callers (offscreen renderers, tools) that don't want to feed a
    // diagnostics system.
    public RenderCommandList(IFrameRecorder? recorder = null)
    {
        this.recorder = recorder;
    }

    public IReadOnlyList<RenderPass> Passes => passes;

    public void Pass(string name, RenderPassDescription description, Action<RenderPassBuilder> record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(record);

        recorder?.OnPassBegin(name);
        try
        {
            var builder = new RenderPassBuilder(recorder);
            record(builder);
            passes.Add(new RenderPass(name, description, builder.Commands));
        }
        finally
        {
            // OnPassEnd must fire even if `record` throws, otherwise the
            // recorder's pass scope leaks into subsequent passes and the
            // attribution silently corrupts.
            recorder?.OnPassEnd();
        }
    }

    // Record a compute pass — a single dispatch with no framebuffer. Ordered
    // in submission with the graphics passes around it (declaration order), so
    // a compute pass that writes a storage image must be recorded before the
    // graphics pass that samples it. Vulkan-only.
    public void ComputePass(string name, DispatchCommand dispatch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(dispatch);

        recorder?.OnPassBegin(name);
        try
        {
            var desc = new RenderPassDescription(
                RenderSurfaceHandle.Default, Array.Empty<GraphicsColor?>(), ClearDepth: false, Compute: true);
            passes.Add(new RenderPass(name, desc, new RenderCommand[] { dispatch }));
        }
        finally
        {
            recorder?.OnPassEnd();
        }
    }
}

public sealed class RenderPassBuilder
{
    private readonly List<RenderCommand> commands = [];
    private readonly IFrameRecorder? recorder;

    internal RenderPassBuilder(IFrameRecorder? recorder)
    {
        this.recorder = recorder;
    }

    internal IReadOnlyList<RenderCommand> Commands => commands;

    public void DrawIndexed(
        VertexBufferHandle vertexBuffer,
        IndexBufferHandle indexBuffer,
        PipelineHandle pipeline,
        int indexCount,
        IReadOnlyList<ShaderUniform> uniforms,
        IReadOnlyList<ShaderTextureBinding> textures)
    {
        var command = new DrawIndexedCommand(vertexBuffer, indexBuffer, pipeline, indexCount, uniforms, textures);
        commands.Add(command);
        recorder?.OnDraw(in command);
    }

    // Per-material indirect multi-draw: one call issuing drawCount sub-draws from
    // an indirect buffer, all sharing the bound state (pipeline + shared VB/IB +
    // set0 uniforms/textures + set2 material + push). Vulkan-only. See
    // DrawIndexedIndirectCommand.
    public void DrawIndexedIndirect(
        VertexBufferHandle vertexBuffer,
        IndexBufferHandle indexBuffer,
        PipelineHandle pipeline,
        IndirectBufferHandle indirectBuffer,
        int indirectByteOffset,
        int drawCount,
        IReadOnlyList<ShaderUniform> uniforms,
        IReadOnlyList<ShaderTextureBinding> textures,
        MaterialHandle? material = null,
        byte[]? pushConstants = null)
    {
        commands.Add(new DrawIndexedIndirectCommand(
            vertexBuffer, indexBuffer, pipeline, indirectBuffer, indirectByteOffset, drawCount,
            uniforms, textures, material, pushConstants));
    }

    // Scope an occlusion query around the draws that follow until
    // EndOcclusionQuery(). The QueryId must come from the graphics
    // device's occlusion-query pool; the backend issues GL begin/end
    // calls and the result becomes readable asynchronously.
    public void BeginOcclusionQuery(int queryId)
    {
        commands.Add(new BeginOcclusionQueryCommand(queryId));
    }

    public void EndOcclusionQuery()
    {
        commands.Add(new EndOcclusionQueryCommand());
    }

    // Sub-range draw: starts at indexOffset into the index buffer. Lets multiple
    // partitions share one (vertex buffer, index buffer) pair after a single
    // batched upload — without this, calling UpdateVertexBuffer between two
    // recorded DrawMesh commands would overwrite the first partition's data
    // before the GPU executed either draw.
    public void DrawIndexed(
        VertexBufferHandle vertexBuffer,
        IndexBufferHandle indexBuffer,
        PipelineHandle pipeline,
        int indexCount,
        int indexOffset,
        IReadOnlyList<ShaderUniform> uniforms,
        IReadOnlyList<ShaderTextureBinding> textures)
    {
        var command = new DrawIndexedCommand(vertexBuffer, indexBuffer, pipeline, indexCount, uniforms, textures, indexOffset);
        commands.Add(command);
        recorder?.OnDraw(in command);
    }

    // Draw with a MaterialBindings handle bound at its declared set index.
    public void DrawIndexed(
        VertexBufferHandle vertexBuffer,
        IndexBufferHandle indexBuffer,
        PipelineHandle pipeline,
        int indexCount,
        IReadOnlyList<ShaderUniform> uniforms,
        IReadOnlyList<ShaderTextureBinding> textures,
        MaterialHandle material)
    {
        var command = new DrawIndexedCommand(
            vertexBuffer, indexBuffer, pipeline, indexCount, uniforms, textures, IndexOffset: 0, Material: material);
        commands.Add(command);
        recorder?.OnDraw(in command);
    }

    // Draw with material + per-draw push-constant payload.
    public void DrawIndexed(
        VertexBufferHandle vertexBuffer,
        IndexBufferHandle indexBuffer,
        PipelineHandle pipeline,
        int indexCount,
        IReadOnlyList<ShaderUniform> uniforms,
        IReadOnlyList<ShaderTextureBinding> textures,
        MaterialHandle material,
        byte[] pushConstants,
        // Shared-buffer sub-range: firstIndex into the index buffer + vertexOffset
        // added to every index (vkCmdDrawIndexed). Lets many primitives draw out
        // of one consolidated (VB, IB) pair. 0/0 preserves whole-buffer behavior.
        int indexOffset = 0,
        int vertexOffset = 0)
    {
        var command = new DrawIndexedCommand(
            vertexBuffer, indexBuffer, pipeline, indexCount, uniforms, textures,
            IndexOffset: indexOffset, Material: material, PushConstants: pushConstants,
            VertexOffset: vertexOffset);
        commands.Add(command);
        recorder?.OnDraw(in command);
    }

    // Draw with push-constant payload but NO material (set 2 unused).
    // Shadow / depth-only passes that only need set 0 + push constants.
    public void DrawIndexed(
        VertexBufferHandle vertexBuffer,
        IndexBufferHandle indexBuffer,
        PipelineHandle pipeline,
        int indexCount,
        IReadOnlyList<ShaderUniform> uniforms,
        IReadOnlyList<ShaderTextureBinding> textures,
        byte[] pushConstants,
        // Shared-buffer sub-range (see the material overload above).
        int indexOffset = 0,
        int vertexOffset = 0)
    {
        var command = new DrawIndexedCommand(
            vertexBuffer, indexBuffer, pipeline, indexCount, uniforms, textures,
            IndexOffset: indexOffset, Material: null, PushConstants: pushConstants,
            VertexOffset: vertexOffset);
        commands.Add(command);
        recorder?.OnDraw(in command);
    }

    // Skinned-draw overload: material at its SetIndex (typically 2) +
    // perDrawMaterial at its SetIndex (typically 3, e.g. bone palette
    // SSBO). Push constants required (uModel per draw). Vulkan-only.
    public void DrawIndexed(
        VertexBufferHandle vertexBuffer,
        IndexBufferHandle indexBuffer,
        PipelineHandle pipeline,
        int indexCount,
        IReadOnlyList<ShaderUniform> uniforms,
        IReadOnlyList<ShaderTextureBinding> textures,
        MaterialHandle material,
        MaterialHandle perDrawMaterial,
        byte[] pushConstants)
    {
        var command = new DrawIndexedCommand(
            vertexBuffer, indexBuffer, pipeline, indexCount, uniforms, textures,
            IndexOffset: 0, Material: material, PushConstants: pushConstants,
            PerDrawMaterial: perDrawMaterial);
        commands.Add(command);
        recorder?.OnDraw(in command);
    }

    // Instanced draw: one index range drawn instanceCount times. Per-instance
    // data (transform + tint) rides perDrawMaterial — a storage-buffer
    // MaterialBindings at its declared set index (set 3), indexed in the vertex
    // shader by gl_InstanceIndex. uViewProjection is the typical push constant.
    // An optional set-2 material carries shared textures. Vulkan-only.
    public void DrawIndexedInstanced(
        VertexBufferHandle vertexBuffer,
        IndexBufferHandle indexBuffer,
        PipelineHandle pipeline,
        int indexCount,
        int instanceCount,
        IReadOnlyList<ShaderUniform> uniforms,
        IReadOnlyList<ShaderTextureBinding> textures,
        MaterialHandle perDrawMaterial,
        byte[] pushConstants,
        MaterialHandle? material = null)
    {
        var command = new DrawIndexedCommand(
            vertexBuffer, indexBuffer, pipeline, indexCount, uniforms, textures,
            IndexOffset: 0, Material: material, PushConstants: pushConstants,
            PerDrawMaterial: perDrawMaterial, InstanceCount: instanceCount);
        commands.Add(command);
        recorder?.OnDraw(in command);
    }

    // ImGui overlay draw: one shared (vertex, index) buffer pair holding every
    // cmd-list concatenated, indexed per draw via indexOffset + vertexOffset, a
    // per-cmd clip scissor, and scale/translate push constants. Set 0 binds the
    // font atlas; no per-frame UBO uniforms. Vulkan-only.
    public void DrawIndexed(
        VertexBufferHandle vertexBuffer,
        IndexBufferHandle indexBuffer,
        PipelineHandle pipeline,
        int indexCount,
        int indexOffset,
        int vertexOffset,
        IReadOnlyList<ShaderTextureBinding> textures,
        byte[] pushConstants,
        ScissorRect scissor)
    {
        var command = new DrawIndexedCommand(
            vertexBuffer, indexBuffer, pipeline, indexCount,
            Array.Empty<ShaderUniform>(), textures,
            IndexOffset: indexOffset, Material: null, PushConstants: pushConstants,
            PerDrawMaterial: null, VertexOffset: vertexOffset, Scissor: scissor);
        commands.Add(command);
        recorder?.OnDraw(in command);
    }

    // Skinned-shadow overload: per-draw bone palette only (no set-2
    // material — shadow shader uses set 0 + set 3 only). Vulkan-only.
    public void DrawIndexedSkinnedShadow(
        VertexBufferHandle vertexBuffer,
        IndexBufferHandle indexBuffer,
        PipelineHandle pipeline,
        int indexCount,
        IReadOnlyList<ShaderUniform> uniforms,
        IReadOnlyList<ShaderTextureBinding> textures,
        MaterialHandle perDrawMaterial,
        byte[] pushConstants)
    {
        var command = new DrawIndexedCommand(
            vertexBuffer, indexBuffer, pipeline, indexCount, uniforms, textures,
            IndexOffset: 0, Material: null, PushConstants: pushConstants,
            PerDrawMaterial: perDrawMaterial);
        commands.Add(command);
        recorder?.OnDraw(in command);
    }
}
