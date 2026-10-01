using System.Text.Json;

namespace Blix.Graphics;

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
        // The //@sampler sidecar sits beside the same .spv, and gives each separate sampler its state.
        var samplers = reflJsonPath.EndsWith(".refl.json", StringComparison.Ordinal)
            ? ShaderSamplerSidecar.Load(reflJsonPath[..^".refl.json".Length])
            : Array.Empty<ShaderSampler>();
        return Parse(File.ReadAllText(reflJsonPath), reflJsonPath, samplers);
    }

    public static ReflStage Parse(string json, string? sourceName = null, IReadOnlyList<ShaderSampler>? samplers = null)
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
        // Separate images and samplers (texture2D + sampler), and storage images (compute writes).
        // Separate images used to be read as combined, which gave a texture2D the wrong descriptor
        // type; each is its own kind now. A separate sampler takes its state from //@sampler.
        AddImageSlots(root, "separate_images", ShaderResourceType.SeparateImage, stage, slots);
        AddImageSlots(root, "separate_samplers", ShaderResourceType.Sampler, stage, slots, samplers);
        AddImageSlots(root, "images", ShaderResourceType.StorageImage, stage, slots);

        var pushConstants = ParsePushConstants(root, types, stage, where);

        return new ReflStage(stage, slots, pushConstants);
    }

    /// <summary>
    /// The interface of a whole program, read from the reflection sidecars beside its shaders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The three callers that wanted this each wrote it, identically, as a local function.</b>
    /// Three copies of one step, and with them three spellings of the <c>.spv.refl.json</c>
    /// suffix — which is every spelling of it in the tree. Nothing about that was going to fail
    /// loudly; it is the shape conventions §4 names, and it is why this is here rather than in
    /// whichever renderer needed it first.
    /// </para>
    /// <para>
    /// The stage names are the shader filenames, <c>lit.vert</c> and not <c>lit.vert.spv</c> — the
    /// sidecar suffix belongs to this method, being the thing the build decided to call them.
    /// </para>
    /// </remarks>
    public static ShaderInterface ForProgram(string shaderDirectory, params string[] stages)
    {
        ArgumentNullException.ThrowIfNull(shaderDirectory);
        ArgumentNullException.ThrowIfNull(stages);
        return MergeStages(stages
            .Select(s => Load(Path.Combine(shaderDirectory, s + SidecarSuffix)))
            .ToArray());
    }

    /// <summary>What the build calls a stage's reflection, beside the stage's own .spv.</summary>
    public const string SidecarSuffix = ".spv.refl.json";

    // Combine the per-stage reflections of one program into a single interface.
    // Shared (set,binding) across stages must agree on type, count and block layout; their stage
    // flags are OR'd. Every one of those disagreements is a real cross-stage bug and throws.
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
                if (existing.Sampler is { } one && slot.Sampler is { } two && one != two)
                {
                    throw new InvalidOperationException(
                        $"ShaderReflection: sampler '{slot.Name}' at (set={slot.Set}, binding={slot.Binding}) is " +
                        $"declared with different //@sampler states in different stages.");
                }
                if (existing.Type != slot.Type || existing.Count != slot.Count)
                {
                    throw new InvalidOperationException(
                        $"ShaderReflection: conflicting declarations at (set={slot.Set}, binding={slot.Binding}) " +
                        $"across stages — {existing.Type}×{existing.Count} vs {slot.Type}×{slot.Count}.");
                }
                // A stage only reflects the UBO members it references, so the same (set,binding)
                // block can come back with a different member count per stage — measured:
                // studio_lit.vert sees one 64-byte member of the Frame block and studio_lit.frag
                // sees eleven, 368 bytes. std140 offsets are positional, so the fuller block is
                // the authoritative layout for the by-name write path.
                //
                // But "fuller" is only meaningful if the shorter really is a PREFIX of it, and
                // that has to be checked rather than assumed. Two stages declaring different
                // members at the same offset is not a longer and a shorter view of one block, it
                // is two different blocks sharing a binding — and picking either one silently
                // sends the by-name path to write B where the shader reads C. Nothing downstream
                // would object: the descriptor type matches, so the device has no opinion.
                RequireCompatibleBlocks(existing, slot);

                var fuller = (slot.BlockLayout?.TotalSize ?? 0) > (existing.BlockLayout?.TotalSize ?? 0)
                    ? slot : existing;
                merged[key] = fuller with { Stages = existing.Stages | slot.Stages, Sampler = existing.Sampler ?? slot.Sampler };
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

    /// <summary>
    /// Two stages' views of one block must be the same block: overlapping members agree exactly,
    /// and the shorter is a prefix of the longer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched by OFFSET, because that is what std140 makes authoritative and what the by-name
    /// write path ultimately resolves to. A member present in one view and absent from the other
    /// at the same offset means the shorter is not a prefix, which is the same disagreement wearing
    /// a gap instead of a clash.
    /// </para>
    /// <para>
    /// This lives in the merge rather than in a repository test on purpose. A source-level check
    /// can keep one tree's shaders honest; this is public machinery, and an external project's
    /// shaders never pass through that gate.
    /// </para>
    /// </remarks>
    private static void RequireCompatibleBlocks(DescriptorSetSlot a, DescriptorSetSlot b)
    {
        if (a.BlockLayout is not { } la || b.BlockLayout is not { } lb) return;

        // Compare the shorter against the longer, so "missing at this offset" means what it says.
        var (shorter, longer) = la.TotalSize <= lb.TotalSize ? (la, lb) : (lb, la);
        var byOffset = new Dictionary<int, UniformBlockMember>();
        foreach (var m in longer.Members) byOffset[m.Offset] = m;

        foreach (var m in shorter.Members)
        {
            if (!byOffset.TryGetValue(m.Offset, out var other))
            {
                throw new InvalidOperationException(
                    $"ShaderReflection: stages disagree about the block at (set={a.Set}, binding={a.Binding}) — " +
                    $"'{m.Name}' at offset {m.Offset} in one stage has no member at that offset in the other. " +
                    "One view of a block must be a prefix of the other; these are two different blocks " +
                    "sharing a binding.");
            }

            if (other.Name == m.Name && other.Size == m.Size && other.ElementStride == m.ElementStride) continue;

            throw new InvalidOperationException(
                $"ShaderReflection: stages disagree about the block at (set={a.Set}, binding={a.Binding}) — " +
                $"offset {m.Offset} is '{m.Name}' ({m.Size}B, stride {m.ElementStride}) in one stage and " +
                $"'{other.Name}' ({other.Size}B, stride {other.ElementStride}) in the other. Writing by name " +
                "would put bytes where the other stage reads something else.");
        }
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
        ShaderStages stage, List<DescriptorSetSlot> outSlots, IReadOnlyList<ShaderSampler>? samplers = null)
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
            var name = r.TryGetProperty("name", out var n) ? n.GetString() : null;
            var state = type == ShaderResourceType.Sampler
                ? samplers?.FirstOrDefault(s => s.Name == name)?.Description
                : null;
            outSlots.Add(new DescriptorSetSlot(set, binding, type, stage, Count: count, Name: name, Sampler: state));
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
            var runtimeSized = false;
            if (m.TryGetProperty("array", out var dims) && dims.ValueKind == JsonValueKind.Array)
            {
                // std140 array stride is the per-element span; total size is
                // stride × element count (every dim multiplied). A 0 dimension is an unsized
                // array: its stride is known, its count is the application's.
                elementStride = m.GetProperty("array_stride").GetInt32();
                var count = 1;
                foreach (var d in dims.EnumerateArray()) count *= d.GetInt32();
                runtimeSized = count == 0;
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
            result[i++] = new UniformBlockMember(name, offset, size, elementStride, runtimeSized);
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
