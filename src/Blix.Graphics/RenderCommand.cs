namespace Blix.Graphics;

public abstract record RenderCommand;

// Per-draw scissor rectangle in framebuffer pixels, origin top-left. Used by
// the ImGui overlay to clip each draw command to its widget's clip rect.
// Honored via vkCmdSetScissor (scissor is dynamic state on every pipeline).
// null on a draw means "use the pass-wide scissor".
public readonly record struct ScissorRect(int X, int Y, int Width, int Height);

// A compute dispatch (Vulkan-only). The pipeline is a compute pipeline; the
// work-group counts are the vkCmdDispatch arguments. Uniforms write the
// program's UBOs; Textures bind sampled (read) and storage (read/write) images
// — the backend picks the descriptor type from the program's interface slot
// (StorageImage → bound in GENERAL and barriered for compute write, SampledImage
// → combined sampler). PushConstants matches the declared ranges. Recorded as a
// compute pass via RenderCommandList.ComputePass.
// <b>Recorded commands own their push payload.</b> A pass body RECORDS; the GPU work
// happens later, at Execute. A byte[] handed over here is therefore read long after the
// caller has moved on — and a caller that reuses one scratch array across draws gives
// every draw the array's FINAL contents.
//
// That is not hypothetical. VkLineDrawer hit it (all views submitting whatever the last
// one left), StudioRenderer hit it (seven objects rendering at the seventh's transform, six
// apparently missing while the draw counts looked perfectly healthy), and the
// index-offset DrawIndexed overload below exists because the same hazard bit vertex
// buffers. Three consumers, one semantic mistake: recording accepted mutable
// caller-owned payloads whose lifetime had to secretly extend to Execute.
//
// The copy lives on the RECORD rather than at the call sites because there are ten call
// sites today and the eleventh is the one that would get it wrong. Push payloads are
// bounded by the Vulkan minimum of 128 bytes, so this is a memcpy of at most that per
// draw.
//
// MEASURED AND SPLIT, rather than left open. This note used to say the Uniforms and
// Textures lists were "not yet frozen" and that the arrays inside them "want measuring
// before it is done". The measurement was taken (RenderCommandDiagnostics, 120 frames of
// each application under BLIX_VK_VALIDATE) and it separates the two halves cleanly:
//
//   THE VALUES ARE FROZEN NOW. Every scalar and vector uniform always held its value by
//   struct copy and was never at risk; the three ARRAY variants were the only payload a
//   caller could change under a recorded command, and they copy on construction as of the
//   character arc's prologue. Cost, measured: 0 B/frame in the toolchain lab, TankArena,
//   Bulwark, VulkanParticles and RTSGame — none of them uses the uniform-array path at
//   all. Sponza's cascade view-projections are the tree's only consumer, at 4 matrices a
//   pass. The worry that copying "costs far more than 128 bytes" was about a shape no
//   application here has.
//
//   THE LISTS ARE DETECTED, NOT COPIED. A reused List<ShaderUniform> can still be
//   rewritten between two draws. Freezing that is one allocation per draw, and the tree
//   cannot say what that costs at scale: every game measured passes ZERO inline uniforms
//   (push constants and materials instead), and only the lab uses the path at all, at ~88
//   entries a frame. A number from a draw-heavy consumer beats a guess, so the list waits
//   for one — and until then a record-time fingerprint, re-checked at execute, throws
//   naming the uniform and the pass wherever validation is on.
//
// The skeletal work did not settle any of this and was never going to: a bone palette
// does NOT travel as a Matrix4x4ArrayUniform in any consumer. Runner, Bulwark, RTSGame
// and the toolchain lab all send it as a set-3 storage buffer through MaterialBindings.
// What settled it was a consumer asking — see plan-blix-character.md, prologue.
public sealed record DispatchCommand(
    PipelineHandle Pipeline,
    int GroupsX,
    int GroupsY,
    int GroupsZ,
    IReadOnlyList<ShaderUniform> Uniforms,
    IReadOnlyList<ShaderTextureBinding> Textures,
    byte[]? PushConstants = null) : RenderCommand
{
    private readonly byte[]? pushConstants = PushConstants is null ? null : PushConstants.AsSpan().ToArray();

    private readonly IReadOnlyList<ShaderUniform> uniforms = Uniforms;
    private readonly ulong[]? uniformFingerprint = RenderCommandDiagnostics.Fingerprint(Uniforms);
    private readonly IReadOnlyList<ShaderTextureBinding> textures = Textures;
    private readonly ulong[]? textureFingerprint = RenderCommandDiagnostics.Fingerprint(Textures);

    /// <summary>The uniforms this draw writes. NOT copied — see <see cref="RenderCommandDiagnostics"/>.</summary>
    public IReadOnlyList<ShaderUniform> Uniforms
    {
        get => uniforms;
        init
        {
            uniforms = value;
            uniformFingerprint = RenderCommandDiagnostics.Fingerprint(value);
        }
    }

    /// <summary>The textures this draw binds. NOT copied — see <see cref="RenderCommandDiagnostics"/>.</summary>
    public IReadOnlyList<ShaderTextureBinding> Textures
    {
        get => textures;
        init
        {
            textures = value;
            textureFingerprint = RenderCommandDiagnostics.Fingerprint(value);
        }
    }

    /// <summary>Per-entry fingerprint of <see cref="Uniforms"/> as recorded, or null when the check is off.</summary>
    public ulong[]? UniformFingerprint => uniformFingerprint;

    /// <summary>Per-entry fingerprint of <see cref="Textures"/> as recorded, or null when the check is off.</summary>
    public ulong[]? TextureFingerprint => textureFingerprint;


    /// <summary>The push payload, copied at record time so a caller may reuse its scratch buffer.</summary>
    public byte[]? PushConstants
    {
        get => pushConstants;
        init => pushConstants = value is null ? null : value.AsSpan().ToArray();
    }
}


