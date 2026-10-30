#pragma once

// KHR_materials_iridescence: a thin film's Fresnel over a base, in place of Schlick's -- Belcour and Barla, "A Practical Extension to Microfacet Theory for the Modeling of
// Varying Iridescence" (SIGGRAPH 2017), in the form the Khronos glTF sample viewer evaluates it, which lab/bsdf/bsdflab/gltf_bsdf.py follows (iridescence_fresnel; the python is
// the reference, tests/test_gltf_layers.py holds it, and tests/test_gltf_gpu.py the GLSL through bsdf_gltf.glsl). Shared by the BSDF and the lit pass.
//
//   blix_iridescenceFresnel(outsideIor, filmIor, cosTheta, thicknessNm, baseF0)   the stack's Rec.709 reflectance at cosTheta, rgb: a film of index filmIor and thickness
//   thicknessNm over a base of normal-incidence reflectance baseF0 (per channel), seen from outsideIor. The factor KHR_materials_iridescence weighs it by is the caller's:
//   F = mix(F_schlick, blix_iridescenceFresnel(...), factor).

#ifndef BLIX_PI
#define BLIX_PI 3.14159265359
#endif

vec3 blix_iridescenceSensitivity(float opd, vec3 shift) {
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

vec3 blix_iridescenceFresnel(float outsideIor, float eta2, float cos1, float thickness, vec3 baseF0) {
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
        i += cm * 2.0 * blix_iridescenceSensitivity(float(k) * opd, float(k) * phi);
    }
    return max(i, vec3(0.0));
}

