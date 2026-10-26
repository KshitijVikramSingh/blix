#pragma once

// The glTF surface model as a BSDF a path tracer evaluates and samples: GGX metal and dielectric with KHR_materials_ior, _specular, _transmission
// (the spec's thin-walled rule), _diffuse_transmission, and a SOLID mode (KHR_materials_volume: the transmitted share is a refracting rough interface,
// Walter et al. 2007, its transmission lobe the BTDF for radiance), with the diffuse lobe EON (diffuse_eon.glsl).
//
// --- THE REFERENCE IS PYTHON. -----------------------------------------------------------------------------------------------------------------
// lab/bsdf/bsdflab/gltf_bsdf.py and dielectric.py are the reference, held to the spec's claims and to their own sampling by lab/bsdf/tests; this file
// follows them line for line (the same names where a name carries over), and Blix.Test.Graphics's section on the BSDF holds a test-local C# twin
// against golden values the python writes (lab/bsdf/tools/generate_gltf_golden.py). Change the python first.
//
// The frame is the surface's, +z the shading normal, both directions pointing AWAY from the surface: wo toward the viewer (above, z > 0, for the material
// functions; the interface functions take either side), wi toward the light (below the surface, z < 0, is the transmitted side).
//
//   blix_gltfEval(m, wo, wi)                    f(wo, wi), rgb, without the cosine
//   blix_gltfPdf(m, wo, wi)                     the density blix_gltfSample draws wi at
//   blix_gltfSample(m, wo, seed, wi, weight)    draws wi; weight = f |cos| / pdf; false where the draw is no sample (it fell below the horizon, or
//                                               past total internal reflection)
//   blix_ifaceEval / blix_ifacePdf / blix_ifaceSample   the interface alone, for a path inside a solid (wo below the surface)
//
// `seed` is a PCG state advanced per random number (blix_bsdfRand), so a caller carries one uint.

#include "diffuse_eon.glsl"

#ifndef BLIX_PI
#define BLIX_PI 3.14159265359
#endif

struct BlixGltfMaterial {
    vec3 baseColor;
    float metallic;
    vec3 specularColor;     // KHR_materials_specular's colour factor
    float specular;         // and its factor: the dielectric's f90
    vec3 dtColor;           // KHR_materials_diffuse_transmission's colour
    float diffuseTransmission;
    float roughness;        // perceptual: alpha = roughness^2
    float ior;
    float transmission;
    float diffuseRoughness; // EON's roughness (0: Lambert)
    bool solid;             // KHR_materials_volume with a thickness: the transmitted share refracts
};