// Per-material indirect multi-draw (Vulkan-only). Binds the same state as a
// DrawIndexedCommand (pipeline, shared VB/IB, set0 uniforms/textures, set2
// material, push constants), then issues one vkCmdDrawIndexedIndirect that reads
// DrawCount VkDrawIndexedIndirectCommand structs from IndirectBuffer starting at
// IndirectByteOffset. All sub-draws share the bound state — group objects by
// (pipeline, material) and emit one of these per group.
// <b>Recorded commands own their push payload.</b> A pass body RECORDS; the GPU work
// happens later, at Execute. A byte[] handed over here is therefore read long after the
// caller has moved on — and a caller that reuses one scratch array across draws gives
// every draw the array's FINAL contents.
//
// That is not hypothetical. VkLineDrawer hit it (all views submitting whatever the last
// one left), StudioRenderer hit it (seven objects rendering at the seventh's transform, six
// apparently missing while the draw counts looked perfectly healthy), and the
// index-offset DrawIndexed overload below exists because the same hazard bit vertex
// buffers. Three consumers, one semantic mistake: recording accepted mutable
// caller-owned payloads whose lifetime had to secretly extend to Execute.
//
// The copy lives on the RECORD rather than at the call sites because there are ten call
// sites today and the eleventh is the one that would get it wrong. Push payloads are
// bounded by the Vulkan minimum of 128 bytes, so this is a memcpy of at most that per
// draw.
//
// MEASURED AND SPLIT, rather than left open. This note used to say the Uniforms and
// Textures lists were "not yet frozen" and that the arrays inside them "want measuring
// before it is done". The measurement was taken (RenderCommandDiagnostics, 120 frames of
// each application under BLIX_VK_VALIDATE) and it separates the two halves cleanly:
//
//   THE VALUES ARE FROZEN NOW. Every scalar and vector uniform always held its value by
//   struct copy and was never at risk; the three ARRAY variants were the only payload a
//   caller could change under a recorded command, and they copy on construction as of the
//   character arc's prologue. Cost, measured: 0 B/frame in the toolchain lab, TankArena,
//   Bulwark, VulkanParticles and RTSGame — none of them uses the uniform-array path at
//   all. Sponza's cascade view-projections are the tree's only consumer, at 4 matrices a
//   pass. The worry that copying "costs far more than 128 bytes" was about a shape no
//   application here has.
//
//   THE LISTS ARE DETECTED, NOT COPIED. A reused List<ShaderUniform> can still be
//   rewritten between two draws. Freezing that is one allocation per draw, and the tree
//   cannot say what that costs at scale: every game measured passes ZERO inline uniforms
//   (push constants and materials instead), and only the lab uses the path at all, at ~88
//   entries a frame. A number from a draw-heavy consumer beats a guess, so the list waits
//   for one — and until then a record-time fingerprint, re-checked at execute, throws
//   naming the uniform and the pass wherever validation is on.
//
// The skeletal work did not settle any of this and was never going to: a bone palette
// does NOT travel as a Matrix4x4ArrayUniform in any consumer. Runner, Bulwark, RTSGame
// and the toolchain lab all send it as a set-3 storage buffer through MaterialBindings.
// What settled it was a consumer asking — see plan-blix-character.md, prologue.
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
    byte[]? PushConstants = null) : RenderCommand
{
    private readonly byte[]? pushConstants = PushConstants is null ? null : PushConstants.AsSpan().ToArray();

    private readonly IReadOnlyList<ShaderUniform> uniforms = Uniforms;
    private readonly ulong[]? uniformFingerprint = RenderCommandDiagnostics.Fingerprint(Uniforms);
    private readonly IReadOnlyList<ShaderTextureBinding> textures = Textures;
    private readonly ulong[]? textureFingerprint = RenderCommandDiagnostics.Fingerprint(Textures);

    /// <summary>The uniforms this draw writes. NOT copied — see <see cref="RenderCommandDiagnostics"/>.</summary>
    public IReadOnlyList<ShaderUniform> Uniforms
    {
        get => uniforms;
        init
        {
            uniforms = value;
            uniformFingerprint = RenderCommandDiagnostics.Fingerprint(value);
        }
    }

    /// <summary>The textures this draw binds. NOT copied — see <see cref="RenderCommandDiagnostics"/>.</summary>
    public IReadOnlyList<ShaderTextureBinding> Textures
    {
        get => textures;
        init
        {
            textures = value;
            textureFingerprint = RenderCommandDiagnostics.Fingerprint(value);
        }
    }

    /// <summary>Per-entry fingerprint of <see cref="Uniforms"/> as recorded, or null when the check is off.</summary>
    public ulong[]? UniformFingerprint => uniformFingerprint;

    /// <summary>Per-entry fingerprint of <see cref="Textures"/> as recorded, or null when the check is off.</summary>
    public ulong[]? TextureFingerprint => textureFingerprint;


    /// <summary>The push payload, copied at record time so a caller may reuse its scratch buffer.</summary>
    public byte[]? PushConstants
    {
        get => pushConstants;
        init => pushConstants = value is null ? null : value.AsSpan().ToArray();
    }
}


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
    ulong VertexBufferByteOffset = 0) : RenderCommand
{
    private readonly byte[]? pushConstants = PushConstants is null ? null : PushConstants.AsSpan().ToArray();

    private readonly IReadOnlyList<ShaderUniform> uniforms = Uniforms;
    private readonly ulong[]? uniformFingerprint = RenderCommandDiagnostics.Fingerprint(Uniforms);
    private readonly IReadOnlyList<ShaderTextureBinding> textures = Textures;
    private readonly ulong[]? textureFingerprint = RenderCommandDiagnostics.Fingerprint(Textures);

    /// <summary>The uniforms this draw writes. NOT copied — see <see cref="RenderCommandDiagnostics"/>.</summary>
    public IReadOnlyList<ShaderUniform> Uniforms
    {
        get => uniforms;
        init
        {
            uniforms = value;
            uniformFingerprint = RenderCommandDiagnostics.Fingerprint(value);
        }
    }

    /// <summary>The textures this draw binds. NOT copied — see <see cref="RenderCommandDiagnostics"/>.</summary>
    public IReadOnlyList<ShaderTextureBinding> Textures
    {
        get => textures;
        init
        {
            textures = value;
            textureFingerprint = RenderCommandDiagnostics.Fingerprint(value);
        }
    }

    /// <summary>Per-entry fingerprint of <see cref="Uniforms"/> as recorded, or null when the check is off.</summary>
    public ulong[]? UniformFingerprint => uniformFingerprint;

    /// <summary>Per-entry fingerprint of <see cref="Textures"/> as recorded, or null when the check is off.</summary>
    public ulong[]? TextureFingerprint => textureFingerprint;


    /// <summary>
    /// The push payload, copied at record time so a caller may reuse its scratch buffer.
    /// </summary>
    /// <remarks>
    /// This is the one that bit: StudioRenderer packed each object's model matrix into a single shared array
    /// and every draw read the last object's, so seven boxes rendered in one place and six looked missing
    /// while the draw counts stayed perfectly healthy. Found from a screenshot, not from four green runs.
    /// </remarks>
    public byte[]? PushConstants
    {
        get => pushConstants;
        init => pushConstants = value is null ? null : value.AsSpan().ToArray();
    }
}
