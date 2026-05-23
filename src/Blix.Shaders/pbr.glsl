#pragma once

// Physically-based rendering primitives -- Cook-Torrance specular BRDF,
// Fresnel approximations, microfacet distribution and geometry terms.
// Direct lights, IBL evaluation, and the BRDF integration. Demo shaders
// combine these into their material pipeline; this file is the reusable
// vocabulary, not a turn-key "evaluate PBR" call (the surrounding light
// state varies too much between demos for one-shot evaluation to land).

#ifndef BLIX_PI
#define BLIX_PI 3.14159265359
#endif

// --- Microfacet distribution (D) ------------------------------------------
// Trowbridge-Reitz GGX. Models the orientation distribution of microfacets;
// rougher surfaces have a wider lobe (more scattered highlights), smoother
// surfaces a tighter one. `roughness` here is perceptual (linear) -- the
// `a = r*r` line below remaps to the "GGX alpha" the literature uses.
float blix_distributionGGX(float NdotH, float roughness)
{
    float a = roughness * roughness;
    float a2 = a * a;
    float denom = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / (BLIX_PI * denom * denom);
}

// --- Geometry (G) ---------------------------------------------------------
// Schlick-Beckmann (Smith) approximation, "k = (r+1)^2 / 8" remap that
// matches the direct-lighting case (vs k = a/2 for IBL). The Smith form
// products the half-vector and view-vector geometric shadowing terms.
float blix_geometrySchlickGGX(float NdotX, float k)
{
    return NdotX / (NdotX * (1.0 - k) + k);
}

float blix_geometrySmith(float NdotV, float NdotL, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return blix_geometrySchlickGGX(NdotV, k) * blix_geometrySchlickGGX(NdotL, k);
}

// --- Fresnel (F) ----------------------------------------------------------
// Schlick's polynomial approximation of Fresnel reflectance. Cheap, ubiquitous.
vec3 blix_fresnelSchlick(float cosTheta, vec3 F0)
{
    return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
}

// Lazarov 2013 "roughness-aware" Fresnel. For IBL evaluation where the
// reflection cone widens with roughness, the standard Schlick term over-
// shoots at grazing angles on rough surfaces (turns matte plaster into a
// white halo around silhouettes). This curves the saturation by 1 - r so
// rougher surfaces lose grazing reflection cleanly.
//
// Component-wise max because Apple's GLSL parser chokes on
// `max(vec3-from-scalar, vec3)` (see lit.frag history). Stays correct on
// all drivers, costs nothing.
vec3 blix_fresnelLazarov(float cosTheta, vec3 F0, float roughness)
{
    float invR = 1.0 - roughness;
    vec3 F90 = vec3(
        max(invR, F0.x),
        max(invR, F0.y),
        max(invR, F0.z));
    return F0 + (F90 - F0) * pow(1.0 - cosTheta, 5.0);
}

// --- BRDF integration -----------------------------------------------------
// Cook-Torrance specular BRDF lobe combined with Lambert diffuse. Returns
// the radiance the surface scatters back toward `V` from incoming light
// along `L` with radiance `radiance`. Caller multiplies by NdotL (geometric
// factor) -- not built in so the same function works for one-bounce GI
// integrators that handle the cosine separately.
vec3 blix_cookTorranceBrdf(
    vec3 N, vec3 V, vec3 L,
    vec3 albedo, vec3 F0, float metallic, float roughness)
{
    vec3 H = normalize(L + V);
    float NdotV = max(dot(N, V), 0.0);
    float NdotL = max(dot(N, L), 0.0);
    float NdotH = max(dot(N, H), 0.0);
    float HdotV = max(dot(H, V), 0.0);

    float D = blix_distributionGGX(NdotH, roughness);
    float G = blix_geometrySmith(NdotV, NdotL, roughness);
    vec3  F = blix_fresnelSchlick(HdotV, F0);

    vec3 spec = (D * G * F) / max(4.0 * NdotV * NdotL, 0.001);
    vec3 kD = (vec3(1.0) - F) * (1.0 - metallic);
    return kD * albedo / BLIX_PI + spec;
}

// --- Distance attenuation ------------------------------------------------
// Karis 2013 windowed inverse-square. Falls off cleanly to zero at the
// declared range without the discontinuity of a hard cutoff. Use range
// in world units; pass `dist` likewise.
float blix_punctualAttenuation(float dist, float range)
{
    float dr = dist / max(range, 0.0001);
    float window = clamp(1.0 - dr * dr * dr * dr, 0.0, 1.0);
    return window * window / max(dist * dist, 0.01);
}
