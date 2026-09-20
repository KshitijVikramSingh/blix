#version 450

// Temporal resolve: this frame, made quieter by the ones before it.
//
// <b>The present frame owns the image; history gets permission to make it calmer.</b> That is the
// whole policy, and every choice here follows from it. History is rejected outright where it cannot
// be trusted, clamped to what this frame would have accepted where it can, and blended at a weight
// that loses to a disagreement. Nothing here is allowed to invent detail the current frame does not
// have — the renderer's spatial frame is good enough at native resolution that temporal only has to
// stop it shimmering.
//
// Reprojection is camera-only and exact. Nothing in this scene animates, so a velocity buffer would
// be a per-object previous-transform, an extra target and an extra pass to encode a quantity that is
// identically zero. Depth plus two matrices is the whole motion model, and the day something moves
// is the day that stops being true — loudly, not subtly.
//
// The quantity reprojected is radiance at a WORLD POINT, which is the rule the froxel fog arrived at
// the hard way: an integral along a ray belongs to the ray, and moving it to another camera is
// meaningless. Colour at a surface point belongs to the point.

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform Taa {
    // Inverse of the JITTERED view-projection this frame rendered with — depth lives in that clip
    // space, so anything else unprojects to the wrong world point.
    mat4 uInvViewProjJittered;
    // Previous frame's UNJITTERED view-projection. History is the accumulated image, which is
    // aligned to the un-jittered pixel grid; reprojecting into the jittered one would chase the
    // offset that exists to be averaged away.
    mat4 uPrevViewProj;
    // x = history weight, y = 1 when history is valid at all, z = show rejection, w unused.
    vec4 uParams;
} t;

layout(set = 0, binding = 1) uniform sampler2D uCurrent;
layout(set = 0, binding = 2) uniform sampler2D uHistory;
layout(set = 0, binding = 3) uniform sampler2D uDepth;

// Luminance-weighted blending in a tonemapped space, so one very bright sample cannot dominate the
// average and leave a trail behind a specular highlight. Reinhard by luminance is the cheap standard
// here; it is inverted exactly after the blend, so the result is still linear HDR.
vec3 toneIn(vec3 c)  { return c / (1.0 + dot(c, vec3(0.2126, 0.7152, 0.0722))); }
vec3 toneOut(vec3 c) { return c / max(1.0 - dot(c, vec3(0.2126, 0.7152, 0.0722)), 1e-4); }

void main() {
    vec3 current = texture(uCurrent, vUv).rgb;
    float depth = texture(uDepth, vUv).r;

    // The neighbourhood this frame would accept. Gathered from the 3x3 around the pixel, which is
    // the same information a spatial filter would use — here it bounds history instead of blurring.
    vec3 lo = current;
    vec3 hi = current;
    vec2 texel = 1.0 / vec2(textureSize(uCurrent, 0));
    for (int y = -1; y <= 1; ++y) {
        for (int x = -1; x <= 1; ++x) {
            if (x == 0 && y == 0) continue;
            vec3 n = texture(uCurrent, vUv + vec2(x, y) * texel).rgb;
            lo = min(lo, n);
            hi = max(hi, n);
        }
    }

    float refused = 1.0;
    vec3 resolved = current;
    if (t.uParams.y > 0.5 && t.uParams.x > 0.0 && depth < 1.0) {
        vec4 world = t.uInvViewProjJittered * vec4(vUv * 2.0 - 1.0, depth, 1.0);
        vec3 worldPos = world.xyz / world.w;
        vec4 clipPrev = t.uPrevViewProj * vec4(worldPos, 1.0);
        if (clipPrev.w > 1e-4) {
            vec2 uvPrev = (clipPrev.xy / clipPrev.w) * 0.5 + 0.5;
            if (all(greaterThanEqual(uvPrev, vec2(0.0))) && all(lessThanEqual(uvPrev, vec2(1.0)))) {
                vec3 history = texture(uHistory, uvPrev).rgb;
                // Clamped to the neighbourhood, not rejected on a threshold: a disocclusion shows up
                // as history outside what any nearby pixel of this frame contains, and pulling it to
                // the edge of that range keeps the stability while dropping the stale colour.
                vec3 bounded = clamp(history, lo, hi);
                refused = any(greaterThan(abs(bounded - history), vec3(1e-4))) ? 1.0 : 0.0;
                resolved = toneOut(mix(toneIn(current), toneIn(bounded), clamp(t.uParams.x, 0.0, 1.0)));
            }
        }
    }

    // Bright where history was refused or clamped back — disocclusions, the screen edge, and
    // anything the camera has just turned onto. A still camera should show almost nothing.
    outColor = t.uParams.z > 0.5 ? vec4(vec3(refused), 1.0) : vec4(resolved, 1.0);
}
