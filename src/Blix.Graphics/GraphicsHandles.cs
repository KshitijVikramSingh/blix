namespace Blix.Graphics;

public readonly record struct VertexBufferHandle(int Id);

public readonly record struct IndexBufferHandle(int Id);

public readonly record struct ShaderProgramHandle(int Id);

public readonly record struct PipelineHandle(int Id);

public readonly record struct TextureHandle(int Id);

public readonly record struct RenderSurfaceHandle(int Id)
{
    public static RenderSurfaceHandle Default { get; } = new(0);
}
