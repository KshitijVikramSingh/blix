#pragma once

// EON: the energy-preserving Oren-Nayar diffuse BRDF, in place of the fixed Lambert lobe `albedo / pi`.
//
//   Portsmouth, Kutz, Hill. "EON: A practical energy-preserving rough diffuse BRDF." Journal of Computer Graphics Techniques 14(1), 2025
//   (arXiv 2410.18026); reference GLSL at github.com/portsmouth/EON-diffuse. The formulas and every constant below are the paper's.
//
// Vocabulary, not a turn-key evaluator, like pbr.glsl: it answers the diffuse lobe's two questions and nothing else, and leaves the
// energy split with the specular layer (kD = (1 - F)(1 - metallic)) to the caller, who already has it.
//
//   blix_eonBrdf(rho, r, NdotL, NdotV, LdotV)   f(wi, wo): the lobe for a light from a KNOWN direction (the sun). Multiply by
//                                                NdotL and the light's irradiance, as a Lambert lobe is.
//   blix_eonAlbedo(rho, r, NdotV)               the albedo the lobe has toward the eye, for light arriving from everywhere equally
//                                                (an irradiance cache, the sky): radiance out = E / pi * blix_eonAlbedo(...), where
//                                                Lambert has E / pi * rho. This is the exact answer for a cosine-distributed field,
//                                                and the only one an irradiance (a quantity with no direction) can ask for.
//
//   blix_eonHemisphericalAlbedo(rho, r)         the albedo averaged over incident directions too (cosine-weighted): the fraction of the light
//                                                arriving at a patch from anywhere that leaves it. A transport that bounces light patch to patch
//                                                (a radiosity solve, a gather's hits) conserves THIS, not rho: baked into the rays' surface word
//                                                (ray_surface_bake.comp), so every bounce loses what the roughness loses.
//
//   rho     albedo, per channel.        r       the diffuse roughness, 0..1. r = 0 IS Lambert, exactly (rho / pi, rho).
//   NdotL, NdotV  cosines to the shading normal; LdotV the cosine between light and eye. The frame needs no tangent: the model is
//                 isotropic, and s = wi.wo - mu_i mu_o is the dot of the two directions projected into the surface's plane.
//
// --- THE REFERENCE IS PYTHON, AND THERE IS A TEST-LOCAL C# TWIN. -----------------------------------------------------------
// lab/bsdf/bsdflab/eon.py is the reference; it is held to the paper's claims (Lambert at r = 0, reciprocity, a white surface conserves
// energy at every roughness) by lab/bsdf/tests/test_eon.py, and this file is held to it per pixel by lab/bsdf/tests/test_eon_gpu.py
// (the TonemapLab demo's diffuse-table pattern renders this code's answers). Blix.Test.Graphics Section BH carries a test-local C#
// twin against golden values the python generates, and fails when a constant here stops matching it.

#ifndef BLIX_PI
#define BLIX_PI 3.14159265359
#endif

const float BLIX_EON_C1 = 0.5 - 2.0 / (3.0 * BLIX_PI);               // constant1_FON
const float BLIX_EON_C2 = 2.0 / 3.0 - 28.0 / (15.0 * BLIX_PI);       // constant2_FON
const float BLIX_EON_G1 = 0.0571085289;                               // G(mu) / pi ~ sum g_k (1 - mu)^k, k = 1..4 (Eq. 14)
const float BLIX_EON_G2 = 0.491881867;
const float BLIX_EON_G3 = -0.332181442;
const float BLIX_EON_G4 = 0.0714429953;

// The single-scatter directional albedo per unit rho: A (1 + r G(mu) / pi).
float blix_eonE(float mu, float r)
{
    float m = 1.0 - clamp(mu, 0.0, 1.0);
    float gOverPi = m * (BLIX_EON_G1 + m * (BLIX_EON_G2 + m * (BLIX_EON_G3 + m * BLIX_EON_G4)));
    return (1.0 + r * gOverPi) / (1.0 + BLIX_EON_C1 * r);
}

// <E>: E averaged over the hemisphere, cosine-weighted.
float blix_eonAvgE(float r)
{
    return (1.0 + BLIX_EON_C2 * r) / (1.0 + BLIX_EON_C1 * r);
}

// f_EON(wi, wo) = f_F + f_F^ms, per channel.
vec3 blix_eonBrdf(vec3 rho, float r, float NdotL, float NdotV, float LdotV)
{
    float muI = clamp(NdotL, 0.0, 1.0);
    float muO = clamp(NdotV, 1e-4, 1.0);
    float s = LdotV - muI * muO;
    float sOverT = s > 0.0 ? s / max(max(muI, muO), 1e-7) : s;
    float a = 1.0 / (1.0 + BLIX_EON_C1 * r);
    vec3 fSs = (rho / BLIX_PI) * a * (1.0 + r * sOverT);

    float avgE = blix_eonAvgE(r);
    vec3 rhoMs = (rho * rho) * avgE / (vec3(1.0) - rho * (1.0 - avgE));
    float shape = max(1e-7, 1.0 - blix_eonE(muI, r)) * max(1e-7, 1.0 - blix_eonE(muO, r)) / max(1e-7, 1.0 - avgE);
    return fSs + (rhoMs / BLIX_PI) * shape;
}

// E_EON(mu_o) = rho E + rho_ms (1 - E) (Eq. 20), per channel.
vec3 blix_eonAlbedo(vec3 rho, float r, float NdotV)
{
    float muO = clamp(NdotV, 1e-4, 1.0);
    float e = blix_eonE(muO, r);
    float avgE = blix_eonAvgE(r);
    vec3 rhoMs = (rho * rho) * avgE / (vec3(1.0) - rho * (1.0 - avgE));
    return rho * e + rhoMs * (1.0 - e);
}

// rho <E> + rho_ms (1 - <E>): the cosine-weighted average of blix_eonAlbedo over the view direction. Lambert's is rho; EON's is lower for
// rho < 1 and rho itself at rho = 1.
vec3 blix_eonHemisphericalAlbedo(vec3 rho, float r)
{
    float avgE = blix_eonAvgE(r);
    vec3 rhoMs = (rho * rho) * avgE / (vec3(1.0) - rho * (1.0 - avgE));
    return rho * avgE + rhoMs * (1.0 - avgE);
}
