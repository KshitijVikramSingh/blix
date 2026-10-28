// The lit pass's fragment shader, built by lit.frag (cutout materials) and lit_opaque.frag (LIT_OPAQUE).

// The engine's shared cascade implementation owns PCF shape, normal offset, and cascade selection.
// Sponza overrides only the tap count. Four taps measured about 18% cheaper than sixteen for the
// current high sun with no material image difference; remeasure if the default sky or sun angle
// changes enough to put broad penumbrae across the view.
#define BLIX_SHADOW_PCF_TAPS 4
// ── What this pass costs, measured ───────────────────────────────────────────
// Each term ablated in-process on the measurement orbit (--ab <term>), ratios first because the
// three runs sat at 28.8, 32.6 and 49.7 ms medians and the ratio is the part that reproduces:
//
//     probe-volume terms          1.312x   8.69 ms   (the baked volumes, since retired)
//     material texture bandwidth  1.150x   5.42 ms
//     normal mapping              1.108x   3.97 ms
//     image-based lighting        1.084x   3.77 ms
//     ambient visibility (GTAO)   1.076x   2.30 ms
//     sun shadows (sample + PCF)  1.052x   2.46 ms
//     the GGX specular lobe       1.002x   0.04 ms
//
// They do not sum to the pass: each is what removing that term saves with everything else present,
// so the table ranks levers rather than partitioning a budget.
//
// The attribution shows a memory-bound pass: material/normal-map traffic is much more expensive
// than the GGX arithmetic. Measurements must use the foliage-inclusive orbit; a wall-only path
// changes the ranking rather than merely scaling it.
//
// Do not enable early_fragment_tests while cutout coverage depends on fwidth(alpha). The depth and
// lit passes can evaluate derivatives over different live quads, producing mismatched leaf edges
// and visible holes. Late-Z preserves agreement; foliage cost is primarily thin-sheet shading, not
// recoverable overdraw.
//
// Built twice: lit.frag (cutout, late-Z, as above) for alpha-mask materials and lit_opaque.frag (LIT_OPAQUE) for
// everything else, with no discard and no sample mask, so its draws keep the early depth test. One shared build had
// the cutout's discard turn the early test off for every opaque draw, so covered surfaces shaded too (2026-10-24).
#ifdef LIT_OPAQUE
layout(early_fragment_tests) in;
#endif
// LIT_RARE(c): a branch most pixels never take -- sheen, diffuse transmission, the cascades' fallback, the debug views,
// fog. The lean build (lit_lean.frag) compiles them out: a pixel pays for the registers of code it does not run, and
// the opaque pass went 3.6 -> 3.1 ms at 2880 x 1620 without them (2026-10-24). SponzaLoop.Render.cs picks it only
// where none can be taken: a material with neither sheen nor diffuse transmission, a frame with none of the rest.
#ifdef LIT_LEAN
#define LIT_RARE(c) false
#else
#define LIT_RARE(c) (c)
#endif

#include "shadow.glsl"
#include "sheen.glsl"
#include "noise.glsl"
#include "coverage.glsl"
#include "froxel.glsl"

// Lit fragment shader — Cook-Torrance split-sum IBL on top of a Lambert N·L
// sun term, with cascaded shadows, a Fresnel-glass branch, and froxel-fog
// composite.
//
//   set 0 binding 0 : per-frame UBO (viewProj, sun, IBL strength, camera, fog)
//   set 1           : per-pass textures, all SEPARATE images read through one sampler
//                     (uLinearClamp, binding 19) -- see the note above that binding. Among them:
//   set 1 binding 0 : textureCube uIrradiance      (diffuse IBL)
//   set 1 binding 1 : textureCube uPrefilteredEnv  (specular IBL; mip = roughness LOD)
//   set 1 binding 2 : texture2D   uBrdfLut         (split-sum BRDF integration)
//   set 1 binding 3 : texture2D   uCascadeShadowMaps[3]
//   set 1 binding 4 : texture3D   uFroxelGrid      (volumetric fog)
//   set 1 binding 5 : texture2D   uAmbientVisibility (GTAO: visibility in .r)
//   set 2 binding 0 : per-material UBO (BaseColorFactor, EmissiveFactor,
//                                       MaterialParams = alphaCutoff/normalScale/
//                                       roughness/metallic, MaterialParams2 = the two
//                                       transmissions, sheen roughness, and the MSAA/cutout
//                                       policy -- see the block for why the last one lives there)
//   set 2 binding 1 : albedo  (sRGB)
//   set 2 binding 2 : normal  (linear; tangent-space)
//   set 2 binding 3 : emissive (sRGB)
//   set 2 binding 4 : metallic-roughness (linear; G=rough, B=metal)
//   set 2 binding 5 : occlusion (linear; R=AO)
//
// Roughness/metallic are sampled from the MR texture × per-material factors.

#include "frame.glsl"

layout(set = 1, binding = 0) uniform textureCube uIrradiance;
layout(set = 1, binding = 1) uniform textureCube uPrefilteredEnv;
layout(set = 1, binding = 2) uniform texture2D   uBrdfLut;
// Cascaded sun shadow maps (one depth target per cascade, near→far). Sampled
// with manual depth comparison + 3×3 PCF; cascade chosen by the fragment's
// view-space depth. GLSL forbids non-uniform dynamic indexing of a sampler
// array, so the picker dispatches with constant indices (MoltenVK-safe).
layout(set = 1, binding = 3) uniform texture2D   uCascadeShadowMaps[3];
#define CASCADE_COUNT 3
// Froxel volumetric fog grid: (xy) = screen UV, z = world distance / fogFar.
// .rgb = integrated in-scattering to that distance, .a = transmittance.
layout(set = 1, binding = 4) uniform texture3D   uFroxelGrid;
// Ambient visibility from the GTAO pass: .xyz = bent normal (WORLD space), .a = visibility.
layout(set = 1, binding = 5) uniform texture2D   uAmbientVisibility;
// The environment convolved with CHARLIE rather than GGX, and the Charlie lobe's directional
// albedo. Separate from uPrefilteredEnv on purpose: a GGX cube in sheen's place renders something
// dimmer and rimless and entirely plausible, which is the failure this whole arc keeps closing.
layout(set = 1, binding = 8) uniform textureCube uSheenEnv;
layout(set = 1, binding = 9) uniform texture2D   uSheenLut;
// Not read here: the lit shader reads the prefiltered chain, not the raw sky. Declared because the
// lit draws are handed the pass's shared binding list, which carries it, and a texture bound by name
// that the program does not declare is an error. (It used to be for layout compatibility with the
// skybox, which per-draw descriptor sets made moot.)
layout(set = 1, binding = 10) uniform textureCube uEnvCube;

// The incident-light field from the probe clipmap: rgb = all the indirect diffuse light arriving (sky included),
// a = sky visibility. Read when uIncident.z says the pass ran. See incident_clipmap.frag.
layout(set = 1, binding = 17) uniform texture2D uIncidentField;
// How the incident light's luminance changes with the normal, at the geometric normal (incident_clipmap.frag):
// what lets light the incident pass evaluated at the geometric normal reach the normal-mapped one.
layout(set = 1, binding = 20) uniform texture2D uIncidentGradient;
// The pre-pass normal, bound here ONLY so viz channel 22 can show what the incident field reads.
// Nothing in the lit path samples it: lit.frag has its own, better normal.
layout(set = 1, binding = 18) uniform texture2D uPrepassNormalViz;

// <b>Every set-1 texture above is a separate image, read through this one sampler.</b> As combined
// samplers they were 19 of this stage's 25, against MoltenVK's 16 per stage; as separate images they
// count against sampled images (256) instead, and the stage holds 7 samplers: this and set 2's six.
// One is enough because every one of those textures was created LinearClamp or LinearClampMipmap,
// which are the same VkSampler: GenerateMipmaps is a texture-creation flag, and every sampler leaves
// MaxLod unclamped so the texture's own mip count limits it. See docs/renderer.md, "Separate images
// and samplers".
//@sampler LinearClamp
layout(set = 1, binding = 19) uniform sampler uLinearClamp;

