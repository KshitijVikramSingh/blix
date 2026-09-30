using Blix.Graphics;

namespace Blix;

/// <summary>Making imported models resident: the same verb as <c>CreateMesh</c>, one level up.</summary>
/// <remarks>
/// The CPU side (<see cref="GltfNodeModel"/>, <see cref="GltfModel"/>) exists without a device, for cooking,
/// collision, tests and the headless host; the resident side needs one. This is the one call between them,
/// and what it returns keeps the source facts a consumer asks about (bounds, skeletons, clips, materials),
/// so nothing needs to hold both. Textures are resolved through <paramref name="textures"/>, which owns them.
/// </remarks>
public static class ResidencyExtensions
{
    /// <param name="name">What its GPU resources are named under, for the resource tables and validation.</param>
    public static Model CreateModel(this IGraphicsDevice device, GltfNodeModel model, GltfTextureLoader textures, string name)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(textures);
        return Model.Load(device, model, textures, name);
    }

    /// <inheritdoc cref="CreateModel"/>
    public static Rig CreateRig(this IGraphicsDevice device, GltfModel model, GltfTextureLoader textures, string name)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(textures);
        return Rig.Load(device, model, textures, name);
    }
}
