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
// The layers over it, each following the python (gltf_bsdf.py): clearcoat (a smooth GGX layer, the base darkened by its reflectance toward the view), sheen
// (Charlie, the engine's sheen.glsl rule, its directional albedo by the same midpoint grid as the python), iridescence (the thin film's Fresnel, Belcour and Barla as
// the Khronos viewer has it) and anisotropy (GGX with two roughnesses along the rotated tangent frame's axes). Emission is the caller's; the nested solids are
// `etaOut`, the index of the medium the surface is entered from.
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
    vec3 sheenColor;        // KHR_materials_sheen
    float sheenRoughness;
    float clearcoat;        // KHR_materials_clearcoat
    float clearcoatRoughness;
    float iridescence;      // KHR_materials_iridescence: the factor, the film's ior and thickness in nm
    float iridescenceIor;
    float iridescenceThickness;
    float anisotropy;       // KHR_materials_anisotropy: strength and rotation from the tangent (x of the frame)
    float anisotropyRotation;
    float etaOut;           // the index of the medium this surface is entered from (1: air)
};

// A material with every layer off, the ior 1.5 default, in air.
BlixGltfMaterial blix_gltfDefaultMaterial() {
    BlixGltfMaterial m;
    m.baseColor = vec3(1.0); m.metallic = 0.0; m.specularColor = vec3(1.0); m.specular = 1.0;
    m.dtColor = vec3(1.0); m.diffuseTransmission = 0.0; m.roughness = 0.5; m.ior = 1.5; m.transmission = 0.0; m.diffuseRoughness = 0.0; m.solid = false;
    m.sheenColor = vec3(0.0); m.sheenRoughness = 0.0; m.clearcoat = 0.0; m.clearcoatRoughness = 0.0;
    m.iridescence = 0.0; m.iridescenceIor = 1.3; m.iridescenceThickness = 400.0; m.anisotropy = 0.0; m.anisotropyRotation = 0.0; m.etaOut = 1.0;
    return m;
}

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

float blix_bsdfAlphaCoat(float roughness) { return max(roughness * roughness, 1e-3); }
float blix_bsdfAlphaSheen(float roughness) { return max(roughness * roughness, 1e-4); }

// KHR_materials_specular: min(((ior - 1) / (ior + 1))^2 specularColor, 1) specular -- the factor scales f0 as it scales f90.
vec3 blix_bsdfF0Dielectric(BlixGltfMaterial m) {
    float f0 = (m.ior - 1.0) / (m.ior + 1.0);
    return min(f0 * f0 * m.specularColor, vec3(1.0)) * m.specular;
}

// ---- The thin film (gltf_bsdf.py's iridescence_fresnel) ----------------------------------------------------------------------------------------

vec3 blix_bsdfSensitivity(float opd, vec3 shift) {
    float phase = 2.0 * BLIX_PI * opd * 1e-9;
    const vec3 val = vec3(5.4856e-13, 4.4201e-13, 5.2481e-13);
    const vec3 pos = vec3(1.6810e+06, 1.7953e+06, 2.2084e+06);
    const vec3 var = vec3(4.3278e+09, 9.3046e+09, 6.6121e+09);
    vec3 xyz = val * sqrt(2.0 * BLIX_PI * var) * cos(pos * phase + shift) * exp(-var * phase * phase);
    xyz.x += 9.7470e-14 * sqrt(2.0 * BLIX_PI * 4.5282e+09) * cos(2.2399e+06 * phase + shift.x) * exp(-4.5282e+09 * phase * phase);
    xyz /= 1.0685e-7;
    const mat3 xyzToRec709 = mat3(3.2404542, -0.9692660, 0.0556434, -1.5371385, 1.8760108, -0.2040259, -0.4985314, 0.0415560, 1.0572252);
    return xyzToRec709 * xyz;
}

