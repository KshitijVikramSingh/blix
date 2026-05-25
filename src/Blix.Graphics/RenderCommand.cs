namespace Blix.Graphics;

public abstract record RenderCommand;

// Wrap one or more DrawIndexedCommands to scope a GL occlusion query.
// QueryId is allocated by the graphics device's occlusion-query pool and
// the result is read back asynchronously (typically next frame). The
// backend issues glBeginQuery(GL_ANY_SAMPLES_PASSED, id) on the begin
// and glEndQuery on the end; any draws between them contribute samples
// to the result.
public sealed record BeginOcclusionQueryCommand(int QueryId) : RenderCommand;
public sealed record EndOcclusionQueryCommand : RenderCommand;

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
