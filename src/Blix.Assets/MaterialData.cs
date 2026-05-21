using Blix.Graphics;

namespace Blix.Assets;

public sealed record MaterialData(
    string PipelineName,
    IReadOnlyList<ShaderUniform> Uniforms,
    IReadOnlyList<MaterialTextureBinding> Textures);

public sealed record MaterialTextureBinding(string Name, AssetId Asset, int Slot);
