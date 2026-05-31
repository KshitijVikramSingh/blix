using System.Text.Json;

namespace Blix.Graphics.Vulkan;

// Loads a `spirv-cross --reflect` JSON sidecar into the engine's existing
// binding records (DescriptorSetSlot / UniformBlockLayout / PushConstantRange).
// One sidecar per shader STAGE (lit.vert.spv.refl.json, lit.frag.spv.refl.json);
// MergeStages combines the stages of a program into one ShaderInterface.
//
// This replaces hand-authored binding tables: every field the demos used to
// write by hand — set/binding/type/count and std140 member offsets — is read
// straight from the compiled SPIR-V's reflection. See
// docs/dynamic-juggling-crescent (plan) for the migration.
//
// Supported member types: the scalar/vector/matrix set glslc emits for Blix
// shaders, plus arrays of those. Nested structs throw — no current shader uses
// them, and a clear failure beats silently-wrong offsets.
public static class ShaderReflection
{
    // Reflected binding contract for a single stage. Slots carry that stage's
    // flag; MergeStages ORs flags across stages at shared (set,binding).
    public sealed record ReflStage(
        ShaderStages Stage,
        IReadOnlyList<DescriptorSetSlot> Slots,
        IReadOnlyList<PushConstantRange> PushConstants);

    public static ReflStage Load(string reflJsonPath)
    {
        ArgumentNullException.ThrowIfNull(reflJsonPath);
        return Parse(File.ReadAllText(reflJsonPath), reflJsonPath);
    }

    public static ReflStage Parse(string json, string? sourceName = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var where = sourceName ?? "<spirv-cross reflection>";

        var stage = ParseStage(root, where);
        // Resolve struct member layouts lazily from the "types" table.
        var types = root.TryGetProperty("types", out var t) ? t : default;

        var slots = new List<DescriptorSetSlot>();

        // UBOs / SSBOs — buffer slots with a std140 BlockLayout.
        AddBufferSlots(root, "ubos", ShaderResourceType.UniformBuffer, types, stage, where, slots);
        AddBufferSlots(root, "ssbos", ShaderResourceType.StorageBuffer, types, stage, where, slots);

        // Combined image samplers (GLSL sampler2D/Cube/3D) → SampledImage,
        // matching the engine's SampledImage→CombinedImageSampler mapping.
        AddImageSlots(root, "textures", ShaderResourceType.SampledImage, stage, slots);
        // Separate images / samplers (rare; not used by current shaders but
        // cheap to support) and storage images (compute writes).
        AddImageSlots(root, "separate_images", ShaderResourceType.SampledImage, stage, slots);
        AddImageSlots(root, "separate_samplers", ShaderResourceType.Sampler, stage, slots);
        AddImageSlots(root, "images", ShaderResourceType.StorageImage, stage, slots);

        var pushConstants = ParsePushConstants(root, types, stage, where);

        return new ReflStage(stage, slots, pushConstants);
    }

    // Combine the per-stage reflections of one program into a single interface.
    // Shared (set,binding) across stages must agree on type/count/layout; their
    // stage flags are OR'd. A type/size conflict is a real cross-stage bug and
    // throws (today it's a silent mismatch between two hand-tables).
    public static ShaderInterface MergeStages(params ReflStage[] stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        var merged = new Dictionary<(int, int), DescriptorSetSlot>();
        foreach (var st in stages)
        {
            foreach (var slot in st.Slots)
            {
                var key = (slot.Set, slot.Binding);
                if (!merged.TryGetValue(key, out var existing))
                {
                    merged[key] = slot;
                    continue;
                }
                if (existing.Type != slot.Type || existing.Count != slot.Count)
                {
                    throw new InvalidOperationException(
                        $"ShaderReflection: conflicting declarations at (set={slot.Set}, binding={slot.Binding}) " +
                        $"across stages — {existing.Type}×{existing.Count} vs {slot.Type}×{slot.Count}.");
                }
                // A stage only reflects the UBO members it references, so the
                // same (set,binding) block can come back with a different member
                // count — even different trailing names — per stage (e.g. the
                // vertex stage of a lit shader sees a short prefix of the Frame
                // block the fragment stage fully reads). std140 offsets are
                // positional, so the fuller block is the authoritative layout
                // for the by-name write path; keep it and OR the stage flags.
                var fuller = (slot.BlockLayout?.TotalSize ?? 0) > (existing.BlockLayout?.TotalSize ?? 0)
                    ? slot : existing;
                merged[key] = fuller with { Stages = existing.Stages | slot.Stages };
            }
        }

        // Coalesce push-constant ranges describing the same byte span across
        // stages into ONE range with OR'd stage flags. A push_constant block is
        // reflected once per stage that declares it — the full block each time
        // (e.g. shadow_mask's vertex AND fragment stages both reflect the whole
        // [0,144) block). Concatenating would yield two identical ranges, and
        // the emit path (PushConstantsToCommandBuffer) sums range sizes for the
        // expected payload length — double-counting to 288 for a 144B payload.
        // One range per distinct (offset,size) with all stages OR'd matches the
        // hand-authored shape and the emit path's partition assumption.
        var pushByRange = new Dictionary<(int Offset, int Size), PushConstantRange>();
        foreach (var st in stages)
        {
            foreach (var pc in st.PushConstants)
            {
                var key = (pc.Offset, pc.Size);
                pushByRange[key] = pushByRange.TryGetValue(key, out var existing)
                    ? existing with { Stages = existing.Stages | pc.Stages }
                    : pc;
            }
        }
        return new ShaderInterface(merged.Values.ToArray(), pushByRange.Values.ToArray());
    }

