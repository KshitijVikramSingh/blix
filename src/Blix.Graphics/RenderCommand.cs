namespace Blix.Graphics;

public abstract record RenderCommand;

// Per-draw scissor rectangle in framebuffer pixels, origin top-left. Used by
// the ImGui overlay to clip each draw command to its widget's clip rect.
// Vulkan honors it via vkCmdSetScissor (scissor is dynamic state on every
// pipeline); the GL backend currently ignores it (GL's ImGui path manages
// scissor itself). null on a draw means "use the pass-wide scissor".
public readonly record struct ScissorRect(int X, int Y, int Width, int Height);

// Wrap one or more DrawIndexedCommands to scope a GL occlusion query.
// QueryId is allocated by the graphics device's occlusion-query pool and
// the result is read back asynchronously (typically next frame). The
// backend issues glBeginQuery(GL_ANY_SAMPLES_PASSED, id) on the begin
// and glEndQuery on the end; any draws between them contribute samples
// to the result.
public sealed record BeginOcclusionQueryCommand(int QueryId) : RenderCommand;
public sealed record EndOcclusionQueryCommand : RenderCommand;

// A compute dispatch (Vulkan-only). The pipeline is a compute pipeline; the
// work-group counts are the vkCmdDispatch arguments. Uniforms write the
// program's UBOs; Textures bind sampled (read) and storage (read/write) images
// — the backend picks the descriptor type from the program's interface slot
// (StorageImage → bound in GENERAL and barriered for compute write, SampledImage
// → combined sampler). PushConstants matches the declared ranges. Recorded as a
// compute pass via RenderCommandList.ComputePass; GL rejects it.
public sealed record DispatchCommand(
    PipelineHandle Pipeline,
    int GroupsX,
    int GroupsY,
    int GroupsZ,
    IReadOnlyList<ShaderUniform> Uniforms,
    IReadOnlyList<ShaderTextureBinding> Textures,
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
    // Vulkan-only: a MaterialBindings handle whose descriptor set should be
    // bound at its declared set index for this draw. GL backend rejects
    // non-null with a clear error — GL demos use Blix.Render.Material which
    // flattens into Uniforms + Textures instead. Default null preserves
    // historical behavior for every caller that doesn't set this.
    MaterialHandle? Material = null,
    // Vulkan-only: per-draw push-constant payload. Bytes are laid out to
    // match the shader's declared PushConstantRange(s) — total length must
    // equal the sum of declared range sizes. Vulkan binds via
    // vkCmdPushConstants once per declared range. GL rejects non-null
    // (push constants have no direct GL equivalent — emulate via uniform
    // writes in the GL backend instead).
    byte[]? PushConstants = null,
    // Vulkan-only: a SECOND MaterialBindings handle bound at its declared
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
    // historical behavior. GL backend ignores it (no GL caller sets it).
    int VertexOffset = 0,
    // Per-draw scissor clip (framebuffer pixels). null = pass-wide scissor.
    // See ScissorRect. Honored by Vulkan, ignored by GL.
    ScissorRect? Scissor = null) : RenderCommand;
