#pragma once

// Single-tap directional (sun) shadow lookup — the look-free technique shared by the
// engine's lit demos (TankArena, Bulwark). `coord` is the fragment in the light's clip
// space (uSunShadowVP * world); `ndotl` (surface-to-sun dot) drives an angle-scaled
// depth bias to suppress acne on grazing faces. Vulkan convention: clip Z is already
// [0,1] and the Y-flip is baked into the light's ortho, so NDC->UV needs no flip.
// Returns 1 = lit, 0 = shadowed; the caller composes it into its own lighting/mood.
// Pair with Blix.Graphics.GraphicsMatrices.SunShadowViewProjection on the CPU side.
//
// Two variants: a single tap (hard, cheapest) and a percentage-closer filter
// (blix_sun_shadow_soft, below) for scenes where a hard shadow edge reads as a
// staircase rather than as a shadow.
#ifndef BLIX_SHADOW_PCF_TAPS
#define BLIX_SHADOW_PCF_TAPS 16
#endif

// World-space offset to apply to a position BEFORE projecting it into the light's
// clip space, so that a surface does not shadow itself.
//
// This is the fix for shadow acne, and it is a different and better one from depth
// bias. Depth bias pushes the comparison along the light's Z, which is the wrong axis
// for a surface the light grazes: the error a shadow map makes on a near-parallel
// surface is that one texel covers a long slice of it, so the depth stored for the
// texel centre is wrong everywhere else in the texel by an amount that grows without
// bound as the angle closes. Enough depth bias to cover that detaches every shadow
// from its caster; not enough leaves the broad dirty smears that look like dirt on
// the ground rather than like shadow.
//
// Offsetting ALONG THE SURFACE NORMAL moves the sample sideways, off the surface, by
// a distance related to how wide a texel is in world units — which is the actual
// scale of the error. Scaled by how far from face-on the light is, so a surface
// pointing at the sun (where the error is small) is barely moved and a grazing one is
// moved by most of a texel.
//
// `texelWorld` is the world-space size of one shadow-map texel: the light's ortho
// extent divided by the map's side.
//
// <b>And CAPPED in world units, because the two error terms do not scale together.</b> The acne
// this corrects scales with texel size, so the offset does too — but the thing it must not do is
// walk the sample off its own surface into a neighbour's shadow, and THAT limit is set by the
// scene's geometry, which knows nothing about cascade resolution. In this tree cascade 2's texels
// are 0.205 m, so at four texels a grazing surface is moved 0.82 m before being asked whether it
// is in shadow — most of a metre, in a building whose corridors are about three metres wide.
//
// <b>Defensive, and explicitly NOT the fix for anything observed.</b> It was added while hunting
// indoor sun seams and it did not move them by a single pixel: the walls in question sit in
// cascade 0, whose 0.018 m texels give a 0.072 m offset that was already under this cap, and the
// direct-sun channel was byte-identical with the cap in and out. That artifact was shadow caster
// culling dropping the occluding wall — see SceneExitDistance in SponzaLoop.Render.cs, where
// measuring the sweep from the leading face clamped it to zero. The cap is kept because 0.82 m is
// indefensible on its own terms and will bite once something is looked at from far enough away to
// sit in cascade 2, not because it repaired a defect anybody saw.
//
// It costs the far cascade nothing real: 0.1 m is still five times cascade 0's texel, and acne in
// cascade 2 is at a distance where one texel is already larger than the artifact.
#ifndef BLIX_SHADOW_OFFSET_MAX_M
#define BLIX_SHADOW_OFFSET_MAX_M 0.10
#endif

vec3 blix_shadow_normal_offset(vec3 world, vec3 normal, float ndotl, float texelWorld, float texels) {
    float slope = clamp(1.0 - ndotl, 0.0, 1.0);
    // The square root keeps a little offset even on face-on surfaces, where a pure
    // linear falloff leaves nothing at all and the last of the acne survives.
    float offset = texelWorld * texels * (0.30 + 0.70 * sqrt(slope));
    return world + normal * min(offset, BLIX_SHADOW_OFFSET_MAX_M);
}

