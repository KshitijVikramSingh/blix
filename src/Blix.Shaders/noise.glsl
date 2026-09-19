#ifndef BLIX_NOISE_GLSL
#define BLIX_NOISE_GLSL
// #pragma once is owned by Blix's build-time preprocessor, which consumes the
// directive before glslc sees the expanded source. world.frag reaches this both
// directly and through veil.glsl; that diamond is the regression case that
// makes this line a contract rather than documentation.

// Hash + value-noise primitives. Cheap, deterministic, no texture lookups.
// Use these for procedural detail (fire, dust, dithering) and for cheap
// per-pixel randomness (jitter, decorrelation).
//
// Naming: blix_hash<dim>, blix_vnoise<dim>, blix_fbm<dim>. The trailing
// digit is the input dimensionality, not the output.

// --- Hashes ---------------------------------------------------------------
// 2D and 3D Inigo-Quilez-style hashes. Stronger than the canonical
// fract(sin(...)) trick: the dot-with-self stage decorrelates the banding
// you'd otherwise see when stacking octaves.

float blix_hash21(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 78.233);
    return fract(p.x * p.y);
}

float blix_hash31(vec3 p)
{
    p = fract(p * vec3(127.1, 311.7, 74.7));
    p += dot(p, p.yzx + 17.31);
    return fract((p.x + p.y) * p.z);
}

// Cheap screen-space hash. Same trig-multiply pattern most engines use;
// good enough for SSR jitter or banding decorrelation, not for "real"
// noise. Pass gl_FragCoord.xy or vUv as input.
float blix_screenHash(vec2 uv)
{
    return fract(sin(dot(uv, vec2(12.9898, 78.233))) * 43758.5453);
}

// --- Value noise ----------------------------------------------------------
// Trilinear interpolation of hash values at lattice corners with cubic
// smoothing. Range ~[0, 1]. Cheap; for higher-quality use a gradient noise.

float blix_vnoise2(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = blix_hash21(i);
    float b = blix_hash21(i + vec2(1.0, 0.0));
    float c = blix_hash21(i + vec2(0.0, 1.0));
    float d = blix_hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float blix_vnoise3(vec3 p)
{
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = blix_hash31(i + vec3(0.0, 0.0, 0.0));
    float n100 = blix_hash31(i + vec3(1.0, 0.0, 0.0));
    float n010 = blix_hash31(i + vec3(0.0, 1.0, 0.0));
    float n110 = blix_hash31(i + vec3(1.0, 1.0, 0.0));
    float n001 = blix_hash31(i + vec3(0.0, 0.0, 1.0));
    float n101 = blix_hash31(i + vec3(1.0, 0.0, 1.0));
    float n011 = blix_hash31(i + vec3(0.0, 1.0, 1.0));
    float n111 = blix_hash31(i + vec3(1.0, 1.0, 1.0));
    float nx00 = mix(n000, n100, f.x);
    float nx10 = mix(n010, n110, f.x);
    float nx01 = mix(n001, n101, f.x);
    float nx11 = mix(n011, n111, f.x);
    float nxy0 = mix(nx00, nx10, f.y);
    float nxy1 = mix(nx01, nx11, f.y);
    return mix(nxy0, nxy1, f.z);
}

// --- FBM (fractional Brownian motion) ------------------------------------
// 4-octave value-noise FBM. The 2D variant rotates each octave so the
// stack doesn't read as "stretched along Y" -- important when the caller
// will repeatedly sample with y - time scrolling.

float blix_fbm2(vec2 p)
{
    const mat2 rot = mat2(0.80, 0.60, -0.60, 0.80);
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 4; ++i)
    {
        v += a * blix_vnoise2(p);
        p = rot * p * 2.02;
        a *= 0.5;
    }
    return v;
}

float blix_fbm3(vec3 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 4; ++i)
    {
        v += a * blix_vnoise3(p);
        p *= 2.07;
        a *= 0.5;
    }
    return v;
}

#endif // BLIX_NOISE_GLSL
