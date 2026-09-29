#pragma once

// Evaluating the baked sky-visibility volume: how much of the sky a point can see, in a direction.
//
// The volume holds a cosine-convolvable L2 spherical-harmonic expansion of the visibility function
// per cell — L0 plus three L1 plus five L2 coefficients across three RGBA textures. The bake side
// (SkyVisibilityBaker) projects and windows; this is the only place it is read.
//
// <b>One copy, because two drifted.</b> The evaluation used to live inline in lit.frag and any
// second reader would have had to re-derive the band constants, the cosine convolution and the
// normal push — three things that are correct only together. It now has two readers (the lit pass
// for glass, the half-res indirect pass for everything else), which is exactly the moment the
// duplicate would have been made.

#ifndef BLIX_SKYVIS_PI
#define BLIX_SKYVIS_PI 3.14159265359
#endif

/// The nine coefficients at a point, fetched once.
///
/// <b>Because the fetch is the cost and the direction is not.</b> A query is three 3D-texture taps
/// followed by about a dozen multiply-adds, and a surface that asks about two directions at the same
/// POSITION — a thin sheet, wanting the sky on each side — was paying for six taps to use three.
/// Splitting the lookup lets it fetch once and evaluate twice, which is exact rather than an
/// approximation of the second answer from the first.
struct BlixSkySample { vec4 sh0; vec4 sh1; float l2p2; };

/// Evaluates a fetched sample along `dir`; see blix_skyVisibility for the band constants.
float blix_skyEvaluate(BlixSkySample s, vec3 dir)
{
    const float Y0 = 0.282095, Y1 = 0.488603, Y2 = 1.092548, Y20C = 0.315392, Y22C = 0.546274;
    float band2 = Y2 * s.sh1.x * dir.x * dir.y
                + Y2 * s.sh1.y * dir.y * dir.z
                + Y20C * s.sh1.z * (3.0 * dir.z * dir.z - 1.0)
                + Y2 * s.sh1.w * dir.x * dir.z
                + Y22C * s.l2p2 * (dir.x * dir.x - dir.y * dir.y);
    return clamp((BLIX_SKYVIS_PI * Y0 * s.sh0.x
                  + (2.0 * BLIX_SKYVIS_PI / 3.0) * Y1 * dot(s.sh0.yzw, dir)
                  + (BLIX_SKYVIS_PI / 4.0) * band2) / BLIX_SKYVIS_PI, 0.0, 1.0);
}

/// Bands 0 and 1 only, from the four coefficients that fit in one vec4.
///
/// <b>Kept for the diagnostic that priced L2, not for a caller.</b> sh0 IS (L0, L1x, L1y, L1z), so
/// the obvious way to move this term to half resolution is to carry those four numbers and evaluate
/// them at full resolution in the shading normal. That was built and measured and it lost — 3.69
/// mean sRGB against 2.73 for evaluating coarsely — because interpolating coefficients and then
/// evaluating is worse than interpolating the evaluated scalar, which is clamped and
/// low-dynamic-range where the coefficients are neither. L2, the band it gives up to make room, is
/// worth only 0.36. See incident.frag: the error is in WHERE the volume was sampled, not in which
/// direction it was evaluated.
float blix_skyEvaluateL1(vec4 sh0, vec3 dir)
{
    const float Y0 = 0.282095, Y1 = 0.488603;
    return clamp((BLIX_SKYVIS_PI * Y0 * sh0.x
                  + (2.0 * BLIX_SKYVIS_PI / 3.0) * Y1 * dot(sh0.yzw, dir)) / BLIX_SKYVIS_PI,
                 0.0, 1.0);
}

// The sampling functions, in both forms: combined (sampler2D) and separate (texture2D + one sampler).
// See sampling_form.glsl for why both exist and how the two are spelled.
#include "sampling_form.glsl"
#include "sky_visibility.sampled.glsl"
#define BLIX_SAMPLING_SEPARATE
#include "sampling_form.glsl"
#include "sky_visibility.sampled.glsl"
#undef BLIX_SAMPLING_SEPARATE
#include "sampling_form.glsl"
