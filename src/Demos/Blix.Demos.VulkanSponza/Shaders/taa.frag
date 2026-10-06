#version 450

// Temporal resolve: this frame, made quieter by the ones before it.
//
// The present frame owns the image. History is rejected where reprojection is invalid, clamped to
// the current neighbourhood where valid, and blended only to reduce shimmer.
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
    // x = history weight, y = 1 when history is valid at all, z = show rejection, w = 1: count rejections into
    // TaaStats (measurement only; the image is untouched).
    vec4 uParams;
    // This frame's UNJITTERED view-projection: where the surface point sits on the un-jittered grid now, so history
    // moves by the surface's motion and never by the jitter.
    mat4 uViewProj;
    // x reprojection: 0 the jittered point's own position (history resampled at the jitter offset every frame),
    //   1 the surface's motion applied to this pixel's centre (a still camera reads its own pixel);
    // y history bound: 0 per-channel min/max of the 5-tap cross, 1 variance clip toward the mean in YCoCg;
    // z the clip's gamma (box = mean +- gamma * sigma); w 1: blend in linear space (the control for the tonemapped
    // blend's bias, which averages high-contrast detail away from its linear mean).
    vec4 uMode;
    // x: accumulate up to this many frames per pixel (0: the fixed history weight uParams.x). The count rides in
    // the history's alpha (Rgba16F history). A still pixel then keeps converging toward the true average of what
    // its jitter sees: sub-pixel foliage, where each frame sees leaf or background, never settled at a fixed 0.9
    // (about 10 frames). Clipping cuts the count back, which is what keeps motion from ghosting.
    // y: how much wider the clamp box may be where the pixel does not move (1: never); z: the motion, in pixels, by
    // which it is back to its normal width. With no motion there is nothing to ghost, and a box from five taps of
    // one jittered frame rarely contains what sub-pixel foliage averages to, so it clipped (and reset) every frame.
    // w unused.
    vec4 uAccumulate;
} t;

layout(std430, set = 0, binding = 4) buffer TaaStats { uint refusedCount; uint totalCount; };

layout(set = 0, binding = 1) uniform sampler2D uCurrent;
layout(set = 0, binding = 2) uniform sampler2D uHistory;
layout(set = 0, binding = 3) uniform sampler2D uDepth;

// Luminance-weighted blending in a tonemapped space, so one very bright sample cannot dominate the
// average and leave a trail behind a specular highlight. Reinhard by luminance is the cheap standard
// here; it is inverted exactly after the blend, so the result is still linear HDR.
vec3 toneIn(vec3 c)  { return c / (1.0 + dot(c, vec3(0.2126, 0.7152, 0.0722))); }
vec3 toneOut(vec3 c) { return c / max(1.0 - dot(c, vec3(0.2126, 0.7152, 0.0722)), 1e-4); }

vec3 toYCoCg(vec3 c) { return vec3(dot(c, vec3(0.25, 0.5, 0.25)), dot(c, vec3(0.5, 0.0, -0.5)), dot(c, vec3(-0.25, 0.5, -0.25))); }
vec3 fromYCoCg(vec3 c) { return vec3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z); }

// Clip a point toward a box's centre (Salvi; Karis's "clip, not clamp"): the history keeps its hue, moving along
// the line to the neighbourhood's mean until it is inside, where a per-channel clamp would bend it.
vec3 clipToBox(vec3 history, vec3 centre, vec3 extent) {
    vec3 offset = history - centre;
    vec3 units = abs(offset / max(extent, vec3(1e-5)));
    float largest = max(units.x, max(units.y, units.z));
    return largest > 1.0 ? centre + offset / largest : history;
}

