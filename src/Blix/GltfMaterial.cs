using System.Numerics;

namespace Blix;

// glTF 2.0 PBR-metallic-roughness material, decoded into the subset the engine
// consumes. The renderer doesn't get the full glTF surface (KHR_materials_*
// extension grab-bag is large), but it covers the fields the current PBR lit
// shader can actually act on plus the ones a downstream shader is likely to.
public sealed record GltfMaterial(
    /// <summary>
    /// What this material IS — stable across loads, and equal for two loads of the same source.
    /// </summary>
    /// <remarks>
    /// <b><c>&lt;container&gt;#material&lt;N&gt;</c>, for the same reason <see cref="GltfTexture"/>
    /// carries one.</b> VulkanSponza's material cache is keyed by this record's OBJECT, so two
    /// imports of one file would build two GPU materials for one material — inert today only
    /// because it loads each of its four packs exactly once.
    /// </remarks>
    /// <para>
    /// <b>The identity is here; the storage is deliberately not.</b> A texture has one canonical GPU
    /// form — <c>(identity, format) → TextureHandle</c> is true for everyone — so a shared
    /// TextureRegistry is possible. A material does not: <c>GltfTextureLoader</c>'s own header says
    /// binding is the renderer's and the UBO layout is the game's, and Sponza caches a
    /// Sponza-shaped tuple. A generic registry over that would be a type parameter pretending to be
    /// a shared decision.
    /// </para>
    /// <para>
    /// Done now rather than when a second owner appears, because the shape does not depend on who
    /// that is: this string is the same whoever shows up. Waiting would buy no information and
    /// guarantee the broken key gets written once more first.
    /// </para>
    string ResourceId,
    string Name,
    // BaseColor: linear-space tint multiplied with the sampled albedo texture.
    Vector4 BaseColorFactor,
    GltfTexture? BaseColorTexture,

    /// <summary>Which TEXCOORD set <see cref="BaseColorTexture"/> samples. Almost always 0.</summary>
    /// <remarks>
    /// <b>Carried because a material naming set 1 on a reader that assumes 0 does not fail — it
    /// samples the wrong coordinates.</b> glTF lets every texture on a material choose its set
    /// independently, so this is a property of the CHANNEL rather than of the mesh or the material.
    /// Only base colour is recorded because only base colour is sampled by anything here; the rest
    /// of the channels keep their sets when something reads them.
    /// </remarks>
    int BaseColorTexCoord,
    // Tangent-space normal map. Null when the material has none -- the lit
    // shader's NormalScale uniform doubles as "use this map at all" gate.
    GltfTexture? NormalTexture,
    // glTF packs metallic + roughness into one texture: G channel = roughness,
    // B channel = metallic. The PBR fragment shader samples this and multiplies
    // by the factors below. Materials without an explicit MR texture use a
    // default (R=0, G=128, B=0) so factors alone drive the BRDF.
    GltfTexture? MetallicRoughnessTexture,
    float MetallicFactor,
    float RoughnessFactor,
    // Ambient-occlusion: glTF-spec R channel of the occlusion-roughness-metallic
    // texture (often the same texture as MR with R=AO). When the channel author
    // packed AO into the MR texture, OcclusionTexture and MetallicRoughnessTexture
    // point at the same GltfTexture and shaders should sample once. Strength is
    // the multiplier on `(1.0 - sample)` before applying.
    GltfTexture? OcclusionTexture,
    float OcclusionStrength,
    // Emissive: additive radiance from the surface, in linear HDR. EmissiveTexture
    // is sRGB-encoded per spec (loader decodes); EmissiveFactor is linear-space.
    // EmissiveStrength is KHR_materials_emissive_strength -- multiplier on the
    // EmissiveFactor that lets authors push emission >1.0 for visible-bloom
    // emissives. 1.0 is the spec default (no extension or factor=1).
    GltfTexture? EmissiveTexture,
    Vector3 EmissiveFactor,
    float EmissiveStrength,
    // Alpha handling: OPAQUE (no test), MASK (binary discard at AlphaCutoff),
    // BLEND (alpha blend, depth-test-no-write, back-to-front sort). The renderer
    // picks a pipeline per material based on this; AlphaCutoff is only used in
    // MASK mode.
    GltfAlphaMode AlphaMode,
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

    /// <summary>Every <c>KHR_materials_*</c> property the spec defines. Defaults to <see cref="GltfMaterialExtensions.None"/>.</summary>
    /// <remarks>
    /// TransmissionFactor above is kept as its own member rather than folded in, because it predates
    /// this and the pipeline sorts on it; it mirrors <c>Extensions.TransmissionFactor</c>.
    /// </remarks>
    GltfMaterialExtensions? Extensions = null)
{
    /// <summary>The extensions, never null — an absent block reads as every spec default.</summary>
    /// <remarks>
    /// Nullable on the record so the two construction sites that predate it keep compiling, and a
    /// cooked material that has not been re-cooked yet says "no extensions" rather than crashing.
    /// Consumers read THIS, so neither of those cases becomes a null check at the call site.
    /// </remarks>
    public GltfMaterialExtensions Ext => Extensions ?? GltfMaterialExtensions.None;
}

