// The temporal resolve, built twice: taa.frag (depth and camera reprojection) and taa_surface.frag (stage 4e:
// the pre-pass's velocity and surface identity, with the count in its own integer target).

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
    // w: 1 skips the surface-identity test (taa_surface.frag only; the A/B for what it rejects).
    vec4 uAccumulate;
    // Stage 4f, the surface variant: the reach of what moved this frame (w of the min 1 when there is one), the count
    // a dependent pixel accumulates up to (w of the max), and the direction the sun travels. A pixel's shading
    // depends on the mover when its surface is the mover's (the key's DynamicSurface bit: its own pose changes its
    // shading) or its way to the sun crosses the reach (a shadow receiver).
    vec4 uDynamicMin;
    vec4 uDynamicMax;
    vec4 uSunDirection;
} t;

layout(std430, set = 0, binding = 4) buffer TaaStats { uint refusedCount; uint totalCount; };

layout(set = 0, binding = 1) uniform sampler2D uCurrent;
layout(set = 0, binding = 2) uniform sampler2D uHistory;
layout(set = 0, binding = 3) uniform sampler2D uDepth;

#ifdef TAA_SURFACE
// Stage 4e (taa_surface.frag): the pre-pass's motion and surface identity drive the history instead of depth and the
// camera. History is found by the pixel's own velocity (the surface's motion, objects included); it is dropped where
// last frame did not show this surface near there (a disocclusion); and the accumulated count is an integer in its
// own target, read at the nearest pixel, not a colour channel interpolated with the history.
layout(set = 0, binding = 5) uniform sampler2D uVelocity;
layout(set = 0, binding = 6) uniform usampler2D uSurfaceKey;
layout(set = 0, binding = 7) uniform usampler2D uSurfaceKeyPrev;
layout(set = 0, binding = 8) uniform usampler2D uCountPrev;
layout(location = 1) out uint outCount;
#endif

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
#ifdef TAA_SURFACE
        ivec2 pixel = ivec2(gl_FragCoord.xy);
        ivec2 size = textureSize(uCurrent, 0);
        vec2 uvPrev = vUv - texelFetch(uVelocity, pixel, 0).xy;
        bool reprojects = true;
#else
        vec4 world = t.uInvViewProjJittered * vec4(vUv * 2.0 - 1.0, depth, 1.0);
        vec3 worldPos = world.xyz / world.w;
        vec4 clipPrev = t.uPrevViewProj * vec4(worldPos, 1.0);
        vec4 clipNow = t.uViewProj * vec4(worldPos, 1.0);
        bool reprojects = clipPrev.w > 1e-4 && clipNow.w > 1e-4;
        vec2 uvPrev = (clipPrev.xy / clipPrev.w) * 0.5 + 0.5;
        // The surface's motion on the un-jittered grid, applied to this pixel's centre.
        if (t.uMode.x > 0.5) uvPrev = vUv + (uvPrev - ((clipNow.xy / clipNow.w) * 0.5 + 0.5));
#endif
        if (reprojects) {
            bool onScreen = all(greaterThanEqual(uvPrev, vec2(0.0))) && all(lessThanEqual(uvPrev, vec2(1.0)));
#ifdef TAA_SURFACE
            // The same surface near there last frame? 3x3, because TAA's own jitter moves which surface a pixel at an
            // edge shows from frame to frame, and that is anti-aliasing, not a disocclusion.
            ivec2 past = clamp(ivec2(uvPrev * vec2(size)), ivec2(0), size - 1);
            uint key = texelFetch(uSurfaceKey, pixel, 0).r;
            // An alpha-tested surface (the key's top bit, set at load) is a coin flip per frame under the jitter: a
            // leaf, then the wall behind it. Neither it nor what shows through it owns the pixel, so the test is
            // skipped where one is here or was at the centre last frame. Measured without this: the still image's
            // pixels varying over 2% went 7.9% -> 11.0%, the foliage resetting every frame again.
            uint pastKey = texelFetch(uSurfaceKeyPrev, past, 0).r;
            bool sameSurface = t.uAccumulate.w > 0.5   // w: 1 skips the identity test (--taa-no-key)
                || ((key | pastKey) & 0x80000000u) != 0u;
            for (int k = 0; k < 9 && !sameSurface; ++k) {
                sameSurface = texelFetch(uSurfaceKeyPrev, clamp(past + ivec2(k % 3 - 1, k / 3 - 1), ivec2(0), size - 1), 0).r == key;
            }
            onScreen = onScreen && sameSurface;
#endif
            if (onScreen) {
                vec4 historySample = texture(uHistory, uvPrev);
                vec3 history = historySample.rgb;
#ifdef TAA_SURFACE
                float historyCount = float(texelFetch(uCountPrev, past, 0).r);
#else
                float historyCount = historySample.a;
#endif
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
                    float count = refused > 0.5 ? min(historyCount, 3.0) : historyCount;
                    // The long count is for pixels that hold still: by a pixel of motion it is back to 10 frames,
                    // the fixed 0.9 weight's, so a moving view behaves as it did.
                    float cap = mix(t.uAccumulate.x, min(t.uAccumulate.x, 10.0), clamp(motionPx, 0.0, 1.0));
#ifdef TAA_SURFACE
                    // Correctly owned history of a surface whose shading is changing is stale all the same: on the
                    // moving curtain TAA added +7.8 points of bias (30 deg / 1 s) on top of the image it resolved.
                    if (t.uDynamicMin.w > 0.5) {
                        bool dependent = (key & 0x40000000u) != 0u;
                        if (!dependent) {
                            vec4 w4 = t.uInvViewProjJittered * vec4(vUv * 2.0 - 1.0, depth, 1.0);
                            vec3 p = w4.xyz / w4.w;
                            vec3 toSun = -normalize(t.uSunDirection.xyz);
                            vec3 safe = mix(toSun, vec3(1e-8), lessThan(abs(toSun), vec3(1e-8)));
                            vec3 t0 = (t.uDynamicMin.xyz - p) / safe;
                            vec3 t1 = (t.uDynamicMax.xyz - p) / safe;
                            vec3 a = min(t0, t1), b = max(t0, t1);
                            dependent = max(max(a.x, a.y), max(a.z, 0.0)) <= min(b.x, min(b.y, b.z));
                        }
                        if (dependent) cap = min(cap, max(t.uDynamicMax.w, 1.0));
                    }
#endif
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
#ifdef TAA_SURFACE
    outCount = uint(accumulated);
#endif
}
