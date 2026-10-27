#version 450

// --rtgi-blur (SponzaLoop.Rtgi.cs): the cache's half-res readout blurred in screen space by how sure the cache is of
// it, ReBLUR's way, before the upsample. A cache cell's leftover error sits still from frame to frame (the cell holds its
// value): a screen history averages its flicker, never its blotch. Spread over the pixels around, it averages away. At
// half resolution: in the full-res temporal chain (its passes, its history copy) it cost ~11 ms a frame at 2880 x 1620,
// for a history of one frame (2026-10-26). Half-res pixel h stands for full-res pixel 2h + uHalf.yz, as the readout's.
//
// The radius in the world: the pixel's cell's size times its relative error over the done threshold (uBlur.y; a done
// cell about a cell, an unsure one up to four), twice (as measured: with no screen history every pixel is new), at most
// uBlur.z of the rays' harmonic mean hit distance there -- light in a nook changes over the nook's size, open light over
// the room's (ReBLUR sizes its blur by hit distance). In half-res pixels: that over a full-res pixel's footprint at its
// distance, halved, at most uBlur.x / 2 (uBlur.x in full-res pixels). Twelve taps on a disc turned by the pixel and the frame, weighed by the plane (a tap off the pixel's plane,
// another surface, weighs nothing), the normal and their distance from the centre; never by luminance (a bright crease
// sample is the light, not an edge).

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;
// Kept for the next frame's readout to reuse (rtgi_readout.frag's uReuse): its raw light, gradient, guide and this blur.
layout(location = 1) out vec4 outHistRaw;
layout(location = 2) out vec4 outHistGradient;
layout(location = 3) out vec4 outHistBlurred;
layout(location = 4) out vec4 outHistGuide;

layout(set = 0, binding = 0) uniform RtgiBlur {
    mat4 uInvViewProj;   // this frame's (jittered), inverted
    vec4 uParams;        // rtgiParams (rtgi_cache.glsl)
    vec4 uCamera;        // xyz position; w the frame's number
    vec4 uNearField;     // x the near field's radius (RTGI_NEAR)
    vec4 uBlur;          // x the radius at most (full-res pixels; 0: no blur); y the done threshold (relative error); z
                         // the share of the mean hit distance the radius may reach; w a full-res pixel's footprint per metre
    vec4 uHalf;          // x 1: the radius painted (the RTGI panel's blur view: 0 blue to the most red); yz the full-res
                         // pixel of its 2x2; w the radius map's row (half-res pixels)
} u;

layout(set = 0, binding = 1) uniform sampler2D uIn;
// The readout's guide (half_guide.glsl): each half-res pixel's position and normal, the centre's and the taps'.
layout(set = 0, binding = 3) uniform sampler2D uGuide;

layout(std430, set = 0, binding = 5) readonly buffer RtgiBlurRadius { float rtgiBlurRadius[]; };
// The readout's reuse (rtgi_readout.frag): last frame's blurred value for a pixel taken from history, else -1.
layout(set = 0, binding = 6) uniform sampler2D uReuse;
layout(set = 0, binding = 7) uniform sampler2D uGradient;

uint rtgiHash(uint x) {
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

#include "half_guide.glsl"

const vec2 DISC[12] = vec2[12](
    vec2(-0.326, -0.406), vec2(-0.840, -0.074), vec2(-0.696, 0.457), vec2(-0.203, 0.621),
    vec2(0.962, -0.195), vec2(0.473, -0.480), vec2(0.519, 0.767), vec2(0.185, -0.893),
    vec2(0.507, 0.064), vec2(0.896, 0.412), vec2(-0.322, -0.933), vec2(-0.792, -0.598));


vec3 heat(float t) {
    t = clamp(t, 0.0, 1.0);
    return clamp(vec3(1.5 - abs(4.0 * t - 3.0), 1.5 - abs(4.0 * t - 2.0), 1.5 - abs(4.0 * t - 1.0)), 0.0, 1.0);
}


vec4 blurred(ivec2 hq) {
    vec4 reused = texelFetch(uReuse, hq, 0);
    if (reused.a >= 0.0) return reused;
    vec4 centre = texelFetch(uIn, hq, 0);
    if (u.uBlur.x <= 0.0) return centre;
    ivec2 halfSize = textureSize(uIn, 0);
    vec4 g = texelFetch(uGuide, hq, 0);
    if (guideSky(g)) return centre;
    vec3 p = g.xyz, n = guideNormalOf(g.w);
    float dist = length(p - u.uCamera.xyz);
    // The radius the readout sized (rtgi_readout.frag: its cell's error, size and hit distance), in half-res pixels.
    uint at = uint(hq.y) * uint(u.uHalf.w) + uint(hq.x);
    float radius = rtgiBlurRadius[at];
    float worldR = radius * 2.0 * dist * u.uBlur.w;
    if (u.uHalf.x > 0.5) return vec4(heat(radius / max(0.5 * u.uBlur.x, 1.0)), centre.a);
    if (radius < 1.0) return centre;
    // The disc turned by the pixel and the frame.
    uint h = rtgiHash(uint(hq.x) * 0x9E3779B1u ^ uint(hq.y) * 0x85EBCA77u ^ uint(u.uCamera.w) * 0xC2B2AE3Du);
    float a = float(h & 0xFFFFu) / 65536.0 * 6.28318530718;
    mat2 rot = mat2(cos(a), sin(a), -sin(a), cos(a));
    vec4 sum = centre;
    float wSum = 1.0;
    float planeTol = 0.01 * dist + 0.1 * worldR;
    for (int k = 0; k < 12; k++) {
        vec2 o = rot * DISC[k];
        ivec2 th = hq + ivec2(round(o * radius));
        if (any(lessThan(th, ivec2(0))) || any(greaterThanEqual(th, halfSize)) || th == hq) continue;
        vec4 gt = texelFetch(uGuide, th, 0);
        if (guideSky(gt)) continue;
        float plane = dot(n, gt.xyz - p) / planeTol;
        float w = exp(-0.5 * plane * plane) * pow(max(dot(n, guideNormalOf(gt.w)), 0.0), 16.0)
            * exp(-1.5 * dot(o, o));
        sum += texelFetch(uIn, th, 0) * w;
        wSum += w;
    }
    return sum / wSum;
}

void main() {
    ivec2 hq = ivec2(gl_FragCoord.xy);
    outIncident = blurred(hq);
    outHistRaw = texelFetch(uIn, hq, 0);
    outHistGradient = texelFetch(uGradient, hq, 0);
    outHistBlurred = outIncident;
    outHistGuide = texelFetch(uGuide, hq, 0);
}
