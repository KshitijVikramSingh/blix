namespace Blix.Graphics;

public sealed class RenderCommandList
{
    private readonly List<RenderPass> passes = [];

    public IReadOnlyList<RenderPass> Passes => passes;

    public void Pass(string name, RenderPassDescription description, Action<RenderPassBuilder> record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(record);

        var builder = new RenderPassBuilder();
        record(builder);
        passes.Add(new RenderPass(name, description, builder.Commands));
    }
}

public sealed class RenderPassBuilder
{
    private readonly List<RenderCommand> commands = [];

    internal IReadOnlyList<RenderCommand> Commands => commands;

    public void DrawIndexed(
        VertexBufferHandle vertexBuffer,
        IndexBufferHandle indexBuffer,
        PipelineHandle pipeline,
        int indexCount,
        IReadOnlyList<ShaderUniform> uniforms,
        IReadOnlyList<ShaderTextureBinding> textures)
    {
        commands.Add(new DrawIndexedCommand(vertexBuffer, indexBuffer, pipeline, indexCount, uniforms, textures));
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
        commands.Add(new DrawIndexedCommand(vertexBuffer, indexBuffer, pipeline, indexCount, uniforms, textures, indexOffset));
    }
}
