using System.Text.Json.Nodes;
using SharpGLTF.Schema2;

namespace Blix.Recipes;

/// <summary>A source's <c>KHR_materials_variants</c>: the variant names, and each primitive's mapping.</summary>
/// <remarks>
/// SharpGLTF 1.0.6 does not read this extension (it keeps it as an internal unknown node), so it is
/// read here from the file's own JSON — the <c>.gltf</c> text, or a <c>.glb</c>'s JSON chunk. Meshes and
/// primitives are addressed by their glTF indices, which SharpGLTF's logical indices are.
/// </remarks>
internal sealed class MaterialVariants
{
    private readonly JsonNode? meshes;

    private MaterialVariants(string[] names, JsonNode? meshes)
    {
        Names = names;
        this.meshes = meshes;
    }

    /// <summary>The variant names, in the file's order; empty when it has none.</summary>
    public string[] Names { get; }

    public static MaterialVariants Read(string path)
    {
        var root = JsonNode.Parse(Json(path));
        var names = root?["extensions"]?["KHR_materials_variants"]?["variants"] is JsonArray list
            ? list.Select((v, i) => v?["name"]?.GetValue<string>() ?? $"variant_{i}").ToArray()
            : Array.Empty<string>();
        return new MaterialVariants(names, names.Length > 0 ? root?["meshes"] : null);
    }

    /// <summary>Per variant, the material it gives <paramref name="prim"/>, or -1 to keep its own; null without variants.</summary>
    public int[]? For(MeshPrimitive prim)
    {
        if (Names.Length == 0) return null;
        var map = new int[Names.Length];
        Array.Fill(map, -1);
        var mappings = meshes?[prim.LogicalParent.LogicalIndex]?["primitives"]?[prim.LogicalIndex]
            ?["extensions"]?["KHR_materials_variants"]?["mappings"] as JsonArray;
        foreach (var mapping in mappings ?? new JsonArray())
        {
            var material = mapping?["material"]?.GetValue<int>()
                ?? throw new InvalidDataException("a KHR_materials_variants mapping names no material.");
            foreach (var v in mapping["variants"]?.AsArray() ?? new JsonArray())
            {
                var index = v!.GetValue<int>();
                if ((uint)index >= (uint)map.Length)
                    throw new InvalidDataException($"a KHR_materials_variants mapping names variant {index}, and the file has {map.Length}.");
                map[index] = material;
            }
        }

        return map;
    }

    // The JSON document: a .gltf is it; a .glb holds it as its first chunk (header 12 bytes, then
    // chunk length and type, then the chunk).
    private static byte[] Json(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20 || BitConverter.ToUInt32(bytes, 0) != 0x46546C67) return bytes;
        var length = (int)BitConverter.ToUInt32(bytes, 12);
        return bytes.AsSpan(20, length).ToArray();
    }
}
