#version 450

// One level of the Hi-Z pyramid: the min and max LINEAR view depth of a 2x2 region.
//
// ── Why a pyramid, and why this shape ────────────────────────────────────────
// Three things in this renderer want to ask "what is the depth over THERE", where "there" is an
// area rather than a point, and all three currently either cannot or pay full price:
//   * GTAO's horizon search reads individual texels out to a radius, so a wide search costs
//     proportionally more cache misses. It is capped at 48 texels for exactly that reason, which
//     costs near-field occlusion on close geometry.
//   * Occlusion culling does not exist, and the measured prize is large: the depth pre-pass already
//     saves 29 ms/frame by suppressing overdraw, which says the scene is heavily occluded and
//     nothing is exploiting that before submission.
//   * Later: contact shadows, screen-space reflection, light culling.
//
// ── Min AND max, because the two consumers want opposite answers ─────────────
// Occlusion culling wants the FARTHEST depth in a region: "my bounding box's nearest point is
// behind everything there" is then a conservative reject, and being wrong the other way drops
// visible geometry, which is a correctness bug rather than a quality one. GTAO wants the NEAREST:
// the closest surface in a region is the strongest occluder. Storing only one would silently make
// the other consumer wrong, so the pyramid carries both and each reader takes its own channel.
//
// ── LINEAR metres, not raw depth ────────────────────────────────────────────
// Half-float has about ten bits of mantissa, and a Vulkan [0,1] depth buffer crowds nearly all of
// its resolution near the near plane — so min/max of raw depth in a half would be nearly useless
// far away, which is precisely where a pyramid is consulted. Linear view depth spreads evenly, and
// it is also what a horizon search actually wants: a position is then ray * depth rather than a
// matrix multiply per tap.
//
// ── Separate targets per level, rather than mips of one image ───────────────
// Reading level i-1 while writing level i of the SAME image is the classic mip-generation hazard —
// one image cannot be colour-attachment and shader-read at once without General layout and per-mip
// barriers, none of which this graph has. Distinct images have no hazard at all, and the graph's
// existing barrier inference already orders exactly this shape. A consumer needing a RUNTIME-chosen
// level is what would justify building mipped storage images; a consumer whose level is fixed per
// unrolled step (GTAO) needs no such thing.

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outMinMax;

layout(set = 0, binding = 0) uniform HiZ {
    mat4  uInvProjection;
    // xy = SOURCE size in texels, zw = destination size in texels.
    vec4  uSizes;
    // x = 1 when the source is the scene depth buffer (raw, needs linearising),
    // 0 when it is a previous pyramid level (already linear min/max). y/z/w unused.
    vec4  uMode;
} h;

layout(set = 0, binding = 1) uniform sampler2D uSource;

// Linear view depth (positive metres) from a raw depth sample at a given uv.
float linearDepthAt(ivec2 texel, vec2 sourceSize) {
    float raw = texelFetch(uSource, texel, 0).r;
    vec2 uv = (vec2(texel) + 0.5) / sourceSize;
    vec4 view = h.uInvProjection * vec4(uv * 2.0 - 1.0, raw, 1.0);
    return -(view.z / view.w);
}

void main() {
    vec2 sourceSize = h.uSizes.xy;
    vec2 destSize = h.uSizes.zw;
    ivec2 maxTexel = ivec2(sourceSize) - 1;

    // The 2x2 (or wider) source footprint of this destination texel. Computed from the size RATIO
    // rather than assumed to be two, because halving an odd extent leaves a level whose footprint
    // is 2 in one axis and 3 in the other; assuming 2 drops a column and the pyramid quietly stops
    // being conservative — which for occlusion culling means dropping geometry that is visible.
    ivec2 base = ivec2(floor(vUv * destSize)) * ivec2(round(sourceSize / destSize));
    ivec2 span = ivec2(ceil(sourceSize / destSize));

    float lo = 1e30;
    float hi = -1e30;
    for (int y = 0; y < span.y; ++y) {
        for (int x = 0; x < span.x; ++x) {
            ivec2 texel = clamp(base + ivec2(x, y), ivec2(0), maxTexel);
            if (h.uMode.x > 0.5) {
                float d = linearDepthAt(texel, sourceSize);
                lo = min(lo, d);
                hi = max(hi, d);
            } else {
                vec2 mm = texelFetch(uSource, texel, 0).rg;
                lo = min(lo, mm.x);
                hi = max(hi, mm.y);
            }
        }
    }

    outMinMax = vec4(lo, hi, 0.0, 1.0);
}
