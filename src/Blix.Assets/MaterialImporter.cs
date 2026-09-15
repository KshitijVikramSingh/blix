using System.Numerics;
using System.Text.Json;
using Blix.Graphics;
using Blix.Cooked;

namespace Blix.Assets;

public sealed class MaterialImporter : IAssetImporter<MaterialData>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string Name => "material.json";

    public MaterialData Import(AssetImportContext context)
    {
        if (!File.Exists(context.SourcePath))
        {
            throw new FileNotFoundException($"Material file not found: {context.SourcePath}", context.SourcePath);
        }

        JsonDocument document;

        try
        {
            using var stream = File.OpenRead(context.SourcePath);
            document = JsonDocument.Parse(stream);
        }
        catch (JsonException ex)
        {
            throw new AssetImportException(context.SourcePath, null, $"invalid material JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AssetImportException(context.SourcePath, null, "material JSON root must be an object.");
            }

            if (!root.TryGetProperty("pipeline", out var pipelineEl) || pipelineEl.ValueKind != JsonValueKind.String)
            {
                throw new AssetImportException(context.SourcePath, null, "material JSON missing 'pipeline' string.");
            }

            var pipelineName = pipelineEl.GetString();
            if (string.IsNullOrWhiteSpace(pipelineName))
            {
                throw new AssetImportException(context.SourcePath, null, "'pipeline' must be a non-empty string.");
            }

            var uniforms = ParseUniforms(context.SourcePath, root);
            var textures = ParseTextures(context.SourcePath, root);

            return new MaterialData(pipelineName, uniforms, textures);
        }
    }

    private static IReadOnlyList<ShaderUniform> ParseUniforms(string sourcePath, JsonElement root)
    {
        if (!root.TryGetProperty("uniforms", out var uniformsEl))
        {
            return Array.Empty<ShaderUniform>();
        }

        if (uniformsEl.ValueKind != JsonValueKind.Object)
        {
            throw new AssetImportException(sourcePath, null, "'uniforms' must be an object keyed by uniform name.");
        }

        var result = new List<ShaderUniform>();

        foreach (var property in uniformsEl.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                throw new AssetImportException(sourcePath, null, "uniform name must be non-empty.");
            }

            var value = ParseUniformValue(sourcePath, property.Name, property.Value);
            result.Add(new ShaderUniform(property.Name, value));
        }

        return result;
    }

    private static ShaderUniformValue ParseUniformValue(string sourcePath, string name, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return new FloatUniform(element.GetSingle());

            case JsonValueKind.Array:
            {
                var floats = new List<float>(4);
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number)
                    {
                        throw new AssetImportException(
                            sourcePath, null,
                            $"uniform '{name}' array must contain only numbers.");
                    }
                    floats.Add(item.GetSingle());
                }

                return floats.Count switch
                {
                    2 => new Vector2Uniform(new Vector2(floats[0], floats[1])),
                    3 => new Vector3Uniform(new Vector3(floats[0], floats[1], floats[2])),
                    4 => new Vector4Uniform(new Vector4(floats[0], floats[1], floats[2], floats[3])),
                    _ => throw new AssetImportException(
                        sourcePath, null,
                        $"uniform '{name}' array must have 2, 3, or 4 entries; got {floats.Count}.")
                };
            }

            default:
                throw new AssetImportException(
                    sourcePath, null,
                    $"uniform '{name}' must be a number or array of numbers; got {element.ValueKind}.");
        }
    }

    private static IReadOnlyList<MaterialTextureBinding> ParseTextures(string sourcePath, JsonElement root)
    {
        if (!root.TryGetProperty("textures", out var texturesEl))
        {
            return Array.Empty<MaterialTextureBinding>();
        }

        if (texturesEl.ValueKind != JsonValueKind.Object)
        {
            throw new AssetImportException(sourcePath, null, "'textures' must be an object keyed by sampler name.");
        }

        var result = new List<MaterialTextureBinding>();

        foreach (var property in texturesEl.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                throw new AssetImportException(sourcePath, null, "texture name must be non-empty.");
            }

            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw new AssetImportException(
                    sourcePath, null,
                    $"texture '{property.Name}' must be an object with 'asset' and 'slot' fields.");
            }

            if (!property.Value.TryGetProperty("asset", out var assetEl) || assetEl.ValueKind != JsonValueKind.String)
            {
                throw new AssetImportException(
                    sourcePath, null,
                    $"texture '{property.Name}' missing string 'asset'.");
            }

            if (!property.Value.TryGetProperty("slot", out var slotEl) || slotEl.ValueKind != JsonValueKind.Number)
            {
                throw new AssetImportException(
                    sourcePath, null,
                    $"texture '{property.Name}' missing numeric 'slot'.");
            }

            AssetId asset;
            try
            {
                asset = AssetId.Parse(assetEl.GetString()!);
            }
            catch (ArgumentException ex)
            {
                throw new AssetImportException(
                    sourcePath, null,
                    $"texture '{property.Name}' has invalid asset id '{assetEl.GetString()}': {ex.Message}", ex);
            }

            var slot = slotEl.GetInt32();
            if (slot < 0)
            {
                throw new AssetImportException(
                    sourcePath, null,
                    $"texture '{property.Name}' slot must be non-negative; got {slot}.");
            }

            result.Add(new MaterialTextureBinding(property.Name, asset, slot));
        }

        return result;
    }
}
