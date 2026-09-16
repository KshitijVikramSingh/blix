#pragma once

// Image-based lighting: the AMBIENT half of a physically-based surface.
//
// Split from pbr.glsl because this half needs textures. Everything in pbr.glsl is
// arithmetic a caller can use anywhere; this needs three bound resources — a diffuse
// irradiance cube, a GGX-prefiltered specular cube, and the split-sum BRDF LUT — and a
// file that mixes the two makes every consumer of the maths also declare samplers.
// shadow.glsl is separated from pbr.glsl for the same reason.
//
// <b>It composes, where pbr.glsl deliberately does not.</b> That file's header says it is a
// vocabulary rather than a turn-key "evaluate PBR" call, because the surrounding light state
// varies too much between consumers for one-shot evaluation to land. Ambient is the case where
// it does land: there is no light loop and no per-light state, so every consumer that has
// written this term has written the same seven lines. Two of them are in this tree today and
// they agree line for line.
//
// <b>What is NOT here: a prefilter LOD ceiling.</b> A demo carried one as
// `const float MAX_REFLECTION_LOD = 6.0`, which has to equal (prefilter mip count - 1) from a
// bake that happens in C# — a constant in GLSL that silently disagrees with a number in another
// language is a picture that is wrong and compiles. It is a parameter here, and the bake is what
// supplies it: EnvironmentProbe.PrefilteredSpecularMipCount - 1.

#include "pbr.glsl"

// The ambient term: diffuse irradiance plus the split-sum specular approximation.
//
//   N, V            unit normal and unit view vector (V points at the eye)
//   albedo          base colour, already multiplied by anything that modulates it
//   metallic        0 dielectric .. 1 conductor
//   roughness       perceptual, clamped away from 0 by the caller
//   F0              surface reflectance at normal incidence, mix(vec3(0.04), albedo, metallic)
//   irradiance      diffuse irradiance cube (cosine-convolved)
//   prefiltered     GGX-prefiltered specular cube; mip = roughness * maxReflectionLod
//   brdfLut         Karis split-sum LUT. R = scale, G = bias, sampled (NdotV, roughness)
//   maxReflectionLod  prefilter mip count - 1, FROM THE BAKE
//
// Returns radiance. The caller scales it — an ambient strength, an exposure, an occlusion term —
// rather than this doing it, because "how much environment reaches this surface" is a question
// about the scene and not about the integration.
vec3 blix_iblAmbient(
    vec3 N, vec3 V, vec3 albedo, float metallic, float roughness, vec3 F0,
    samplerCube irradiance, samplerCube prefiltered, sampler2D brdfLut,
    float maxReflectionLod)
{
    // Clamped, and it matters: blix_fresnelLazarov raises (1 - cosTheta) to the fifth, so a
    // negative cosine — a normal facing away from the eye, which interpolation and two-sided
    // geometry both produce — returns a value above one and the surface gains energy.
    float NdotV = clamp(dot(N, V), 0.0, 1.0);

    vec3 F = blix_fresnelLazarov(NdotV, F0, roughness);

    // Metals have no diffuse response, and what the specular lobe reflects is not also
    // available to scatter — hence (1 - F) as well as (1 - metallic).
    vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);
    vec3 diffuse = texture(irradiance, N).rgb * albedo;

    // The reflection vector, at the roughness-appropriate mip. A rough surface reads a blurrier
    // level of the same cube, which is what the prefilter baked those levels for.
    vec3 R = reflect(-V, N);
    vec3 specular = textureLod(prefiltered, R, roughness * maxReflectionLod).rgb;

    // Karis split-sum: the prefiltered radiance times the environment BRDF, stored as a scale
    // and a bias so one 2D lookup stands in for the second integral.
    vec2 ab = texture(brdfLut, vec2(NdotV, roughness)).rg;

    return kD * diffuse + specular * (F0 * ab.x + ab.y);
}
