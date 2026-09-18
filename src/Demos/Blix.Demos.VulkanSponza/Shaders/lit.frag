#version 450

// <b>The shadow lookup is the engine's, not this demo's.</b> shadow.glsl said the problem out loud
// before this change was made: "there are already three PCF implementations in this tree (here,
// Sponza's, VulkanLit's) and the way to stop there being a fourth is for the cascade layer not to
// need one." Sponza's was the second of the three, and it was the weakest — a square tap grid, a
// hand-tuned radius, and a depth bias scaled by two tuned constants where the shared one offsets
// along the normal by a length the cascade fit already computes.
// Tap count for the sun's percentage-closer filter, before shadow.glsl picks its default of 16.
//
// <b>FOUR, and sixteen was buying nothing.</b> The sun shadow measured 1.216x of this frame at
// sixteen taps and 1.031x at four — about eighteen per cent of the whole picture — and the two
// outputs differ on 0.00% of pixels above 2/255, with a maximum difference of 21/255 on one pixel
// in a hundred thousand.
//
// The reason is the disc, not the sampling. radiusTexels is 2 and a cascade-0 texel is around 8 mm
// of world, so the filter spans roughly 1.6 cm — a couple of screen pixels. Sixteen samples over
// two texels is oversampling by a large factor, and what comes out is very nearly a hard edge
// either way. Widening the disc does not rescue it: at 6 and 12 texels the image still changes on
// 0.01% of pixels, because this scene's shadow BOUNDARIES are a sliver of the frame. The shadow
// itself is doing real work — turning it off changes 24-50% of pixels by more than 16/255 — but as
// an in-or-out answer, which four taps give as well as sixteen.
//
// <b>This is a property of this sun, not a law.</b> The direction comes from the probe and sits
// high, about 46 degrees, so the atrium is mostly interior shade with sunlight on the upper walls.
// A low sun raking long shadows across the floor would put penumbra everywhere the eye goes, and
// then the taps would be worth their price again. The measurement is written down here so that
// changing the sun prompts re-checking the number rather than inheriting it.
#define BLIX_SHADOW_PCF_TAPS 4
#include "shadow.glsl"

// Lit fragment shader — Cook-Torrance split-sum IBL on top of a Lambert N·L
// sun term, with cascaded shadows, a Fresnel-glass branch, and froxel-fog
// composite.
//
//   set 0 binding 0 : per-frame UBO (viewProj, sun, IBL strength, camera, fog)
//   set 1 binding 0 : samplerCube uIrradiance      (diffuse IBL)
//   set 1 binding 1 : samplerCube uPrefilteredEnv  (specular IBL; mip = roughness LOD)
//   set 1 binding 2 : sampler2D   uBrdfLut         (split-sum BRDF integration)
//   set 1 binding 3 : sampler2D   uCascadeShadowMaps[3]
//   set 1 binding 4 : sampler3D   uFroxelGrid      (volumetric fog)
//   set 1 binding 5 : sampler2D   uAmbientVisibility (GTAO: bent normal + visibility)
//   set 2 binding 0 : per-material UBO (BaseColorFactor, EmissiveFactor,
//                                       MaterialParams = alphaCutoff/normalScale/
//                                       roughness/metallic, MaterialParams2 = transmission)
//   set 2 binding 1 : albedo  (sRGB)
//   set 2 binding 2 : normal  (linear; tangent-space)
//   set 2 binding 3 : emissive (sRGB)
//   set 2 binding 4 : metallic-roughness (linear; G=rough, B=metal)
//   set 2 binding 5 : occlusion (linear; R=AO)
//
// Roughness/metallic are sampled from the MR texture × per-material factors.

