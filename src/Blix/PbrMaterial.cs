using System.Numerics;

namespace Blix;

// glTF 2.0 PBR-metallic-roughness material plus the KHR_materials_* values
// surfaced by the current SharpGLTF boundary. Import preserves parameters;
// individual renderers decide which ones they implement.
public sealed record PbrMaterial(
    /// <summary>
    /// Stable origin identity for sharing the same material across loads.
    /// </summary>
    /// <remarks>
    /// Formed as <c>&lt;container&gt;#material&lt;N&gt;</c>, parallel to
    /// <see cref="TextureData.ResourceId"/>.
    /// </remarks>
    /// <para>
    /// Identity is shared engine data; GPU material storage remains renderer-owned because binding
    /// layouts and shader policy differ by application.
    /// </para>
    string ResourceId,
    string Name,
    // BaseColor: linear-space tint multiplied with the sampled albedo texture.
    Vector4 BaseColorFactor,
    TextureData? BaseColorTexture,

    /// <summary>Which TEXCOORD set <see cref="BaseColorTexture"/> samples. Almost always 0.</summary>
    /// <remarks>
    /// glTF lets every texture on a material choose its coordinate set independently, so each core
    /// channel records its own: a property of the CHANNEL rather than of the mesh or the material.
    /// </remarks>
    int BaseColorTexCoord,
    // Tangent-space normal map. Null when the material has none.
    TextureData? NormalTexture,
    /// <summary>Which TEXCOORD set <see cref="NormalTexture"/> samples.</summary>
    int NormalTexCoord,
    /// <summary>
    /// <c>normalTexture.scale</c>: the multiplier on the sampled normal's X and Y. 1 when unauthored.
    /// </summary>
    float NormalScale,
    // glTF packs metallic + roughness into one texture: G channel = roughness,
    // B channel = metallic. The PBR fragment shader samples this and multiplies
    // by the factors below. Materials without an explicit MR texture use a
    // default (R=0, G=128, B=0) so factors alone drive the BRDF.
    TextureData? MetallicRoughnessTexture,
    /// <summary>Which TEXCOORD set <see cref="MetallicRoughnessTexture"/> samples.</summary>
    int MetallicRoughnessTexCoord,
    float MetallicFactor,
    float RoughnessFactor,
    // Ambient-occlusion: glTF-spec R channel of the occlusion-roughness-metallic
    // texture (often the same texture as MR with R=AO). When the channel author
    // packed AO into the MR texture, OcclusionTexture and MetallicRoughnessTexture
    // point at the same TextureData and shaders should sample once. Strength is
    // the multiplier on `(1.0 - sample)` before applying.
    TextureData? OcclusionTexture,
    /// <summary>Which TEXCOORD set <see cref="OcclusionTexture"/> samples.</summary>
    int OcclusionTexCoord,
    float OcclusionStrength,
    // Emissive: additive radiance from the surface, in linear HDR. EmissiveTexture
    // is sRGB-encoded per spec (loader decodes); EmissiveFactor is linear-space.
    // EmissiveStrength is KHR_materials_emissive_strength -- multiplier on the
    // EmissiveFactor that lets authors push emission >1.0 for visible-bloom
    // emissives. 1.0 is the spec default (no extension or factor=1).
    TextureData? EmissiveTexture,
    /// <summary>Which TEXCOORD set <see cref="EmissiveTexture"/> samples.</summary>
    int EmissiveTexCoord,
    Vector3 EmissiveFactor,
    float EmissiveStrength,
    // Alpha handling: OPAQUE (no test), MASK (binary discard at AlphaCutoff),
    // BLEND (alpha blend, depth-test-no-write, back-to-front sort). The renderer
    // picks a pipeline per material based on this; AlphaCutoff is only used in
    // MASK mode.
    AlphaMode AlphaMode,
    float AlphaCutoff,
    // Whether to draw both faces. Foliage, curtains, and decals typically need
    // this; back-face culling stays the default everywhere else. The renderer
    // picks a no-cull pipeline variant for materials with this set.
    bool DoubleSided,
    // KHR_materials_transmission: fraction of light transmitted through the
    // surface (0 = opaque, 1 = fully transmissive, e.g. clear glass). The
    // renderer routes transmission>0 materials through the blend pipeline and
    // shades them as Fresnel glass. Default 0 (no extension). Appended last so
    // existing positional constructors keep compiling.
    float TransmissionFactor = 0f,

    /// <summary>Every <c>KHR_materials_*</c> property the spec defines. Defaults to <see cref="PbrMaterialExtensions.None"/>.</summary>
    /// <remarks>
    /// TransmissionFactor above is kept as its own member rather than folded in, because it predates
    /// this and the pipeline sorts on it; it mirrors <c>Extensions.TransmissionFactor</c>.
    /// </remarks>
    PbrMaterialExtensions? Extensions = null)
{
    /// <summary>The extensions, never null — an absent block reads as every spec default.</summary>
    /// <remarks>
    /// Nullable on the record so the two construction sites that predate it keep compiling, and a
    /// cooked material that has not been re-cooked yet says "no extensions" rather than crashing.
    /// Consumers read THIS, so neither of those cases becomes a null check at the call site.
    /// </remarks>
    public PbrMaterialExtensions Ext => Extensions ?? PbrMaterialExtensions.None;

    /// <summary>What a material IS, as a string two loads agree on: <c>&lt;container&gt;#material&lt;N&gt;</c>.</summary>
    /// <remarks>
    /// Empty when the caller cannot say which file it came from — never shared, for the same reason
    /// an unnamed texture is not: an identity nobody can reproduce is not an identity. One spelling,
    /// written by the source importer and the cooked reader alike.
    /// </remarks>
    public static string IdentityOf(string containerPath, int index) =>
        containerPath.Length == 0 ? string.Empty : $"{Path.GetFullPath(containerPath)}#material{index}";
}

