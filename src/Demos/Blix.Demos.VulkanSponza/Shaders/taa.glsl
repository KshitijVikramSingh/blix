// The temporal resolve, built twice: taa.frag (depth and camera reprojection) and taa_surface.frag (stage 4e:
// the pre-pass's velocity and surface identity, with the count in its own integer target).

// Temporal resolve: this frame, made quieter by the ones before it.
//
// The present frame owns the image. History is rejected where reprojection is invalid, clamped to
// the current neighbourhood where valid, and blended only to reduce shimmer.
//
// taa.frag's reprojection is camera-only and exact: nothing in these scenes animates, so depth plus two
// matrices is the whole motion model. taa_surface.frag reads the pre-pass's velocity instead, the same
// motion carried per pixel, with the surface's identity beside it.
//
// The quantity reprojected is radiance at a WORLD POINT: an integral along a ray (a volume's
// scattering) belongs to the ray, and moving it to another camera is meaningless. Colour at a
// surface point belongs to the point.

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
    // moves by the surface's motion applied to this pixel's centre (a still camera reads its own pixel) and never by
    // the jitter.
    mat4 uViewProj;
    // x: accumulate up to this many frames per pixel (0: the fixed history weight uParams.x). The count rides in
    // the history's alpha (Rgba16F history). A still pixel then keeps converging toward the true average of what
    // its jitter sees: sub-pixel foliage, where each frame sees leaf or background, never settled at a fixed 0.9
    // (about 10 frames). Clipping cuts the count back, which is what keeps motion from ghosting.
    // y: how much wider the clamp box may be where the pixel does not move (1: never); z: the motion, in pixels, by
    // which it is back to its normal width. With no motion there is nothing to ghost, and a box from five taps of
    // one jittered frame rarely contains what sub-pixel foliage averages to, so it clipped (and reset) every frame.
    vec3 uAccumulate;
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
#endif

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

    float refused = 1.0;
    vec3 resolved = current;
    float accumulated = 1.0;
    // The sky too (depth 1): with no history it showed each frame's raw colour, and where a canopy's edge met it the
    // jitter put a pixel on sky one frame (raw, no history) and on a leaf the next (history just reset) -- the
    // treetops and the ivy against the sky shimmered under TAA alone (2026-10-20). It reprojects as the far plane's
    // point (camera rotation; the velocity buffer holds nothing there) and is clamped to its neighbourhood as any.
    if (t.uParams.y > 0.5 && t.uParams.x > 0.0) {
#ifdef TAA_SURFACE
        ivec2 pixel = ivec2(gl_FragCoord.xy);
        ivec2 size = textureSize(uCurrent, 0);
        vec2 uvPrev = vUv - texelFetch(uVelocity, pixel, 0).xy;
        if (depth >= 1.0) {
            vec4 far = t.uInvViewProjJittered * vec4(vUv * 2.0 - 1.0, depth, 1.0);
            vec4 clipPrev = t.uPrevViewProj * far;
            vec4 clipNow = t.uViewProj * far;
            if (abs(clipPrev.w) > 1e-6 && abs(clipNow.w) > 1e-6)
                uvPrev = vUv + ((clipPrev.xy / clipPrev.w) - (clipNow.xy / clipNow.w)) * 0.5;
        }
        bool reprojects = true;
#else
        vec4 world = t.uInvViewProjJittered * vec4(vUv * 2.0 - 1.0, depth, 1.0);
        vec3 worldPos = world.xyz / world.w;
        vec4 clipPrev = t.uPrevViewProj * vec4(worldPos, 1.0);
        vec4 clipNow = t.uViewProj * vec4(worldPos, 1.0);
        bool reprojects = clipPrev.w > 1e-4 && clipNow.w > 1e-4;
        vec2 uvPrev = (clipPrev.xy / clipPrev.w) * 0.5 + 0.5;
        // The surface's motion on the un-jittered grid, applied to this pixel's centre.
        uvPrev = vUv + (uvPrev - ((clipNow.xy / clipNow.w) * 0.5 + 0.5));
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
            bool sameSurface = ((key | pastKey) & 0x80000000u) != 0u;
            for (int k = 0; k < 9 && !sameSurface; ++k) {
                sameSurface = texelFetch(uSurfaceKeyPrev, clamp(past + ivec2(k % 3 - 1, k / 3 - 1), ivec2(0), size - 1), 0).r == key;
            }
            onScreen = onScreen && sameSurface;
#endif
            if (onScreen) {
                vec4 historySample = texture(uHistory, uvPrev);
                vec3 history = historySample.rgb;
#ifdef TAA_SURFACE
                // The count rides in the history's alpha (fp16 holds whole counts to 2048): read at the reprojected pixel,
                // unfiltered. It had its own R32UI pair (36 MB, ~37 MB a frame of traffic) until 2026-10-20.
                float historyCount = texelFetch(uHistory, past, 0).a;
#else
                float historyCount = historySample.a;
#endif
                // A target made afresh (the render scale's resize after loading) holds whatever memory it was given: a NaN
                // there resolved to NaN and fed itself back, the frame black for good while TAA was on (2026-10-20).
                // Anything not finite is no history.
                if (any(isnan(historySample)) || any(isinf(historySample)) || isnan(historyCount) || isinf(historyCount)) {
                    history = current;
                    historyCount = 0.0;
                }
                historyCount = clamp(historyCount, 0.0, 2048.0);
                // Clamped to the neighbourhood, not rejected on a threshold: a disocclusion shows up
                // as history outside what any nearby pixel of this frame contains, and pulling it to
                // the edge of that range keeps the stability while dropping the stale colour.
                // Relax the box where nothing moves (in pixels of this frame).
                float motionPx = length((uvPrev - vUv) * vec2(textureSize(uCurrent, 0)));
                float still = 1.0 - clamp(motionPx / max(t.uAccumulate.z, 1e-3), 0.0, 1.0);
                float widen = mix(1.0, max(t.uAccumulate.y, 1.0), still);

                vec3 centre = 0.5 * (lo + hi);
                lo = centre - (centre - lo) * widen;
                hi = centre + (hi - centre) * widen;
                vec3 bounded = clamp(history, lo, hi);
                refused = any(greaterThan(abs(bounded - history), vec3(1e-4 * max(1.0, dot(history, vec3(0.3333)))))) ? 1.0 : 0.0;
                float weight = clamp(t.uParams.x, 0.0, 1.0);
                if (t.uAccumulate.x > 0.0) {
                    // A clipped pixel keeps a little history (the clip already pulled it to the neighbourhood).
                    // Graded by how far the clip moved the history: refused (above) is any clip at all, and with the
                    // threshold at 1e-4 nearly every still pixel clipped by a hair every frame, so the count sat at
                    // ~3 frames everywhere and sub-pixel foliage against the sky shimmered (the rejection view bright
                    // over the whole frame, 2026-10-20). A nudge under a few percent of the pixel's light keeps the
                    // count; a real change (disocclusion, a light change) still cuts it to three frames.
                    float moved = length(bounded - history) / max(max(dot(history, vec3(0.3333)), dot(current, vec3(0.3333))), 1e-3);
                    float cut = smoothstep(0.03, 0.15, moved);
                    float count = mix(historyCount, min(historyCount, 3.0), cut);
                    refused = cut;   // the rejection view and count: history actually cut back, not every nudge

                    // The long count is for pixels that hold still: by a pixel of motion it is back to 10 frames,
                    // the fixed 0.9 weight's, so a moving view behaves as it did.
                    float cap = mix(t.uAccumulate.x, min(t.uAccumulate.x, 10.0), clamp(motionPx, 0.0, 1.0));
                    accumulated = min(count + 1.0, cap);
                    weight = 1.0 - 1.0 / accumulated;
                }
                resolved = toneOut(mix(toneIn(current), toneIn(bounded), weight));
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
