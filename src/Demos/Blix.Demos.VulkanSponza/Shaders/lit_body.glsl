// The lit pass's fragment shader, built by lit.frag (cutout materials) and lit_opaque.frag (LIT_OPAQUE).

// ── What this pass costs, measured ───────────────────────────────────────────
// Each term ablated in-process on a moving camera through the foliage (--ab <term>), ratios first
// because the three runs sat at 28.8, 32.6 and 49.7 ms medians and the ratio is the part that
// reproduces. Measured before the traced sun shadows and RTGI's incident field replaced the
// cascades, the baked volumes and screen-space ambient occlusion, so the terms they replaced are
// not listed; remeasure (--ab, BLIX_PROBE) before leaning on the numbers:
//
//     material texture bandwidth  1.150x   5.42 ms
//     normal mapping              1.108x   3.97 ms
//     image-based lighting        1.084x   3.77 ms
//     the GGX specular lobe       1.002x   0.04 ms
//
// They do not sum to the pass: each is what removing that term saves with everything else present,
// so the table ranks levers rather than partitioning a budget.
//
// The attribution shows a memory-bound pass: material/normal-map traffic is much more expensive
// than the GGX arithmetic. Measurements must include the foliage; a wall-only view changes the
// ranking rather than merely scaling it.
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
// LIT_RARE(c): a branch most pixels never take -- sheen, diffuse transmission, the debug views. The lean build
// (lit_lean.frag) compiles them out: a pixel pays for the registers of code it does not run, and the opaque pass went
// 3.6 -> 3.1 ms at 2880 x 1620 without them (2026-10-24, with the branches since removed among them).
// SponzaLoop.Render.cs picks it only where none can be taken: a material with neither sheen nor diffuse transmission,
// a frame with no debug view.
#ifdef LIT_LEAN
#define LIT_RARE(c) false
#else
#define LIT_RARE(c) (c)
#endif

#include "sheen.glsl"
#include "iridescence.glsl"
#include "diffuse_eon.glsl"
#include "noise.glsl"
#include "coverage.glsl"

// Lit fragment shader — Cook-Torrance split-sum IBL on top of a Lambert N·L
// sun term, with the traced sun shadows (SponzaLoop.RtShadows), RTGI's incident
// light for everything indirect, and a Fresnel-glass branch.
//
//   set 0 binding 0 : per-frame UBO (viewProj, sun, camera, the incident field's switches: frame.glsl)
//   set 1           : per-pass textures, all SEPARATE images read through one sampler
//                     (uLinearClamp, binding 19) -- see the note above that binding. Among them:
//   set 1 binding 0 : textureCube uIrradiance      (the sky behind a thin sheet)
//   set 1 binding 1 : textureCube uPrefilteredEnv  (specular IBL; mip = roughness LOD)
//   set 1 binding 2 : texture2D   uBrdfLut         (split-sum BRDF integration, --ibl-brdf-lut)
//   set 1 binding 17: texture2D   uIncidentField   (RTGI's light and sky visibility)
//   set 1 binding 21: buffer      RtShadowMask     (the traced sun's visibility)
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

// The diffuse lobe, in one place: EON, or Lambert for the A/B (--diffuse lambert, --ab diffuse). A lobe for a light from a KNOWN direction (the
// sun), and an albedo for light arriving from everywhere (an irradiance cache, the sky): radiance out is E / PI * albedo, which EON makes depend on
// the view angle and Lambert does not.
vec3 blixDiffuseLobe(vec3 rho, float r, float NdotL, float NdotV, float LdotV)
{
    return frame.uDiffuseModel.x > 0.5 ? blix_eonBrdf(rho, r, NdotL, NdotV, LdotV) : rho / BLIX_PI;
}

vec3 blixDiffuseAlbedo(vec3 rho, float r, float NdotV)
{
    return frame.uDiffuseModel.x > 0.5 ? blix_eonAlbedo(rho, r, NdotV) : rho;
}

layout(set = 1, binding = 0) uniform textureCube uIrradiance;
layout(set = 1, binding = 1) uniform textureCube uPrefilteredEnv;
layout(set = 1, binding = 2) uniform texture2D   uBrdfLut;
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