// --- Cascaded directional shadows -----------------------------------------
//
// Selection only: the sampling is blix_sun_shadow_soft above, called once, on whichever
// cascade contains the fragment. That is the whole reason this is three lines of dispatch
// rather than a fourth percentage-closer filter — there are already three PCF
// implementations in this tree (here, Sponza's, VulkanLit's) and the way to stop there
// being a fourth is for the cascade layer not to need one.
//
// <b>THREE, fixed, and that is a portability fact rather than a preference.</b> Sampling a
// sampler2D array at a dynamically computed index needs
// shaderSampledImageArrayDynamicIndexing, which is not guaranteed — so every implementation
// that works everywhere branches on a constant index instead. Three named samplers say that
// honestly, where an array parameter would look general and only work on some drivers. Making
// the count a #define variant would make it the first axis of a shader permutation matrix, for
// a number nobody has wanted to change.
//
//   texelWorld  one shadow texel in WORLD UNITS, per cascade — the light ortho's extent divided by
//               its map side. Per cascade because the boxes differ in size by an order of magnitude,
//               which is the whole point of having three of them.
//   chosen      which cascade answered, or -1 when no cascade contains the fragment. For a debug
//               tint, and for finding out that a scene is spending three passes on one cascade's
//               work.
//
// <b>The normal offset happens HERE, not at the call site, and that is the fix for a bug both
// callers had.</b> The offset is sized in world units and the PCF radius is sized in UV, and the
// cascade that answers sets both — which nobody knows until selection has run. So every caller
// offset the position first, using cascade 0's number because it was the only one it could pick,
// and passed the same vec3 on for the kernel. Sponza's held metres, so its 2-texel kernel became a
// ~100-texel smear; the studio's held 1/2048, so its offset was three millimetres and did nothing.
// One name meaning two things, and each caller got one of its two uses right.
//
// Selection has to run before either number is known, so the offset is applied per candidate inside
// the loop and the UV texel comes from textureSize() — a quantity the sampler already carries and no
// caller can get wrong.
//
// <b>Selection is by CONTAINMENT, not by view depth, and that is not the textbook choice.</b> The
// usual scheme slices the view frustum by depth and picks by `dot(world - eye, forward)`. It assumes
// a camera standing AMONG the things it looks at. A camera that orbits its subject from outside is
// the other case, and there the frustum at the subject's depth is far wider than the subject —
// measured on the studio stage, every split ratio put the subject in a cascade COARSER than the
// single origin-fitted box it replaced, by 1.8x to 4.1x. Containment lets the boxes be fitted to
// the content instead of to the frustum, which is the arrangement that stage actually wants, and it
// costs the strictly more robust rule: the first cascade that can answer, answers.
//
// Returns 1 = lit, 0 = shadowed. Outside every cascade it returns 1: unshadowed is the honest answer
// where there is no data, and it is the one that does not draw a hard edge across the world.
float blix_cascade_contains(mat4 vp, vec3 world, out vec4 coord) {
    coord = vp * vec4(world, 1.0);
    vec3 ndc = coord.xyz / coord.w;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    // A margin, so a fragment is not handed to a cascade whose PCF kernel would reach off the edge
    // of the map and read whatever the clamp returns.
    const float EDGE = 0.02;
    if (uv.x < EDGE || uv.x > 1.0 - EDGE || uv.y < EDGE || uv.y > 1.0 - EDGE) return 0.0;
    if (ndc.z < 0.0 || ndc.z > 1.0) return 0.0;
    return 1.0;
}

// How many shadow texels the sample is pushed along the surface normal. Fixed, because it is
// measured in the one unit that already tracks how wrong a texel can be.
#define BLIX_SHADOW_OFFSET_TEXELS 4.0

// The sampling functions, in both forms: combined (sampler2D) and separate (texture2D + one sampler).
// See sampling_form.glsl for why both exist and how the two are spelled.
#include "sampling_form.glsl"
#include "shadow.sampled.glsl"
#define BLIX_SAMPLING_SEPARATE
#include "sampling_form.glsl"
#include "shadow.sampled.glsl"
#undef BLIX_SAMPLING_SEPARATE
#include "sampling_form.glsl"

// A flat tint per cascade, for looking at where the splits landed. Magenta means "beyond the
// last cascade", which is the case a still picture otherwise cannot distinguish from "lit".
vec3 blix_cascade_tint(int chosen) {
    if (chosen == 0) return vec3(1.0, 0.45, 0.45);
    if (chosen == 1) return vec3(0.45, 1.0, 0.45);
    if (chosen == 2) return vec3(0.45, 0.6, 1.0);
    return vec3(1.0, 0.2, 1.0);
}
