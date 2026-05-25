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
}
