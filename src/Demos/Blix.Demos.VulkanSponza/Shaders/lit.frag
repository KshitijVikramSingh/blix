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
#include "sheen.glsl"
#include "probe_volume.glsl"
#include "noise.glsl"
#include "sky_visibility.glsl"

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
    // The camera's forward axis in world space. Only uGatherConstant reads it: it is the exact
    // vector gtao.frag emits for a background texel, so setting that dial to 1 reproduces the
    // --ab prepass off-phase's gather direction rather than approximating it with a literal.
    vec4  uCameraForward;
    float uSheenMipCount;
    // Sample count, so the coverage dither knows how big one quantum is.
    float uMsaaSamples;
    vec4  uSkyDims;        // xyz visibility probe counts
    vec4  uBounceDims;     // xyz bounce probe counts
    vec4  uClothOverride;  // x=sheenRoughness, y=diffuseTransmission; x<0 = use the material's
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
    // <b>How much of the bent normal each consumer gets.</b> gatherN feeds three different fields
    // and they are not equally forgiving of a direction that wobbles: the irradiance CUBE is smooth,
    // the L2 sky-visibility VOLUME is not, and the probe volume is the least forgiving of the three
    // (29% of neighbouring probes differ by more than 2x).
    //
    // Isolated by accident: --ab prepass's off-phase leaves GTAO reading a CLEARED depth buffer, so
    // every pixel takes gtao.frag's background early-out and gatherN becomes ONE CONSTANT DIRECTION
    // for the whole screen. Both the green volume and the wall streak vanish. --ab gtao's off-phase
    // gives an unbent but still per-pixel direction and fixes neither — so it is the direction
    // VARYING that carries them, not the bending and not the occlusion.
    //
    // 1 = today's bent normal, 0 = the shading normal. One dial each, so which field carries it can
    // be found by looking rather than by rebuilding once per guess.
    // <b>0, judged by eye.</b> The cube is the sky's colour and the bent normal steers it toward
    // openings; on this scene that read worse than gathering about the shading normal.
    //@tune 0..1 = 0
    float uBentForCube;
    // <b>1: this one keeps the bend.</b> Sky visibility is the term the bent normal was introduced
    // for -- a surface in a corner should see the opening's share of sky, not its wall's -- and it is
    // the only one of the three that still reads better bent.
    //@tune 0..1 = 1
    float uBentForSkyVis;
    // <b>0, judged by eye, and the measurement agrees with the eye here.</b> The probe volume is the
    // least forgiving index of the three -- 29% of neighbouring probes differ by more than 2x -- so
    // steering it with a screen-space direction turns that contrast into structure. It is not the
    // green streak (that survives this at 0) but it does read better.
    //@tune 0..1 = 0
    float uBentForBounce;
    // <b>How much to decorrelate the alpha-to-coverage sample mask between overlapping cards.</b>
    // Alpha-to-coverage derives the mask from the alpha value DETERMINISTICALLY, so two leaf cards
    // with the same alpha cover the same samples. Three cards at 30% coverage should accumulate to
    // about 66% opacity; correlated, they stay at 30%. The canopy therefore cannot occlude itself
    // and you see sky through depth that should be solid -- which is the blue, and is why raising
    // the sample count from 2 to 4 changed nothing: finer quantisation decorrelates nothing.
    //
    // Bisected to here: with the viz channels writing alpha 1 they show no blue at all, while the
    // lit path writing `coverage` does, on the same pixels with the same shading. That value is the
    // only difference between them.
    //
    // 0 restores today's behaviour exactly, so the comparison is one slider.
    //@tune 0..1 = 1
    float uCoverageDecorrelate;
    // <b>How far the sky-visibility lookup is pushed along the normal, in metres.</b> It exists for
    // the same reason shadow bias does: a volume cell straddling a wall holds both sides of it, so a
    // query taken exactly on the surface reads the enclosure on the wrong side.
    //
    // <b>For foliage it is precisely wrong.</b> A leaf is not embedded in an occluder, it IS one, and
    // 0.6 m along a needle's normal leaves the canopy entirely — so every leaf asks how much sky is
    // visible from half a metre outside the tree, and is told "most of it". That is the tree reading
    // evenly lit top to bottom while the walls around it are dark.
    //@tune 0..2 = 0.6
    float uSkyNormalPush;
    // The same push for CUTOUT surfaces specifically, so foliage can be taken off the wall's setting
    // without changing it. 0 asks the volume where the leaf actually is.
    //@tune 0..2 = 0.6
    float uSkyNormalPushCutout;
    // <b>1 makes cutout surfaces fully opaque, so the lit path can be compared with the viz
    // channels.</b> Those write alpha 1 while the lit path writes `coverage`, which means over
    // foliage they are not the same pixels: viz shows the nearest leaf solid, lit shows an average
    // of ~10 semi-transparent layers and whatever lies beyond them. Every shading term can read
    // dark in viz while the lit image glows, with no contradiction and nothing to point at.
    //
    // This removes that difference. If the tree goes dark at 1, the glow is compositing and the
    // canopy's 6.3%-per-card opacity is the subject. If it still glows, a shading term is being
    // added that none of channels 12 and 17-20 can see, and that is a different hunt.
    //@tune 0..1 = 0
    float uForceOpaqueCutout;
    // <b>The two remaining halves of what --ab prepass's off-phase actually switches.</b> That arm
    // leaves GTAO reading a cleared depth buffer, so it emits its background answer for every pixel:
    // one CONSTANT world direction, and visibility exactly 1.0. Both symptoms vanish there, and
    // neither the three dials above (direction -> shading normal) nor --ab gtao (radius 0) reproduce
    // it. So the carrier is one of these two, and they have never been separable until now.
    //
    // uGatherConstant 1 replaces gatherN with a single screen-wide direction — NOT the shading
    // normal, which still varies per pixel, but literally the same vector everywhere, which is the
    // state that fixes it.
    //@tune 0..1 = 0
    float uGatherConstant;
    // uForceFullVis 1 pins ambient visibility to 1.0 without touching anything else, so "no
    // occlusion" can be tested apart from "no direction".
    //@tune 0..1 = 0
    float uForceFullVis;
    // <b>The two halves of "specular", separated because --ab pbr moved both at once.</b> That arm
    // zeroes the GGX sun lobe, and zeroing it also zeroes Fsun — which appears again in
    // kDsun = (1 - Fsun)(1 - metallic), so the DIFFUSE sun term gets brighter at the same moment
    // the glint disappears. About 4% head-on where F0 is 0.04, and far more at grazing angles where
    // Fresnel approaches 1 and the split takes nearly all the diffuse away. Reported from the chair
    // as that arm reading the best lit of the seven, which it cannot be attributed to until the two
    // effects move independently.
    //
    //   uSunSpecular    scales the GGX highlight alone. 0 removes the glint, diffuse unchanged.
    //@tune 0..2 = 1
    float uSunSpecular;
    //   uFresnelDiffuse how much of the Fresnel reflectance is taken OUT of diffuse, sun and IBL
    //                   alike. 1 is the energy split as written; 0 keeps the full Lambert lobe and
    //                   lets the specular sit on top of it.
    //@tune 0..1 = 1
    float uFresnelDiffuse;
    // Measurement switches, one per --ab mode. Each removes one term from the fragment so a paired
    // interleaved run can price it:
    //   x  collapse every material UV to a constant, so the five material samples all hit one
    //      cached texel. The sample INSTRUCTIONS remain — this prices BANDWIDTH, not instruction
    //      count, which is the distinction the whole question turns on.
    //   y  skip the GGX/Smith/Fresnel specular lobe, leaving Lambert. Prices ALU.
    //   z  skip the three IBL lookups and the split-sum, using a flat ambient. Prices IBL whole.
    //   w  skip the normal map sample and the tangent-space transform.
    vec4  uAbFlags;
    //   x  skip the probe bounce lookup AND the baked sky-visibility evaluation. Prices the two
    //      terms that read the probe volumes, which is the pair a half-res pass would move.
    vec4  uAbFlags2;
    // --viz N: write one of the shading inputs instead of the lit colour. The normal path is the
    // hardest thing here to be sure about by reading code — a double-sided sheet whose back face
    // lights from the wrong hemisphere looks exactly like a material problem — so it is worth being
    // able to LOOK at the inputs rather than reason about them.
    //   1 geometric normal (world, after the facing flip)   2 shading normal (after the map)
    //   3 tangent-space normal-map value                    4 front/back facing
    //   5 world tangent                                     6 world bitangent
    float uVizChannel;
    // xyz = world-space min of the sky volume, w = 1 when the volume is loaded.
    vec4  uSkyMin;
    // xyz = 1 / (max - min), w = how far along the normal to push the lookup, in metres.
    vec4  uSkyScale;
    float uBounceStrength;
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
// Baked sky visibility as L1 spherical harmonics, one Rgba16F texel per probe cell. Geometry, not
// lighting: what escapes the building, which no sun position changes.
// <b>L1 spherical harmonics in a small 3D texture, and an octahedral atlas was tried and
// reverted.</b> The atlas reconstructs better — a courtyard floor reads 0.063 against L1's 0.041,
// where the raw transmittance is 0.057 — and it doubled the frame, 35.9 ms to 70.8 ms, on a single
// fetch before any blending. Four floats in 331 KB get hardware trilinear with perfect locality;
// an atlas costs an octahedral encode and a scattered texel at every one of this shader's call
// sites. The same change was right for the bounce and wrong here, which is about how often and how
// coherently each volume is read rather than about what either holds.
layout(set = 1, binding = 6)  uniform sampler3D uSkyVisibility;   // L0, L1 x/y/z
layout(set = 1, binding = 14) uniform sampler3D uSkyVisibility1;  // L2 -2,-1,0,+1
layout(set = 1, binding = 15) uniform sampler3D uSkyVisibility2;  // L2 +2
// Sun bounce, injected each frame over the same voxel grid. RGB irradiance, no direction: the
// visibility volume supplies the shape, this supplies the colour and the level.
// One volume per colour channel, each holding that channel's (L0, L1x, L1y, L1z). Directional, so
// a surface receives what reaches the side it FACES — the previous single RGB gave every surface at
// a point the same answer, which is why a teal curtain metres away tinted a whole tree.
// An octahedral atlas: one 8x8 tile per probe, 6x6 interior plus a border ring. Directional at
// thirty-six samples rather than L1's four, which is the difference between knowing a curtain is
// over there and knowing how much of the sky it covers.
layout(set = 1, binding = 7) uniform sampler2D uSkyBounce;
layout(set = 1, binding = 13) uniform sampler2D uSkyBounceDepth;
// <b>No storage image here, and the reason is measured.</b> Marking probes from the fragment stage
// is the obvious way to learn which ones shading reads — and on this tile-based GPU merely
// DECLARING an image3D in this shader cost 8x the frame: 35 ms became 290 ms. Not the write, the
// declaration: disabling every store left it at 289 ms, and deleting the binding restored 35 ms.
// A TBDR tiler cannot keep its guarantees about a fragment shader that might scatter to memory, so
// it stops trying, and the image resolves visibly block by block.
//
// The information is still wanted and still only exists at render time — it just has to be derived
// somewhere that can afford to write. Reading the depth buffer in a compute pass gives the same
// answer, which probes sit near visible geometry, from a stage where scattering is free.
// The environment convolved with CHARLIE rather than GGX, and the Charlie lobe's directional
// albedo. Separate from uPrefilteredEnv on purpose: a GGX cube in sheen's place renders something
// dimmer and rimless and entirely plausible, which is the failure this whole arc keeps closing.
layout(set = 1, binding = 8) uniform samplerCube uSheenEnv;
layout(set = 1, binding = 9) uniform sampler2D   uSheenLut;
// Declared to keep set 1 layout-compatible with the skybox pipeline in the same pass; the lit
// shader reads the prefiltered chain, not the raw sky.
layout(set = 1, binding = 10) uniform samplerCube uEnvCube;