// The probe clipmap itself, for the one surface the incident field cannot answer: a glass pane, absent from the
// pre-pass, so the field at its pixel is the surface behind it. Its reflection asks the clipmap how much sky R sees
// from the pane (frame.uClip*). Bound to a placeholder until the clipmap exists; uIncident.z gates the read.
layout(set = 1, binding = 11) uniform texture2D uClipmapIrradiance;
layout(set = 1, binding = 12) uniform texture2D uClipmapDepth;
layout(std430, set = 1, binding = 13) readonly buffer ClipmapState { uvec4 clipmapStates[]; };
// --rt-shadows: the sun's visibility traced on Apple's ray tracing hardware (SponzaLoop.RtShadows): a header (width,
// height, 1 while live, 1 with sky rays), then one word per pixel: 1 where the sun is blocked, plus 2, with sky rays,
// where this frame's sky ray met something. Live with no size: the trace is not ready, and the sun is off
// (the cascades are not asked). A placeholder of zeros otherwise, and the cascades answer as before.
layout(std430, set = 1, binding = 21) readonly buffer RtShadowMask { uvec4 rtShadowHeader; uint rtShadowWords[]; };

float rtSunVisibility(vec2 fragCoord) {
    // RenderSettings.HalfResShadows: the mask is half-res, and lighting_upsample.frag has brought it back to this
    // pixel along the depth and normals, fractional at a shadow's edge, into the gradient's w.
    if (frame.uIncident.w > 0.5) return texelFetch(sampler2D(uIncidentGradient, uLinearClamp), ivec2(fragCoord), 0).w;
    uvec2 px = uvec2(fragCoord);
    if (px.x >= rtShadowHeader.x || px.y >= rtShadowHeader.y) return 0.0;
    uint word = rtShadowWords[px.y * rtShadowHeader.x + px.x];
    // z 2: rt_shadow_filter.comp has written the penumbra's visibility into bits 8-15.
    if (rtShadowHeader.z == 2u) return float((word >> 8) & 255u) / 255.0;
    return (word & 1u) != 0u ? 0.0 : 1.0;
}

// The light sunlit sheets (curtains) send the pixel through them, a fraction of the sun's on it (the Metal pass's sheet
// light: SponzaLoop.RtShadows.cs RtMaskPlanes): the filter's spread third plane where it ran (bit 3), the ray's own
// second plane otherwise (bit 2 with the blocked bit). At half res (RenderSettings.HalfResShadows) the half-res pixel
// under this one: the glow is spread over a curtain's width, so its nearest half-res answer is as good as an upsample.
// Diffuse light from the cloth, not the sun's beam: it lights the diffuse lobe alone.
vec3 rtSheetGlow(vec2 fragCoord) {
    uvec2 px = frame.uIncident.w > 0.5 ? uvec2(fragCoord) / 2u : uvec2(fragCoord);
    if (px.x >= rtShadowHeader.x || px.y >= rtShadowHeader.y) return vec3(0.0);
    uint index = px.y * rtShadowHeader.x + px.x;
    uint plane = rtShadowHeader.x * rtShadowHeader.y;
    uint word = rtShadowWords[index];
    vec3 g;
    if (rtShadowHeader.z == 2u) {
        if ((word & 8u) == 0u) return vec3(0.0);
        g = unpackUnorm4x8(rtShadowWords[2u * plane + index]).rgb;
    } else {
        if ((word & 5u) != 5u) return vec3(0.0);
        g = unpackUnorm4x8(rtShadowWords[plane + index]).rgb;
    }
    return g * g;
}

// The shadow terminator (Chiang, Li and Burley 2019, "Taming the Shadow Terminator"): a mesh shaded by normals that
// are not its triangles' -- smooth vertex normals over coarse facets, a normal map over those -- is lit by the shading
// normal Ns out to where the flat triangle Ng turns from the light, and there the light ended in jagged facet-shaped
// blocks: the sun's rays from the flat triangles meet the next facets, and the shading has no falloff to hide it (the
// curtains' folds, 2026-10-16). G is the share of a microfacet surface tilted to Ns that Ng's plane leaves lit, smoothed
// to a C1 falloff; 1 wherever the two agree.
float blix_terminator(vec3 Ns, vec3 Ng, vec3 L) {
    float cg = dot(Ng, L), cs = dot(Ns, L), cd = dot(Ng, Ns);
    if (cg <= 0.0 || cs <= 0.0) return 0.0;
    if (cd <= 1e-4) return 1.0;
    float g = min(1.0, cg / (cs * cd));
    return g * (1.0 + g - g * g);
}

// This frame's traced sky ray at the pixel: 1 where it reached the sky. One cosine-weighted ray a frame, so TAA's
// history is what makes it the sky visibility.
float rtSkyVisibility(vec2 fragCoord) {
    uvec2 px = uvec2(fragCoord);
    if (px.x >= rtShadowHeader.x || px.y >= rtShadowHeader.y) return 1.0;
    return (rtShadowWords[px.y * rtShadowHeader.x + px.x] & 2u) != 0u ? 0.0 : 1.0;
}
#define BLIX_CLIPMAP_IRRADIANCE(t) texelFetch(sampler2D(uClipmapIrradiance, uLinearClamp), t, 0)
#define BLIX_CLIPMAP_DEPTH(t) texelFetch(sampler2D(uClipmapDepth, uLinearClamp), t, 0)
#define BLIX_CLIPMAP_IRRADIANCE_FILTERED(p) textureLod(sampler2D(uClipmapIrradiance, uLinearClamp), (p) / vec2(textureSize(sampler2D(uClipmapIrradiance, uLinearClamp), 0)), 0.0)
#define BLIX_CLIPMAP_DEPTH_FILTERED(p) textureLod(sampler2D(uClipmapDepth, uLinearClamp), (p) / vec2(textureSize(sampler2D(uClipmapDepth, uLinearClamp), 0)), 0.0)
#define BLIX_CLIPMAP_STATE(s) clipmapStates[s]
#include "probe_clipmap.glsl"
#include "lightmap_frame.glsl"

layout(set = 2, binding = 0) uniform Material {
    vec4 uBaseColorFactor;
    vec4 uEmissiveFactor;
    vec4 uMaterialParams;  // x=alphaCutoff, y=normalScale, z=roughness, w=metallic
    // x=transmission (KHR_materials_transmission, the clear pane)
    // y=sheenRoughness  z=diffuseTransmission (KHR_materials_diffuse_transmission, the thin sheet)
    // w=MSAA sample count, or the cutout policy at one sample. NOT a material property: the depth
    //   pre-pass's mask shader has no frame block and needs the same coverage decision this pass
    //   makes, and a spare component here beat duplicating a std140 layout to reach one float.
    //   See SponzaLoop.Scene.cs, which packs it, and depth_prepass_mask.frag, which reads it.
    vec4 uMaterialParams2;
    vec4 uSheenColor;              // KHR_materials_sheen colour factor; w 1: KHR_materials_unlit
    vec4 uDiffuseTransmissionColor;// KHR_materials_diffuse_transmission colour; w 1: the colour texture's hue (--cloth-transmission)
} mat;

layout(set = 2, binding = 1) uniform sampler2D uAlbedo;
layout(set = 2, binding = 2) uniform sampler2D uNormalMap;
layout(set = 2, binding = 3) uniform sampler2D uEmissive;
// Metallic-roughness, LINEAR encoded:
//   G = roughness, B = metallic.
// R can carry AO when an asset packs ORM into one texture, but Sponza
// Modern ships AO as a separate OcclusionTexture and leaves MR.R empty —
// so we sample AO from its own dedicated binding below instead of MR.R.
layout(set = 2, binding = 4) uniform sampler2D uMetallicRoughness;
// Ambient occlusion (R channel, linear). Default texture is white so
// materials without an OcclusionTexture get no extra attenuation.
layout(set = 2, binding = 5) uniform sampler2D uOcclusion;
// KHR_materials_diffuse_transmission's colour texture (sRGB), multiplying the factor beside it.
// Defaults to 1x1 white, so a material that authors only the factor is unaffected.
layout(set = 2, binding = 6) uniform sampler2D uDiffuseTransmissionColorTex;
// Its factor texture: the alpha times diffuseTransmissionFactor is the share that goes through (white by default).
layout(set = 2, binding = 7) uniform sampler2D uDiffuseTransmissionTex;

layout(location = 0) in vec3 vNormalWorld;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;
layout(location = 3) in vec3 vTangentWorld;   // world-space tangent (forwarded glTF TANGENT.xyz)
layout(location = 4) in float vTangentSign;   // glTF TANGENT.w handedness
layout(location = 10) in vec2 vLightmapTexel; // --lightmap: the lightmap atlas texel (lit.vert, instances.glsl)

