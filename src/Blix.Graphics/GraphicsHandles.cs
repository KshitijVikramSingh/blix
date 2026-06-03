namespace Blix.Graphics;

public readonly record struct VertexBufferHandle(int Id);

public readonly record struct IndexBufferHandle(int Id);

// A sub-allocation from the per-frame transient vertex arena: a slice of one of
// the arena's ring-slot vertex buffers. Bind Buffer at ByteOffset and draw the
// matching index buffer with base-0 indices — ByteOffset is aligned to the vertex
// stride so firstVertex = 0 addresses the slice correctly. Valid only for the
// frame it was allocated in (the underlying ring slot is recycled a few frames
// later). Created via IGraphicsDevice.AllocVertices.
public readonly record struct TransientVertexSlice(
    VertexBufferHandle Buffer, ulong ByteOffset, int ByteLength);

// A GPU buffer of VkDrawIndexedIndirectCommand structs, replicated per
// frame-in-flight and rewritten each frame (CPU-filled indirect path). Consumed
// by DrawIndexedIndirectCommand. Vulkan-only; created via
// VulkanGraphicsDevice.CreateIndirectBuffer.
public readonly record struct IndirectBufferHandle(int Id);

public readonly record struct ShaderProgramHandle(int Id);

public readonly record struct PipelineHandle(int Id);

public readonly record struct TextureHandle(int Id);

// Opaque per-backend handle for a MaterialBindings instance. Construction
// goes through the backend (VulkanGraphicsDevice.CreateMaterial).
public readonly record struct MaterialHandle(int Id);

public readonly record struct RenderSurfaceHandle(int Id)
{
    public static RenderSurfaceHandle Default { get; } = new(0);
}