layout(set = 0, binding = 0) uniform Frame {
    mat4  uViewProjection;
    vec3  uSunDirection;
    float uSunPad;
    // <b>The sun's irradiance, MEASURED from the probe — not a knob.</b> It replaces
    // uSunIntensity, whose default was 9.42 because that is 3*PI, chosen by its own comment to
    // "match the old look". The probe now reports what the sun in the HDR actually delivers, in the
    // HDR's own units, and the bake removes that disc from the diffuse and specular integrals — so
    // the sun arrives exactly once and in the same units as the sky it came from.
    //
    // uIblIntensity is gone with it, and for the same reason. A scale between sun and sky only
    // needs tuning while the two are in different units; measured from one capture they are not.
    // The artistic knob for overall brightness is exposure, which already exists.
    vec3  uSunIrradiance;
    float uIblPad;
    vec3  uCameraPos;
    float uEnvMipCount;
    float uShadowStrength;         // 0 = sun shadows off, 1 = on
    vec3  _cascadePad;
    mat4  uCascadeViewProj[3];     // light view-proj per cascade
    // .xyz = one shadow texel in WORLD units per cascade. Derived from the cascade fit — the same
    // number it already used to texel-snap the ortho footprint — where this slot previously held a
    // per-cascade NDC depth bias that was that length converted and then tuned twice over.
    vec4  uCascadeTexels;
    vec4  uFog;                    // x=screenW, y=screenH, z=fogFar, w=enabled(0/1)
    // Live-tunable shader params. Un-packed from the former uShaderParams /
    // uShadowParams / uIblParams vec4s into named members so each carries its
    // own //@tune range+default and the diagnostics overlay can auto-bind and
    // label it (see docs/renderer.md "SPIR-V reflection" + the tune scanner).
    //@tune 0..1 = 0
    float uVisualizeCascades;
    // 1 = visibility as greyscale, 2 = bent normal as RGB. A term you cannot look at on its own is
    // a term you tune by staring at the final image, which is how the five deleted knobs happened.
    // Note it still goes through exposure + tonemap in the present pass, so read it for STRUCTURE
    // (where the corners darken, where the normals bend) rather than as calibrated values.
    //@tune 0..2 = 0
    float uVisualizeAmbient;
    // Measurement switches, one per --ab mode. Each removes one term from the fragment so a paired
    // interleaved run can price it:
    //   x  collapse every material UV to a constant, so the five material samples all hit one
    //      cached texel. The sample INSTRUCTIONS remain — this prices BANDWIDTH, not instruction
    //      count, which is the distinction the whole question turns on.
    //   y  skip the GGX/Smith/Fresnel specular lobe, leaving Lambert. Prices ALU.
    //   z  skip the three IBL lookups and the split-sum, using a flat ambient. Prices IBL whole.
    //   w  skip the normal map sample and the tangent-space transform.
    vec4  uAbFlags;
} frame;

layout(set = 1, binding = 0) uniform samplerCube uIrradiance;
layout(set = 1, binding = 1) uniform samplerCube uPrefilteredEnv;
layout(set = 1, binding = 2) uniform sampler2D   uBrdfLut;
// Cascaded sun shadow maps (one depth target per cascade, near→far). Sampled
// with manual depth comparison + 3×3 PCF; cascade chosen by the fragment's
// view-space depth. GLSL forbids non-uniform dynamic indexing of a sampler
// array, so the picker dispatches with constant indices (MoltenVK-safe).
layout(set = 1, binding = 3) uniform sampler2D   uCascadeShadowMaps[3];
#define CASCADE_COUNT 3
// Froxel volumetric fog grid: (xy) = screen UV, z = world distance / fogFar.
// .rgb = integrated in-scattering to that distance, .a = transmittance.
layout(set = 1, binding = 4) uniform sampler3D   uFroxelGrid;
// Ambient visibility from the GTAO pass: .xyz = bent normal (WORLD space), .a = visibility.
layout(set = 1, binding = 5) uniform sampler2D   uAmbientVisibility;

