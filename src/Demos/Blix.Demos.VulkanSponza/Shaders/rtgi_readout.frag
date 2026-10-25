#version 450

// --rtgi (S1, SponzaLoop.Rtgi.cs): the cache read at each half-res pixel, into the incident targets the upsample and the
// lit pass read (in place of incident-light-half): rgb the indirect irradiance (sky and bounce, no direct sun), a the
// sky visibility. Half-res pixel h stands for full-res pixel 2h + uHalf.yz (SponzaLoop.HalfLighting.cs). The eight
// cells around the point at its level (the same side), trilinear, those holding nothing left out; where none do, the
// level two coarser; where neither, nothing (black, the sky seen).

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;
layout(location = 1) out vec4 outIncidentGradient;

layout(set = 0, binding = 0) uniform RtgiReadout {
    mat4 uInvViewProj;   // inverse of the view-projection the pre-pass drew with
    vec4 uParams;        // rtgiParams (rtgi_cache.glsl)
    vec4 uCamera;        // xyz position
    vec4 uHalf;          // yz the full-res pixel of its 2x2
} u;

layout(set = 0, binding = 1) uniform sampler2D uSceneDepth;
layout(set = 0, binding = 2) uniform sampler2D uPrepassNormal;
layout(std430, set = 0, binding = 3) readonly buffer RtgiKeys { uint rtgiKeys[]; };
layout(std430, set = 0, binding = 4) readonly buffer RtgiResolved { vec4 rtgiResolved[]; };

#define RTGI_KEYS(i) rtgiKeys[i]
#include "rtgi_cache.glsl"

// Trilinear over the level's cells around p (their centres), w the weight found.
vec4 readLevel(vec3 p, uint side, uint level, out float sky) {
    float size = rtgiSize(level, u.uParams);
    vec3 f = p / size - 0.5;
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
        // Weighted by what it holds too, full at 32 samples: a new cell (a landing just requested, a level just blended in,
        // one refilled) fades in under its neighbours rather than showing its first draw -- odd lights that came and went.
        float wk = b.x * b.y * b.z * clamp(r1.y / 32.0, 1.0 / 32.0, 1.0);
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
    uint side = rtgiSide(n);
    // The two levels around the point's, blended by where it sits between them (rtgiLevelF): no jump at a level's edge.
    float lf = rtgiLevelF(p, u.uCamera.xyz, u.uParams);
    uint level = uint(lf);
    uint top = uint(u.uParams.w);
    float t = lf - float(level);
    float sky0, sky1;
    vec4 e0 = readLevel(p, side, level, sky0);
    vec4 e1 = level < top ? readLevel(p, side, level + 1u, sky1) : vec4(0.0);
    vec4 e;
    float sky;
    if (e0.w >= 0.05 && e1.w >= 0.05) { e = vec4(mix(e0.rgb, e1.rgb, t), 1.0); sky = mix(sky0, sky1, t); }
    else if (e0.w >= 0.05) { e = e0; sky = sky0; }
    else if (e1.w >= 0.05) { e = e1; sky = sky1; }
    else e = readLevel(p, side, min(level + 2u, top), sky);
    outIncident = e.w > 0.0 ? vec4(e.rgb, sky) : vec4(0.0, 0.0, 0.0, 1.0);
}