vec3 blix_bsdfIridescenceFresnel(float outsideIor, float eta2, float cos1, float thickness, vec3 baseF0) {
    cos1 = clamp(cos1, 0.0, 1.0);
    float iriIor = mix(outsideIor, eta2, smoothstep(0.0, 0.03, thickness));
    float sin2T = (outsideIor / iriIor) * (outsideIor / iriIor) * (1.0 - cos1 * cos1);
    float cos2Sq = 1.0 - sin2T;
    if (cos2Sq < 0.0) return vec3(1.0);
    float cos2 = sqrt(cos2Sq);
    float r0 = (iriIor - outsideIor) / (iriIor + outsideIor);
    r0 *= r0;
    float r12 = r0 + (1.0 - r0) * pow(1.0 - cos1, 5.0);
    float t121 = 1.0 - r12;
    float phi12 = iriIor < outsideIor ? BLIX_PI : 0.0;
    float phi21 = BLIX_PI - phi12;
    vec3 f0 = clamp(baseF0, vec3(0.0), vec3(0.9999));
    vec3 baseIor = (vec3(1.0) + sqrt(f0)) / (vec3(1.0) - sqrt(f0));
    vec3 r1 = (baseIor - iriIor) / (baseIor + iriIor);
    r1 *= r1;
    vec3 r23 = r1 + (vec3(1.0) - r1) * pow(1.0 - cos2, 5.0);
    vec3 phi23 = vec3(baseIor.x < iriIor ? BLIX_PI : 0.0, baseIor.y < iriIor ? BLIX_PI : 0.0, baseIor.z < iriIor ? BLIX_PI : 0.0);
    float opd = 2.0 * iriIor * thickness * cos2;
    vec3 phi = vec3(phi21) + phi23;
    vec3 r123 = clamp(r12 * r23, vec3(1e-5), vec3(0.9999));
    vec3 sq = sqrt(r123);
    vec3 rs = t121 * t121 * r23 / (vec3(1.0) - r123);
    vec3 i = vec3(r12) + rs;
    vec3 cm = rs - vec3(t121);
    for (int k = 1; k <= 2; ++k) {
        cm *= sq;
        i += cm * 2.0 * blix_bsdfSensitivity(float(k) * opd, float(k) * phi);
    }
    return max(i, vec3(0.0));
}

// (the dielectric's Fresnel, the metal's): KHR_materials_specular's Schlick and the base colour's, each replaced by the film's stack in proportion to iridescence.
void blix_bsdfFresnels(BlixGltfMaterial m, float vh, out vec3 fd, out vec3 fm) {
    vh = clamp(vh, 0.0, 1.0);
    float k = pow(1.0 - vh, 5.0);
    vec3 f0 = blix_bsdfF0Dielectric(m);
    fd = f0 + (vec3(m.specular) - f0) * k;
    fm = m.baseColor + (vec3(1.0) - m.baseColor) * k;
    if (m.iridescence > 0.0) {
        fd = (1.0 - m.iridescence) * fd + m.iridescence * blix_bsdfIridescenceFresnel(1.0, m.iridescenceIor, vh, m.iridescenceThickness, f0);
        fm = (1.0 - m.iridescence) * fm + m.iridescence * blix_bsdfIridescenceFresnel(1.0, m.iridescenceIor, vh, m.iridescenceThickness, m.baseColor);
    }
}