void main() {
    vec3 current = texture(uCurrent, vUv).rgb;
    float depth = texture(uDepth, vUv).r;

    // The neighbourhood this frame would accept. Gathered from the 3x3 around the pixel, which is
    // the same information a spatial filter would use — here it bounds history instead of blurring.
    // A five-tap cross bounds plausible current colour without the weaker corner samples; the
    // nine-tap square measured 7.73 ms in this bandwidth-bound resolve.
    vec3 lo = current;
    vec3 hi = current;
    vec2 texel = 1.0 / vec2(textureSize(uCurrent, 0));
    vec3 n0 = texture(uCurrent, vUv + vec2( texel.x, 0.0)).rgb;
    vec3 n1 = texture(uCurrent, vUv + vec2(-texel.x, 0.0)).rgb;
    vec3 n2 = texture(uCurrent, vUv + vec2(0.0,  texel.y)).rgb;
    vec3 n3 = texture(uCurrent, vUv + vec2(0.0, -texel.y)).rgb;
    lo = min(lo, min(min(n0, n1), min(n2, n3)));
    hi = max(hi, max(max(n0, n1), max(n2, n3)));
    // The same five taps' mean and spread in YCoCg (tonemapped, so a highlight cannot widen the box alone).
    vec3 y0 = toYCoCg(toneIn(current)), y1 = toYCoCg(toneIn(n0)), y2 = toYCoCg(toneIn(n1)), y3 = toYCoCg(toneIn(n2)), y4 = toYCoCg(toneIn(n3));
    vec3 mean = (y0 + y1 + y2 + y3 + y4) / 5.0;
    vec3 meanSq = (y0 * y0 + y1 * y1 + y2 * y2 + y3 * y3 + y4 * y4) / 5.0;
    vec3 sigma = sqrt(max(meanSq - mean * mean, vec3(0.0)));

    float refused = 1.0;
    vec3 resolved = current;
    float accumulated = 1.0;
    if (t.uParams.y > 0.5 && t.uParams.x > 0.0 && depth < 1.0) {
        vec4 world = t.uInvViewProjJittered * vec4(vUv * 2.0 - 1.0, depth, 1.0);
        vec3 worldPos = world.xyz / world.w;
        vec4 clipPrev = t.uPrevViewProj * vec4(worldPos, 1.0);
        vec4 clipNow = t.uViewProj * vec4(worldPos, 1.0);
        if (clipPrev.w > 1e-4 && clipNow.w > 1e-4) {
            vec2 uvPrev = (clipPrev.xy / clipPrev.w) * 0.5 + 0.5;
            // The surface's motion on the un-jittered grid, applied to this pixel's centre.
            if (t.uMode.x > 0.5) uvPrev = vUv + (uvPrev - ((clipNow.xy / clipNow.w) * 0.5 + 0.5));
            if (all(greaterThanEqual(uvPrev, vec2(0.0))) && all(lessThanEqual(uvPrev, vec2(1.0)))) {
                vec4 historySample = texture(uHistory, uvPrev);
                vec3 history = historySample.rgb;
                // Clamped to the neighbourhood, not rejected on a threshold: a disocclusion shows up
                // as history outside what any nearby pixel of this frame contains, and pulling it to
                // the edge of that range keeps the stability while dropping the stale colour.
                // Relax the box where nothing moves (in pixels of this frame).
                float motionPx = length((uvPrev - vUv) * vec2(textureSize(uCurrent, 0)));
                float widen = mix(max(t.uAccumulate.y, 1.0), 1.0, clamp(motionPx / max(t.uAccumulate.z, 1e-3), 0.0, 1.0));
                vec3 centre = 0.5 * (lo + hi);
                lo = centre - (centre - lo) * widen;
                hi = centre + (hi - centre) * widen;
                sigma *= widen;
                vec3 bounded;
                if (t.uMode.y > 0.5) {
                    vec3 h = toYCoCg(toneIn(history));
                    bounded = toneOut(fromYCoCg(clipToBox(h, mean, t.uMode.z * sigma)));
                } else {
                    bounded = clamp(history, lo, hi);
                }
                refused = any(greaterThan(abs(bounded - history), vec3(1e-4 * max(1.0, dot(history, vec3(0.3333)))))) ? 1.0 : 0.0;
                float weight = clamp(t.uParams.x, 0.0, 1.0);
                if (t.uAccumulate.x > 0.0) {
                    // A clipped pixel keeps a little history (the clip already pulled it to the neighbourhood).
                    float count = refused > 0.5 ? min(historySample.a, 3.0) : historySample.a;
                    // The long count is for pixels that hold still: by a pixel of motion it is back to 10 frames,
                    // the fixed 0.9 weight's, so a moving view behaves as it did.
                    float cap = mix(t.uAccumulate.x, min(t.uAccumulate.x, 10.0), clamp(motionPx, 0.0, 1.0));
                    accumulated = min(count + 1.0, cap);
                    weight = 1.0 - 1.0 / accumulated;
                }
                resolved = t.uMode.w > 0.5
                    ? mix(current, bounded, weight)
                    : toneOut(mix(toneIn(current), toneIn(bounded), weight));
                if (t.uParams.w > 0.5) {
                    atomicAdd(totalCount, 1u);
                    if (refused > 0.5) atomicAdd(refusedCount, 1u);
                }
            }
        }
    }

    // Bright where history was refused or clamped back — disocclusions, the screen edge, and
    // anything the camera has just turned onto. A still camera should show almost nothing.
    outColor = t.uParams.z > 0.5 ? vec4(vec3(refused), 1.0) : vec4(resolved, accumulated);
}
