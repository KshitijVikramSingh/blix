#version 450

// Present: sample HDR, apply exposure, tonemap, output to the sRGB swapchain.
// The swapchain is a *_Srgb format, so the hardware applies the linear->sRGB
// OETF on write — every operator below outputs DISPLAY-LINEAR (operators that
// natively bake in gamma are linearised so we don't double-encode).
//
// uTonemap selects the operator live (overlay -> Post -> Tonemap):
//   0 Reinhard  1 ACES (Narkowicz)  2 AgX (neutral)  3 Hejl-Dawson

layout(set = 0, binding = 0) uniform sampler2D uHdr;

layout(push_constant) uniform PushConstants {
    float uExposure;
    uint  uTonemap;
} pc;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

// --- Reinhard --------------------------------------------------------------
vec3 reinhard(vec3 x) { return x / (x + vec3(1.0)); }

// --- ACES filmic (Krzysztof Narkowicz 2016 fit) ----------------------------
vec3 acesFilmic(vec3 x) {
    const float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

// --- AgX (Troy Sobotka; minimal neutral fit) -------------------------------
vec3 agxContrast(vec3 x) {
    vec3 x2 = x * x;
    vec3 x4 = x2 * x2;
    return 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4
         - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
}
vec3 agx(vec3 val) {
    const mat3 m = mat3(
        0.842479062253094,  0.0423282422610123, 0.0423756549057051,
        0.0784335999999992, 0.878468636469772,  0.0784336,
        0.0792237451477643, 0.0791661274605434, 0.879142973793104);
    const mat3 mInv = mat3(
        1.19687900512017,   -0.0528968517574562, -0.0529716355144438,
       -0.0980208811401368,  1.15190312990417,   -0.0980434501171241,
       -0.0990297440797205, -0.0989611768448433,  1.15107367264116);
    const float minEv = -12.47393, maxEv = 4.026069;
    val = m * val;
    val = clamp(log2(max(val, 1e-10)), minEv, maxEv);
    val = (val - minEv) / (maxEv - minEv);
    val = agxContrast(val);
    val = mInv * val;
    return pow(max(val, 0.0), vec3(2.2)); // back to display-linear
}

// --- Hejl-Dawson filmic (gamma baked in -> linearise) ----------------------
vec3 hejl(vec3 x) {
    x = max(vec3(0.0), x - 0.004);
    vec3 m = (x * (6.2 * x + 0.5)) / (x * (6.2 * x + 1.7) + 0.06);
    return pow(m, vec3(2.2)); // undo baked gamma; swapchain re-applies sRGB
}

void main() {
    vec3 hdr = texture(uHdr, vUv).rgb * pc.uExposure;
    vec3 mapped;
    if      (pc.uTonemap == 0u) mapped = reinhard(hdr);
    else if (pc.uTonemap == 2u) mapped = agx(hdr);
    else if (pc.uTonemap == 3u) mapped = hejl(hdr);
    else                        mapped = acesFilmic(hdr); // 1 = default
    outColor = vec4(mapped, 1.0);
}
