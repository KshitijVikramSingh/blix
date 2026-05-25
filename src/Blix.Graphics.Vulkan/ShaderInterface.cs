namespace Blix.Graphics.Vulkan;

// Binding contract for a shader program: which descriptor slots it samples
// from (organized into sets 0/1/2/3 by lifetime per
// docs/vulkan-reshape-shaderlab-target.md Section 1) and which push-constant
// ranges it consumes. Vertex input stays on PipelineDescription for now.
//
// Set-by-lifetime convention:
//   set 0 = per-frame   (viewProjection, sun, camera, ambient)
//   set 1 = per-pass    (shadow maps, env, BRDF LUT)
//   set 2 = per-material (albedo/normal/MR + factors)
//   set 3 = per-draw    (push constants for ≤256B, descriptor sets above)
//
// 2a scope (this file): pure type declarations + structural Validate(). No
// backend wiring — CreateShaderProgramFromSpv still consumes the legacy
// UniformBlockLayout. 2b lands the wiring that generates a real
// VkDescriptorSetLayout from these declarations.

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
    SampledImage,
    StorageImage,
    Sampler,
}

// One descriptor slot inside a set. BlockLayout is required for UniformBuffer
// and StorageBuffer (so the runtime knows the byte size for descriptor write
// Range and can route per-member writes); it must be null for image/sampler
// types.
//
// Count > 1 declares an array binding (e.g. uSpotShadowMaps[4]). The
// underlying SPIR-V binding must be a sized array of the matching descriptor
// type.
public sealed record DescriptorSetSlot(
    int Set,
    int Binding,
    ShaderResourceType Type,
    ShaderStages Stages,
    int Count = 1,
    UniformBlockLayout? BlockLayout = null);

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

    // Throws InvalidOperationException with a specific reason if the
    // declared interface is structurally invalid. Caller-side check before
    // the backend uses these slots to build a VkDescriptorSetLayout.
    //
    // Catches at this layer:
    //   - (set, binding) collisions within the interface
    //   - buffer slots missing BlockLayout / image+sampler slots carrying one
    //   - ShaderStages.None on any slot or push-constant range
    //   - negative Set / Binding / Count / Offset / Size
    //   - zero-Size push-constant ranges
    //   - push-constant range overlap within a shared stage
    //
    // Does NOT catch (deferred to backend or device-feature checks):
    //   - maxPushConstantsSize (device-feature, checked at pipeline-layout creation)
    //   - SSBO support (device-feature, checked at device creation)
    //   - block-layout offset/size matching the actual SPIR-V reflection
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