    // --- parsing helpers ---------------------------------------------------

    private static ShaderStages ParseStage(JsonElement root, string where)
    {
        if (root.TryGetProperty("entryPoints", out var eps) && eps.GetArrayLength() > 0)
        {
            var mode = eps[0].GetProperty("mode").GetString();
            return mode switch
            {
                "vert" => ShaderStages.Vertex,
                "frag" => ShaderStages.Fragment,
                "comp" => ShaderStages.Compute,
                _ => throw new InvalidOperationException(
                    $"ShaderReflection ({where}): unsupported entry-point mode '{mode}'."),
            };
        }
        throw new InvalidOperationException($"ShaderReflection ({where}): no entryPoints in reflection.");
    }

    private static void AddBufferSlots(
        JsonElement root, string section, ShaderResourceType type,
        JsonElement types, ShaderStages stage, string where, List<DescriptorSetSlot> outSlots)
    {
        if (!root.TryGetProperty(section, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var b in arr.EnumerateArray())
        {
            var set = b.GetProperty("set").GetInt32();
            var binding = b.GetProperty("binding").GetInt32();
            var totalSize = b.GetProperty("block_size").GetInt32();
            var typeRef = b.GetProperty("type").GetString()!;
            var members = ParseMembers(types, typeRef, where);
            outSlots.Add(new DescriptorSetSlot(
                set, binding, type, stage,
                BlockLayout: new UniformBlockLayout(totalSize, members)));
        }
    }

    private static void AddImageSlots(
        JsonElement root, string section, ShaderResourceType type,
        ShaderStages stage, List<DescriptorSetSlot> outSlots)
    {
        if (!root.TryGetProperty(section, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var r in arr.EnumerateArray())
        {
            var set = r.GetProperty("set").GetInt32();
            var binding = r.GetProperty("binding").GetInt32();
            var count = 1;
            if (r.TryGetProperty("array", out var dims) && dims.ValueKind == JsonValueKind.Array)
            {
                count = 1;
                foreach (var d in dims.EnumerateArray()) count *= d.GetInt32();
            }
            outSlots.Add(new DescriptorSetSlot(set, binding, type, stage, Count: count));
        }
    }

    private static UniformBlockMember[] ParseMembers(JsonElement types, string typeRef, string where)
    {
        if (types.ValueKind != JsonValueKind.Object || !types.TryGetProperty(typeRef, out var typeDef))
        {
            throw new InvalidOperationException(
                $"ShaderReflection ({where}): block type '{typeRef}' not found in types table.");
        }
        var membersJson = typeDef.GetProperty("members");
        var result = new UniformBlockMember[membersJson.GetArrayLength()];
        var i = 0;
        foreach (var m in membersJson.EnumerateArray())
        {
            var name = m.GetProperty("name").GetString()!;
            var offset = m.GetProperty("offset").GetInt32();
            var memberType = m.GetProperty("type").GetString()!;

            int elementStride = 0;
            int size;
            if (m.TryGetProperty("array", out var dims) && dims.ValueKind == JsonValueKind.Array)
            {
                // std140 array stride is the per-element span; total size is
                // stride × element count (every dim multiplied).
                elementStride = m.GetProperty("array_stride").GetInt32();
                var count = 1;
                foreach (var d in dims.EnumerateArray()) count *= d.GetInt32();
                size = elementStride * count;
            }
            else if (m.TryGetProperty("matrix_stride", out var ms))
            {
                size = ms.GetInt32() * MatrixColumns(memberType, where);
            }
            else
            {
                size = ScalarOrVectorSize(memberType, where);
            }
            result[i++] = new UniformBlockMember(name, offset, size, elementStride);
        }
        return result;
    }

    private static IReadOnlyList<PushConstantRange> ParsePushConstants(
        JsonElement root, JsonElement types, ShaderStages stage, string where)
    {
        if (!root.TryGetProperty("push_constants", out var arr) || arr.ValueKind != JsonValueKind.Array
            || arr.GetArrayLength() == 0)
        {
            return Array.Empty<PushConstantRange>();
        }
        var ranges = new List<PushConstantRange>();
        foreach (var pc in arr.EnumerateArray())
        {
            var typeRef = pc.GetProperty("type").GetString()!;
            var members = ParseMembers(types, typeRef, where);
            // One range spanning the whole block. Offset is the first member's
            // offset (usually 0); size reaches the end of the last member.
            var start = members.Length == 0 ? 0 : members.Min(x => x.Offset);
            var end = members.Length == 0 ? 0 : members.Max(x => x.Offset + x.Size);
            ranges.Add(new PushConstantRange(stage, start, end - start));
        }
        return ranges;
    }

    // std140 sizes. Vectors are tight (vec3 = 12) — alignment/padding is the
    // caller's concern (offsets come from reflection); Size is the write-guard
    // span MaterialBindings.WriteUniformBytes checks against.
    private static int ScalarOrVectorSize(string type, string where) => type switch
    {
        "float" or "int" or "uint" or "bool" => 4,
        "vec2" or "ivec2" or "uvec2" => 8,
        "vec3" or "ivec3" or "uvec3" => 12,
        "vec4" or "ivec4" or "uvec4" => 16,
        _ => throw new InvalidOperationException(
            $"ShaderReflection ({where}): unsupported UBO member type '{type}' " +
            "(nested structs / unhandled scalars not supported)."),
    };

    private static int MatrixColumns(string type, string where) => type switch
    {
        "mat2" => 2,
        "mat3" => 3,
        "mat4" => 4,
        _ => throw new InvalidOperationException(
            $"ShaderReflection ({where}): unexpected matrix type '{type}'."),
    };
}