layout(set = 2, binding = 0) uniform Material {
    vec4 uBaseColorFactor;
    vec4 uEmissiveFactor;
    vec4 uMaterialParams;  // x=alphaCutoff, y=normalScale, z=roughness, w=metallic
    vec4 uMaterialParams2; // x=transmission, y=sheenRoughness, z=diffuseTransmission
    vec4 uSheenColor;              // KHR_materials_sheen colour factor
    vec4 uDiffuseTransmissionColor;// KHR_materials_diffuse_transmission colour
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

// <b>Hoisted, because glass was the one surface that skipped every occlusion term.</b> The
// transmissive branch returns before ambient visibility, before this, and before the debug views —
// so a pane reflected an unoccluded outdoor sky from inside a closed room, and viz channel 7 drew
// the skybox on it. The lookup needs only a position and a direction, so there is no reason it had
// to live where it did.
float blixSkyVisibility(vec3 worldPos, vec3 dir, float push) {
    if (frame.uSkyMin.w <= 0.5 || frame.uAbFlags2.x > 0.5) return 1.0;
    return blix_skyVisibility(uSkyVisibility, uSkyVisibility1, uSkyVisibility2,
                              frame.uSkyMin.xyz, frame.uSkyScale.xyz, push,
                              worldPos, dir);
}

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
        // <b>The discard is unchanged, deliberately.</b> The depth pre-pass decides which fragments
        // exist with its own copy of this test and the two must agree -- they disagreed once tonight
        // and it cost an hour. Only the MASK is jittered, after the silhouette is settled.
        if (coverage <= 0.0) discard;

        // <b>A WORLD-space hash, so overlapping cards decorrelate and the pattern does not crawl.</b>
        // Screen space would hand two cards at the same pixel the same offset, which is the problem
        // rather than the fix. Hashing quantised world position gives each card its own and anchors
        // the dither to the geometry when the camera moves. 16 per metre is finer than a needle.
        //
        // The offset is +/- half a coverage quantum, so expected coverage is unchanged and only
        // WHICH samples get written moves -- same average opacity, different samples, so a card
        // behind another lands on the samples the first left empty.
        float layerHash = blix_hash31(floor(vWorldPos * 16.0));
        float quantum = 1.0 / max(frame.uMsaaSamples, 1.0);
        coverage = clamp(coverage + (layerHash - 0.5) * quantum * frame.uCoverageDecorrelate,
                         0.0, 1.0);
        coverage = mix(coverage, 1.0, frame.uForceOpaqueCutout);
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
        // Occluded like every other indirect term. Evaluated along R rather than N because a
        // reflection gathers from where it points, and a window deep inside a room points at a wall.
        vec3 envRefl = textureLod(uPrefilteredEnv, R, lod).rgb * blixSkyVisibility(vWorldPos, R, frame.uSkyNormalPush);
        float fresnel = 0.04 + 0.96 * pow(clamp(1.0 - NdotV, 0.0, 1.0), 5.0);
        // <b>No opacity floor.</b> Clean glass IS ~96% transparent head-on, and lifting it was
        // faking the presence that refraction and absorption would give for free. The panes will
        // read as nearly absent until there is a transmission pass; that is the honest picture of
        // what this model currently computes.
        float glassAlpha = mix(albedo4.a, fresnel, transmission);
        // The debug views have to reach glass too. They did not, because this branch returns first
        // — so every channel drew a lit pane over whatever it was meant to be showing, and the one
        // surface worth interrogating was the one the instrument could not see.
        if (frame.uVizChannel > 0.5) {
            float vis = blixSkyVisibility(vWorldPos, R, frame.uSkyNormalPush);
            vec3 c = frame.uVizChannel < 1.5 ? vizGeometricN * 0.5 + 0.5 :
                     frame.uVizChannel < 2.5 ? N * 0.5 + 0.5 :
                     frame.uVizChannel < 7.5 ? vec3(vis) : vec3(vis);
            outColor = vec4(c, 1.0);
            return;
        }
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
    // <b>And then diffuse transmission made that premise false.</b> A transmitting surface uses the
    // sun on the side facing AWAY — dot(-N, L) — which is precisely where NdotL is zero, so the
    // gate skipped the lookup on exactly the fragments that needed it and sunShadow kept its
    // initial 1.0. Every back-facing curtain fragment received full unshadowed sun through it, and
    // the curtains held their colour in deep shade as though lit from within. The saving is still
    // real and still taken; what the gate asks is now "can ANY term here use a shadow", which for
    // an opaque material is the same question it was before.
    //
    // The cascade index stays -1 for those fragments, so --visualize-cascades paints them as
    // "beyond the last cascade". That is a debug view reading a fragment that asked no question.
    float backNdotL = max(dot(-N, L), 0.0);
    float shadowNeed = max(NdotL, mat.uMaterialParams2.z > 0.0 ? backNdotL : 0.0);
    if (frame.uShadowStrength > 0.0 && shadowNeed > 0.0) {
        sunShadow = blix_sun_shadow_cascaded(
            uCascadeShadowMaps[0], uCascadeShadowMaps[1], uCascadeShadowMaps[2],
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
        sunSpecular = (Dsun * Gsun * Fsun) / max(4.0 * NdotV * NdotL, 1e-3) * frame.uSunSpecular;
    }

    // Energy split: diffuse keeps only the non-reflected fraction (1 - F) and vanishes on metals
    // (1 - metallic); /PI normalizes the Lambert lobe. uSunIrradiance is the irradiance the probe
    // measured, so this line is the rendering equation for a directional light rather than a shape
    // scaled until it looked right.
    vec3 kDsun = (vec3(1.0) - Fsun * frame.uFresnelDiffuse) * (1.0 - metallic);

    // --- Cloth: the two things metallic-roughness cannot say -------------
    // Sheen is a retroreflective rim at grazing angles; diffuse transmission is light entering the
    // back of a single layer and scattering out the front. A curtain wants both, and the glTF spec
    // keeps them as separate extensions because they are separate physics — one you see, one you
    // see THROUGH.
    vec3  sheenColor     = mat.uSheenColor.rgb;
    float sheenRoughness = clamp(frame.uClothOverride.x >= 0.0 ? frame.uClothOverride.x
                                                                : mat.uMaterialParams2.y, 0.0, 1.0);
    // Only where the material already HAS the term: the override is for finding a value, not for
    // making stone translucent.
    float diffTrans      = mat.uMaterialParams2.z > 0.0 && frame.uClothOverride.y >= 0.0
                         ? clamp(frame.uClothOverride.y, 0.0, 1.0)
                         : clamp(mat.uMaterialParams2.z, 0.0, 1.0);
    // A probe older than v4 has no Charlie cube, so sheen is off rather than approximated.
    bool  hasSheen       = dot(sheenColor, vec3(1.0)) > 0.0 && frame.uSheenMipCount > 0.0;

    // How much light the sheen layer takes, so the base layer beneath can be darkened by it.
    // Without this, sheen is added energy and cloth ends up brighter than the light falling on it.
    float sheenAlbedo  = hasSheen ? blix_sheenAlbedo(uSheenLut, NdotV, sheenRoughness) : 0.0;
    float sheenScale   = hasSheen ? blix_sheenScaling(sheenColor, sheenAlbedo) : 1.0;
    // And what leaves through the back did not leave through the front.
    float transScale   = blix_diffuseTransmissionScaling(diffTrans);

    vec3 sunSheen = vec3(0.0);
    if (hasSheen) {
        sunSheen = blix_sheenBrdf(sheenColor, sheenRoughness, NdotH, NdotL, NdotV)
                 * NdotL * frame.uSunIrradiance * sunShadow;
    }

    // The sun through the cloth. Its own shadow term is deliberately the SAME sunShadow: a curtain
    // in shade transmits nothing, and a curtain in sun glows whichever side you are on.
    vec3 sunTransmission = vec3(0.0);
    if (diffTrans > 0.0) {
        sunTransmission = blix_diffuseTransmission(
            N, L, frame.uSunIrradiance * sunShadow,
            mat.uDiffuseTransmissionColor.rgb * albedo, diffTrans);
    }

    vec3 direct = (kDsun * albedo / PI * transScale + sunSpecular)
                  * NdotL * frame.uSunIrradiance * sunShadow
                  + (sunSheen + sunTransmission) * sheenScale;

    // --- IBL: split-sum diffuse + specular ------------------------------
    // Diffuse: irradiance cube × albedo, modulated by (1 - F) and (1 - metallic)
    // (metallics have no diffuse contribution).
    vec3 F = fresnelSchlickRoughness(NdotV, F0, roughness);
    vec3 kS = F;
    vec3 kD = (vec3(1.0) - kS * frame.uFresnelDiffuse) * (1.0 - metallic);

    // --- Ambient visibility ---------------------------------------------
    // <b>The term that was missing, and the reason five knobs could be deleted without one.</b>
    // The GTAO pass answers, from depth alone, how much of the sky this point can see and which
    // way the opening faces.
    //
    // Skipped for TRANSMISSIVE surfaces, and not as a special case: glass is the only thing that
    // goes through the blend pipelines, so it is the only thing the depth pre-pass did not write.
    // Sampling this buffer from glass would read the visibility of whatever is BEHIND it. The test
    // is uMaterialParams2.x because that is literally the predicate the scene sorts on
    // (isBlend = material.TransmissionFactor > 0), so the two cannot drift apart.
    vec4 ambientVis = texture(uAmbientVisibility, gl_FragCoord.xy / frame.uFog.xy);
    bool opaqueSurface = mat.uMaterialParams2.x <= 0.0;
    float visibility = mix(opaqueSurface ? ambientVis.a : 1.0, 1.0, frame.uForceFullVis);
    // The bent normal is where the unoccluded sky actually is. Gathering irradiance along it
    // instead of along N is what makes a surface in a corner pick up the light from the opening
    // rather than an average that includes the wall it is pressed against.
    vec3 gatherN = opaqueSurface ? normalize(ambientVis.xyz) : N;
    // <b>A bent normal is a refinement of N, never a replacement for it.</b> GTAO derives it in
    // screen space from depth, which cannot tell which side of a leaf card is being shaded — so on
    // the back face of two-sided geometry it points into the hemisphere the surface does NOT face,
    // and the fragment gathers its indirect light from the wrong side of itself.
    //
    // Harmless while the bounce was one direction-free RGB, mild under L1's single smooth lobe, and
    // plainly wrong at thirty-six directional samples: it fetches a different part of the probe's
    // map entirely. Reported from the chair as back-facing leaves shifting blue.
    if (dot(gatherN, N) < 0.0) gatherN = N;

    // The same constant gtao.frag writes for a background texel: the camera's view axis in world
    // space. Applied BEFORE the per-consumer dials so it reproduces the arm exactly.
    gatherN = normalize(mix(gatherN, normalize(frame.uCameraForward.xyz), frame.uGatherConstant));
    vec3 cubeN   = normalize(mix(N, gatherN, frame.uBentForCube));
    vec3 skyVisN = normalize(mix(N, gatherN, frame.uBentForSkyVis));
    vec3 bounceN = normalize(mix(N, gatherN, frame.uBentForBounce));
    vec3 irradiance = frame.uAbFlags.z > 0.5 ? vec3(0.2) : texture(uIrradiance, cubeN).rgb;

    // --- Baked sky visibility -------------------------------------------
    // <b>What this surface can SEE, which nothing in this renderer previously knew.</b> The
    // screen-space term above has a sub-metre radius: it answers whether a leaf is near this stone,
    // not whether the stone is at the bottom of a courtyard. Measured, the atrium floor receives
    // about 0.15 of the sky and the shader was giving it 0.85.
    //
    // Sampled a little along the NORMAL, because a probe cell straddling a wall holds both sides of
    // it and a lookup taken exactly at the surface reads the enclosure on the wrong side. It is the
    // same failure as shadow acne — a query about a surface, taken on that surface — and the same
    // remedy.
    float skyVisibility = 1.0;
    vec3 vizProbeUv = vec3(0.0);
    vec4 vizSh = vec4(0.0);
    if (frame.uSkyMin.w > 0.5) {
        vec3 probeUv = (vWorldPos + N * frame.uSkyScale.w - frame.uSkyMin.xyz) * frame.uSkyScale.xyz;
        vizProbeUv = probeUv;
        // One call, the same one glass and everything else uses. Two copies of this evaluation is
        // how the volume and its readers drifted apart before.
        skyVisibility = blixSkyVisibility(vWorldPos, skyVisN, alphaCutoff > 0.0 ? frame.uSkyNormalPushCutout : frame.uSkyNormalPush);
        vizSh = vec4(skyVisibility);
    }

    vec3 diffuseIBL = irradiance * albedo * skyVisibility;

    // Specular: prefiltered env at LOD = roughness × (mipCount - 1), times
    // the BRDF LUT integration (split-sum approximation of the specular term).
    vec3 specularIBL = vec3(0.0);
    if (frame.uAbFlags.z < 0.5) {
        float lod = roughness * (frame.uEnvMipCount - 1.0);
        vec3 prefiltered = textureLod(uPrefilteredEnv, R, lod).rgb;
        vec2 envBrdf = texture(uBrdfLut, vec2(NdotV, roughness)).rg;
        // The reflected sky is the same sky. Not occluding it leaves a courtyard floor with a
        // mirror of an open horizon it cannot see.
        specularIBL = prefiltered * (F * envBrdf.x + envBrdf.y) * skyVisibility;
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
    // <b>ADDED, not multiplied — which is the distinction the first attempt got wrong.</b> Sky
    // visibility scales the sky a surface can see; bounced sunlight is light arriving from
    // elsewhere and belongs in the sum. Folding it into the visibility SH made an up-facing floor
    // evaluate negative, because two directional fields multiplied double-count direction.
    vec3 bounce = vec3(0.0);
    vec3 vizBounceRaw = vec3(0.0);
    float probeConfidence = 0.0;
    if (frame.uBounceStrength > 0.0 && frame.uAbFlags2.x < 0.5) {
        vec3 probeUv = (vWorldPos + N * frame.uSkyScale.w - frame.uSkyMin.xyz) * frame.uSkyScale.xyz;
        // The volume stores average incident RADIANCE; irradiance is PI times it. Getting this
        // conversion wrong is invisible in a single pass and fatal once the pass feeds itself.
        // <b>Reflected exactly the way diffuseIBL is, because they are the same kind of quantity.</b>
        // That line is irradiance x albedo; this must be too, or the two halves of the ambient are
        // in different units. The volume stores average incident RADIANCE, so irradiance is PI times
        // it — and the /PI that briefly sat here is the radiance conversion, which belongs in the
        // injection pass where a surface re-emits, not here where one receives.
        // <b>Eight probes, weighted by whether each can actually SEE this point.</b> The previous
        // nearest-probe fetch had no visibility term at all, so a wall took its light from whatever
        // probe happened to be closest — including one on the far side of itself. That leak is why
        // colour bled through walls from curtains and a tree they do not face.
        vec3 incident = blix_probeIrradianceEx(
            uSkyBounce, uSkyBounceDepth, ivec3(frame.uBounceDims.xyz),
            frame.uSkyMin.xyz, 1.0 / frame.uSkyScale.xyz, vWorldPos, bounceN, probeConfidence);
        vizBounceRaw = incident;
        bounce = incident * albedo * (1.0 - metallic) * ao * visibility;
    }

    // Sheen's own prefiltered environment, at the sheen roughness rather than the base one, times
    // the same directional albedo that pays for it. Occluded like every other indirect term.
    vec3 sheenIBL = vec3(0.0);
    if (hasSheen && frame.uAbFlags.z < 0.5) {
        float sheenLod = sheenRoughness * max(frame.uSheenMipCount - 1.0, 0.0);
        vec3 sheenEnv = textureLod(uSheenEnv, R, sheenLod).rgb;
        sheenIBL = sheenEnv * sheenColor * sheenAlbedo * skyVisibility * visibility * ao;
    }

    // The ambient half of transmission: irradiance gathered along -N, which is the sky on the side
    // the surface is not facing. A curtain with a bright courtyard behind it glows without any
    // direct sun on it at all, and that is most of what makes cloth read as thin.
    vec3 transmittedIBL = vec3(0.0);
    if (diffTrans > 0.0) {
        // <b>The BACK side's own sky visibility, queried along -N.</b> This reused the front face's
        // `skyVisibility`, which is a different question with a different answer: a curtain whose
        // front faces a 3%-sky wall and whose back faces an open courtyard was told it could see 3%
        // of the sky from behind. That handed cloth a second light path scaled by the wrong occlusion,
        // and in an atrium where the walls see 2.4-3.8% of the sky it routinely outweighed the
        // properly shadowed front face.
        //
        // The baked volume CAN answer this, which is the point — it is a world-space field, so -N is
        // as valid a query direction as N. Only the screen-space term (`visibility` below) genuinely
        // cannot see the back side, and that one stays an approximation.
        //
        // Diagnosed, then mis-fixed: the curtain patch set diffuseTransmission to 0 and recorded this
        // exact reasoning as the justification. Deleting the term because its occlusion was wrong is
        // hiding a symptom; the occlusion is what was wrong.
        vec3 backIrradiance = texture(uIrradiance, -cubeN).rgb * blixSkyVisibility(vWorldPos, -N, alphaCutoff > 0.0 ? frame.uSkyNormalPushCutout : frame.uSkyNormalPush);
        transmittedIBL = blix_diffuseTransmissionAmbient(
            backIrradiance, mat.uDiffuseTransmissionColor.rgb * albedo, diffTrans) * ao * visibility;
    }

    vec3 ambient = ((kD * diffuseIBL * transScale * visibility + specularIBL * specularVisibility) * ao
                    + bounce * transScale) * sheenScale
                 + sheenIBL + transmittedIBL;

    // --- Emissive ------------------------------------------------------
    vec3 emissive = texture(uEmissive, uv).rgb * mat.uEmissiveFactor.rgb * mat.uEmissiveFactor.a;

    vec3 color = direct + ambient + emissive;

    // Debug: tint by which cascade shadowed this fragment (red/green/blue,
    // near→far). Helps confirm split placement + texel-snap stability.
    if (frame.uVizChannel > 0.5) {
        vec3 c =
            frame.uVizChannel < 1.5 ? vizGeometricN * 0.5 + 0.5 :
            frame.uVizChannel < 2.5 ? N * 0.5 + 0.5 :
            frame.uVizChannel < 3.5 ? vizTangentN * 0.5 + 0.5 :
            frame.uVizChannel < 4.5 ? (gl_FrontFacing ? vec3(0.1, 0.8, 0.2) : vec3(0.9, 0.15, 0.1)) :
            frame.uVizChannel < 5.5 ? T * 0.5 + 0.5 :
            frame.uVizChannel < 6.5 ? B * 0.5 + 0.5 :
            frame.uVizChannel < 7.5 ? vec3(skyVisibility) :
            // 8 = the probe lookup coordinate, 9 = the raw L0 it read back, scaled to [0,1] by the
            // value a fully open sphere produces. Between them these say whether a near-zero result
            // is a bad coordinate, a bad uniform, or a bad texel.
            frame.uVizChannel < 8.5 ? clamp(vizProbeUv, vec3(0.0), vec3(1.0)) :
            frame.uVizChannel < 9.5 ? vec3(clamp(vizSh.x / (4.0 * PI * 0.282095), 0.0, 1.0)) :
            // <b>The bounce, on its own and unscaled by anything it is later multiplied into.</b>
            // "There is almost no light bouncing" is a claim about a quantity nothing displayed:
            // the injected field only ever reached the eye after albedo, AO and ambient visibility
            // had each taken a share, so a weak result and a correct-but-attenuated one looked the
            // same. 10 is the radiance the probe volume holds here; 11 is what it contributes after
            // the surface takes its share; 12 is the direct sun alone, for scale — read 10 against
            // 12 and the ratio IS the bounce's strength, which is the number in dispute.
            frame.uVizChannel < 10.5 ? vizBounceRaw :
            frame.uVizChannel < 11.5 ? bounce :
            frame.uVizChannel < 12.5 ? direct :
            // <b>The occlusion stack, one term at a time and then multiplied.</b> Ambient is
            // irradiance x albedo x skyVisibility x GTAO x textureAO — three occlusion terms in a
            // row, each defensible alone. Whether their product is defensible is a question nobody
            // had looked at, because nothing displayed it.
            frame.uVizChannel < 13.5 ? vec3(visibility) :
            frame.uVizChannel < 14.5 ? vec3(ao) :
            frame.uVizChannel < 15.5 ? vec3(skyVisibility * visibility * ao) :
            // <b>16: how much of the eight-probe blend survived the visibility test.</b> Green is a
            // full blend; darkening green is a partial one; RED is the fallback — every probe
            // rejected, so the surface is lit by an unweighted nearest probe with NO occlusion term
            // at all. That state is invisible in the final image, which is the problem: it looks
            // like light rather than like a reconstruction failure, and a large red area would mean
            // the bounce is painting flat fill wherever the Chebyshev test gives up.
            //
            // <b>BLUE is the term being switched off, and it is a separate colour for a reason.</b>
            // The first version of this channel painted red whenever the confidence was zero, which
            // is also what an unexecuted bounce block leaves behind — so a whole scene running
            // without --sky read as "every probe rejected" instead of "this feature is not on". One
            // glance cost an hour. A diagnostic must distinguish a measured zero from an absent
            // measurement.
            frame.uVizChannel < 16.5 ? (frame.uBounceStrength <= 0.0
                                            ? vec3(0.0, 0.1, 1.0)
                                            : probeConfidence <= 1e-5
                                                ? vec3(1.0, 0.0, 0.0)
                                                : vec3(0.0, clamp(probeConfidence, 0.0, 1.0), 0.0)) :
            // <b>17-20: the ambient sum, one term at a time.</b> "The shadowed leaves are blue" is a
            // statement about a SUM, and the four things in it are lit very differently: the sky
            // diffuse is albedo-tinted, the specular is not (it is ~4% of the sky whatever colour the
            // surface is), the transmitted term is the new thin-sheet path, and the bounce carries
            // whatever the probes hold. Any one of them can own a hue without the others moving, and
            // reading which from the total is guesswork -- these are the same terms the final line
            // adds up, exposed before they are added.
            frame.uVizChannel < 17.5 ? kD * diffuseIBL * transScale * visibility * ao :
            frame.uVizChannel < 18.5 ? specularIBL * specularVisibility * ao :
            frame.uVizChannel < 19.5 ? transmittedIBL :
                                       bounce * transScale;
        // Straight out, no exposure and no tonemap — these are directions and flags, and a film
        // curve on a direction is a way to misread it.
        // <b>Coverage 1, not the fragment's own — a diagnostic must not be alpha-to-coverage masked.</b>
        // Writing `coverage` here hands the pipeline a partial sample mask, so the samples it drops
        // keep whatever drew next, which for a canopy is the SKYBOX. Every viz channel then showed
        // the same blue over foliage no matter what it was displaying, because none of them were
        // displaying anything there -- that is how "the exact same blue in all of them" got noticed,
        // and it is also the blue on the tree in the lit image.
        outColor = vec4(c, 1.0);
        return;
    }

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
