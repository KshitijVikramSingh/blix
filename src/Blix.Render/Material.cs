using Blix.Graphics;

namespace Blix.Render;

public sealed class Material
{
    private readonly List<ShaderUniform> uniforms = [];
    private readonly List<ShaderTextureBinding> textures = [];

    public Material(string name, PipelineHandle pipeline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Pipeline = pipeline;
    }

    public string Name { get; }

    public PipelineHandle Pipeline { get; }

    public IReadOnlyList<ShaderUniform> Uniforms => uniforms;

    public IReadOnlyList<ShaderTextureBinding> Textures => textures;

    public Material SetUniform(string uniformName, ShaderUniformValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniformName);

        for (var i = 0; i < uniforms.Count; i++)
        {
            if (uniforms[i].Name == uniformName)
            {
                uniforms[i] = new ShaderUniform(uniformName, value);
                return this;
            }
        }

        uniforms.Add(new ShaderUniform(uniformName, value));
        return this;
    }

    public Material SetTexture(string uniformName, TextureHandle texture, int slot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniformName);

        if (slot < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "Texture slot must not be negative.");
        }

        for (var i = 0; i < textures.Count; i++)
        {
            if (textures[i].Name == uniformName)
            {
                textures[i] = new ShaderTextureBinding(uniformName, texture, slot);
                return this;
            }
        }

        textures.Add(new ShaderTextureBinding(uniformName, texture, slot));
        return this;
    }
}