// The incident-light field, RTGI's readout brought to full resolution (lighting_upsample.frag): rgb = all the indirect
// diffuse light arriving (sky included), a = sky visibility. Zero until RTGI's readout is ready.
layout(set = 1, binding = 17) uniform texture2D uIncidentField;
// How the incident light's luminance changes with the normal, at the geometric normal (rtgi_readout.frag): what lets
// light the readout evaluated at the geometric normal reach the normal-mapped one.
layout(set = 1, binding = 20) uniform texture2D uIncidentGradient;
// The pre-pass normal, bound here ONLY so viz channel 16 can show what the incident field reads.
// Nothing in the lit path samples it: lit.frag has its own, better normal.
layout(set = 1, binding = 18) uniform texture2D uPrepassNormalViz;

// <b>Every set-1 texture above is a separate image, read through this one sampler.</b> As combined
// samplers they were 19 of this stage's 25, against MoltenVK's 16 per stage; as separate images they
// count against sampled images (256) instead, and the stage holds 8 samplers: this and set 2's seven.
// One is enough because every one of those textures was created LinearClamp or LinearClampMipmap,
// which are the same VkSampler: GenerateMipmaps is a texture-creation flag, and every sampler leaves
// MaxLod unclamped so the texture's own mip count limits it. See docs/renderer.md, "Separate images
// and samplers".
//@sampler LinearClamp
layout(set = 1, binding = 19) uniform sampler uLinearClamp;

// The scene as a pane sees what is behind it: the lit pass's colour before the frosted and thick panes, and four halvings of it (full, 1/2, 1/4, 1/8, 1/16 of the frame),
// the blur a rough pane reads at (lit_glass_backdrop.frag; SponzaLoop.GlassBackdrop.cs). Bound to a placeholder everywhere else.
layout(set = 1, binding = 40) uniform texture2D uBehind0;
layout(set = 1, binding = 41) uniform texture2D uBehind1;
layout(set = 1, binding = 42) uniform texture2D uBehind2;
layout(set = 1, binding = 43) uniform texture2D uBehind3;
layout(set = 1, binding = 44) uniform texture2D uBehind4;

// The sun's visibility traced on Apple's ray tracing hardware (SponzaLoop.RtShadows): a header (width,
// height, 1 while live -- 2 once rt_shadow_filter.comp has filtered it --, 0), then one word per pixel: 1 where the sun
// is blocked. Live with no size: the trace is not ready, and the sun is off. A placeholder of zeros where there is no
// Metal tracer: nothing shadows the sun then.
layout(std430, set = 1, binding = 21) readonly buffer RtShadowMask { uvec4 rtShadowHeader; uint rtShadowWords[]; };