uint blix_bsdfHash(uint v) {
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

float blix_bsdfRand(inout uint s) {
    s = blix_bsdfHash(s);
    return float(s & 0xFFFFFFu) / 16777216.0;
}

float blix_bsdfAlpha(float roughness) { return max(roughness * roughness, 1e-3); }

// The thin sheet's lobe: the roughness scaled by the ior, then squared.
float blix_bsdfAlphaTransmission(float roughness, float ior) {
    float r = clamp(roughness * clamp(ior * 2.0 - 2.0, 0.0, 1.0), 0.0, 1.0);
    return max(r * r, 1e-3);
}

vec3 blix_bsdfF0Dielectric(BlixGltfMaterial m) {
    float f0 = (m.ior - 1.0) / (m.ior + 1.0);
    return min(f0 * f0 * m.specularColor, vec3(1.0));
}

vec3 blix_bsdfFresnelDielectric(BlixGltfMaterial m, float vh) {
    vec3 f0 = blix_bsdfF0Dielectric(m);
    float k = pow(1.0 - clamp(vh, 0.0, 1.0), 5.0);
    return f0 + (vec3(m.specular) - f0) * k;
}

vec3 blix_bsdfFresnelMetal(BlixGltfMaterial m, float vh) {
    float k = pow(1.0 - clamp(vh, 0.0, 1.0), 5.0);
    return m.baseColor + (vec3(1.0) - m.baseColor) * k;
}

float blix_bsdfD(float nh, float a) {
    float d = nh * nh * (a * a - 1.0) + 1.0;
    return a * a / (BLIX_PI * d * d);
}

float blix_bsdfLambda(float c, float a) {
    float c2 = max(c * c, 1e-12);
    return 0.5 * (sqrt(1.0 + a * a * (1.0 - c2) / c2) - 1.0);
}

float blix_bsdfG1(float nv, float a) { return 1.0 / (1.0 + blix_bsdfLambda(nv, a)); }
float blix_bsdfG2(float nl, float nv, float a) { return 1.0 / (1.0 + blix_bsdfLambda(nl, a) + blix_bsdfLambda(nv, a)); }

vec3 blix_bsdfMirror(vec3 wi) { return vec3(wi.xy, abs(wi.z)); }

// D G2 / (4 NL NV) for wo and wi both above the surface; 0 elsewhere.
float blix_bsdfSpecBrdf(vec3 wo, vec3 wi, float a) {
    vec3 h = normalize(wo + wi);
    float nl = wi.z, nv = wo.z, nh = h.z;
    if (!(nl > 0.0 && nv > 0.0 && nh > 0.0)) return 0.0;
    return blix_bsdfD(nh, a) * blix_bsdfG2(nl, nv, a) / (4.0 * nl * nv);
}

// ---- The interface (dielectric.py) --------------------------------------------------------------------------------------------------------

// Unpolarised dielectric Fresnel for light arriving at cosI from the medium etaI into etaT; 1 beyond the critical angle.
float blix_ifaceFresnel(float cosI, float etaI, float etaT) {
    cosI = clamp(cosI, 0.0, 1.0);
    float sin2T = (etaI / etaT) * (etaI / etaT) * (1.0 - cosI * cosI);
    if (sin2T >= 1.0) return 1.0;
    float cosT = sqrt(max(1.0 - sin2T, 0.0));
    float rs = (etaI * cosI - etaT * cosT) / max(etaI * cosI + etaT * cosT, 1e-12);
    float rp = (etaT * cosI - etaI * cosT) / max(etaT * cosI + etaI * cosT, 1e-12);
    return 0.5 * (rs * rs + rp * rp);
}

// The interface between air (etaOut, above) and the solid (etaIn, below); wo on either side. Returns f(wo, wi) with the transmitted part tinted.
vec3 blix_ifaceEval(float alpha, float etaOut, float etaIn, vec3 tint, vec3 wo, vec3 wi) {
    if (wo.z < 0.0) { wo = -wo; wi = -wi; float t = etaOut; etaOut = etaIn; etaIn = t; }
    float etaV = etaOut, etaOther = etaIn;
    bool refl = wi.z > 0.0;
    float etaL = refl ? etaV : etaOther;
    vec3 h;
    if (refl) h = normalize(wo + wi);
    else {
        h = -(etaL * wi + etaV * wo);
        h = h / max(length(h), 1e-12);
    }
    if (h.z < 0.0) h = -h;
    float nv = wo.z, nl = abs(wi.z), nh = h.z;
    float vh = dot(wo, h), lh = dot(wi, h);
    float d = blix_bsdfD(max(nh, 0.0), alpha);
    float gg = blix_bsdfG2(max(nl, 1e-9), max(nv, 1e-9), alpha);
    float f = blix_ifaceFresnel(vh, etaV, etaOther);
    if (refl) {
        if (!(vh > 0.0 && lh > 0.0 && nv > 0.0 && nh > 0.0)) return vec3(0.0);
        return vec3(f * d * gg / (4.0 * nv * max(nl, 1e-9)));
    }
    float denom = (etaL * lh + etaV * vh) * (etaL * lh + etaV * vh);
    if (!(vh > 0.0 && lh < 0.0 && nv > 0.0 && nh > 0.0 && denom > 1e-12)) return vec3(0.0);
    return tint * (abs(lh) * vh / (nv * max(nl, 1e-9)) * etaV * etaV * (1.0 - f) * gg * d / max(denom, 1e-12));
}

float blix_ifacePdf(float alpha, float etaOut, float etaIn, vec3 wo, vec3 wi) {
    if (wo.z < 0.0) { wo = -wo; wi = -wi; float t = etaOut; etaOut = etaIn; etaIn = t; }
    float etaV = etaOut, etaOther = etaIn;
    bool refl = wi.z > 0.0;
    float etaL = refl ? etaV : etaOther;
    vec3 h;
    if (refl) h = normalize(wo + wi);
    else {
        h = -(etaL * wi + etaV * wo);
        h = h / max(length(h), 1e-12);
    }
    if (h.z < 0.0) h = -h;
    float nv = max(wo.z, 1e-9), nh = h.z;
    float vh = dot(wo, h), lh = dot(wi, h);
    float f = blix_ifaceFresnel(vh, etaV, etaOther);
    float dv = blix_bsdfG1(nv, alpha) * blix_bsdfD(max(nh, 0.0), alpha) * max(vh, 0.0) / nv;
    if (refl) return (vh > 0.0 && lh > 0.0) ? f * dv / max(4.0 * vh, 1e-12) : 0.0;
    float denom = (etaL * lh + etaV * vh) * (etaL * lh + etaV * vh);
    return (vh > 0.0 && lh < 0.0 && denom > 1e-12) ? (1.0 - f) * dv * etaL * etaL * abs(lh) / max(denom, 1e-12) : 0.0;
}

// Heitz 2018: a microfacet normal from the distribution of normals visible from wo.
vec3 blix_bsdfSampleVndf(vec3 wo, float a, inout uint seed) {
    vec3 vh = normalize(vec3(a * wo.x, a * wo.y, wo.z));
    float lensq = vh.x * vh.x + vh.y * vh.y;
    vec3 t1 = lensq > 0.0 ? vec3(-vh.y, vh.x, 0.0) / sqrt(max(lensq, 1e-20)) : vec3(1.0, 0.0, 0.0);
    vec3 t2 = cross(vh, t1);
    float u1 = blix_bsdfRand(seed), u2 = blix_bsdfRand(seed);
    float r = sqrt(u1);
    float phi = 2.0 * BLIX_PI * u2;
    float p1 = r * cos(phi), p2 = r * sin(phi);
    float s = 0.5 * (1.0 + vh.z);
    p2 = (1.0 - s) * sqrt(max(0.0, 1.0 - p1 * p1)) + s * p2;
    vec3 nh = p1 * t1 + p2 * t2 + sqrt(max(0.0, 1.0 - p1 * p1 - p2 * p2)) * vh;
    return normalize(vec3(a * nh.x, a * nh.y, max(0.0, nh.z)));
}

vec3 blix_bsdfCosine(inout uint seed) {
    float u1 = blix_bsdfRand(seed), u2 = blix_bsdfRand(seed);
    float r = sqrt(u1), phi = 2.0 * BLIX_PI * u2;
    return vec3(r * cos(phi), r * sin(phi), sqrt(max(0.0, 1.0 - u1)));
}

// A direction for the interface and whether it is one (a reflection beyond the horizon, a refraction past total internal reflection, is not).
bool blix_ifaceSampleDirection(float alpha, float etaOut, float etaIn, vec3 wo, inout uint seed, out vec3 wi) {
    float flipS = wo.z < 0.0 ? -1.0 : 1.0;
    vec3 wf = wo * flipS;
    float etaV = wo.z < 0.0 ? etaIn : etaOut;
    float etaO = wo.z < 0.0 ? etaOut : etaIn;
    vec3 h = blix_bsdfSampleVndf(wf, alpha, seed);
    float vh = dot(wf, h);
    float f = blix_ifaceFresnel(vh, etaV, etaO);
    bool reflect = blix_bsdfRand(seed) < f;
    vec3 wr = 2.0 * vh * h - wf;
    float k = etaV / etaO;
    float sin2 = k * k * (1.0 - vh * vh);
    float ct = sqrt(max(1.0 - sin2, 0.0));
    vec3 wt = -k * wf + (k * vh - ct) * h;
    wi = (reflect ? wr : wt) * flipS;
    return reflect ? wr.z > 0.0 : (wt.z < 0.0 && sin2 < 1.0);
}

// ---- The material (gltf_bsdf.py) ----------------------------------------------------------------------------------------------------------

bool blix_gltfIsSolid(BlixGltfMaterial m) { return m.solid && m.transmission > 0.0; }

vec3 blix_gltfEval(BlixGltfMaterial m, vec3 wo, vec3 wi) {
    float a = blix_bsdfAlpha(m.roughness);
    float aT = blix_bsdfAlphaTransmission(m.roughness, m.ior);
    vec3 wm = blix_bsdfMirror(wi);
    vec3 h = normalize(wo + wm);
    float vh = dot(wo, h);
    bool below = wi.z < 0.0;
    vec3 fr = blix_bsdfFresnelDielectric(m, vh);
    float t = m.transmission, dt = m.diffuseTransmission;
    bool solid = blix_gltfIsSolid(m);
    vec3 fi = vec3(0.0);
    if (solid) fi = blix_ifaceEval(a, 1.0, m.ior, m.baseColor, wo, wi);

    if (!below) {
        vec3 spec = vec3(blix_bsdfSpecBrdf(wo, wi, a));
        vec3 diffuse = blix_eonBrdf(m.baseColor, m.diffuseRoughness, wi.z, wo.z, dot(wi, wo));
        vec3 dielectric = fr * spec + (vec3(1.0) - fr) * ((1.0 - t) * (1.0 - dt) * diffuse);
        if (solid) dielectric = (1.0 - t) * (fr * spec + (vec3(1.0) - fr) * ((1.0 - dt) * diffuse)) + t * fi;
        vec3 metal = blix_bsdfFresnelMetal(m, vh) * spec;
        return (1.0 - m.metallic) * dielectric + m.metallic * metal;
    }
    vec3 btdf = vec3(blix_bsdfSpecBrdf(wo, wm, aT)) * m.baseColor * t;
    vec3 dtl = m.dtColor * ((1.0 - t) * dt / BLIX_PI);
    vec3 down = (1.0 - m.metallic) * (vec3(1.0) - fr) * (btdf + dtl);
    if (solid) down = (1.0 - m.metallic) * ((vec3(1.0) - fr) * dtl + t * fi);
    return down;
}

// The five lobes blix_gltfSample picks among: specular reflection, diffuse reflection, thin transmission, diffuse transmission, the solid's interface.
void blix_gltfLobes(BlixGltfMaterial m, out float p[5]) {
    float t = m.transmission, dt = m.diffuseTransmission, k = m.metallic;
    bool solid = blix_gltfIsSolid(m);
    p[0] = (solid ? 0.2 * (1.0 - t) : 0.2) + 0.8 * k;
    p[1] = (1.0 - k) * (1.0 - t) * (1.0 - dt);
    p[2] = solid ? 0.0 : (1.0 - k) * t;
    p[3] = (1.0 - k) * (1.0 - t) * dt;
    p[4] = solid ? (1.0 - k) * t : 0.0;
    float sum = p[0] + p[1] + p[2] + p[3] + p[4];
    for (int i = 0; i < 5; ++i) p[i] /= sum;
}

// The density of the visible-normal sampled reflection: G1(wo) D(h) / (4 NV).
float blix_bsdfVndfPdf(vec3 wo, vec3 wiReflect, float a) {
    vec3 h = normalize(wo + wiReflect);
    float nv = max(wo.z, 1e-9);
    return blix_bsdfG1(nv, a) * blix_bsdfD(max(h.z, 0.0), a) / (4.0 * nv);
}

float blix_gltfPdf(BlixGltfMaterial m, vec3 wo, vec3 wi) {
    float p[5];
    blix_gltfLobes(m, p);
    float a = blix_bsdfAlpha(m.roughness);
    float aT = blix_bsdfAlphaTransmission(m.roughness, m.ior);
    vec3 wm = blix_bsdfMirror(wi);
    float cosine = abs(wi.z) / BLIX_PI;
    float pdf = wi.z < 0.0 ? p[2] * blix_bsdfVndfPdf(wo, wm, aT) + p[3] * cosine : p[0] * blix_bsdfVndfPdf(wo, wm, a) + p[1] * cosine;
    if (p[4] > 0.0) pdf += p[4] * blix_ifacePdf(a, 1.0, m.ior, wo, wi);
    return pdf;
}

bool blix_gltfSample(BlixGltfMaterial m, vec3 wo, inout uint seed, out vec3 wi, out vec3 weight) {
    float p[5];
    blix_gltfLobes(m, p);
    float a = blix_bsdfAlpha(m.roughness);
    float aT = blix_bsdfAlphaTransmission(m.roughness, m.ior);
    float u = blix_bsdfRand(seed);
    int lobe = 4;
    float acc = 0.0;
    for (int i = 0; i < 5; ++i) {
        acc += p[i];
        if (u < acc) { lobe = i; break; }
    }
    if (p[lobe] <= 0.0) { for (int i = 4; i >= 0; --i) if (p[i] > 0.0) { lobe = i; break; } }
    bool valid = true;
    wi = vec3(0.0, 0.0, 1.0);
    if (lobe == 0 || lobe == 2) {
        vec3 h = blix_bsdfSampleVndf(wo, lobe == 0 ? a : aT, seed);
        vec3 r = 2.0 * dot(wo, h) * h - wo;
        valid = r.z > 0.0;
        wi = lobe == 2 ? r * vec3(1.0, 1.0, -1.0) : r;
    } else if (lobe == 1) {
        wi = blix_bsdfCosine(seed);
    } else if (lobe == 3) {
        wi = blix_bsdfCosine(seed) * vec3(1.0, 1.0, -1.0);
    } else {
        valid = blix_ifaceSampleDirection(a, 1.0, m.ior, wo, seed, wi);
    }
    float d = blix_gltfPdf(m, wo, wi);
    vec3 f = blix_gltfEval(m, wo, wi);
    weight = (d > 0.0 && valid) ? f * abs(wi.z) / max(d, 1e-30) : vec3(0.0);
    return valid && d > 0.0;
}
