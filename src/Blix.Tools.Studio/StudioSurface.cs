using System.Numerics;
using Blix;

namespace Blix.Tools.Studio;

/// <summary>A glTF material's terms beyond base colour and alpha, as Studio's lit shader reads them.</summary>
/// <remarks>
/// Zero is "absent" for every factor here, so a draw that pushes none of them keeps the base-colour
/// look: <see cref="NormalScale"/> 0 leaves the interpolated normal, <see cref="OcclusionStrength"/> 0
/// leaves the ambient, a black <see cref="Emissive"/> adds nothing.
/// </remarks>
/// <param name="Textures">The resolved channels; the loader's defaults stand in for absent ones.</param>
/// <param name="Emissive">Linear radiance: <c>emissiveFactor × emissive_strength</c>.</param>
/// <param name="OcclusionStrength">The material's strength where it has an occlusion texture; 0 otherwise.</param>
/// <param name="NormalScale">The material's <c>normalTexture.scale</c> where it has a normal texture; 0 otherwise.</param>
/// <param name="EmissiveTextured">
/// Whether the material names an emissive texture. Without one glTF's emission is the factor alone,
/// so Studio binds white there rather than the loader's black stand-in.
/// </param>
/// <param name="NormalUvSet">The TEXCOORD set each channel samples, as the material authors it.</param>
internal readonly record struct StudioSurface(
    MaterialTextures Textures, Vector3 Emissive, float OcclusionStrength, float NormalScale, bool EmissiveTextured,
    int NormalUvSet, int MetallicRoughnessUvSet, int OcclusionUvSet, int EmissiveUvSet)
{
    public static StudioSurface Of(GltfMaterial? material, MaterialTextures textures) => new(
        textures,
        material is null ? Vector3.Zero : material.EmissiveFactor * material.EmissiveStrength,
        material?.OcclusionTexture is null ? 0f : material.OcclusionStrength,
        material?.NormalTexture is null ? 0f : material.NormalScale,
        material?.EmissiveTexture is not null,
        material?.NormalTexCoord ?? 0,
        material?.MetallicRoughnessTexCoord ?? 0,
        material?.OcclusionTexCoord ?? 0,
        material?.EmissiveTexCoord ?? 0);
}
