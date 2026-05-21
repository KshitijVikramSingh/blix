using System.Numerics;

namespace Blix;

// glTF PBR-metallic-roughness material decoded into the subset the engine consumes.
// BaseColor (factor + optional texture), normal map, and metallic-roughness texture
// + factors all feed into the Cook-Torrance lit pipeline.
//
// Other glTF material channels (occlusion, emissive, alpha mode) aren't extracted —
// they need a richer fragment shader to consume.
public sealed record GltfMaterial(
    string Name,
    Vector4 BaseColorFactor,
    GltfTexture? BaseColorTexture,
    GltfTexture? NormalTexture,
    // glTF packs metallic + roughness into one texture: G channel = roughness,
    // B channel = metallic. The PBR fragment shader samples this and multiplies
    // by the factors below to get final per-fragment values. Materials without
    // an explicit MR texture use a default (R=0, G=128, B=0) so factors alone
    // drive the BRDF.
    GltfTexture? MetallicRoughnessTexture,
    float MetallicFactor,
    float RoughnessFactor,
    // Emissive channel: light the surface emits on its own, additive over the
    // BRDF result. Per the glTF spec the texture is sRGB-encoded (decoder's
    // responsibility) and the factor is a linear-space multiplier in [0, inf).
    // EmissiveTexture is null for materials that don't author one; the factor
    // alone still drives uniform emission if non-zero.
    GltfTexture? EmissiveTexture,
    Vector3 EmissiveFactor);
