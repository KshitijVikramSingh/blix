namespace Blix.Graphics;

public abstract record RenderCommand;

public sealed record DrawIndexedCommand(
    VertexBufferHandle VertexBuffer,
    IndexBufferHandle IndexBuffer,
    PipelineHandle Pipeline,
    int IndexCount,
    IReadOnlyList<ShaderUniform> Uniforms,
    IReadOnlyList<ShaderTextureBinding> Textures,
    // Index of the first element in the index buffer to draw. Sub-range draws
    // out of a shared index buffer — e.g. SpriteBatch packs one upload across
    // multiple texture partitions and issues each partition's draw with a
    // different offset. 0 means "draw from the start", which preserves the
    // historical behavior for every caller that doesn't set this.
    int IndexOffset = 0) : RenderCommand;
