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

/// Fraction of the sky visible from `worldPos` looking along `dir`, in [0,1].
///
/// `normalPush` moves the lookup a little along `dir` before sampling: a cell straddling a wall
/// holds both sides of it, and a query taken exactly on the surface reads the enclosure on the
/// wrong side. It is the same failure as shadow acne and the same remedy.
float blix_skyVisibility(
    sampler3D shA, sampler3D shB, sampler3D shC,
    vec3 boundsMin, vec3 invSpan, float normalPush,
    vec3 worldPos, vec3 dir)
{
    vec3 uv = clamp((worldPos + dir * normalPush - boundsMin) * invSpan, vec3(0.0), vec3(1.0));
    vec4 sh0 = texture(shA, uv);
    vec4 sh1 = texture(shB, uv);
    float l2p2 = texture(shC, uv).x;

    // Cosine-convolved L2 evaluation (Ramamoorthi & Hanrahan): band coefficients pi, 2pi/3, pi/4.
    // <b>The quadratic band is what makes a cone expressible.</b> L0 is direction-independent, so
    // under L1 alone a vault ceiling inherited the arcade opening's brightness and read as
    // sky-facing — one linear lobe cannot subtract a bright opening from a surface pointing away
    // from it. The same deficit at the other end lost a courtyard floor's narrow zenith cone.
    const float Y0 = 0.282095, Y1 = 0.488603, Y2 = 1.092548, Y20C = 0.315392, Y22C = 0.546274;
    float band2 = Y2 * sh1.x * dir.x * dir.y
                + Y2 * sh1.y * dir.y * dir.z
                + Y20C * sh1.z * (3.0 * dir.z * dir.z - 1.0)
                + Y2 * sh1.w * dir.x * dir.z
                + Y22C * l2p2 * (dir.x * dir.x - dir.y * dir.y);
    return clamp((BLIX_SKYVIS_PI * Y0 * sh0.x
                  + (2.0 * BLIX_SKYVIS_PI / 3.0) * Y1 * dot(sh0.yzw, dir)
                  + (BLIX_SKYVIS_PI / 4.0) * band2) / BLIX_SKYVIS_PI, 0.0, 1.0);
}

/// Mean sky visibility over the WHOLE sphere, for a point inside a participating medium.
///
/// <b>A froxel has no normal, so the cosine form above does not apply to it.</b> What a point in
/// air scatters toward the eye is sky arriving from every direction at once, not sky arriving over
/// a hemisphere weighted by a surface it does not have. The spherical mean of an SH expansion is
/// its L0 coefficient alone — every higher band integrates to zero over the sphere — so this costs
/// one texel and one multiply, and needs neither of the other two band textures.
float blix_skyVisibilityMean(sampler3D shA, vec3 boundsMin, vec3 invSpan, vec3 worldPos)
{
    vec3 uv = clamp((worldPos - boundsMin) * invSpan, vec3(0.0), vec3(1.0));
    return clamp(texture(shA, uv).x * 0.282095, 0.0, 1.0);
}