float rtSunVisibility(vec2 fragCoord) {
    // RenderSettings.HalfResShadows: the mask is half-res, and lighting_upsample.frag has brought it back to this
    // pixel along the depth and normals, fractional at a shadow's edge, into the gradient's w.
    if (frame.uIncident.y > 0.5) return texelFetch(sampler2D(uIncidentGradient, uLinearClamp), ivec2(fragCoord), 0).w;
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
    uvec2 px = frame.uIncident.y > 0.5 ? uvec2(fragCoord) / 2u : uvec2(fragCoord);
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
    vec4 uMaterialParams3;         // x the diffuse roughness override (--material diffuse-roughness), or -1: the roughness below decides;
                                   // y KHR_materials_clearcoat factor, z its roughness
    vec4 uMaterialParams4;         // x the ior (KHR_materials_ior), y the volume's thickness in metres (0: thin-walled), z KHR_materials_specular's factor
    vec4 uAttenuation;             // rgb KHR_materials_volume's attenuation coefficient per metre, -ln(colour) / distance
    vec4 uMaterialParams5;         // KHR_materials_iridescence: x the factor, y the film's ior, z its thickness in nm; w KHR_materials_anisotropy's strength
    vec4 uSpecularColor;           // rgb KHR_materials_specular's colour factor; w the anisotropy's rotation (radians, from the tangent)
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
// KHR_materials_volume's thickness texture (G, linear), multiplying the factor beside it: a glass pane's thickness where it varies over the surface.
layout(set = 2, binding = 8) uniform sampler2D uThicknessTex;

layout(location = 0) in vec3 vNormalWorld;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;
layout(location = 3) in vec3 vTangentWorld;   // world-space tangent (forwarded glTF TANGENT.xyz)
layout(location = 4) in float vTangentSign;   // glTF TANGENT.w handedness

layout(location = 0) out vec4 outColor;

#define PI 3.14159265359

// Roughness-aware Fresnel-Schlick that softens edges as surfaces roughen
// (otherwise rough metals over-glow at grazing angles where the split-sum
// approximation breaks down). Used for the wide IBL reflection cone.
vec3 fresnelSchlickRoughness(float cosTheta, vec3 F0, float roughness, vec3 F90) {
    return F0 + (max(vec3(1.0 - roughness) * F90, F0) - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
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
vec3 fresnelSchlick(float cosTheta, vec3 F0, vec3 F90) {
    return F0 + (F90 - F0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

// KHR_materials_anisotropy's GGX (lab/bsdf gltf_bsdf.py's aniso_d and aniso_lambda): alphas along the anisotropy direction and across it, D G2 / (4 NL NV) with the
// height-correlated Smith the python and the BSDF twin use. h, l and v are in the frame whose x axis is the anisotropy direction and z the normal.
float anisoSpecular(vec3 h, vec3 l, vec3 v, float ax, float ay) {
    float d = (h.x / ax) * (h.x / ax) + (h.y / ay) * (h.y / ay) + h.z * h.z;
    float D = 1.0 / (PI * ax * ay * d * d);
    float lamL = 0.5 * (sqrt(1.0 + (ax * ax * l.x * l.x + ay * ay * l.y * l.y) / max(l.z * l.z, 1e-12)) - 1.0);
    float lamV = 0.5 * (sqrt(1.0 + (ax * ax * v.x * v.x + ay * ay * v.y * v.y) / max(v.z * v.z, 1e-12)) - 1.0);
    return D / (1.0 + lamL + lamV) / max(4.0 * l.z * v.z, 1e-3);
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

    // KHR_materials_ior and KHR_materials_specular (the BSDF's fresnels): the dielectric's f0 is the ior's, tinted by the specular colour and scaled by the factor, and its f90 is
    // the factor; a metal's is the base colour and 1. At the defaults (ior 1.5, specular 1) it is the 0.04 and 1 this shader always had. The textures are not read.
    float dielectricIor = mat.uMaterialParams4.x;
    float dielectricF0Ior = (dielectricIor - 1.0) / (dielectricIor + 1.0);
    vec3 dielectricF0 = min(dielectricF0Ior * dielectricF0Ior * mat.uSpecularColor.rgb, vec3(1.0)) * mat.uMaterialParams4.z;
    vec3 F0 = mix(dielectricF0, albedo, metallic);
    vec3 F90 = mix(vec3(mat.uMaterialParams4.z), vec3(1.0), metallic);
    // KHR_materials_iridescence: a thin film's Fresnel (iridescence.glsl) in place of Schlick's, weighed by the factor, in the sun's lobe and the sky's. Factors only (the thickness
    // texture is not read: the maximum stands); a branch most pixels never take. The film is evaluated on the mixed F0, not mixed from a dielectric's and a metal's as the
    // BSDF reference does it: the two part only where metallic is fractional.
    float irid = LIT_RARE(true) ? mat.uMaterialParams5.x : 0.0;
    // KHR_materials_anisotropy: the specular lobe's roughness stretched along a direction in the surface (the authored tangent turned by the rotation): the sun's highlight
    // with the GGX the BSDF twin has (anisoSpecular), the sky's with the Khronos viewer's bent normal. Factors only (the anisotropy texture is not read).
    float anisotropy = LIT_RARE(true) ? mat.uMaterialParams5.w : 0.0;
    vec3 anisoDir = T, anisoCross = B;
    if (anisotropy > 0.0) {
        vec3 t0 = normalize(T - N * dot(N, T));
        float rot = mat.uSpecularColor.w;
        anisoDir = normalize(cos(rot) * t0 + sin(rot) * cross(N, t0));
        anisoCross = cross(N, anisoDir);
    }

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
#ifndef LIT_OPAQUE
    float transmission = mat.uMaterialParams2.x;
    if (transmission > 0.0) {
        float lod = roughness * (frame.uEnvMipCount - 1.0);
        // The reflection sees the open sky. Panes are absent from the pre-pass, so the incident field at their pixel is
        // the surface behind them, whose sky visibility says nothing of what R sees from the pane, and RTGI's cache is
        // not read here: a pane deep inside a room reflects the sky it cannot see (its occlusion along R is to come).
        float glassSky = 1.0;
        vec3 envRefl = textureLod(samplerCube(uPrefilteredEnv, uLinearClamp), R, lod).rgb * glassSky;
        // The pane's two lights, as the BSDF reference has them (gltf_bsdf.py, thin-walled): the Fresnel share of the environment reflects off it, and the rest of
        // what is behind goes through, times the base colour (KHR_materials_transmission) and, with a volume thickness, Beer-Lambert's attenuation along the
        // refracted chord (KHR_materials_volume; the bending itself needs the scene behind, which a forward pass does not have, so a solid shows what is behind it
        // unbent). A material with transmission under 1 keeps its remainder as the base's diffuse lit by the sky alone.
        float glassIor = mat.uMaterialParams4.x;
        float glassF0 = (glassIor - 1.0) / (glassIor + 1.0);
        glassF0 *= glassF0 * mat.uMaterialParams4.z;
        float fresnel = glassF0 + (1.0 - glassF0) * pow(clamp(1.0 - NdotV, 0.0, 1.0), 5.0);
        float cosThrough = sqrt(max(1.0 - (1.0 - NdotV * NdotV) / (glassIor * glassIor), 0.0));
        // KHR_materials_volume's thickness is in the mesh's coordinate space (the attenuation distance in the world's): times the placement's scale, which the vertex stage's
        // tangent (the model's linear part times the authored tangent) carries. The dragon's thickness 2.27 sits on a node scaled 0.25: 0.57 m, not 2.27.
        float glassThickness = mat.uMaterialParams4.y * texture(uThicknessTex, uv).g * length(vTangentWorld);
        vec3 glassAtten = exp(-mat.uAttenuation.rgb * glassThickness / max(cosThrough, 0.05));
        // Handle diagnostics before the early return so transmissive surfaces remain inspectable.
        if (frame.uVizChannel > 0.5) {
            float vis = glassSky;
            vec3 c = frame.uVizChannel < 1.5 ? vizGeometricN * 0.5 + 0.5 :
                     frame.uVizChannel < 2.5 ? N * 0.5 + 0.5 :
                     vec3(vis);
            outColor = vec4(c, 1.0);
            return;
        }
#if defined(LIT_GLASS_BACKDROP)
        // What is behind the pane: for a solid (a thickness) the view refracts in and is taken where it would leave, at the screen position of that exit point (the spec's
        // thickness model, a screen-space stand-in for the bending); blurred by the pane's own roughness (scaled by its ior as the BSDF's thin sheet is), along the
        // chain of halvings. Replaces the pixel: the pane's reflection and the base's remainder are added here, not by a second draw.
        vec2 screenUv = gl_FragCoord.xy / frame.uScreenSize;
        vec2 behindUv = screenUv;
        if (glassThickness > 0.0) {
            vec3 refracted = refract(-V, N, 1.0 / glassIor);
            vec4 exitClip = frame.uViewProjUnjittered * vec4(vWorldPos + normalize(refracted) * glassThickness, 1.0);
            if (exitClip.w > 1e-4) behindUv = clamp(exitClip.xy / exitClip.w * 0.5 + 0.5, vec2(0.001), vec2(0.999));
        }
        float transmissionRoughness = clamp(roughness * clamp(glassIor * 2.0 - 2.0, 0.0, 1.0), 0.0, 1.0);
        float behindLevel = transmissionRoughness * 4.0;
        vec3 behindLow = vec3(0.0), behindHigh = vec3(0.0);
        int levelLow = int(floor(behindLevel));
        {
            vec3 s0 = texture(sampler2D(uBehind0, uLinearClamp), behindUv).rgb, s1 = texture(sampler2D(uBehind1, uLinearClamp), behindUv).rgb,
                 s2 = texture(sampler2D(uBehind2, uLinearClamp), behindUv).rgb, s3 = texture(sampler2D(uBehind3, uLinearClamp), behindUv).rgb,
                 s4 = texture(sampler2D(uBehind4, uLinearClamp), behindUv).rgb;
            behindLow = levelLow == 0 ? s0 : levelLow == 1 ? s1 : levelLow == 2 ? s2 : levelLow == 3 ? s3 : s4;
            behindHigh = levelLow == 0 ? s1 : levelLow == 1 ? s2 : levelLow == 2 ? s3 : s4;
        }
        vec3 behind = mix(behindLow, behindHigh, behindLevel - float(levelLow));
        vec3 skyDiffuseB = texture(samplerCube(uIrradiance, uLinearClamp), N).rgb * glassSky * albedo / PI;
        outColor = vec4(behind * ((1.0 - fresnel) * transmission * albedo * glassAtten) + fresnel * envRefl + (1.0 - transmission) * (1.0 - fresnel) * skyDiffuseB, 1.0);
#elif defined(LIT_GLASS_FILTER)
        // The first draw: what is behind, filtered (blended Multiply: destination times this).
        outColor = vec4((1.0 - fresnel) * transmission * albedo * glassAtten, 1.0);
#elif defined(LIT_GLASS_REFLECT)
        // The second: what the pane adds (blended Additive): its reflection, and the base's remainder lit by the sky.
        vec3 skyDiffuse = texture(samplerCube(uIrradiance, uLinearClamp), N).rgb * glassSky * albedo / PI;
        outColor = vec4(fresnel * envRefl + (1.0 - transmission) * (1.0 - fresnel) * skyDiffuse, 1.0);
#else
        // No opacity floor: this branch models Fresnel reflection over the background, without
        // refraction or absorption. Clean head-on glass is therefore nearly transparent.
        float glassAlpha = mix(albedo4.a, fresnel, transmission);
        outColor = vec4(envRefl, glassAlpha);
#endif
        return;
    }
#endif

    // --- Direct sun: Cook-Torrance (diffuse + analytic specular) --------
    vec3 L = -normalize(frame.uSunDirection);
    // The diffuse lobe is EON, not Lambert: its roughness follows the material's (glTF has one roughness), and its albedo toward the eye is what the
    // indirect terms below multiply the irradiance by. At diffuse roughness 0 both are exactly Lambert's.
    float diffuseRoughness = mat.uMaterialParams3.x >= 0.0 ? clamp(mat.uMaterialParams3.x, 0.0, 1.0) : clamp(roughness * frame.uDiffuseModel.y, 0.0, 1.0);
    vec3 diffuseAlbedoEye = blixDiffuseAlbedo(albedo, diffuseRoughness, NdotV);
    vec3 diffuseSun = blixDiffuseLobe(albedo, diffuseRoughness, max(dot(N, L), 0.0), NdotV, dot(L, V));
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
    float sunShadow = 1.0;
    vec3 sheetGlow = vec3(0.0);   // through sunlit curtains (rtSheetGlow), on the diffuse lobe beside sunShadow
    // Read the traced mask only where front-side reflection or back-side diffuse transmission can consume it. With no
    // trace at all (no Metal tracer: the placeholder, header z 0) nothing shadows the sun.
    float backNdotL = max(dot(-N, L), 0.0);
    float shadowNeed = max(NdotL, mat.uMaterialParams2.z > 0.0 ? backNdotL : 0.0);
    if (frame.uShadowStrength > 0.0 && shadowNeed > 0.0 && rtShadowHeader.z != 0u) {
        sunShadow = rtSunVisibility(gl_FragCoord.xy);
        sunShadow = mix(1.0, sunShadow, frame.uShadowStrength);
        // The light sunlit sheets send through to this pixel. RTGI's rays do not carry light through thin cloth, so
        // this, the frame's own, is the only glow behind a curtain there is.
        sheetGlow = rtSheetGlow(gl_FragCoord.xy) * frame.uShadowStrength;
    }

    // GGX microfacet highlight from the sun. Without this the sun produces
    // no glint on metal/polished stone, and metals read flat and chalky.
    vec3 sunSpecular = vec3(0.0);
    vec3 Fsun = vec3(0.0);
    if (frame.uAbFlags.y < 0.5) {
        float Dsun = distributionGGX(NdotH, roughness);
        float Gsun = geometrySmith(NdotV, NdotL, roughness);
        Fsun = fresnelSchlick(VdotH, F0, F90);
        if (irid > 0.0) Fsun = mix(Fsun, blix_iridescenceFresnel(1.0, mat.uMaterialParams5.y, VdotH, mat.uMaterialParams5.z, F0), irid);
        sunSpecular = (Dsun * Gsun * Fsun) / max(4.0 * NdotV * NdotL, 1e-3);
        if (anisotropy > 0.0) {
            float aBase = roughness * roughness;
            sunSpecular = Fsun * anisoSpecular(vec3(dot(H, anisoDir), dot(H, anisoCross), NdotH), vec3(dot(L, anisoDir), dot(L, anisoCross), NdotL),
                                               vec3(dot(V, anisoDir), dot(V, anisoCross), NdotV), mix(aBase, 1.0, anisotropy * anisotropy), aBase);
        }
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
        // strength (over its brightest channel). The albedo
        // as it is passed 2-7% of the sun: the sheet's glow read as opaque cloth (2026-10-16).
        // Capped at what the cloth's lit face reflects ((1 - t) x its brightest channel): through at t in its full hue,
        // dark cloth (0.2-0.3) passed more than it sent back, and every fold's shaded side outshone its lit side -- a
        // bright rim along each edge where a fold turned from the sun (2026-10-16). Heavy cloth passes less than it
        // reflects. The shadow rays' sheets cap alike (RtRowTransmit).
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
    vec3 direct = ((kDsun * diffuseSun * transScale + sunSpecular) * NdotL * frame.uSunIrradiance * sunShadow * terminatorFront
                   + kDsun * diffuseSun * transScale * sheetGlow * max(dot(Nsmooth, L), 0.0) * frame.uSunIrradiance
                   + sunTransmission) * sheenScale
                + sunSheen;

    // --- IBL: split-sum diffuse + specular ------------------------------
    // Diffuse: irradiance cube × albedo, modulated by (1 - F) and (1 - metallic)
    // (metallics have no diffuse contribution).
    vec3 F = fresnelSchlickRoughness(NdotV, F0, roughness, F90);
    if (irid > 0.0) F = mix(F, blix_iridescenceFresnel(1.0, mat.uMaterialParams5.y, NdotV, mat.uMaterialParams5.z, F0), irid);
    vec3 kS = F;
    vec3 kD = (vec3(1.0) - kS) * (1.0 - metallic);

    // IBL uses the shading normal.
    vec3 cubeN   = N;

    // --- The incident field (RTGI's readout) -----------------------------------------------------
    // .a is the sky visibility RTGI's rays found; .rgb is all the indirect diffuse light arriving, sky
    // included -- the sky's diffuse light arrives through it, as the sky the surface actually sees, so
    // the irradiance cube is not read for it. Before RTGI's readout is ready the targets hold zero, so
    // the frame has no indirect light rather than a guess.
    vec4 incidentField = texture(sampler2D(uIncidentField, uLinearClamp), gl_FragCoord.xy / frame.uScreenSize);
    float skyVisibility = incidentField.a;

    // Specular: prefiltered env at LOD = roughness × (mipCount - 1), times
    // the BRDF LUT integration (split-sum approximation of the specular term).
    vec3 specularIBL = vec3(0.0);
    // Not where the pixel sees almost no sky (corridors, under the arcades): the term is scaled by that visibility, so
    // under 2% of it is left out, and the read and its maths with it -- 0.13 ms of the lit pass (Crytek Sponza 1080p,
    // the in-boot A/B twice, 2026-10-28).
    if (frame.uAbFlags.z < 0.5 && skyVisibility >= 0.02) {
        float lod = roughness * (frame.uEnvMipCount - 1.0);
        vec3 reflectionDir = R;
        if (anisotropy > 0.0) {
            // The Khronos viewer's: the normal bent toward the direction the lobe is stretched along, in proportion to how anisotropic the lobe is.
            vec3 anisoNormal = cross(cross(anisoDir, V), anisoDir);
            float bend = 1.0 - anisotropy * (1.0 - roughness);
            reflectionDir = reflect(-V, normalize(mix(anisoNormal, N, bend * bend * bend * bend)));
        }
        vec3 prefiltered = textureLod(samplerCube(uPrefilteredEnv, uLinearClamp), reflectionDir, lod).rgb;
        // The split sum's scale and bias by Karis' analytic fit (Unreal's mobile EnvBRDFApprox), not the LUT's read: the read
        // ~0.26 ms a frame (Crytek Sponza 1080p, the in-boot A/B, 2026-10-28). frame.uIblBrdfLut 1 (--ibl-brdf-lut): the LUT.
        vec2 envBrdf;
        if (frame.uIblBrdfLut > 0.5) envBrdf = texture(sampler2D(uBrdfLut, uLinearClamp), vec2(NdotV, roughness)).rg;
        else {
            vec4 envR = roughness * vec4(-1.0, -0.0275, -0.572, 0.022) + vec4(1.0, 0.0425, 1.04, -0.04);
            float envA = min(envR.x * envR.x, exp2(-9.28 * NdotV)) * envR.x + envR.y;
            envBrdf = vec2(-1.04, 1.04) * envA + envR.zw;
        }
        // The reflected sky is the same sky. Not occluding it leaves a courtyard floor with a
        // mirror of an open horizon it cannot see.
        specularIBL = prefiltered * (F * envBrdf.x + F90 * envBrdf.y) * skyVisibility;
    }

    // The field's light is added to the sum, reflected with the same albedo response as diffuse IBL.
    // It is incident light arriving from elsewhere, not another visibility factor.
    vec3 bounce = vec3(0.0);
    vec3 vizBounceRaw = vec3(0.0);
    if (frame.uNoBounceTerm < 0.5) {
        vec3 incident = incidentField.rgb;
        // The field was evaluated at the geometric normal (the pre-pass carries no normal map). Carry it to
        // the normal-mapped normal to first order: all indirect light arrives here, and without this a normal
        // map's folds and relief go flat in it (15% of Sponza's pixels moved when the inline path was
        // evaluated at the geometric normal instead).
        vec3 gradient = texture(sampler2D(uIncidentGradient, uLinearClamp), gl_FragCoord.xy / frame.uScreenSize).xyz;
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
            incident *= clamp(1.0 + dot(gradient, dn) / lum, 1.0 - range, 1.0 + range);
        }
        vizBounceRaw = incident;
        // A field that holds occlusion (uIncident.x: its rays found it) takes no material's baked occlusion
        // texture: that is a stand-in for what those rays traced, and on top of them the teacup's occlusion map
        // (0.48 mean) left its wall -16% against the path trace, its shade -59%; without, -1% and -4%
        // (2026-10-22). uIncident.x 2: --material-ao-on-gi, the map as before.
        bool fieldOccludes = frame.uIncident.x > 0.5;
        float giAo = fieldOccludes && frame.uIncident.x < 1.5 ? 1.0 : ao;
        // The diffuse share of it as the cube's diffuse has it (kD: (1 - F)(1 - metallic)): the specular sky below takes the
        // reflected share F of the same light, and with (1 - metallic) alone here that share was counted twice -- a sky
        // term on top of the sky, ~5-10% on rough stone where the sky is all the light (the shade), 2026-10-28.
        bounce = incident * diffuseAlbedoEye / PI * kD * giAo;
    }

    // Sheen's own prefiltered environment, at the sheen roughness rather than the base one, times
    // the same directional albedo that pays for it. Occluded like every other indirect term.
    vec3 sheenIBL = vec3(0.0);
    if (hasSheen && frame.uAbFlags.z < 0.5) {
        float sheenLod = sheenRoughness * max(frame.uSheenMipCount - 1.0, 0.0);
        vec3 sheenEnv = textureLod(samplerCube(uSheenEnv, uLinearClamp), R, sheenLod).rgb;
        sheenIBL = sheenEnv * sheenColor * sheenAlbedo * skyVisibility * ao;
    }

    // The ambient half of transmission: irradiance gathered along -N, which is the sky on the side
    // the surface is not facing. A curtain with a bright courtyard behind it glows without any
    // direct sun on it at all, and that is most of what makes cloth read as thin.
    vec3 transmittedIBL = vec3(0.0);
    if (LIT_RARE(diffTrans > 0.0)) {
        // The back side's sky visibility. The incident field answers only at the front's geometric
        // normal, so its visibility stands in for -N's (enclosure at room scale, where the two sides
        // of a sheet share it).
        float backVis = incidentField.a;
        vec3 backIrradiance = texture(samplerCube(uIrradiance, uLinearClamp), -cubeN).rgb * backVis;
        // The helper returns irradiance times the transmitted fraction; Lambert about -N divides by PI.
        transmittedIBL = blix_diffuseTransmissionAmbient(
            backIrradiance, dtColor, diffTrans) / PI * ao;
    }

    // Same shape as `direct` above, deliberately: (base + transmitted) * sheenScale + sheen.
    vec3 ambient = (specularIBL * ao
                    + bounce * transScale
                    + transmittedIBL) * sheenScale
                 + sheenIBL;

    // --- Emissive ------------------------------------------------------
    vec3 emissive = texture(uEmissive, uv).rgb * mat.uEmissiveFactor.rgb * mat.uEmissiveFactor.a;

    // --- Clear coat (KHR_materials_clearcoat) ----------------------------------------------------------------------------------------------
    // A smooth GGX layer of ior 1.5 over everything beneath it, as the BSDF reference has it (lab/bsdf gltf_bsdf.py): the base -- diffuse, specular, sheen, the
    // bounce, the emission -- is darkened by the coat's Fresnel reflectance toward the VIEW, and the coat adds its own highlight (the sun's, F at the half vector)
    // and its reflection of the sky (the prefiltered environment at the coat's roughness, the split-sum LUT with F0 0.04). Factors only: the coat's textures and its
    // normal map are not read. A branch most pixels never take: the lean build compiles it out.
    float coat = LIT_RARE(true) ? mat.uMaterialParams3.y : 0.0;
    vec3 coatLight = vec3(0.0);
    float coatScale = 1.0;
    if (coat > 0.0) {
        float coatRoughness = clamp(mat.uMaterialParams3.z, 0.04, 1.0);
        float coatFresnelView = 0.04 + 0.96 * pow(clamp(1.0 - NdotV, 0.0, 1.0), 5.0);
        coatScale = 1.0 - coat * coatFresnelView;
        float coatD = distributionGGX(NdotH, coatRoughness);
        float coatG = geometrySmith(NdotV, NdotL, coatRoughness);
        float coatF = 0.04 + 0.96 * pow(clamp(1.0 - VdotH, 0.0, 1.0), 5.0);
        coatLight = vec3(coat * coatD * coatG * coatF / max(4.0 * NdotV * NdotL, 1e-3)) * NdotL * frame.uSunIrradiance * sunShadow * terminatorFront;
        if (frame.uAbFlags.z < 0.5) {
            float coatLod = coatRoughness * (frame.uEnvMipCount - 1.0);
            vec3 coatEnv = textureLod(samplerCube(uPrefilteredEnv, uLinearClamp), R, coatLod).rgb;
            vec2 coatBrdf = texture(sampler2D(uBrdfLut, uLinearClamp), vec2(NdotV, coatRoughness)).rg;
            // Occluded as the base's specular sky is (skyVisibility and the material's occlusion; GTAO's specular occlusion is gone with GTAO).
            coatLight += coat * coatEnv * (0.04 * coatBrdf.x + coatBrdf.y) * skyVisibility * ao;
        }
    }

    vec3 color = (direct + ambient + emissive) * coatScale + coatLight;
    // KHR_materials_unlit: the base colour, unshaded -- the spec's whole model (DiffuseTransmissionTest's labels and its
    // swatches read black before, 2026-10-22).
    bool unlit = mat.uSheenColor.w > 0.5;
    if (unlit) color = albedo;

    // Debug views (--viz N, the overlay's Show): one shading input instead of the lit colour. The ids are
    // SponzaLoop's VizChannelNames, in this order.
    if (LIT_RARE(frame.uVizChannel > 0.5)) {
        vec3 c =
            frame.uVizChannel < 1.5 ? vizGeometricN * 0.5 + 0.5 :
            frame.uVizChannel < 2.5 ? N * 0.5 + 0.5 :
            frame.uVizChannel < 3.5 ? vizTangentN * 0.5 + 0.5 :
            frame.uVizChannel < 4.5 ? (gl_FrontFacing ? vec3(0.1, 0.8, 0.2) : vec3(0.9, 0.15, 0.1)) :
            frame.uVizChannel < 5.5 ? T * 0.5 + 0.5 :
            frame.uVizChannel < 6.5 ? B * 0.5 + 0.5 :
            frame.uVizChannel < 7.5 ? vec3(skyVisibility) :
            // 8 is raw incident bounce; 9 includes surface response and occlusion; 10 is direct
            // sun for scale. Keeping these stages separate distinguishes weak transport from later
            // attenuation.
            frame.uVizChannel < 8.5 ? vizBounceRaw :
            frame.uVizChannel < 9.5 ? bounce :
            frame.uVizChannel < 10.5 ? direct :
            // 11: the material's occlusion texture.
            frame.uVizChannel < 11.5 ? vec3(ao) :
            // 12-14 expose the ambient sum before addition: specular sky, thin-sheet transmission, and bounce.
            frame.uVizChannel < 12.5 ? specularIBL * ao :
            frame.uVizChannel < 13.5 ? transmittedIBL :
            frame.uVizChannel < 14.5 ? bounce * transScale :
            // 15: the material's albedo as shaded (texture, factor and vertex colour), which multiplies every light above --
            // a fault in it (a chart's UVs, a broken mip) shows in the image and in no lighting channel.
            frame.uVizChannel < 15.5 ? albedo :
            // 16 shows the pre-pass normal used by the half-resolution incident field. Compare it
            // with geometric channel 1 and shading channel 2 to localize reconstruction faults.
            texture(sampler2D(uPrepassNormalViz, uLinearClamp), gl_FragCoord.xy / frame.uScreenSize).xyz * 0.5 + 0.5;
        // Straight out, no exposure and no tonemap — these are directions and flags, and a film
        // curve on a direction is a way to misread it.
        // Diagnostics force full coverage so dropped foliage samples cannot reveal the skybox and
        // contaminate the quantity being inspected.
        outColor = vec4(c, 1.0);
        return;
    }

    // Output coverage (not raw alpha) so alpha-to-coverage gets the sharpened
    // cutout edge; opaque keeps coverage 1.0 → fully covered.
    outColor = vec4(color, coverage);
}
