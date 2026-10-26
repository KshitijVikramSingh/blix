#version 450

// --rtgi-blur (SponzaLoop.RtgiTemporal.cs): the accumulated indirect light blurred in screen space by how sure the cache
// is of it, ReBLUR's way -- what the lit pass reads; the history keeps the unblurred (rtgi-history), so the blur never
// compounds over frames. A cache cell's leftover error sits still from frame to frame (the cell holds its value): the
// temporal accumulation averages its flicker, never its blotch. Spread over the pixels around, it averages away.
//
// The radius in the world: the pixel's cell's size times its relative error over the done threshold (uBlur.y; a done
// cell about a cell, an unsure one up to four), at most uBlur.z of the rays' harmonic mean hit distance there -- light
// in a nook changes over the nook's size, open light over the room's (ReBLUR sizes its blur by hit distance); a pixel
// whose history just started (disoccluded) twice. In pixels: that over the pixel's footprint at its distance, at most
// uBlur.x. Twelve taps on a disc turned by the pixel and the frame, weighed by the plane (a tap off the pixel's plane,
// another surface, weighs nothing), the normal and their distance from the centre; never by luminance (a bright crease
// sample is the light, not an edge).

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;

layout(set = 0, binding = 0) uniform RtgiBlur {
    mat4 uInvViewProj;   // this frame's (jittered), inverted
    vec4 uParams;        // rtgiParams (rtgi_cache.glsl)
    vec4 uCamera;        // xyz position; w the frame's number
    vec4 uNearField;     // x the near field's radius (RTGI_NEAR)
    vec4 uBlur;          // x the radius at most (pixels; 0: no blur); y the done threshold (relative error); z the share
                         // of the mean hit distance the radius may reach; w a pixel's footprint per metre of distance
} u;

layout(set = 0, binding = 1) uniform sampler2D uIn;
layout(set = 0, binding = 2) uniform sampler2D uState;
layout(set = 0, binding = 3) uniform sampler2D uSceneDepth;
layout(set = 0, binding = 4) uniform sampler2D uPrepassNormal;

layout(std430, set = 0, binding = 5) readonly buffer RtgiKeys { uint rtgiKeys[]; };
layout(std430, set = 0, binding = 6) readonly buffer RtgiConfidence { vec4 rtgiConfidence[]; };
layout(std430, set = 0, binding = 7) readonly buffer RtgiHitInv { float rtgiHitInv[]; };

#define RTGI_KEYS(i) rtgiKeys[i]
#define RTGI_NEAR u.uNearField.x
#include "rtgi_cache.glsl"

const vec2 DISC[12] = vec2[12](
    vec2(-0.326, -0.406), vec2(-0.840, -0.074), vec2(-0.696, 0.457), vec2(-0.203, 0.621),
    vec2(0.962, -0.195), vec2(0.473, -0.480), vec2(0.519, 0.767), vec2(0.185, -0.893),
    vec2(0.507, 0.064), vec2(0.896, 0.412), vec2(-0.322, -0.933), vec2(-0.792, -0.598));

vec3 worldAt(ivec2 q, ivec2 size, float depth) {
    vec2 ndc = (vec2(q) + 0.5) / vec2(size) * 2.0 - 1.0;
    vec4 w = u.uInvViewProj * vec4(ndc, depth, 1.0);
    return w.xyz / w.w;
}

void main() {
    ivec2 q = ivec2(gl_FragCoord.xy);
    vec4 centre = texelFetch(uIn, q, 0);
    outIncident = centre;
    if (u.uBlur.x <= 0.0) return;
    float depth = texelFetch(uSceneDepth, q, 0).r;
    vec4 ns = texelFetch(uPrepassNormal, q, 0);
    if (depth >= 1.0 - 1e-6 || dot(ns.xyz, ns.xyz) < 1e-6) return;
    ivec2 size = textureSize(uSceneDepth, 0);
    vec3 p = worldAt(q, size, depth), n = normalize(ns.xyz);
    float dist = length(p - u.uCamera.xyz);
    // How sure the cache is here: the pixel's cell's relative error (rtgi_filter.comp), its hit distance.
    uint level = rtgiLevel(p, u.uCamera.xyz, u.uParams);
    float cellSize = rtgiSize(level, u.uParams);
    uint s = rtgiFind(rtgiCellHash(ivec3(floor(p / cellSize)), level, rtgiSide(n), u.uParams), u.uParams);
    float rel = s != 0xFFFFFFFFu ? rtgiConfidence[s].x : 1.0;
    float invD = s != 0xFFFFFFFFu ? rtgiHitInv[s] : 0.0;
    float worldR = cellSize * clamp(rel / u.uBlur.y, 0.5, 4.0);
    if (invD > 1e-6) worldR = min(worldR, u.uBlur.z / invD);
    if (texelFetch(uState, q, 0).y < 4.0) worldR *= 2.0;
    float radius = min(worldR / (dist * u.uBlur.w), u.uBlur.x);
    if (radius < 1.0) return;
    // The disc turned by the pixel and the frame.
    uint h = rtgiHash(uint(q.x) * 0x9E3779B1u ^ uint(q.y) * 0x85EBCA77u ^ uint(u.uCamera.w) * 0xC2B2AE3Du);
    float a = float(h & 0xFFFFu) / 65536.0 * 6.28318530718;
    mat2 rot = mat2(cos(a), sin(a), -sin(a), cos(a));
    vec4 sum = centre;
    float wSum = 1.0;
    float planeTol = 0.01 * dist + 0.1 * worldR;
    for (int k = 0; k < 12; k++) {
        vec2 o = rot * DISC[k];
        ivec2 t = q + ivec2(round(o * radius));
        if (any(lessThan(t, ivec2(0))) || any(greaterThanEqual(t, size)) || t == q) continue;
        float dt = texelFetch(uSceneDepth, t, 0).r;
        vec4 nt = texelFetch(uPrepassNormal, t, 0);
        if (dt >= 1.0 - 1e-6 || dot(nt.xyz, nt.xyz) < 1e-6) continue;
        vec3 pt = worldAt(t, size, dt);
        float plane = dot(n, pt - p) / planeTol;
        float w = exp(-0.5 * plane * plane) * pow(max(dot(n, normalize(nt.xyz)), 0.0), 16.0)
            * exp(-1.5 * dot(o, o));
        sum += texelFetch(uIn, t, 0) * w;
        wSum += w;
    }
    outIncident = sum / wSum;
}
