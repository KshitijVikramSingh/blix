namespace Blix.Graphics;

public abstract record RenderCommand;

// Per-draw scissor rectangle in framebuffer pixels, origin top-left. Used by
// the ImGui overlay to clip each draw command to its widget's clip rect.
// Honored via vkCmdSetScissor (scissor is dynamic state on every pipeline).
// null on a draw means "use the pass-wide scissor".
public readonly record struct ScissorRect(int X, int Y, int Width, int Height);

// Wrap one or more DrawIndexedCommands to scope an occlusion query.
// QueryId is allocated by the graphics device's occlusion-query pool and
// the result is read back asynchronously (typically next frame); any draws
// between the begin and end contribute samples to the result.
public sealed record BeginOcclusionQueryCommand(int QueryId) : RenderCommand;
public sealed record EndOcclusionQueryCommand : RenderCommand;

// A compute dispatch (Vulkan-only). The pipeline is a compute pipeline; the
// work-group counts are the vkCmdDispatch arguments. Uniforms write the
// program's UBOs; Textures bind sampled (read) and storage (read/write) images
// — the backend picks the descriptor type from the program's interface slot
// (StorageImage → bound in GENERAL and barriered for compute write, SampledImage
// → combined sampler). PushConstants matches the declared ranges. Recorded as a
// compute pass via RenderCommandList.ComputePass.
public sealed record DispatchCommand(
    PipelineHandle Pipeline,
    int GroupsX,
    int GroupsY,
    int GroupsZ,
    IReadOnlyList<ShaderUniform> Uniforms,
    IReadOnlyList<ShaderTextureBinding> Textures,
    byte[]? PushConstants = null) : RenderCommand;

// Per-material indirect multi-draw (Vulkan-only). Binds the same state as a
// DrawIndexedCommand (pipeline, shared VB/IB, set0 uniforms/textures, set2
// material, push constants), then issues one vkCmdDrawIndexedIndirect that reads
// DrawCount VkDrawIndexedIndirectCommand structs from IndirectBuffer starting at
// IndirectByteOffset. All sub-draws share the bound state — group objects by
// (pipeline, material) and emit one of these per group.
public sealed record DrawIndexedIndirectCommand(
    VertexBufferHandle VertexBuffer,
    IndexBufferHandle IndexBuffer,
    PipelineHandle Pipeline,
    IndirectBufferHandle IndirectBuffer,
    int IndirectByteOffset,
    int DrawCount,
    IReadOnlyList<ShaderUniform> Uniforms,
    IReadOnlyList<ShaderTextureBinding> Textures,
    MaterialHandle? Material = null,
    byte[]? PushConstants = null) : RenderCommand;

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
    int IndexOffset = 0,
    // A MaterialBindings handle whose descriptor set is bound at its declared
    // set index for this draw. Default null preserves historical behavior for
    // every caller that doesn't set this.
    MaterialHandle? Material = null,
    // Per-draw push-constant payload. Bytes are laid out to match the shader's
    // declared PushConstantRange(s) — total length must equal the sum of
    // declared range sizes. Bound via vkCmdPushConstants once per declared range.
    byte[]? PushConstants = null,
    // A SECOND MaterialBindings handle bound at its declared
    // set index. The canonical use is the skinned-mesh path — Material
    // owns set 2 (per-material albedo/tint), PerDrawMaterial owns set 3
    // (per-draw bone palette SSBO, framesInFlight-replicated). The engine
    // binds both at their respective SetIndex values; they must differ
    // from each other and from any per-frame engine-owned set.
    MaterialHandle? PerDrawMaterial = null,
    // Vulkan: vertexOffset added to every index before vertex fetch
    // (vkCmdDrawIndexed's vertexOffset). Lets a single shared vertex buffer
    // hold concatenated sub-meshes — the ImGui overlay packs every cmd-list's
    // vertices into one buffer and draws each with its own base. 0 preserves
    // historical behavior.
    int VertexOffset = 0,
    // Per-draw scissor clip (framebuffer pixels). null = pass-wide scissor.
    // See ScissorRect.
    ScissorRect? Scissor = null,
    // Vulkan: vkCmdDrawIndexed instanceCount. 1 (the default) preserves the
    // historical single-instance behavior for every caller. >1 issues one
    // instanced draw of the same index range; the vertex shader reads
    // gl_InstanceIndex (the Vulkan-GLSL built-in) to index a per-instance
    // storage buffer — bound via PerDrawMaterial — for its transform/tint.
    int InstanceCount = 1,
    // Vulkan: byte offset at which the vertex buffer is BOUND (the pOffsets
    // argument to vkCmdBindVertexBuffers) — distinct from VertexOffset, which is
    // an index bias added at fetch. Lets a sub-slice of a shared/transient vertex
    // buffer be drawn with base-0 indices: the bind shifts the buffer's origin to
    // the slice, so index 0 reads the slice's first vertex. The transient arena
    // (IGraphicsDevice.AllocVertices) returns a stride-aligned offset for exactly
    // this. 0 preserves historical whole-buffer behavior for every caller.
    ulong VertexBufferByteOffset = 0) : RenderCommand;