// A sheet's far side as the GI has it (frame.uIncidentGradient.w): the four atlas texels about the pixel's, of each
// double-sided pair the record facing away from n (the pair: the front's record, then the back's, SponzaLoop.Lightmap.cs),
// their resolved irradiance (rgb9e5; word 1 bit 16 once gathered) bilinear. Surfaces that transmit get such pairs
// whether double-sided or not (SponzaLoop.Scene.cs, GiTwoSided): a closed thin shell -- Khronos' teacup, single-sided,
// T 1 -- reflects almost nothing and showed black where its far side was the sky cube's guess and the GI kept none
// (tools/gi-model/thin_shell.py: the gap's light settles in the fine round and two near rounds, within 2-4%).
layout(std430, set = 1, binding = 22) readonly buffer LitTexels { uvec4 litTexels[]; };          // 2 a record: pos (w < 0 a pair), normal
#define TEXEL_ARRAY litTexels
#include "texel_record.glsl"
layout(std430, set = 1, binding = 23) readonly buffer LitTexelResolved { uvec2 litResolved[]; };
// --near-glow-split (frame.uIncidentGradient.w 3): each record's glow from the sheets within the frame's pass-by -- its thin
// wall's other face -- per unit of the sun's irradiance (slice_resolve.comp, GlowPart): left out here, the frame drawing
// that sun per pixel (sunTransmission), so it is counted once and sharp.
layout(std430, set = 1, binding = 24) readonly buffer LitGlowPart { uvec2 litGlow[]; };
bool lightmapBackIrradiance(vec2 texel, vec3 n, out vec3 e) {
    e = vec3(0.0);
    uvec2 size = lightmapFrames[0];
    if (size.x == 0u || dot(texel, texel) < 1e-6) return false;
    vec2 f = texel - 0.5;
    ivec2 base = ivec2(floor(f));
    vec2 t = f - vec2(base);
    float weight = 0.0;
    for (int i = 0; i < 4; ++i) {
        ivec2 o = ivec2(i & 1, i >> 1);
        ivec2 at = base + o;
        if (any(lessThan(at, ivec2(0))) || any(greaterThanEqual(at, ivec2(size)))) continue;
        uint id = LIGHTMAP_CELL_OF(at);
        if (id == 0xFFFFFFFFu || TEXEL_POSITION(id).w >= 0.0) continue;    // none, or not a pair
        // The pair's record facing away from n: its back.
        uint back = dot(TEXEL_NORMAL(id).xyz, n) < 0.0 ? id : id + 1u;
        if (dot(TEXEL_NORMAL(back).xyz, n) >= 0.0 || abs(TEXEL_POSITION(back).w) < 2.0) continue;
        uvec2 r = litResolved[back];
        if ((r.y & 0x10000u) == 0u) continue;
        uint w9 = r.x;
        float scale = exp2(float(int(w9 >> 27) - 15 - 9));
        vec3 l = vec3(float(w9 & 511u), float((w9 >> 9) & 511u), float((w9 >> 18) & 511u)) * scale;
        if (frame.uIncidentGradient.w > 2.5 && (litGlow[back].y & 0x10000u) != 0u) {
            uint g9 = litGlow[back].x;
            float gs = exp2(float(int(g9 >> 27) - 15 - 9));
            l = max(l - vec3(float(g9 & 511u), float((g9 >> 9) & 511u), float((g9 >> 18) & 511u)) * gs * frame.uSunIrradiance, vec3(0.0));
        }
        float wt = (o.x == 1 ? t.x : 1.0 - t.x) * (o.y == 1 ? t.y : 1.0 - t.y);
        e += wt * l;
        weight += wt;
    }
    if (weight < 1e-4) return false;
    e /= weight;
    return true;
}
// --texel-h4 (frame.uTexelShape.x): the records' pooled shapes (texel_compose.comp; texel_shape.glsl), one uint a record and
// channel -- the four atlas texels about the pixel's, of a pair the record facing n, bilinear: per channel c, the light
// at a normal n' near the record's E_c (1 + s_c . (n' - n)). The incident pass's one gradient is luminance: a bump between
// the red wall's bounce and the white ceiling's took the mix's tilt in both colours (the model: 31-36% of the direction
// term's colour, tools/gi-model/directional_bases.py).
layout(std430, set = 1, binding = 25) readonly buffer LitTexelShape { uint litShape[]; };
#define TEXEL_SHAPE_NO_STORE
#include "texel_shape.glsl"
bool lightmapShape(vec2 texel, vec3 n, out vec3 s0, out vec3 s1, out vec3 s2) {
    s0 = vec3(0.0); s1 = vec3(0.0); s2 = vec3(0.0);
    uvec2 size = lightmapFrames[0];
    if (frame.uTexelShape.x < 0.5 || size.x == 0u || dot(texel, texel) < 1e-6) return false;
    vec2 f = texel - 0.5;
    ivec2 base = ivec2(floor(f));
    vec2 t = f - vec2(base);
    float weight = 0.0;
    // uTexelShape.z (the default; --texel-shape-bilinear: 0): the nearest of the four alone -- the shapes pool over 20 cm, a quarter of
    // the reads; the lookup was 1.8 of the lit pass's 5.0 ms at Crytek Sponza's courtyard (2026-10-24).
    int first = frame.uTexelShape.z > 0.5 ? (t.x >= 0.5 ? 1 : 0) + (t.y >= 0.5 ? 2 : 0) : 0;
    int last = frame.uTexelShape.z > 0.5 ? first + 1 : 4;
    for (int i = first; i < last; ++i) {
        ivec2 o = ivec2(i & 1, i >> 1);
        ivec2 at = base + o;
        if (any(lessThan(at, ivec2(0))) || any(greaterThanEqual(at, ivec2(size)))) continue;
        uint id = LIGHTMAP_CELL_OF(at);
        if (id == 0xFFFFFFFFu) continue;
        // A pair: the record facing n (the front's, then the back's).
        if (TEXEL_POSITION(id).w < 0.0 && dot(TEXEL_NORMAL(id).xyz, n) < 0.0) id += 1u;
        if (abs(TEXEL_POSITION(id).w) < 2.0) continue;   // buried
        vec4 q0 = texelShapeUnpack(litShape[3u * id]);
        if (q0.w < 0.5) continue;
        float wt = (o.x == 1 ? t.x : 1.0 - t.x) * (o.y == 1 ? t.y : 1.0 - t.y);
        s0 += wt * q0.xyz;
        s1 += wt * texelShapeUnpack(litShape[3u * id + 1u]).xyz;
        s2 += wt * texelShapeUnpack(litShape[3u * id + 2u]).xyz;
        weight += wt;
    }
    if (weight < 1e-4) return false;
    s0 /= weight; s1 /= weight; s2 /= weight;
    return true;
}
layout(location = 5) flat in uint vLod;        // the LOD level drawn (instances.glsl): LOD 0 keeps its own frame

// Viz 25, the lightmap's parameterization: a checker one texel wide, tinted per 64-texel block of the atlas -- seams
// show where the tint or the checker breaks, stretch where the cells are not square, density by their size on
// screen. Magenta where a primitive has no lightmap (no texel at all).
vec3 vizLightmap(vec2 t) {
    if (dot(t, t) < 1e-6) return vec3(1.0, 0.0, 1.0);
    ivec2 c = ivec2(floor(t));
    float check = ((c.x + c.y) & 1) == 0 ? 0.55 : 0.9;
    ivec2 block = c / 64;
    uint h = uint(block.x) * 73856093u ^ uint(block.y) * 19349663u;
    h ^= h >> 13;
    vec3 tint = vec3(float(h & 255u), float((h >> 8) & 255u), float((h >> 16) & 255u)) / 255.0 * 0.6 + 0.4;
    return tint * check;
}

layout(location = 0) out vec4 outColor;

#define PI 3.14159265359

