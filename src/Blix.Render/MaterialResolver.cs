using Blix.Assets;
using Blix.Graphics;
using Blix.Graphics.Images;

namespace Blix.Render;

public sealed class MaterialResolver
{
    private readonly IGraphicsDevice device;
    private readonly AssetDatabase assets;
    private readonly SamplerDescription defaultSampler;
    private readonly Dictionary<string, PipelineHandle> pipelines = new(StringComparer.Ordinal);
    private readonly Dictionary<AssetId, TextureHandle> textureCache = new();

    public MaterialResolver(IGraphicsDevice device, AssetDatabase assets, SamplerDescription defaultSampler)
    {
        this.device = device;
        this.assets = assets;
        this.defaultSampler = defaultSampler;
    }

    public MaterialResolver RegisterPipeline(string name, PipelineHandle pipeline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (pipelines.ContainsKey(name))
        {
            throw new InvalidOperationException($"Pipeline '{name}' is already registered on this resolver.");
        }
        pipelines[name] = pipeline;
        return this;
    }

    public Material Resolve(AssetId materialId, Action<Material>? configureDefaults = null)
    {
        var data = assets.Load<MaterialData>(materialId);
        if (!pipelines.TryGetValue(data.PipelineName, out var pipeline))
        {
            throw new InvalidOperationException(
                $"Material '{materialId.Value}' references unknown pipeline '{data.PipelineName}'. " +
                $"Call RegisterPipeline before Resolve.");
        }

        // Defaults run before JSON values so anything the JSON declares cleanly overrides
        // the runtime default (e.g. uNormalMap=flat for materials that don't author a
        // normal map, then real normal maps win where JSON specifies them).
        var material = new Material(materialId.Value, pipeline);
        configureDefaults?.Invoke(material);

        foreach (var uniform in data.Uniforms)
        {
            material.SetUniform(uniform.Name, uniform.Value);
        }

        foreach (var binding in data.Textures)
        {
            var handle = ResolveTexture(binding.Asset);
            material.SetTexture(binding.Name, handle, binding.Slot);
        }

        return material;
    }

    private TextureHandle ResolveTexture(AssetId id)
    {
        if (textureCache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var image = assets.Load<ImageData>(id);
        var handle = device.CreateTexture2D(image, defaultSampler, name: id.Value);
        textureCache[id] = handle;
        return handle;
    }
}
