namespace Blix.Graphics;

public readonly record struct VertexBufferHandle(int Id);

public readonly record struct IndexBufferHandle(int Id);

public readonly record struct ShaderProgramHandle(int Id);

public readonly record struct PipelineHandle(int Id);

public readonly record struct TextureHandle(int Id);

// Opaque per-backend handle for a MaterialBindings instance. Construction
// goes through the backend (Vulkan: VulkanGraphicsDevice.CreateMaterial).
// GL backend rejects this on draw — GL demos use Blix.Render.Material which
// flattens into Uniforms + Textures inside DrawIndexedCommand instead.
public readonly record struct MaterialHandle(int Id);

public readonly record struct RenderSurfaceHandle(int Id)
{
    public static RenderSurfaceHandle Default { get; } = new(0);
}