// Roughness-aware Fresnel-Schlick that softens edges as surfaces roughen
// (otherwise rough metals over-glow at grazing angles where the split-sum
// approximation breaks down). Used for the wide IBL reflection cone.
vec3 fresnelSchlickRoughness(float cosTheta, vec3 F0, float roughness) {
    return F0 + (max(vec3(1.0 - roughness), F0) - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

// --- Cook-Torrance terms for direct (analytic) sun specular --------------
// Inlined rather than #included from Blix.Shaders/pbr.glsl because this
// project's glslc invocation passes no -I library path (the shaders stay
// self-contained); the math is the same as blix_* there.

// Trowbridge-Reitz GGX microfacet distribution. roughness is perceptual;
// a = r*r remaps to the GGX alpha the literature uses.
float distributionGGX(float NdotH, float roughness) {
    float a  = roughness * roughness;
    float a2 = a * a;
    float d  = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d);
}

// Smith geometry with the direct-lighting k = (r+1)^2 / 8 remap (IBL uses a/2).
float geometrySchlickGGX(float NdotX, float k) {
    return NdotX / (NdotX * (1.0 - k) + k);
}
float geometrySmith(float NdotV, float NdotL, float roughness) {
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return geometrySchlickGGX(NdotV, k) * geometrySchlickGGX(NdotL, k);
}

// Standard Schlick Fresnel for the sharp analytic highlight (the roughness-
// aware variant above is for the wide IBL cone, not a punctual light).
vec3 fresnelSchlick(float cosTheta, vec3 F0) {
    return F0 + (1.0 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

// Cascaded sun shadows are provided by shadow.glsl. Its containment-based selection avoids the
// view-depth boundary arcs that can occur across a wide frustum, and it owns PCF, normal offset,
// bias, and diagnostic tint consistently with other renderers.

vec3 vizClipmap(vec3 worldPos, vec3 n, bool solves) {
    if (frame.uIncident.z < 0.5) return vec3(0.0);
    BlixClipmap c;
    c.dims = ivec3(frame.uClipDims.xyz);
    c.baseSpacing = frame.uClipDims.w;
    c.blendProbes = frame.uClipOrigin0.w;
    c.origin[0] = ivec3(frame.uClipOrigin0.xyz);
    c.origin[1] = ivec3(frame.uClipOrigin1.xyz);
    c.origin[2] = ivec3(frame.uClipOrigin2.xyz);
    c.origin[3] = ivec3(frame.uClipOrigin3.xyz);
    int level;
    float n8 = blix_clipmapSolvesAt(c, worldPos, n, level);
    if (level < 0) return vec3(0.0);
    if (!solves) {
        return level == 0 ? vec3(1.0) : level == 1 ? vec3(0.1, 0.9, 0.2) : level == 2 ? vec3(0.2, 0.4, 1.0) : vec3(1.0, 0.2, 1.0);
    }
    return n8 < 1.5 ? vec3(1.0, 0.1, 0.1) : n8 < 3.5 ? vec3(1.0, 0.5, 0.1) : n8 < 7.5 ? vec3(1.0, 0.9, 0.1)
         : n8 < 31.5 ? vec3(0.2, 0.9, 0.3) : vec3(0.2, 0.4, 1.0);
}

void main() {
    // A shader that statically writes gl_SampleMask must assign it on every invocation; opaque
    // fragments start fully covered and cutout handling narrows the mask below.
#ifndef LIT_OPAQUE
    gl_SampleMask[0] = ~0;
#endif

    // UVs arrive in the correct top-down origin already: the Sponza assets are
    // imported with AssetImportContext.FlipTextureV, which bakes the V-flip
    // into the vertex buffer at load. Nothing to do here.
    vec2 uv = vUv;
    // The bandwidth A/B collapses UVs to one cacheable texel while retaining texture instructions.
    uv = mix(uv, vec2(0.5), frame.uAbFlags.x);

    // --- Albedo + alpha test --------------------------------------------
    vec4 sampled = texture(uAlbedo, uv);
    vec4 albedo4 = sampled * mat.uBaseColorFactor;
    float alphaCutoff = mat.uMaterialParams.x;
    // Alpha-to-coverage edge for cutout materials (alphaCutoff > 0): sharpen the
    // sampled alpha to ~1px around the cutoff via its screen-space derivative,
    // so the lit pipeline's alpha-to-coverage turns it into a smooth MSAA leaf
    // silhouette instead of a hard binary edge. Fully-outside texels discard.
    // Opaque (alphaCutoff == 0) keeps coverage 1 → alpha-to-coverage is a no-op.
    float coverage = 1.0;
    // The layer hash reads screen derivatives (blix_layerHash): taken before any discard, in uniform flow.
#ifndef LIT_OPAQUE
    float layerHash = alphaCutoff > 0.0 ? blix_layerHash(vWorldPos) : 0.0;
    if (alphaCutoff > 0.0) {
        coverage = clamp((albedo4.a - alphaCutoff) / max(fwidth(albedo4.a), 1e-5) + 0.5, 0.0, 1.0);
        // The depth pre-pass uses the same discard and coverage decision; only sample ownership is
        // decorrelated after the silhouette is established.
        if (coverage <= 0.0) discard;

        // At one sample there is no mask over which to distribute coverage, so both depth and lit
        // passes use the same hashed binary keep test.
        if (mat.uMaterialParams2.w > 0.0 && mat.uMaterialParams2.w < 1.5) {
            if (!blix_hashedAlphaKeeps(coverage, layerHash)) discard;
            coverage = 1.0;
        }

        coverage = mix(coverage, 1.0, frame.uForceOpaqueCutout);

        // Assign a decorrelated sample mask with the same expected coverage. The depth pre-pass uses
        // the identical world-position hash so unowned samples contain neither leaf depth nor colour.
        {
            float h = layerHash;
            gl_SampleMask[0] = blix_coverageMask(coverage, int(mat.uMaterialParams2.w), h);
            coverage = 1.0;
        }
    }
#endif
    vec3 albedo = albedo4.rgb;

    // --- Normal map (real per-vertex TBN) -------------------------------
    // Geometric normal, flipped on back faces so two-sided geometry (cypress
    // leaf cards, curtains) lights from the inside. Single-sided geometry is
    // back-face culled, so the flip is a no-op there.
    vec3 N = normalize(vNormalWorld);
    vec3 tangentWorld = vTangentWorld;
    float tangentSign = vTangentSign;
    // --lightmap: LOD 0's normal and tangent through the atlas (lightmap_frame.glsl), so a coarse LOD lights and
    // normal-maps as the full surface does. The pre-pass wrote the same normal, which the incident field read.
    {
        vec3 lodZeroN;
        vec4 lodZeroT;
        // Judged against the drawn face, and the face where none agrees (lodFrameReference), as the pre-pass does.
        if (vLod > 0u) {
            vec3 reference = lodFrameReference(vWorldPos, N);
            if (lightmapShadingFrame(vLightmapTexel, reference, lodZeroN, lodZeroT)) { N = lodZeroN; tangentWorld = lodZeroT.xyz; tangentSign = lodZeroT.w; }
            else if (dot(vLightmapTexel, vLightmapTexel) > 1e-6) N = reference;
        }
    }
    if (!gl_FrontFacing) N = -N;
    // Before the normal map: the sheets' glow (rtSheetGlow) is light from across a curtain, not along one line, and
    // the weave's normal map under the sun's line printed a cross-hatch into it (2026-10-16).
    vec3 Nsmooth = N;
    // Build TBN from the authored glTF tangent. Gram-Schmidt removes interpolation drift and
    // TANGENT.w supplies bitangent handedness, including mirrored UVs.
    vec3 T = normalize(tangentWorld - N * dot(N, tangentWorld));
    vec3 B = cross(N, T) * tangentSign;
    // Reconstruct Z from XY. Cooked normals are BC5 (2-channel RG, blue
    // dropped), so the sampled .z is meaningless — derive it from the
    // unit-length constraint. This is also correct for RGBA8 normal maps
    // (their stored Z ≈ sqrt(1 - x² - y²)), so it works for both paths.
    // The authored per-material normal scale is the single strength control.
    float normalScale = mat.uMaterialParams.y;
    vec3 vizGeometricN = N;
    vec2 nxy = (texture(uNormalMap, uv).xy * 2.0 - 1.0) * normalScale * (1.0 - frame.uAbFlags.w);
    vec3 vizTangentN = vec3(nxy, sqrt(max(1.0 - dot(nxy, nxy), 0.0)));
    float nz = sqrt(max(0.0, 1.0 - dot(nxy, nxy)));
    // Default normal map is flat (0,0,1), so untextured materials keep N.
    N = normalize(mat3(T, B, N) * vec3(nxy, nz));

    // --- PBR scalars (factor × texture) ---------------------------------
    // MR.G = roughness, MR.B = metallic. AO comes from its own sampler.
    vec3 mrSample = texture(uMetallicRoughness, uv).rgb;
    float ao        = texture(uOcclusion, uv).r;
    float roughness = clamp(mat.uMaterialParams.z * mrSample.g, 0.04, 1.0);
    float metallic  = clamp(mat.uMaterialParams.w * mrSample.b, 0.0, 1.0);

    // Material metalness is consumed as authored. Asset conformance belongs in source data or the
    // cook, not as a per-pixel threshold in the renderer.

    vec3 F0 = mix(vec3(0.04), albedo, metallic);

    vec3 V = normalize(frame.uCameraPos - vWorldPos);
    float NdotV = max(dot(N, V), 0.0);
    vec3 R = reflect(-V, N);

    // --- Transmissive glass (KHR_materials_transmission) ----------------
    // Shade as Fresnel glass: the reflected fraction (dielectric Fresnel,
    // F0 = 0.04, ramping to 1 at grazing) becomes the blend opacity, so the
    // environment reflection composites over the scene behind:
    //     result = envReflection * F + background * (1 - F)
    // The src colour is the un-weighted environment reflection; the blend
    // multiplies it by alpha = F, giving the Fresnel split. Transmission opens
    // the head-on view to the scene behind instead of reading near-black.
    // (No refraction/absorption tint yet — that's the KHR transmission pass.)
    // Not in the opaque build (LIT_OPAQUE): a transmissive material is a blend one (PickPipeline), drawn by lit.frag's.
    // Its clipmap lookup was registers every opaque pixel paid for (2026-10-24).
#ifndef LIT_OPAQUE
    float transmission = mat.uMaterialParams2.x;
    if (transmission > 0.0) {
        float lod = roughness * (frame.uEnvMipCount - 1.0);
        // Occluded like every other indirect term, along R rather than N: a reflection gathers from where it
        // points, and a pane deep inside a room points at a wall. Panes are absent from the pre-pass, so the
        // incident field (the surface behind) cannot say; the clipmap's sky visibility at the pane, facing R, can.
        // Unoccluded only before the clipmap's first solve, or past its coarsest level.
        float glassSky = 1.0;
        if (frame.uIncident.z > 0.5 && frame.uIncident.x > 0.5) {
            BlixClipmap c;
            c.dims = ivec3(frame.uClipDims.xyz);
            c.baseSpacing = frame.uClipDims.w;
            c.blendProbes = frame.uClipOrigin0.w;
            c.origin[0] = ivec3(frame.uClipOrigin0.xyz);
            c.origin[1] = ivec3(frame.uClipOrigin1.xyz);
            c.origin[2] = ivec3(frame.uClipOrigin2.xyz);
            c.origin[3] = ivec3(frame.uClipOrigin3.xyz);
            bool found;
            vec4 field = blix_clipmapSample(c, vWorldPos, R, found);
            if (found) glassSky = field.a;
        }
        vec3 envRefl = textureLod(samplerCube(uPrefilteredEnv, uLinearClamp), R, lod).rgb * glassSky;
        float fresnel = 0.04 + 0.96 * pow(clamp(1.0 - NdotV, 0.0, 1.0), 5.0);
        // No opacity floor: this branch models Fresnel reflection over the background, without
        // refraction or absorption. Clean head-on glass is therefore nearly transparent.
        float glassAlpha = mix(albedo4.a, fresnel, transmission);
        // Handle diagnostics before the early return so transmissive surfaces remain inspectable.
        if (frame.uVizChannel > 0.5) {
            float vis = glassSky;
            vec3 c = frame.uVizChannel < 1.5 ? vizGeometricN * 0.5 + 0.5 :
                     frame.uVizChannel < 2.5 ? N * 0.5 + 0.5 :
                     frame.uVizChannel < 7.5 ? vec3(vis) : vec3(vis);
            outColor = vec4(c, 1.0);
            return;
        }
        outColor = vec4(envRefl, glassAlpha);
        return;
    }
#endif

    // --- Direct sun: Cook-Torrance (diffuse + analytic specular) --------
    vec3 L = -normalize(frame.uSunDirection);
    vec3 H = normalize(V + L);
    float NdotL = max(dot(N, L), 0.0);
    // The triangle's own plane (the world position's screen derivatives, turned to the shading side), for the terminator.
    vec3 Ngeo = cross(dFdx(vWorldPos), dFdy(vWorldPos));
    Ngeo = dot(Ngeo, Ngeo) > 1e-20 ? normalize(Ngeo) : Nsmooth;
    if (dot(Ngeo, Nsmooth) < 0.0) Ngeo = -Ngeo;
    // Against the smooth (vertex) normal, not the flat triangle's: Ngeo is constant over a facet, and against it the term
    // was a per-facet constant -- 0 on every facet turned even slightly from the sun -- whole triangles black across
    // sunlit cloth, a staircase along each fold (Crytek Sponza's curtains, 2026-10-27). The term softens what the normal
    // map adds over the smooth surface; the coarse facets' own self-shadowing is the sun ray's (rt_shadow_rays.comp, the
    // terminator skip).
    float terminatorFront = blix_terminator(N, Nsmooth, L);
    float terminatorBack = blix_terminator(-N, -Nsmooth, L);
    float NdotH = max(dot(N, H), 0.0);
    float VdotH = max(dot(V, H), 0.0);
    // The shared shadow path chooses the cascade before applying its world-space normal offset and
    // texel-space filter radius, keeping the two units and cascade footprints distinct.
    int shadowCascade = -1;
    float sunShadow = 1.0;
    vec3 sheetGlow = vec3(0.0);   // through sunlit curtains (rtSheetGlow), on the diffuse lobe beside sunShadow
    // Pay for shadow filtering only when front-side reflection or back-side diffuse transmission
    // can consume it. Fragments needing neither leave cascade index -1, which diagnostics render as
    // outside the cascades because no lookup was requested.
    float backNdotL = max(dot(-N, L), 0.0);
    float shadowNeed = max(NdotL, mat.uMaterialParams2.z > 0.0 ? backNdotL : 0.0);
    if (frame.uShadowStrength > 0.0 && shadowNeed > 0.0 && rtShadowHeader.z != 0u) {
        sunShadow = rtSunVisibility(gl_FragCoord.xy);
        sunShadow = mix(1.0, sunShadow, frame.uShadowStrength);
        // --sheet-light: the sheets' glow is in the incident field (the gather's hits on a sheet's far side, the patch
        // solve's twins: tools/gi-model/thin_sheet.py) -- the frame's own here as well counted it twice. Except on a surface
        // that lets light through, whose records leave it out (texel_nodes_shade.glsl, ownSheet): the sun through the
        // sheets beyond it is the frame's, per pixel -- a thin shell's far wall's sun sharp, not its texels' blur (the
        // teacup's smear, 2026-10-22).
        sheetGlow = frame.uIncidentGradient.z > 0.5 && !(abs(frame.uIncidentGradient.w - 2.0) < 0.5 && mat.uMaterialParams2.z > 0.0)
            ? vec3(0.0) : rtSheetGlow(gl_FragCoord.xy) * frame.uShadowStrength;
    } else if (LIT_RARE(frame.uShadowStrength > 0.0 && shadowNeed > 0.0)) {
        sunShadow = blix_sun_shadow_cascaded(
            uCascadeShadowMaps[0], uCascadeShadowMaps[1], uCascadeShadowMaps[2], uLinearClamp,
            frame.uCascadeViewProj[0], frame.uCascadeViewProj[1], frame.uCascadeViewProj[2],
            frame.uCascadeTexels.xyz,
            vWorldPos, N, shadowNeed, 2.0, gl_FragCoord.xy,
            shadowCascade);
        sunShadow = mix(1.0, sunShadow, frame.uShadowStrength);
    }

    // GGX microfacet highlight from the sun. Without this the sun produces
    // no glint on metal/polished stone, and metals read flat and chalky.
    vec3 sunSpecular = vec3(0.0);
    vec3 Fsun = vec3(0.0);
    if (frame.uAbFlags.y < 0.5) {
        float Dsun = distributionGGX(NdotH, roughness);
        float Gsun = geometrySmith(NdotV, NdotL, roughness);
        Fsun = fresnelSchlick(VdotH, F0);
        sunSpecular = (Dsun * Gsun * Fsun) / max(4.0 * NdotV * NdotL, 1e-3);
    }

    // Energy split: diffuse keeps only the non-reflected fraction (1 - F) and vanishes on metals
    // (1 - metallic); /PI normalizes the Lambert lobe. uSunIrradiance is the irradiance the probe
    // measured, so this line is the rendering equation for a directional light rather than a shape
    // scaled until it looked right.
    vec3 kDsun = (vec3(1.0) - Fsun) * (1.0 - metallic);

    // --- Cloth: the two things metallic-roughness cannot say -------------
    // Sheen is a retroreflective rim at grazing angles; diffuse transmission is light entering the
    // back of a single layer and scattering out the front. A curtain wants both, and the glTF spec
    // keeps them as separate extensions because they are separate physics — one you see, one you
    // see THROUGH.
    // The material is the answer. A live override used to sit here so the two cloth numbers could
    // be found by eye and written back into the patch; they have been found, they are in the
    // patch, and a control that can silently disagree with the cooked value is a second source of
    // truth for a question that is settled.
    vec3  sheenColor     = mat.uSheenColor.rgb;
    float sheenRoughness = clamp(mat.uMaterialParams2.y, 0.0, 1.0);
    // T: the factor times the factor texture's alpha (KHR_materials_diffuse_transmission; the teacup's runs 0.14-0.75 over
    // its rim and body, and the cup read as T 1 everywhere before, 2026-10-22).
    float diffTrans      = clamp(mat.uMaterialParams2.z * (mat.uMaterialParams2.z > 0.0 ? texture(uDiffuseTransmissionTex, uv).a : 1.0), 0.0, 1.0);
    // <b>The transmitted lobe's colour is the material's, NOT the base colour.</b> glTF makes
    // diffuseTransmissionColor the colour of the light that goes through, in place of baseColor --
    // it does not modulate it. This used to read `* albedo`, and the scene's patches author the
    // colour as each leaf's MEASURED MEAN ALBEDO, so the tint landed twice: transmitted light came
    // out at albedo squared, roughly 3.6x too dark in green, 5.8x in red and 25x in blue. Not a
    // dimming -- a hue shift, on the one term that exists to make backlit foliage read correctly.
    //
    // The multiply was almost certainly written to make an UNAUTHORED material fall back to
    // albedo-tinted transmission, which looks plausible. The spec's default is white, and the
    // place to say otherwise is the asset or the cook, not the renderer.
    //
    // Fetched only where the term exists. The branch is on a MATERIAL uniform, so it is coherent
    // across the whole draw and costs no divergence -- and without it every stone surface in the
    // scene pays a sampler fetch for a value it discards, which is the per-pixel tax `shadowNeed`
    // above already declines to pay for shadow filtering.
    vec3 dtColor = vec3(0.0);
    if (LIT_RARE(diffTrans > 0.0)) {
        vec3 dtTex = texture(uDiffuseTransmissionColorTex, uv).rgb;
        // --cloth-transmission: the texture is the cloth's albedo, and the light through is its hue at the factor's
        // strength (over its brightest channel), as the cooked bounce has it (SponzaLoop.SunNodesSolve.cs). The albedo
        // as it is passed 2-7% of the sun: the sheet's glow read as opaque cloth (2026-10-16).
        // Capped at what the cloth's lit face reflects ((1 - t) x its brightest channel): through at t in its full hue,
        // dark cloth (0.2-0.3) passed more than it sent back, and every fold's shaded side outshone its lit side -- a
        // bright rim along each edge where a fold turned from the sun (2026-10-16). Heavy cloth passes less than it
        // reflects. The cook and the shadow rays' sheets cap alike (SunNodesSolve, RtRowTransmit).
        if (mat.uDiffuseTransmissionColor.w > 0.5) {
            float m = max(max(dtTex.r, max(dtTex.g, dtTex.b)), 0.05);
            dtTex *= min(1.0, (1.0 - diffTrans) * m / max(diffTrans, 1e-4)) / m;
        }
        dtColor = mat.uDiffuseTransmissionColor.rgb * dtTex;
    }
    // A probe older than v4 has no Charlie cube, so sheen is off rather than approximated.
    bool  hasSheen       = LIT_RARE(dot(sheenColor, vec3(1.0)) > 0.0 && frame.uSheenMipCount > 0.0);

    // How much light the sheen layer takes, so the base layer beneath can be darkened by it.
    // Without this, sheen is added energy and cloth ends up brighter than the light falling on it.
    float sheenAlbedo  = hasSheen ? blix_sheenAlbedo(uSheenLut, uLinearClamp, NdotV, sheenRoughness) : 0.0;
    float sheenScale   = hasSheen ? blix_sheenScaling(sheenColor, sheenAlbedo) : 1.0;
    // And what leaves through the back did not leave through the front.
    float transScale   = blix_diffuseTransmissionScaling(diffTrans);

    vec3 sunSheen = vec3(0.0);
    if (hasSheen) {
        sunSheen = blix_sheenBrdf(sheenColor, sheenRoughness, NdotH, NdotL, NdotV)
                 * NdotL * frame.uSunIrradiance * sunShadow * terminatorFront;
    }

    // The sun through the cloth. Its own shadow term is deliberately the SAME sunShadow: a curtain
    // in shade transmits nothing, and a curtain in sun glows whichever side you are on.
    vec3 sunTransmission = vec3(0.0);
    if (LIT_RARE(diffTrans > 0.0)) {
        sunTransmission = blix_diffuseTransmission(
            N, L, frame.uSunIrradiance * sunShadow * terminatorBack, dtColor, diffTrans);
        // A curtain whose sun side is behind another sunlit curtain: that one's glow on its far side passes through it
        // as the sun would (its far-side ray crossed the other: rtSheetGlow), by the smooth normal as the diffuse
        // lobe takes it.
        sunTransmission += blix_diffuseTransmission(
            Nsmooth, L, frame.uSunIrradiance * sheetGlow, dtColor, diffTrans);
    }

    // <b>The layering order, which these two paths used to disagree about.</b> glTF puts the
    // sheen lobe ON TOP of the base material and scales what is underneath by sheenScale; diffuse
    // transmission is not a third peer but a modification of the base material's DIFFUSE lobe, so
    // it sits under the sheen layer with everything else. One shape, stated once:
    //
    //     (base + transmitted) * sheenScale + sheen
    //
    // This path had it inverted -- the base went unscaled and the sheen lobe was multiplied by its
    // own absorption term -- while the ambient path below had the base right and left transmission
    // out of the scaling. Neither was the spec, they disagreed with each other, and they disagreed
    // with sheen.glsl, which says in as many words that the factor is what the BASE layer is
    // multiplied by. On the curtains it read as a rim slightly too dim under direct sun and a
    // fabric slightly too bright, which is the size of mistake that survives being looked at.
    vec3 direct = ((kDsun * albedo / PI * transScale + sunSpecular) * NdotL * frame.uSunIrradiance * sunShadow * terminatorFront
                   + kDsun * albedo / PI * transScale * sheetGlow * max(dot(Nsmooth, L), 0.0) * frame.uSunIrradiance
                   + sunTransmission) * sheenScale
                + sunSheen;

    // --- IBL: split-sum diffuse + specular ------------------------------
    // Diffuse: irradiance cube × albedo, modulated by (1 - F) and (1 - metallic)
    // (metallics have no diffuse contribution).
    vec3 F = fresnelSchlickRoughness(NdotV, F0, roughness);
    vec3 kS = F;
    vec3 kD = (vec3(1.0) - kS) * (1.0 - metallic);

    // --- Ambient visibility ---------------------------------------------
    // GTAO provides screen-space visibility and a bent normal for opaque surfaces. Transmissive
    // materials are absent from the depth pre-pass, so sampling here would describe geometry behind
    // the pane. The predicate matches the scene's blend classification.
    vec4 ambientVis = texture(sampler2D(uAmbientVisibility, uLinearClamp), gl_FragCoord.xy / frame.uFog.xy);
    // <b>Named for what it actually tests.</b> This is "did this surface write depth in the
    // pre-pass", which is what makes a screen-space visibility lookup meaningful here -- and the
    // only thing excluded from that pre-pass is a KHR_materials_transmission pane. It was called
    // `opaqueSurface`, which is false of a curtain that scatters light through itself: a
    // diffuse-transmission surface IS in the pre-pass and DOES read GTAO, correctly. The behaviour
    // never changed; the name asserted the same near-miss between KHR_materials_transmission and
    // KHR_materials_diffuse_transmission that once put a flat translucency on every cell of the
    // light-transport bake.
    bool inDepthPrepass = mat.uMaterialParams2.x <= 0.0;
    float visibility = inDepthPrepass && frame.uTexelShape.y > 0.5 ? ambientVis.r : 1.0;

    // IBL uses the shading normal. (GTAO no longer writes a bent normal: nothing shaded with it.)
    vec3 cubeN   = N;

    // --- The incident field (the probe clipmap) ------------------------------------------------
    // .a is the sky visibility the clipmap's probes see, supplying building-scale enclosure beyond
    // GTAO's screen-space radius; .rgb is all the indirect diffuse light arriving, sky included.
    // Without the field (--no-incident, or before the clipmap's first solve) the sky is open:
    // visibility 1 and the irradiance cube's light.
    bool incidentOn = frame.uIncident.z > 0.5;
    bool incidentUsed = incidentOn && frame.uIncident.x > 0.5;
    vec4 incidentField = incidentOn
        ? texture(sampler2D(uIncidentField, uLinearClamp), gl_FragCoord.xy / frame.uFog.xy)
        : vec4(0.0);
    float skyVisibility = incidentUsed ? incidentField.a : 1.0;
    // The isolated traced path's own answer: the sky ray traced at this pixel (SponzaLoop.RtShadows).
    if (!incidentUsed && rtShadowHeader.z != 0u && rtShadowHeader.w != 0u) skyVisibility = rtSkyVisibility(gl_FragCoord.xy);

    // With the field the sky's diffuse light arrives through it, as the sky the surface actually sees;
    // adding the cube's irradiance times visibility here would count it twice.
    // Lambert: radiance out is albedo/PI times the irradiance in, as the direct sun has it. Every cache and the
    // irradiance cube hold irradiance E (PbrIblBaker integrates cos-weighted to E), so the /PI belongs here; shading
    // them as albedo * E made all indirect light PI times too bright against the sun (fixed 2026-10-21).
    // The irradiance cube read only where it is used: with the field on (every --rtgi frame) its light is the field's, the
    // read taken anyway ~0.09 ms of the IBL's 0.46 (Crytek Sponza 1080p, the in-boot A/B, 2026-10-28).
    vec3 diffuseIBL = vec3(0.0);
    if (!incidentOn) {
        vec3 irradiance = frame.uAbFlags.z > 0.5 ? vec3(0.2) : texture(samplerCube(uIrradiance, uLinearClamp), cubeN).rgb;
        diffuseIBL = irradiance * albedo / PI * skyVisibility;
    }

    // Specular: prefiltered env at LOD = roughness × (mipCount - 1), times
    // the BRDF LUT integration (split-sum approximation of the specular term).
    vec3 specularIBL = vec3(0.0);
    if (frame.uAbFlags.z < 0.5) {
        float lod = roughness * (frame.uEnvMipCount - 1.0);
        vec3 prefiltered = textureLod(samplerCube(uPrefilteredEnv, uLinearClamp), R, lod).rgb;
        // The split sum's scale and bias by Karis' analytic fit (Unreal's mobile EnvBRDFApprox), not the LUT's read: the read
        // ~0.26 ms a frame (Crytek Sponza 1080p, the in-boot A/B, 2026-10-28). frame.uTexelShape.w 1 (--ibl-brdf-lut): the LUT.
        vec2 envBrdf;
        if (frame.uTexelShape.w > 0.5) envBrdf = texture(sampler2D(uBrdfLut, uLinearClamp), vec2(NdotV, roughness)).rg;
        else {
            vec4 envR = roughness * vec4(-1.0, -0.0275, -0.572, 0.022) + vec4(1.0, 0.0425, 1.04, -0.04);
            float envA = min(envR.x * envR.x, exp2(-9.28 * NdotV)) * envR.x + envR.y;
            envBrdf = vec2(-1.04, 1.04) * envA + envR.zw;
        }
        // The reflected sky is the same sky. Not occluding it leaves a courtyard floor with a
        // mirror of an open horizon it cannot see.
        specularIBL = prefiltered * (F * envBrdf.x + envBrdf.y) * skyVisibility;
    }

    // AO attenuates indirect light only. Sun visibility is not an ambient-occlusion signal.
    // Specular gets its own occlusion, derived rather than dialled: a rough surface gathers over a
    // wide cone and is occluded nearly as much as the diffuse lobe, while a mirror gathers along
    // one ray that the visibility average says little about. Lagarde's approximation is that
    // relationship written down, and it takes roughness and visibility as its only inputs.
    float specularVisibility = clamp(
        pow(max(NdotV + visibility, 0.0), exp2(-16.0 * roughness - 1.0)) - 1.0 + visibility,
        0.0, 1.0);
    // The field's light is added to the sum, reflected with the same albedo response as diffuse IBL.
    // It is incident light arriving from elsewhere, not another visibility factor.
    vec3 bounce = vec3(0.0);
    vec3 vizBounceRaw = vec3(0.0);
    if (incidentUsed && frame.uNoBounceTerm < 0.5) {
        vec3 incident = incidentField.rgb;
        // The field was evaluated at the geometric normal (the pre-pass carries no normal map). Carry it to
        // the normal-mapped normal to first order: all indirect light arrives here, and without this a normal
        // map's folds and relief go flat in it (15% of Sponza's pixels moved when the inline path was
        // evaluated at the geometric normal instead).
        vec3 gradient = texture(sampler2D(uIncidentGradient, uLinearClamp), gl_FragCoord.xy / frame.uFog.xy).xyz;
        float lum = dot(incident, vec3(0.2126, 0.7152, 0.0722));
        if (lum > 1e-6) {
            // The normal map read blurred (uIncidentGradient.x mips down), through the same frame: indirect
            // diffuse follows a surface's folds and relief, and the weave below a pixel that TAA's jitter
            // samples differently each frame stays with the direct light.
            vec2 nxyLow = (texture(uNormalMap, uv, frame.uIncidentGradient.x).xy * 2.0 - 1.0)
                        * normalScale * (1.0 - frame.uAbFlags.w);
            vec3 nLow = normalize(mat3(T, B, vizGeometricN) * vec3(nxyLow, sqrt(max(0.0, 1.0 - dot(nxyLow, nxyLow)))));
            float range = frame.uIncidentGradient.y;
            vec3 dn = nLow - vizGeometricN;
            vec3 s0, s1, s2;
            if (lightmapShape(vLightmapTexel, vizGeometricN, s0, s1, s2))
                incident *= clamp(vec3(1.0) + vec3(dot(s0, dn), dot(s1, dn), dot(s2, dn)), vec3(1.0 - range), vec3(1.0 + range));
            else incident *= clamp(1.0 + dot(gradient, dn) / lum, 1.0 - range, 1.0 + range);
        }
        vizBounceRaw = incident;
        // A field that holds occlusion (uIncident.y: the texels' rays found it) takes neither GTAO nor the material's
        // baked occlusion texture: both are stand-ins for what those rays traced, and on top of them the teacup's
        // occlusion map (0.48 mean) left its wall -16% against the path trace, its shade -59%; without, -1% and -4%
        // (2026-10-22). uIncident.y 2: --material-ao-on-gi, the map as before.
        bool fieldOccludes = frame.uIncident.y > 0.5;
        float giAo = fieldOccludes && frame.uIncident.y < 1.5 ? 1.0 : ao;
        bounce = incident * albedo / PI * (1.0 - metallic) * giAo * (fieldOccludes ? 1.0 : visibility);
    }

    // Sheen's own prefiltered environment, at the sheen roughness rather than the base one, times
    // the same directional albedo that pays for it. Occluded like every other indirect term.
    vec3 sheenIBL = vec3(0.0);
    if (hasSheen && frame.uAbFlags.z < 0.5) {
        float sheenLod = sheenRoughness * max(frame.uSheenMipCount - 1.0, 0.0);
        vec3 sheenEnv = textureLod(samplerCube(uSheenEnv, uLinearClamp), R, sheenLod).rgb;
        sheenIBL = sheenEnv * sheenColor * sheenAlbedo * skyVisibility * visibility * ao;
    }

    // The ambient half of transmission: irradiance gathered along -N, which is the sky on the side
    // the surface is not facing. A curtain with a bright courtyard behind it glows without any
    // direct sun on it at all, and that is most of what makes cloth read as thin.
    vec3 transmittedIBL = vec3(0.0);
    vec3 backGi;
    if (LIT_RARE(diffTrans > 0.0) && frame.uIncidentGradient.w > 0.5 && incidentUsed && lightmapBackIrradiance(vLightmapTexel, N, backGi)) {
        // The GI's far side: the pixel's atlas texels' back records -- their own gathers, the room behind a curtain, the
        // light inside a closed thin shell's walls -- not the sky cube along -N.
        // Traced as the front's records are: no occlusion stand-in on it (the bounce's rule above), and never the front's
        // screen-space visibility, which says nothing of the far side.
        transmittedIBL = blix_diffuseTransmissionAmbient(backGi, dtColor, diffTrans) / PI * (frame.uIncident.y > 1.5 ? ao * visibility : 1.0);
    } else if (LIT_RARE(diffTrans > 0.0)) {
        // The back side's sky visibility. The incident field answers only at the front's geometric
        // normal, so its visibility stands in for -N's (the clipmap resolves enclosure at room scale,
        // where the two sides of a sheet share it); without the field the sky is open.
        float backVis = incidentUsed ? incidentField.a : 1.0;
        vec3 backIrradiance = texture(samplerCube(uIrradiance, uLinearClamp), -cubeN).rgb * backVis;
        // The helper returns irradiance times the transmitted fraction; Lambert about -N divides by PI.
        transmittedIBL = blix_diffuseTransmissionAmbient(
            backIrradiance, dtColor, diffTrans) / PI * ao * visibility;
    }

    // Same shape as `direct` above, deliberately: (base + transmitted) * sheenScale + sheen.
    vec3 ambient = ((kD * diffuseIBL * transScale * visibility + specularIBL * specularVisibility) * ao
                    + bounce * transScale
                    + transmittedIBL) * sheenScale
                 + sheenIBL;

    // --- Emissive ------------------------------------------------------
    vec3 emissive = texture(uEmissive, uv).rgb * mat.uEmissiveFactor.rgb * mat.uEmissiveFactor.a;

    vec3 color = direct + ambient + emissive;
    // KHR_materials_unlit: the base colour, unshaded -- the spec's whole model (DiffuseTransmissionTest's labels and its
    // swatches read black before, 2026-10-22).
    bool unlit = mat.uSheenColor.w > 0.5;
    if (unlit) color = albedo;

    // Debug: tint by which cascade shadowed this fragment (red/green/blue,
    // near→far). Helps confirm split placement + texel-snap stability.
    if (LIT_RARE(frame.uVizChannel > 0.5)) {
        vec3 c =
            frame.uVizChannel < 1.5 ? vizGeometricN * 0.5 + 0.5 :
            frame.uVizChannel < 2.5 ? N * 0.5 + 0.5 :
            frame.uVizChannel < 3.5 ? vizTangentN * 0.5 + 0.5 :
            frame.uVizChannel < 4.5 ? (gl_FrontFacing ? vec3(0.1, 0.8, 0.2) : vec3(0.9, 0.15, 0.1)) :
            frame.uVizChannel < 5.5 ? T * 0.5 + 0.5 :
            frame.uVizChannel < 6.5 ? B * 0.5 + 0.5 :
            frame.uVizChannel < 7.5 ? vec3(skyVisibility) :
            // 8 and 9 are unused (they read the retired baked volume); black.
            frame.uVizChannel < 9.5 ? vec3(0.0) :
            // 10 is raw incident bounce; 11 includes surface response and occlusion; 12 is direct
            // sun for scale. Keeping these stages separate distinguishes weak transport from later
            // attenuation.
            frame.uVizChannel < 10.5 ? vizBounceRaw :
            frame.uVizChannel < 11.5 ? bounce :
            frame.uVizChannel < 12.5 ? direct :
            // 13-15 expose GTAO, material AO, and the complete ambient-occlusion product.
            frame.uVizChannel < 13.5 ? vec3(visibility) :
            frame.uVizChannel < 14.5 ? vec3(ao) :
            frame.uVizChannel < 15.5 ? vec3(skyVisibility * visibility * ao) :
            // 16 is unused (the retired volume's probe-blend confidence); black.
            frame.uVizChannel < 16.5 ? vec3(0.0) :
            // 17-20 expose the ambient sum before addition: diffuse sky, specular sky, thin-sheet
            // transmission, and bounce.
            frame.uVizChannel < 17.5 ? kD * diffuseIBL * transScale * visibility * ao :
            frame.uVizChannel < 18.5 ? specularIBL * specularVisibility * ao :
            frame.uVizChannel < 19.5 ? transmittedIBL :
            frame.uVizChannel < 20.5 ? bounce * transScale :
            // 21: the material's albedo as shaded (texture, factor and vertex colour), which multiplies every light above --
            // a fault in it (a chart's UVs, a broken mip) shows in the image and in no lighting channel.
            frame.uVizChannel > 20.5 && frame.uVizChannel < 21.5 ? albedo :
            // 22 shows the pre-pass normal used by the half-resolution incident field. Compare it
            // with geometric channel 1 and shading channel 2 to localize reconstruction faults.
            frame.uVizChannel < 22.5 && frame.uVizChannel > 21.5
                ? texture(sampler2D(uPrepassNormalViz, uLinearClamp), gl_FragCoord.xy / frame.uFog.xy).xyz * 0.5 + 0.5 :
            // 23-24 inspect the clipmap behind the incident light: 23 how many solves its probes have had (red 1,
            // orange 2-3, yellow 4-7, green 8-31, blue 32+; black where none answers), 24 which level answers
            // (white 0 at 0.5 m, green 1, blue 2, magenta 3; dimmed while the clipmap has not solved).
            frame.uVizChannel > 22.5 && frame.uVizChannel < 24.5 ? vizClipmap(vWorldPos, vizGeometricN, frame.uVizChannel < 23.5) :
            frame.uVizChannel > 24.5 && frame.uVizChannel < 25.5 ? vizLightmap(vLightmapTexel) :
            // 26: the diffuse response as the reference's camera paths model it (--ref-view: Lambert's base lobe, 1 - T,
            // and the sheet's transmission, T c; no specular, no sheen, no Fresnel weight) -- the direct sun on either side
            // and the indirect light, front and back: what the material and the light make of the pixel, judged against
            // the path trace pixel for pixel.
            frame.uVizChannel > 25.5 && frame.uVizChannel < 26.5 && unlit ? albedo :
            frame.uVizChannel > 25.5 && frame.uVizChannel < 26.5
                ? albedo / PI * transScale * (NdotL * frame.uSunIrradiance * sunShadow * terminatorFront
                      + sheetGlow * max(dot(Nsmooth, L), 0.0) * frame.uSunIrradiance)
                  + sunTransmission + bounce * transScale + diffuseIBL * transScale * visibility * ao + transmittedIBL :
            // 21 is unused (the retired volume's leak metric); black.
                                       vec3(0.0);
        // Straight out, no exposure and no tonemap — these are directions and flags, and a film
        // curve on a direction is a way to misread it.
        // Diagnostics force full coverage so dropped foliage samples cannot reveal the skybox and
        // contaminate the quantity being inspected.
        outColor = vec4(c, 1.0);
        return;
    }

    if (LIT_RARE(frame.uVisualizeAmbient > 0.5)) {
        color = vec3(visibility);
    }

    if (LIT_RARE(frame.uVisualizeCascades > 0.5)) {
        color = mix(color, blix_cascade_tint(shadowCascade) * (0.5 + 0.5 * NdotL * sunShadow), 0.4);
    }

    // --- Froxel fog composite -------------------------------------------
    // Sample the pre-integrated scattering grid at this fragment's screen UV
    // and radial distance, then apply: lit*transmittance + in-scatter. The
    // grid is filled by the froxel compute pass earlier this frame.
    if (LIT_RARE(frame.uFog.w > 0.5)) {
        vec2 fuv = gl_FragCoord.xy / frame.uFog.xy;
        float dist = length(vWorldPos - frame.uCameraPos);
        // The grid's slices are not uniform in distance, so neither is this lookup — the curve is
        // shared with the compute pass rather than restated (see froxel.glsl).
        float w = blix_froxelSliceCoord(dist, frame.uFog.z);
        vec4 fog = texture(sampler3D(uFroxelGrid, uLinearClamp), vec3(fuv, w));
        color = color * fog.a + fog.rgb;
    }

    // Output coverage (not raw alpha) so alpha-to-coverage gets the sharpened
    // cutout edge; opaque keeps coverage 1.0 → fully covered.
    outColor = vec4(color, coverage);
}
