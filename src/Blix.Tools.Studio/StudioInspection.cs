using System.Numerics;
using Blix;
using Blix.Assets;
using Blix.Graphics;

namespace Blix.Tools.Studio;

/// <summary>What Studio's tools report about an asset: its textured parts, the images it ships, and the colours it draws.</summary>
/// <remarks>
/// Inspection policy rather than engine fact, so it is Studio's: an image is a distinct resolved base-colour
/// texture of a part whose material names one, listed in the order the parts meet them.
/// </remarks>
public static class StudioInspection
{
    /// <summary>One image the asset ships, with enough to label it in a panel.</summary>
    public readonly record struct Image(string Name, TextureHandle Texture, int Width, int Height);

    /// <summary>The grey Studio draws a model part without a material in.</summary>
    public static readonly Vector3 ModelFallbackColour = new(0.7f);

    /// <summary>The grey Studio draws a rig part without a material in.</summary>
    public static readonly Vector3 RigFallbackColour = new(0.75f);

    /// <summary>The roughness Studio assumes for a part without a material.</summary>
    public const float FallbackRoughness = 0.7f;

    /// <summary>The colour Studio draws <paramref name="material"/> in: its base-colour factor, or <paramref name="fallback"/>.</summary>
    public static Vector3 BaseColour(PbrMaterial? material, Vector3 fallback) =>
        material is null
            ? fallback
            : new Vector3(material.BaseColorFactor.X, material.BaseColorFactor.Y, material.BaseColorFactor.Z);

    /// <summary>Each part's material name and drawn colour, in part order; names repeat.</summary>
    /// <remarks>A model drawn skinned falls back to the rig grey, one drawn static to the model grey.</remarks>
    public static IEnumerable<(string Name, Vector3 Colour)> Materials(Model model)
    {
        var fallback = model.SkinnedParts.Any() ? RigFallbackColour : ModelFallbackColour;
        return model.Parts.Select(p => (p.Material?.Name ?? string.Empty, BaseColour(p.Material, fallback)));
    }

    /// <summary>Parts whose material names a base-colour texture.</summary>
    public static int TexturedParts(Model model) => model.Parts.Count(p => p.Material?.BaseColorTexture is not null);

    /// <summary>The distinct base-colour images the model's parts use, in part order.</summary>
    public static IReadOnlyList<Image> Images(Model model) =>
        Distinct(model.Parts.Select(p => (p.Material, p.Textures)));

    private static IReadOnlyList<Image> Distinct(IEnumerable<(PbrMaterial? Material, MaterialTextures Textures)> parts)
    {
        var images = new List<Image>();
        foreach (var (material, textures) in parts)
        {
            if (material?.BaseColorTexture is not { } texture || images.Any(i => i.Texture == textures.Albedo)) continue;
            images.Add(new Image(
                string.IsNullOrEmpty(texture.Name) ? $"albedo {images.Count}" : texture.Name,
                textures.Albedo, texture.Width, texture.Height));
        }

        return images;
    }
}
