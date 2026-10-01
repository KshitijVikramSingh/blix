namespace Blix.Graphics;

// Binding contract for a shader program: descriptor slots and push-constant
// ranges. Vertex input lives on PipelineDescription.
//
// Set-by-lifetime convention (see docs/architecture.md → the Vulkan binding model):
//   set 0 = per-frame    (viewProjection, sun, camera, ambient)
//   set 1 = per-pass     (shadow maps, env, BRDF LUT)
//   set 2 = per-material (albedo/normal/MR + factors)
//   set 3 = per-draw     (push constants for ≤256B; descriptor sets above)

[Flags]
public enum ShaderStages
{
    None = 0,
    Vertex = 1 << 0,
    Fragment = 1 << 1,
    Compute = 1 << 2,
    All = Vertex | Fragment | Compute,
}

public enum ShaderResourceType
{
    UniformBuffer,
    StorageBuffer,
    /// <summary>A combined image and sampler: GLSL <c>sampler2D</c>, <c>samplerCube</c>, <c>sampler3D</c>.</summary>
    /// <remarks>
    /// Named before the engine could express a separate image, and kept so external code that says
    /// SampledImage still means what it meant. It is Vulkan's COMBINED_IMAGE_SAMPLER, and it counts
    /// against the per-stage sampler limit as well as the sampled-image one.
    /// </remarks>
    SampledImage,
    StorageImage,
    /// <summary>A separate sampler: GLSL <c>sampler</c>. Immutable, from the shader's <c>//@sampler</c>.</summary>
    Sampler,
    /// <summary>A separate image: GLSL <c>texture2D</c>, <c>textureCube</c>, <c>texture3D</c>.</summary>
    /// <remarks>
    /// Vulkan's SAMPLED_IMAGE. It counts only against sampled images, which is the point of it: a
    /// stage with twenty of these and two <see cref="Sampler"/>s is inside a 16-sampler limit.
    /// Read through a sampler the shader pairs it with, so its texture's own sampler is not used.
    /// </remarks>
    SeparateImage,
}

// One descriptor slot inside a set. BlockLayout is required for UniformBuffer
// and StorageBuffer (so the runtime knows the byte size for descriptor write
// Range and can route per-member writes); it must be null for image/sampler
// types.
//
// Count > 1 declares an array binding (e.g. uSpotShadowMaps[4]). The
// underlying SPIR-V binding must be a sized array of the matching descriptor
// type.
//
// Name is the shader's name for the resource, as reflection reports it (uHdr, and uSpotShadowMaps
// for an array). A texture bound by name finds its binding through it.
//
// Sampler is a Sampler slot's state, from the shader's //@sampler. The device builds it into the set
// layout as an immutable sampler, so nothing binds it per draw. Null on every other type.
public sealed record DescriptorSetSlot(
    int Set,
    int Binding,
    ShaderResourceType Type,
    ShaderStages Stages,
    int Count = 1,
    UniformBlockLayout? BlockLayout = null,
    string? Name = null,
    SamplerDescription? Sampler = null);

// One push-constant range. Per Vulkan spec, two ranges sharing any stage
// bit must not have overlapping byte ranges; ranges in disjoint stages may
// overlap (separate hardware push-constant blocks per stage).
public sealed record PushConstantRange(
    ShaderStages Stages,
    int Offset,
    int Size);