vec3 blix_bsdfFresnelDielectric(BlixGltfMaterial m, float vh) {
    vec3 fd, fm;
    blix_bsdfFresnels(m, vh, fd, fm);
    return fd;
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

// v with x and y turned by -r: the frame whose x axis is the anisotropy direction.
vec3 blix_bsdfRot(vec3 v, float r) {
    float c = cos(r), s = sin(r);
    return vec3(v.x * c + v.y * s, -v.x * s + v.y * c, v.z);
}

float blix_bsdfAnisoD(vec3 h, float ax, float ay, float rot) {
    vec3 hr = blix_bsdfRot(h, rot);
    float d = (hr.x / ax) * (hr.x / ax) + (hr.y / ay) * (hr.y / ay) + hr.z * hr.z;
    return 1.0 / (BLIX_PI * ax * ay * d * d);
}

float blix_bsdfAnisoLambda(vec3 w, float ax, float ay, float rot) {
    vec3 wr = blix_bsdfRot(w, rot);
    float c2 = max(w.z * w.z, 1e-12);
    return 0.5 * (sqrt(1.0 + (ax * ax * wr.x * wr.x + ay * ay * wr.y * wr.y) / c2) - 1.0);
}

// D G2 / (4 NL NV) for wo and wi both above the surface; 0 elsewhere. Anisotropic: alphas ax along the rotated x axis, ay across it.
float blix_bsdfSpecBrdfA(vec3 wo, vec3 wi, float ax, float ay, float rot) {
    vec3 h = normalize(wo + wi);
    float nl = wi.z, nv = wo.z, nh = h.z;
    if (!(nl > 0.0 && nv > 0.0 && nh > 0.0)) return 0.0;
    float g = 1.0 / (1.0 + blix_bsdfAnisoLambda(wo, ax, ay, rot) + blix_bsdfAnisoLambda(wi, ax, ay, rot));
    return blix_bsdfAnisoD(h, ax, ay, rot) * g / (4.0 * nl * nv);
}

float blix_bsdfSpecBrdf(vec3 wo, vec3 wi, float a) { return blix_bsdfSpecBrdfA(wo, wi, a, a, 0.0); }

// The specular lobe's two roughnesses: along the anisotropy direction and across it.
vec2 blix_bsdfAlphas(BlixGltfMaterial m) {
    float a = blix_bsdfAlpha(m.roughness);
    return vec2(a + (1.0 - a) * m.anisotropy * m.anisotropy, a);
}

// ---- Sheen (sheen.glsl's Charlie and Ashikhmin terms) ------------------------------------------------------------------------------------------

float blix_bsdfCharlieD(float nh, float alpha) {
    float sin2h = max(1.0 - nh * nh, 1e-7);
    return (2.0 + 1.0 / alpha) * pow(sin2h, 0.5 / alpha) / (2.0 * BLIX_PI);
}

float blix_bsdfSheenV(float nl, float nv) { return clamp(1.0 / (4.0 * (nl + nv - nl * nv)), 0.0, 1.0); }

// E(NV, roughness): the lobe's directional albedo, a midpoint grid of 24 cos(theta) x 16 phi cells (SHEEN_QUADRATURE in the python).
float blix_bsdfSheenAlbedo(float muO, float sheenRoughness) {
    float alpha = blix_bsdfAlphaSheen(sheenRoughness);
    vec3 wo = vec3(sqrt(max(1.0 - muO * muO, 0.0)), 0.0, muO);
    float sum = 0.0;
    for (int i = 0; i < 24; ++i) {
        float mu = (float(i) + 0.5) / 24.0;
        float st = sqrt(1.0 - mu * mu);
        for (int j = 0; j < 16; ++j) {
            float phi = (float(j) + 0.5) / 16.0 * 2.0 * BLIX_PI;
            vec3 wi = vec3(st * cos(phi), st * sin(phi), mu);
            vec3 h = normalize(wo + wi);
            sum += blix_bsdfCharlieD(h.z, alpha) * blix_bsdfSheenV(mu, muO) * mu;
        }
    }
    return sum * (1.0 / 24.0) * (2.0 * BLIX_PI / 16.0);
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
vec3 blix_bsdfSampleVndfA(vec3 woIn, float ax, float ay, float rot, inout uint seed) {
    vec3 wo = blix_bsdfRot(woIn, rot);
    vec3 vh = normalize(vec3(ax * wo.x, ay * wo.y, wo.z));
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
    return blix_bsdfRot(normalize(vec3(ax * nh.x, ay * nh.y, max(0.0, nh.z))), -rot);
}

vec3 blix_bsdfSampleVndf(vec3 wo, float a, inout uint seed) { return blix_bsdfSampleVndfA(wo, a, a, 0.0, seed); }

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
    vec2 al = blix_bsdfAlphas(m);
    float a = blix_bsdfAlpha(m.roughness);
    float aT = blix_bsdfAlphaTransmission(m.roughness, m.ior);
    vec3 wm = blix_bsdfMirror(wi);
    vec3 h = normalize(wo + wm);
    float vh = dot(wo, h);
    bool below = wi.z < 0.0;
    vec3 fr, fmetal;
    blix_bsdfFresnels(m, vh, fr, fmetal);
    float t = m.transmission, dt = m.diffuseTransmission;
    bool solid = blix_gltfIsSolid(m);
    vec3 fi = vec3(0.0);
    if (solid) fi = blix_ifaceEval(a, m.etaOut, m.ior, m.baseColor, wo, wi);

    vec3 base;
    if (!below) {
        vec3 spec = vec3(blix_bsdfSpecBrdfA(wo, wi, al.x, al.y, m.anisotropyRotation));
        vec3 diffuse = blix_eonBrdf(m.baseColor, m.diffuseRoughness, wi.z, wo.z, dot(wi, wo));
        vec3 dielectric = fr * spec + (vec3(1.0) - fr) * ((1.0 - t) * (1.0 - dt) * diffuse);
        if (solid) dielectric = (1.0 - t) * (fr * spec + (vec3(1.0) - fr) * ((1.0 - dt) * diffuse)) + t * fi;
        vec3 metal = fmetal * spec;
        base = (1.0 - m.metallic) * dielectric + m.metallic * metal;
    } else {
        vec3 btdf = vec3(blix_bsdfSpecBrdf(wo, wm, aT)) * m.baseColor * t;
        vec3 dtl = m.dtColor * ((1.0 - t) * dt / BLIX_PI);
        base = (1.0 - m.metallic) * (vec3(1.0) - fr) * (btdf + dtl);
        if (solid) base = (1.0 - m.metallic) * ((vec3(1.0) - fr) * dtl + t * fi);
    }

    // Sheen over the base: its lobe added, the base darkened by what the sheen takes (sheen.glsl's rule; the view's albedo only).
    float sMax = max(m.sheenColor.x, max(m.sheenColor.y, m.sheenColor.z));
    if (sMax > 0.0) {
        float e = blix_bsdfSheenAlbedo(wo.z, m.sheenRoughness);
        vec3 fs = below ? vec3(0.0) : m.sheenColor * (blix_bsdfCharlieD(clamp(h.z, 0.0, 1.0), blix_bsdfAlphaSheen(m.sheenRoughness)) * blix_bsdfSheenV(max(wi.z, 0.0), wo.z));
        base = fs + (1.0 - sMax * e) * base;
    }

    // The clear coat over all of it (gltf_bsdf.py: the base darkened by the coat's reflectance toward the view).
    if (m.clearcoat > 0.0) {
        float fc = 0.04 + 0.96 * pow(1.0 - clamp(vh, 0.0, 1.0), 5.0);
        float fcView = 0.04 + 0.96 * pow(1.0 - clamp(wo.z, 0.0, 1.0), 5.0);
        float specC = blix_bsdfSpecBrdf(wo, wi, blix_bsdfAlphaCoat(m.clearcoatRoughness));
        base = (1.0 - m.clearcoat * fcView) * base + vec3(m.clearcoat * fc * specC);
    }
    return base;
}

// The seven lobes blix_gltfSample picks among: specular reflection, diffuse reflection, thin transmission, diffuse transmission, the solid's interface, the clear
// coat, the sheen.
void blix_gltfLobes(BlixGltfMaterial m, out float p[7]) {
    float t = m.transmission, dt = m.diffuseTransmission, k = m.metallic;
    bool solid = blix_gltfIsSolid(m);
    p[0] = (solid ? 0.2 * (1.0 - t) : 0.2) + 0.8 * k;
    p[1] = (1.0 - k) * (1.0 - t) * (1.0 - dt);
    p[2] = solid ? 0.0 : (1.0 - k) * t;
    p[3] = (1.0 - k) * (1.0 - t) * dt;
    p[4] = solid ? (1.0 - k) * t : 0.0;
    p[5] = 0.25 * m.clearcoat;
    p[6] = 0.3 * max(m.sheenColor.x, max(m.sheenColor.y, m.sheenColor.z));
    float sum = p[0] + p[1] + p[2] + p[3] + p[4] + p[5] + p[6];
    for (int i = 0; i < 7; ++i) p[i] /= sum;
}

// The density of the visible-normal sampled reflection: G1(wo) D(h) / (4 NV).
float blix_bsdfVndfPdfA(vec3 wo, vec3 wiReflect, float ax, float ay, float rot) {
    vec3 h = normalize(wo + wiReflect);
    float nv = max(wo.z, 1e-9);
    float g1o = 1.0 / (1.0 + blix_bsdfAnisoLambda(wo, ax, ay, rot));
    return h.z > 0.0 ? g1o * blix_bsdfAnisoD(h, ax, ay, rot) / (4.0 * nv) : 0.0;
}

float blix_gltfPdf(BlixGltfMaterial m, vec3 wo, vec3 wi) {
    float p[7];
    blix_gltfLobes(m, p);
    vec2 al = blix_bsdfAlphas(m);
    float a = blix_bsdfAlpha(m.roughness);
    float aT = blix_bsdfAlphaTransmission(m.roughness, m.ior);
    vec3 wm = blix_bsdfMirror(wi);
    float cosine = abs(wi.z) / BLIX_PI;
    float pdf;
    if (wi.z < 0.0) pdf = p[2] * blix_bsdfVndfPdfA(wo, wm, aT, aT, 0.0) + p[3] * cosine;
    else pdf = p[0] * blix_bsdfVndfPdfA(wo, wm, al.x, al.y, m.anisotropyRotation) + p[1] * cosine
             + p[5] * blix_bsdfVndfPdfA(wo, wm, blix_bsdfAlphaCoat(m.clearcoatRoughness), blix_bsdfAlphaCoat(m.clearcoatRoughness), 0.0) + p[6] * cosine;
    if (p[4] > 0.0) pdf += p[4] * blix_ifacePdf(a, m.etaOut, m.ior, wo, wi);
    return pdf;
}

bool blix_gltfSample(BlixGltfMaterial m, vec3 wo, inout uint seed, out vec3 wi, out vec3 weight) {
    float p[7];
    blix_gltfLobes(m, p);
    vec2 al = blix_bsdfAlphas(m);
    float a = blix_bsdfAlpha(m.roughness);
    float aT = blix_bsdfAlphaTransmission(m.roughness, m.ior);
    float aC = blix_bsdfAlphaCoat(m.clearcoatRoughness);
    float u = blix_bsdfRand(seed);
    int lobe = 6;
    float acc = 0.0;
    for (int i = 0; i < 7; ++i) {
        acc += p[i];
        if (u < acc) { lobe = i; break; }
    }
    if (p[lobe] <= 0.0) { for (int i = 6; i >= 0; --i) if (p[i] > 0.0) { lobe = i; break; } }
    bool valid = true;
    wi = vec3(0.0, 0.0, 1.0);
    if (lobe == 0 || lobe == 2 || lobe == 5) {
        vec3 h = lobe == 0 ? blix_bsdfSampleVndfA(wo, al.x, al.y, m.anisotropyRotation, seed)
               : blix_bsdfSampleVndf(wo, lobe == 2 ? aT : aC, seed);
        vec3 r = 2.0 * dot(wo, h) * h - wo;
        valid = r.z > 0.0;
        wi = lobe == 2 ? r * vec3(1.0, 1.0, -1.0) : r;
    } else if (lobe == 1 || lobe == 6) {
        wi = blix_bsdfCosine(seed);
    } else if (lobe == 3) {
        wi = blix_bsdfCosine(seed) * vec3(1.0, 1.0, -1.0);
    } else {
        valid = blix_ifaceSampleDirection(a, m.etaOut, m.ior, wo, seed, wi);
    }
    float d = blix_gltfPdf(m, wo, wi);
    vec3 f = blix_gltfEval(m, wo, wi);
    weight = (d > 0.0 && valid) ? f * abs(wi.z) / max(d, 1e-30) : vec3(0.0);
    return valid && d > 0.0;
}
