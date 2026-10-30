#version 450

// --rtgi-screen (S3, SponzaLoop.Rtgi.cs): each half-res pixel's indirect irradiance from the tiles' reservoirs
// (rtgi_reservoir.comp) in place of the cache read at its own cell -- the light gathered from points beside the pixel's
// (its tile's, and uGrid.z - 1 more within uGrid.w tiles, a new draw of them each frame), each where its origin is on
// the pixel's surface: E = mean over them of L x cos (at the pixel, toward the sample) x W. A crease or a contact sees
// the rays that left near it, not a 5 cm cell's blend; a sun move shows as fast as the cells the samples land in follow
// it. The cache answers the sky visibility, and the light where no reservoir is on the pixel's surface.
// Half-res pixel h stands for full-res pixel 2h + uHalf.yz (SponzaLoop.HalfLighting.cs).

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;
layout(location = 1) out vec4 outIncidentGradient;

layout(set = 0, binding = 0) uniform RtgiScreen {
    mat4 uInvViewProj;   // inverse of this frame's view-projection (the pre-pass's)
    mat4 uGridViewProj;  // the view-projection the reservoirs' tiles were laid out in (last frame's)
    vec4 uParams;        // rtgiParams (rtgi_cache.glsl)
    vec4 uCamera;        // xyz position; w the frame
    vec4 uHalf;          // yz the full-res pixel of its 2x2
    vec4 uGrid;          // xy tiles across and down; z reservoirs a pixel; w their radius in tiles
} u;

layout(set = 0, binding = 1) uniform sampler2D uSceneDepth;
layout(set = 0, binding = 2) uniform sampler2D uPrepassNormal;
layout(std430, set = 0, binding = 3) readonly buffer RtgiKeys { uint rtgiKeys[]; };
layout(std430, set = 0, binding = 4) readonly buffer RtgiResolved { vec4 rtgiResolved[]; };
layout(std430, set = 0, binding = 5) readonly buffer RtgiReservoirs { vec4 res[]; };

#define RTGI_KEYS(i) rtgiKeys[i]
#include "rtgi_cache.glsl"

uint hash(uint x) {
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

vec2 octWrap(vec2 v) { return (1.0 - abs(v.yx)) * vec2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0); }
vec3 unpackNormal(float f) {
    vec2 e = unpackSnorm2x16(floatBitsToUint(f));
    vec3 n = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) n.xy = octWrap(n.xy);
    return normalize(n);
}

// The cache at p (rtgi_readout.frag's): trilinear over the level's cells, w the weight found.
vec4 readLevel(vec3 p, uint side, uint level, out float sky) {
    float size = rtgiSize(level, u.uParams);
    vec3 f = (p - rtgiOrigin(side)) / size - 0.5;
    ivec3 c0 = ivec3(floor(f));
    vec3 t = f - vec3(c0);
    vec3 e = vec3(0.0);
    float w = 0.0;
    sky = 0.0;
    for (int k = 0; k < 8; k++) {
        ivec3 o = ivec3(k & 1, (k >> 1) & 1, k >> 2);
        uint s = rtgiFind(rtgiCellHash(c0 + o, level, side, u.uParams), u.uParams);
        if (s == 0xFFFFFFFFu) continue;
        vec4 r1 = rtgiResolved[2u * s + 1u];
        if (r1.y < 0.5) continue;
        vec3 b = mix(1.0 - t, t, vec3(o));
        float wk = b.x * b.y * b.z;
        vec4 r0 = rtgiResolved[2u * s];
        e += r0.rgb * wk;
        sky += r0.a * wk;
        w += wk;
    }
    if (w > 0.0) { e /= w; sky /= w; }
    return vec4(e, w);
}

void main() {
    ivec2 h = ivec2(gl_FragCoord.xy);
    ivec2 fullSize = textureSize(uSceneDepth, 0);
    ivec2 q = min(h * 2 + ivec2(u.uHalf.yz), fullSize - 1);
    float raw = texelFetch(uSceneDepth, q, 0).r;
    vec4 ns = texelFetch(uPrepassNormal, q, 0);
    outIncidentGradient = vec4(0.0);
    if (raw >= 1.0 - 1e-6 || dot(ns.xyz, ns.xyz) < 1e-6) { outIncident = vec4(0.0, 0.0, 0.0, 1.0); return; }
    vec2 ndc = (vec2(q) + 0.5) / vec2(fullSize) * 2.0 - 1.0;
    vec4 world = u.uInvViewProj * vec4(ndc, raw, 1.0);
    vec3 p = world.xyz / world.w;
    vec3 n = normalize(ns.xyz);
    // The cache: the sky visibility, and the light where no reservoir answers.
    uint side = rtgiSide(n);
    uint level = rtgiLevel(p, u.uCamera.xyz, u.uParams);
    float sky;
    vec4 cache = readLevel(p, side, level, sky);
    if (cache.w < 0.05) cache = readLevel(p, side, min(level + 2u, uint(u.uParams.w)), sky);
    if (cache.w <= 0.0) sky = 1.0;
    // The reservoirs' tile this point falls in.
    vec4 clip = u.uGridViewProj * vec4(p, 1.0);
    vec2 uv = clip.xy / max(clip.w, 1e-6) * 0.5 + 0.5;
    ivec2 tile = clamp(ivec2(floor(uv * u.uGrid.xy)), ivec2(0), ivec2(u.uGrid.xy) - 1);
    float tol = 0.05 + 0.01 * length(p - u.uCamera.xyz);
    vec3 e = vec3(0.0);
    float found = 0.0;
    uint seed = hash(uint(h.x) * 0x9E3779B9u ^ uint(h.y) * 0x85EBCA77u ^ hash(uint(u.uCamera.w)));
    int radius = int(u.uGrid.w);
    for (int k = 0; k < int(u.uGrid.z); k++) {
        ivec2 o = ivec2(0);
        if (k > 0) {
            seed = hash(seed);
            o = ivec2(int(seed % uint(2 * radius + 1)) - radius, int((seed >> 16) % uint(2 * radius + 1)) - radius);
        }
        ivec2 tt = tile + o;
        if (any(lessThan(tt, ivec2(0))) || any(greaterThanEqual(tt, ivec2(u.uGrid.xy)))) continue;
        uint r = uint(tt.y) * uint(u.uGrid.x) + uint(tt.x);
        vec4 r0 = res[4u * r], r1 = res[4u * r + 1u], r2 = res[4u * r + 2u], r3 = res[4u * r + 3u];
        if (r3.w < 0.5) continue;
        // Its origin on the pixel's surface: the plane's distance and the normals'.
        if (abs(dot(r0.xyz - p, n)) > tol || dot(unpackNormal(r0.w), n) < 0.9) continue;
        vec3 dir = normalize(r1.xyz - p);
        e += r2.rgb * max(0.0, dot(n, dir)) * r3.z;
        found += 1.0;
    }
    vec3 light = found > 0.0 ? e / found : cache.rgb;
    outIncident = vec4(light, sky);
}