public sealed record ShaderInterface(
    IReadOnlyList<DescriptorSetSlot> Slots,
    IReadOnlyList<PushConstantRange> PushConstants)
{
    public ShaderInterface(IReadOnlyList<DescriptorSetSlot> slots)
        : this(slots, Array.Empty<PushConstantRange>()) { }

    /// <summary>The bytes a draw's push payload carries: as far as the furthest range reaches.</summary>
    /// <remarks>
    /// Not the ranges' sum: one block declared by two stages at different lengths is two overlapping
    /// ranges (one per stage), and the payload is the block once.
    /// </remarks>
    public int PushConstantBytes => PushConstants.Count == 0 ? 0 : PushConstants.Max(r => r.Offset + r.Size);

    /// <summary>
    /// Give a runtime-sized block the length only the application knows, as an element count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The one thing reflection cannot answer.</b> A storage block ending in an unsized array
    /// -- <c>readonly buffer Bones { mat4 m[]; }</c> -- reflects with <c>block_size: 0</c> and
    /// <c>array: [0]</c>, because how many elements there are is not in the shader. It is
    /// bones × bodies for a palette, and only the caller knows that. The array's offset and stride ARE
    /// in the shader, so the caller gives a count and never multiplies by a stride it restated.
    /// </para>
    /// <para>
    /// So this is not an escape hatch from reflection, it is the boundary of what reflection
    /// means: the shader owns the set, the binding, the type, the stages and the stride, and the
    /// application owns the count. Saying it in one call keeps the rest of the table derived
    /// rather than restated. Found by converting a demo and watching it die with
    /// <c>ErrorOutOfDeviceMemory</c>, which is what a zero total size buys you.
    /// </para>
    /// </remarks>
    /// <param name="set">The descriptor set the block is bound in.</param>
    /// <param name="binding">The binding within that set.</param>
    /// <param name="elements">How many elements its unsized array holds: the one number reflection cannot give.</param>
    public ShaderInterface WithArrayLength(int set, int binding, int elements)
    {
        if (elements <= 0) throw new ArgumentOutOfRangeException(nameof(elements), elements,
            "a runtime-sized block needs a positive element count; that is the number reflection could not give.");

        var hit = false;
        var slots = Slots.Select(slot =>
        {
            if (slot.Set != set || slot.Binding != binding) return slot;
            hit = true;
            var layout = slot.BlockLayout is { RuntimeArray: not null } runtime ? runtime : throw new ArgumentException(
                $"set {set} binding {binding} does not end in an unsized array, so it has no length to give: " +
                "its size is the shader's.");
            // The array's offset and stride are the shader's; the count is the caller's.
            var bytes = layout.SizeFor(elements);
            var members = layout.Members.Select(m => m.IsRuntimeSized ? m with { Size = bytes - m.Offset } : m).ToArray();
            return slot with { BlockLayout = new UniformBlockLayout(bytes, members) };
        }).ToArray();

        if (!hit)
        {
            throw new ArgumentException(
                $"no slot at set {set} binding {binding} to size; this interface has " +
                $"[{string.Join(", ", Slots.Select(x => $"{x.Set}.{x.Binding}"))}]. " +
                "A renamed or moved binding in the shader shows up here rather than as a wrong picture.");
        }

        return this with { Slots = slots };
    }

    // Structural checks only. Device-feature limits (maxPushConstantsSize,
    // SSBO support) surface later at pipeline-layout / device creation.
    // Block-layout vs SPIR-V reflection is not cross-checked here.
    public void Validate()
    {
        var seen = new HashSet<(int Set, int Binding)>();
        foreach (var s in Slots)
        {
            if (s.Set < 0) Throw($"slot has negative Set ({s.Set})");
            if (s.Binding < 0) Throw($"slot (set={s.Set}, binding={s.Binding}) has negative Binding");
            if (s.Count < 1) Throw($"slot (set={s.Set}, binding={s.Binding}) has Count={s.Count}; must be ≥ 1");
            if (s.Stages == ShaderStages.None)
                Throw($"slot (set={s.Set}, binding={s.Binding}) has Stages=None; must include at least one stage");

            if (!seen.Add((s.Set, s.Binding)))
                Throw($"duplicate slot at (set={s.Set}, binding={s.Binding})");

            var isBuffer = s.Type is ShaderResourceType.UniformBuffer or ShaderResourceType.StorageBuffer;
            if (isBuffer && s.BlockLayout is null)
                Throw($"slot (set={s.Set}, binding={s.Binding}) is {s.Type} but has no BlockLayout");
            if (!isBuffer && s.BlockLayout is not null)
                Throw($"slot (set={s.Set}, binding={s.Binding}) is {s.Type} but carries a BlockLayout; image/sampler slots must not declare one");
        }

        for (var i = 0; i < PushConstants.Count; i++)
        {
            var r = PushConstants[i];
            if (r.Stages == ShaderStages.None)
                Throw($"push-constant range [{i}] has Stages=None; must include at least one stage");
            if (r.Offset < 0) Throw($"push-constant range [{i}] has negative Offset ({r.Offset})");
            if (r.Size <= 0) Throw($"push-constant range [{i}] has Size={r.Size}; must be > 0");

            for (var j = i + 1; j < PushConstants.Count; j++)
            {
                var s = PushConstants[j];
                var sharedStages = r.Stages & s.Stages;
                if (sharedStages == ShaderStages.None) continue;
                var rEnd = r.Offset + r.Size;
                var sEnd = s.Offset + s.Size;
                var overlaps = r.Offset < sEnd && s.Offset < rEnd;
                if (overlaps)
                    Throw($"push-constant ranges [{i}] and [{j}] share stages {sharedStages} and have overlapping byte ranges [{r.Offset},{rEnd}) ∩ [{s.Offset},{sEnd})");
            }
        }
    }

    private static void Throw(string reason) =>
        throw new InvalidOperationException($"ShaderInterface invalid: {reason}.");
}