public enum GltfAlphaMode
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
/// <b>Read because the spec defines them, not because an asset in the tree asks.</b> Conventions §7:
/// for a format Blix reads, the specification is the requirement and owning an asset that exercises
/// it is a download, not a precondition. SharpGLTF 1.0.6 surfaces thirteen of these and the importer
/// consumed two — transmission and emissive strength — so eleven spec-defined material properties
/// were being parsed by the library and dropped on the floor at our boundary.
/// </para>
/// <para>
/// <b>A group rather than thirty more members on the material.</b> These all belong to one family,
/// they arrive together from one API, and keeping them together leaves GltfMaterial at the width it
/// has. Every field is REQUIRED with a spec default, because the alternative — an optional block —
/// is an optionality mechanism this codebase does not have and does not need: .blixmesh has bumped
/// its version and re-cooked from v2 to v8, and the build re-cooks by itself.
/// </para>
/// <para>
/// <b>What is here is the parameters; what is NOT here is what to do with them.</b> How sheen
/// shades, whether transmission refracts against a scene-colour copy or stays Fresnel-only, what a
/// voxel baker does with diffuse transmission — those are §6 policy and live at the call site. This
/// type's whole job is that the numbers survive the format boundary (§3) so a consumer can decide.
/// </para>
/// </remarks>
public sealed record GltfMaterialExtensions(
    // KHR_materials_transmission — light refracted THROUGH a thin surface: glass, a window.
    float TransmissionFactor,
    GltfTexture? TransmissionTexture,

    // KHR_materials_diffuse_transmission — light SCATTERED through a thin surface. This is the one
    // a leaf and a curtain want: not a clear pane, a sheet that glows when lit from behind.
    float DiffuseTransmissionFactor,
    Vector3 DiffuseTransmissionColorFactor,
    GltfTexture? DiffuseTransmissionTexture,
    GltfTexture? DiffuseTransmissionColorTexture,

    // KHR_materials_sheen — the retroreflective rim fabric has and the metallic-roughness lobe
    // cannot express. Velvet, and the reason a curtain reads as cloth rather than painted board.
    Vector3 SheenColorFactor,
    float SheenRoughnessFactor,
    GltfTexture? SheenColorTexture,
    GltfTexture? SheenRoughnessTexture,

    // KHR_materials_volume — what happens INSIDE a transmissive body. Meaningless without
    // transmission, per the spec, which is why the two are separate extensions.
    float ThicknessFactor,
    float AttenuationDistance,
    Vector3 AttenuationColor,
    GltfTexture? ThicknessTexture,

    // KHR_materials_specular — dielectric F0 strength and tint, without lying about metalness.
    float SpecularFactor,
    Vector3 SpecularColorFactor,
    GltfTexture? SpecularTexture,
    GltfTexture? SpecularColorTexture,

    // KHR_materials_ior — 1.5 is the glTF default and the value the base BRDF assumes.
    float IndexOfRefraction,

    // KHR_materials_clearcoat — a second, smoother specular layer over the first: car paint, lacquer.
    float ClearcoatFactor,
    float ClearcoatRoughnessFactor,
    float ClearcoatNormalScale,
    GltfTexture? ClearcoatTexture,
    GltfTexture? ClearcoatRoughnessTexture,
    GltfTexture? ClearcoatNormalTexture,

    // KHR_materials_iridescence — thin-film interference: soap, beetle shell, oil.
    float IridescenceFactor,
    float IridescenceIor,
    float IridescenceThicknessMinimum,
    float IridescenceThicknessMaximum,
    GltfTexture? IridescenceTexture,
    GltfTexture? IridescenceThicknessTexture,

    // KHR_materials_anisotropy — a directional specular lobe: brushed metal, hair.
    float AnisotropyStrength,
    float AnisotropyRotation,
    GltfTexture? AnisotropyTexture,

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
    public static readonly GltfMaterialExtensions None = new(
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
