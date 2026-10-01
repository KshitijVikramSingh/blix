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
/// <param name="NormalUvSet">The TEXCOORD set each channel samples, as the material authors it.</param>
/// <param name="UvRows">
/// Each channel's <c>KHR_texture_transform</c> as the shader's <c>uUvRows</c>: two rows per channel
/// (base colour, normal, metallic-roughness, occlusion, emissive), two channels per matrix.
/// </param>
internal readonly record struct StudioSurface(
    MaterialTextures Textures, Vector3 Emissive, float OcclusionStrength, float NormalScale,
    int NormalUvSet, int MetallicRoughnessUvSet, int OcclusionUvSet, int EmissiveUvSet, Matrix4x4[] UvRows)
{
    /// <summary>Identity on every channel: what a draw with no material, or no transform, samples with.</summary>
    public static Matrix4x4[] IdentityUvRows { get; } = UvRowsOf(PbrUvTransforms.Identity);

    public static StudioSurface Of(PbrMaterial? material, MaterialTextures textures) => new(
        textures,
        material is null ? Vector3.Zero : material.EmissiveFactor * material.EmissiveStrength,
        material?.OcclusionTexture is null ? 0f : material.OcclusionStrength,
        material?.NormalTexture is null ? 0f : material.NormalScale,
        material?.NormalTexCoord ?? 0,
        material?.MetallicRoughnessTexCoord ?? 0,
        material?.OcclusionTexCoord ?? 0,
        material?.EmissiveTexCoord ?? 0,
        material is null || material.Uv.IsIdentity ? IdentityUvRows : UvRowsOf(material.Uv));

    // Row r of the flattened ten lands in matrix r / 4, row r % 4 — which GLSL reads as column r % 4, the
    // matrix uploaded untransposed (conventions §2).
    private static Matrix4x4[] UvRowsOf(PbrUvTransforms uv)
    {
        var rows = new Vector4[12];
        var channels = new[] { uv.BaseColor, uv.Normal, uv.MetallicRoughness, uv.Occlusion, uv.Emissive };
        for (var c = 0; c < channels.Length; c++)
        {
            var (u, v) = channels[c].Rows;
            rows[c * 2] = new Vector4(u, 0f);
            rows[c * 2 + 1] = new Vector4(v, 0f);
        }

        return Enumerable.Range(0, 3).Select(m => new Matrix4x4(
            rows[m * 4].X, rows[m * 4].Y, rows[m * 4].Z, rows[m * 4].W,
            rows[m * 4 + 1].X, rows[m * 4 + 1].Y, rows[m * 4 + 1].Z, rows[m * 4 + 1].W,
            rows[m * 4 + 2].X, rows[m * 4 + 2].Y, rows[m * 4 + 2].Z, rows[m * 4 + 2].W,
            rows[m * 4 + 3].X, rows[m * 4 + 3].Y, rows[m * 4 + 3].Z, rows[m * 4 + 3].W)).ToArray();
    }
}