public enum AlphaMode
{
    Opaque = 0,
    Mask,
    Blend,
}

/// <summary>
/// The <c>KHR_materials_*</c> extensions, as the specification defines them.
/// </summary>
/// <remarks>
/// <para>
/// These values follow the glTF specification surface exposed by SharpGLTF rather than the subset
/// exercised by repository-owned sample assets.
/// </para>
/// <para>
/// The extension family is grouped separately from core metallic-roughness fields. Every field has
/// its specification default so absence preserves the base material model.
/// </para>
/// <para>
/// This type preserves parameters only. Shading, transmission, and baking policy belong to each
/// renderer or baker.
/// </para>
/// </remarks>
public sealed record PbrMaterialExtensions(
    // KHR_materials_transmission — light refracted THROUGH a thin surface: glass, a window.
    float TransmissionFactor,
    TextureData? TransmissionTexture,

    // KHR_materials_diffuse_transmission — light SCATTERED through a thin surface. This is the one
    // a leaf and a curtain want: not a clear pane, a sheet that glows when lit from behind.
    float DiffuseTransmissionFactor,
    Vector3 DiffuseTransmissionColorFactor,
    TextureData? DiffuseTransmissionTexture,
    TextureData? DiffuseTransmissionColorTexture,

    // KHR_materials_sheen — the retroreflective rim fabric has and the metallic-roughness lobe
    // cannot express. Velvet, and the reason a curtain reads as cloth rather than painted board.
    Vector3 SheenColorFactor,
    float SheenRoughnessFactor,
    TextureData? SheenColorTexture,
    TextureData? SheenRoughnessTexture,

    // KHR_materials_volume — what happens INSIDE a transmissive body. Meaningless without
    // transmission, per the spec, which is why the two are separate extensions.
    float ThicknessFactor,
    float AttenuationDistance,
    Vector3 AttenuationColor,
    TextureData? ThicknessTexture,

    // KHR_materials_specular — dielectric F0 strength and tint, without lying about metalness.
    float SpecularFactor,
    Vector3 SpecularColorFactor,
    TextureData? SpecularTexture,
    TextureData? SpecularColorTexture,

    // KHR_materials_ior — 1.5 is the glTF default and the value the base BRDF assumes.
    float IndexOfRefraction,

    // KHR_materials_clearcoat — a second, smoother specular layer over the first: car paint, lacquer.
    float ClearcoatFactor,
    float ClearcoatRoughnessFactor,
    float ClearcoatNormalScale,
    TextureData? ClearcoatTexture,
    TextureData? ClearcoatRoughnessTexture,
    TextureData? ClearcoatNormalTexture,

    // KHR_materials_iridescence — thin-film interference: soap, beetle shell, oil.
    float IridescenceFactor,
    float IridescenceIor,
    float IridescenceThicknessMinimum,
    float IridescenceThicknessMaximum,
    TextureData? IridescenceTexture,
    TextureData? IridescenceThicknessTexture,

    // KHR_materials_anisotropy — a directional specular lobe: brushed metal, hair.
    float AnisotropyStrength,
    float AnisotropyRotation,
    TextureData? AnisotropyTexture,

    // KHR_materials_dispersion — wavelength-dependent IOR. Needs transmission to mean anything.
    float Dispersion,

    // KHR_materials_unlit — "shade this as base colour and stop". A whole model, not a parameter.
    bool Unlit)
{
    /// <summary>Every field at its glTF-specified default: the material that declares no extension.</summary>
    /// <remarks>
    /// The defaults are the SPEC's, not zero-for-everything. IOR is 1.5 and attenuation distance is
    /// infinite because that is what the absence of those extensions means; writing 0 would be a
    /// different material, not a neutral one.
    /// </remarks>
    public static readonly PbrMaterialExtensions None = new(
        TransmissionFactor: 0f, TransmissionTexture: null,
        DiffuseTransmissionFactor: 0f, DiffuseTransmissionColorFactor: Vector3.One,
        DiffuseTransmissionTexture: null, DiffuseTransmissionColorTexture: null,
        SheenColorFactor: Vector3.Zero, SheenRoughnessFactor: 0f,
        SheenColorTexture: null, SheenRoughnessTexture: null,
        ThicknessFactor: 0f, AttenuationDistance: float.PositiveInfinity, AttenuationColor: Vector3.One,
        ThicknessTexture: null,
        SpecularFactor: 1f, SpecularColorFactor: Vector3.One,
        SpecularTexture: null, SpecularColorTexture: null,
        IndexOfRefraction: 1.5f,
        ClearcoatFactor: 0f, ClearcoatRoughnessFactor: 0f, ClearcoatNormalScale: 1f,
        ClearcoatTexture: null, ClearcoatRoughnessTexture: null, ClearcoatNormalTexture: null,
        IridescenceFactor: 0f, IridescenceIor: 1.3f,
        IridescenceThicknessMinimum: 100f, IridescenceThicknessMaximum: 400f,
        IridescenceTexture: null, IridescenceThicknessTexture: null,
        AnisotropyStrength: 0f, AnisotropyRotation: 0f, AnisotropyTexture: null,
        Dispersion: 0f,
        Unlit: false);
}
