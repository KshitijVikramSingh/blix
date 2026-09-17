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
    float TransmissionFactor = 0f);

public enum GltfAlphaMode
{
    Opaque = 0,
    Mask,
    Blend,
}