layout(set = 2, binding = 0) uniform Material {
    vec4 uBaseColorFactor;
    vec4 uEmissiveFactor;
    vec4 uMaterialParams;  // x=alphaCutoff, y=normalScale, z=roughness, w=metallic
    vec4 uMaterialParams2; // x=transmission (KHR_materials_transmission)
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

// --- Cascaded sun shadows -----------------------------------------------
// Deleted, all of it: a Vogel-disc PCF with a tuned radius, an interleaved-gradient rotation, a
// constant-index cascade dispatch, and a depth bias scaled by uCascadeBias and uSlopeScale. Every
// one of those exists in shadow.glsl, better — the shared version's disc is the same idea with a
// derived radius, and its bias is small on purpose because blix_shadow_normal_offset has already
// moved the sample off the surface it belongs to.
//
// <b>The selection changed with it, and that is a real behaviour change rather than a refactor.</b>
// This picked a cascade by view depth — dot(world - eye, forward) against uCascadeSplits. The
// shared path picks by CONTAINMENT: whichever cascade's box actually holds the fragment. At equal
// view depth a fragment at the edge of a wide frustum is genuinely further from the eye than one at
// its centre, so depth selection can put neighbours in different cascades and draw an arc across
// the picture. Containment cannot.

// The cascade tint is shadow.glsl's blix_cascade_tint — a fourth copy of three colours is
// still a fourth copy, and the shared one also names "beyond the last cascade" in magenta, which
// a still picture otherwise cannot tell from "lit".    // cascade 2 — blue

void main() {
    // UVs arrive in the correct top-down origin already: the Sponza assets are
    // imported with AssetImportContext.FlipTextureV, which bakes the V-flip
    // into the vertex buffer at load. Nothing to do here.
    vec2 uv = vUv;
    // <b>Collapsed to a constant rather than branched around.</b> A uniform-conditional texture read
    // is still a texture read as far as the compiler is concerned, and it may hoist it regardless;
    // pointing every fragment at the same texel keeps the instruction and removes the traffic, which
    // is exactly the variable being isolated. Derivatives go to zero with it, so the sample also
    // pins to mip 0 and stays in cache.
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
    if (alphaCutoff > 0.0) {
        coverage = clamp((albedo4.a - alphaCutoff) / max(fwidth(albedo4.a), 1e-5) + 0.5, 0.0, 1.0);
        if (coverage <= 0.0) discard;
    }
    vec3 albedo = albedo4.rgb;

    // --- Normal map (real per-vertex TBN) -------------------------------
    // Geometric normal, flipped on back faces so two-sided geometry (cypress
    // leaf cards, curtains) lights from the inside. Single-sided geometry is
    // back-face culled, so the flip is a no-op there.
    vec3 N = normalize(vNormalWorld);
    if (!gl_FrontFacing) N = -N;
    // TBN from the forwarded glTF tangent. Gram-Schmidt re-orthonormalize the
    // tangent against N (removes interpolation drift); bitangent handedness
    // from TANGENT.w. This replaces the old screen-space-derivative frame,
    // which swirled on sculpted / mirrored-UV geometry (lavabo, lion heads).
    vec3 T = normalize(vTangentWorld - N * dot(N, vTangentWorld));
    vec3 B = cross(N, T) * vTangentSign;
    // Reconstruct Z from XY. Cooked normals are BC5 (2-channel RG, blue
    // dropped), so the sampled .z is meaningless — derive it from the
    // unit-length constraint. This is also correct for RGBA8 normal maps
    // (their stored Z ≈ sqrt(1 - x² - y²)), so it works for both paths.
    // glTF's own per-material normalScale, with no global multiplier on top. The global was a
    // second control over one quantity, and the material already says what it wants.
    float normalScale = mat.uMaterialParams.y;
    vec2 nxy = (texture(uNormalMap, uv).xy * 2.0 - 1.0) * normalScale * (1.0 - frame.uAbFlags.w);
    float nz = sqrt(max(0.0, 1.0 - dot(nxy, nxy)));
    // Default normal map is flat (0,0,1), so untextured materials keep N.
    N = normalize(mat3(T, B, N) * vec3(nxy, nz));

    // --- PBR scalars (factor × texture) ---------------------------------
    // MR.G = roughness, MR.B = metallic. AO comes from its own sampler.
    vec3 mrSample = texture(uMetallicRoughness, uv).rgb;
    float ao        = texture(uOcclusion, uv).r;
    float roughness = clamp(mat.uMaterialParams.z * mrSample.g, 0.04, 1.0);
    float metallic  = clamp(mat.uMaterialParams.w * mrSample.b, 0.0, 1.0);

    // Metalness noise-gate (asset conformance, not a global look hack).
    // Sponza Modern leaves a stray ~0.35 metalness on dielectric stone/brick
    // (its metallic channel doubled as a specular-intensity dial under the
    // <b>No metallic gate.</b> A threshold snapping metalness to zero was compensating for authored
    // MR values, in the fragment shader, on every pixel, forever — a data question answered in the
    // hottest place it could be. If an asset's metalness is wrong, that is the asset's or the cook's
    // to fix, where it is fixed once.

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
    float transmission = mat.uMaterialParams2.x;
    if (transmission > 0.0) {
        float lod = roughness * (frame.uEnvMipCount - 1.0);
        vec3 envRefl = textureLod(uPrefilteredEnv, R, lod).rgb;
        float fresnel = 0.04 + 0.96 * pow(clamp(1.0 - NdotV, 0.0, 1.0), 5.0);
        // <b>No opacity floor.</b> Clean glass IS ~96% transparent head-on, and lifting it was
        // faking the presence that refraction and absorption would give for free. The panes will
        // read as nearly absent until there is a transmission pass; that is the honest picture of
        // what this model currently computes.
        float glassAlpha = mix(albedo4.a, fresnel, transmission);
        outColor = vec4(envRefl, glassAlpha);
        return;
    }

    // --- Direct sun: Cook-Torrance (diffuse + analytic specular) --------
    vec3 L = -normalize(frame.uSunDirection);
    vec3 H = normalize(V + L);
    float NdotL = max(dot(N, L), 0.0);
    float NdotH = max(dot(N, H), 0.0);
    float VdotH = max(dot(V, H), 0.0);
    // <b>The sample is moved off its own surface before it is projected, by the shared path.</b> It
    // used to happen here, with cascade 0's texel size, because that is the only one a call site can
    // pick before selection has run — and the same vec3 of METRES then went on to size a kernel
    // measured in UV, making a 2-texel filter into a ~100-texel smear. Both halves are the chosen
    // cascade's business, so both now live where the cascade is chosen.
    int shadowCascade = -1;
    float sunShadow = 1.0;
    // <b>NdotL > 0, because a surface facing away from the sun is already shadowed by its own
    // orientation.</b> The taps were being paid for a value that the direct term then multiplies by
    // a zero NdotL — sixteen filtered depth comparisons whose result could not reach the image.
    // Priced at 1.3-1.4x of the frame, the sun shadow is the largest single shading term here, so
    // the fragments that cannot use it are worth not charging.
    //
    // The cascade index stays -1 for those fragments, so --visualize-cascades paints them as
    // "beyond the last cascade". That is a debug view reading a fragment that asked no question.
    if (frame.uShadowStrength > 0.0 && NdotL > 0.0) {
        sunShadow = blix_sun_shadow_cascaded(
            uCascadeShadowMaps[0], uCascadeShadowMaps[1], uCascadeShadowMaps[2],
            frame.uCascadeViewProj[0], frame.uCascadeViewProj[1], frame.uCascadeViewProj[2],
            frame.uCascadeTexels.xyz,
            vWorldPos, N, NdotL, 2.0, gl_FragCoord.xy,
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
    vec3 direct = (kDsun * albedo / PI + sunSpecular)
                  * NdotL * frame.uSunIrradiance * sunShadow;

    // --- IBL: split-sum diffuse + specular ------------------------------
    // Diffuse: irradiance cube × albedo, modulated by (1 - F) and (1 - metallic)
    // (metallics have no diffuse contribution).
    vec3 F = fresnelSchlickRoughness(NdotV, F0, roughness);
    vec3 kS = F;
    vec3 kD = (vec3(1.0) - kS) * (1.0 - metallic);

    // --- Ambient visibility ---------------------------------------------
    // <b>The term that was missing, and the reason five knobs could be deleted without one.</b>
    // The GTAO pass answers, from depth alone, how much of the sky this point can see and which
    // way the opening faces.
    //
    // Skipped for TRANSMISSIVE surfaces, and not as a special case: glass is the only thing that
    // goes through the blend pipelines, so it is the only thing the depth pre-pass did not write.
    // Sampling this buffer from glass would read the visibility of whatever is BEHIND it. The test
    // is uMaterialParams2.x because that is literally the predicate the scene sorts on
    // (isBlend = EffectiveTransmission(material) > 0), so the two cannot drift apart.
    vec4 ambientVis = texture(uAmbientVisibility, gl_FragCoord.xy / frame.uFog.xy);
    bool opaqueSurface = mat.uMaterialParams2.x <= 0.0;
    float visibility = opaqueSurface ? ambientVis.a : 1.0;
    // The bent normal is where the unoccluded sky actually is. Gathering irradiance along it
    // instead of along N is what makes a surface in a corner pick up the light from the opening
    // rather than an average that includes the wall it is pressed against.
    vec3 gatherN = opaqueSurface ? normalize(ambientVis.xyz) : N;

    vec3 irradiance = frame.uAbFlags.z > 0.5 ? vec3(0.2) : texture(uIrradiance, gatherN).rgb;
    vec3 diffuseIBL = irradiance * albedo;

    // Specular: prefiltered env at LOD = roughness × (mipCount - 1), times
    // the BRDF LUT integration (split-sum approximation of the specular term).
    vec3 specularIBL = vec3(0.0);
    if (frame.uAbFlags.z < 0.5) {
        float lod = roughness * (frame.uEnvMipCount - 1.0);
        vec3 prefiltered = textureLod(uPrefilteredEnv, R, lod).rgb;
        vec2 envBrdf = texture(uBrdfLut, vec2(NdotV, roughness)).rg;
        specularIBL = prefiltered * (F * envBrdf.x + envBrdf.y);
    }

    // AO attenuates the indirect contribution only, per the glTF spec.
    //
    // <b>The sun shadow no longer dims indirect light.</b> It used to, as 0.60 + 0.40*sunShadow,
    // and that is wrong in kind rather than degree: sun visibility is not ambient visibility. A
    // crevice facing away from the sun but open to the sky was darkened; one in full sun but
    // enclosed was not. It was standing in for ambient occlusion using the only occlusion signal to
    // hand.
    //
    // It was standing in for ambient occlusion using the only occlusion signal to hand — and the
    // real one now exists above, so this line multiplies by MEASURED visibility rather than by a
    // constant plus a fraction of the sun's shadow.
    //
    // Specular gets its own occlusion, derived rather than dialled: a rough surface gathers over a
    // wide cone and is occluded nearly as much as the diffuse lobe, while a mirror gathers along
    // one ray that the visibility average says little about. Lagarde's approximation is that
    // relationship written down, and it takes roughness and visibility as its only inputs.
    float specularVisibility = clamp(
        pow(max(NdotV + visibility, 0.0), exp2(-16.0 * roughness - 1.0)) - 1.0 + visibility,
        0.0, 1.0);
    vec3 ambient = (kD * diffuseIBL * visibility + specularIBL * specularVisibility) * ao;

    // --- Emissive ------------------------------------------------------
    vec3 emissive = texture(uEmissive, uv).rgb * mat.uEmissiveFactor.rgb * mat.uEmissiveFactor.a;

    vec3 color = direct + ambient + emissive;

    // Debug: tint by which cascade shadowed this fragment (red/green/blue,
    // near→far). Helps confirm split placement + texel-snap stability.
    if (frame.uVisualizeAmbient > 0.5) {
        color = frame.uVisualizeAmbient < 1.5 ? vec3(visibility) : (gatherN * 0.5 + 0.5);
    }

    if (frame.uVisualizeCascades > 0.5) {
        color = mix(color, blix_cascade_tint(shadowCascade) * (0.5 + 0.5 * NdotL * sunShadow), 0.4);
    }

    // --- Froxel fog composite -------------------------------------------
    // Sample the pre-integrated scattering grid at this fragment's screen UV
    // and radial distance, then apply: lit*transmittance + in-scatter. The
    // grid is filled by the froxel compute pass earlier this frame.
    if (frame.uFog.w > 0.5) {
        vec2 fuv = gl_FragCoord.xy / frame.uFog.xy;
        float dist = length(vWorldPos - frame.uCameraPos);
        float w = clamp(dist / frame.uFog.z, 0.0, 1.0);
        vec4 fog = texture(uFroxelGrid, vec3(fuv, w));
        color = color * fog.a + fog.rgb;
    }

    // Output coverage (not raw alpha) so alpha-to-coverage gets the sharpened
    // cutout edge; opaque keeps coverage 1.0 → fully covered.
    outColor = vec4(color, coverage);
}
