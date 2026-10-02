using Blix;
using SharpGLTF.Schema2;

namespace Blix.Recipes;

/// <summary>What a glTF's primitives carry that the cooked vertex does not: the file's <see cref="UnreadAttribute"/>s.</summary>
internal static class GltfUnread
{
    /// <summary>
    /// Every vertex attribute the cooked vertex does not carry. Every primitive is read as the complete vertex
    /// (tangent, two UV sets, COLOR_0); a primitive a skin drives reads its skinning pairs as well.
    /// </summary>
    /// <remarks>
    /// Collection is subtractive, so application-specific and future semantics are surfaced without a
    /// prewritten list. A primitive placed both skinned and unskinned is audited once for each, because either
    /// copy may leave a channel unread.
    /// </remarks>
    public static UnreadAttribute[] Of(ModelRoot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var reads = model.LogicalNodes
            .Where(node => node.Mesh is not null)
            .SelectMany(node => node.Mesh!.Primitives.Select(primitive => (primitive, Skinned: node.Skin is not null)));

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (prim, skinned) in reads.Distinct())
        {
            foreach (var semantic in prim.VertexAccessors.Keys)
            {
                if (Consumes(prim, semantic, skinned)) continue;
                counts[semantic] = counts.GetValueOrDefault(semantic) + 1;
            }

            if (prim.MorphTargetsCount > 0)
            {
                counts[UnreadAttribute.MorphTargets] = counts.GetValueOrDefault(UnreadAttribute.MorphTargets) + 1;
            }
        }

        if (counts.Count == 0) return Array.Empty<UnreadAttribute>();

        // Deterministic order, skin influences first: a mesh that carries skinning but is drawn rigid is the
        // most visible thing a cook can leave unread.
        return counts
            .Select(kv => new UnreadAttribute(kv.Key, kv.Value))
            .OrderByDescending(i => i.Semantic.StartsWith("JOINTS_", StringComparison.Ordinal)
                                 || i.Semantic.StartsWith("WEIGHTS_", StringComparison.Ordinal))
            .ThenBy(i => i.Semantic, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool Consumes(MeshPrimitive primitive, string semantic, bool skinned)
    {
        if (semantic is "POSITION" or "NORMAL" or "TEXCOORD_0" or "TANGENT" or "COLOR_0" or "TEXCOORD_1") return true;
        if (!skinned || !TryInfluenceSet(semantic, out var set)) return false;

        // GltfVertices.Skinned stops at the first incomplete pair, so a later pair is not consumed even
        // when both of its accessors exist.
        for (var i = 0; i <= set; i++)
        {
            if (primitive.GetVertexAccessor($"JOINTS_{i}") is null
                || primitive.GetVertexAccessor($"WEIGHTS_{i}") is null)
                return false;
        }

        return true;
    }

    private static bool TryInfluenceSet(string semantic, out int set)
    {
        set = -1;
        const string joints = "JOINTS_";
        const string weights = "WEIGHTS_";
        var suffix = semantic.StartsWith(joints, StringComparison.Ordinal)
            ? semantic.AsSpan(joints.Length)
            : semantic.StartsWith(weights, StringComparison.Ordinal)
                ? semantic.AsSpan(weights.Length)
                : default;
        return suffix.Length > 0 && int.TryParse(suffix, out set) && set >= 0;
    }
}
