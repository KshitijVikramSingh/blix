#version 450

// Edge-aware upsample of the incident-light field, from incidentScale to full resolution.
//
// <b>A separate pass for the same reason gtao_denoise.frag is one.</b> The lit pass cannot do this
// itself: the gather needs scene depth to decide which coarse neighbours belong to this surface,
// and the lit pass has that depth bound as its own depth ATTACHMENT. Sampling an attachment you are
// also testing against is a feedback loop, not an optimisation. So the reconstruction happens here,
// where depth is an ordinary input, and the lit pass reads one full-res texel.
//
// The cost of that choice is one full-res RGBA16F written and read — the same round trip the
// ambient buffer already pays, and small against the two volume reconstructions per pixel it
// replaces.
//
// <b>Four taps, not hardware bilinear.</b> Plain bilinear averages across every silhouette, which
// puts a halo of a wall's bounced colour around every pillar and a ring of the wrong sky visibility
// around every leaf. The test is on RELATIVE depth: a 5 cm step is a different surface at 1 m and
// the same surface at 50 m.
//
// The weight is smooth rather than binary, and that is not polish. A binary accept admits a
// different set of coarse taps for each fine pixel, which draws the half-res grid as a checkerboard
// across every flat wall — the same bug, found the same way, as the ambient denoise's.

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;

layout(set = 0, binding = 0) uniform Resolve {
    mat4 uInvProjection;
    vec4 uSource;      // xy = the coarse field's size in pixels, zw = 1/size
} r;

layout(set = 0, binding = 1) uniform sampler2D uIncidentRaw;
layout(set = 0, binding = 2) uniform sampler2D uSceneDepth;

// View-space depth (positive, metres) — the scale the tolerance is relative to.
float viewDepth(vec2 uv) {
    float raw = texture(uSceneDepth, uv).r;
    vec4 view = r.uInvProjection * vec4(uv * 2.0 - 1.0, raw, 1.0);
    return -(view.z / view.w);
}

void main() {
    float centreDepth = viewDepth(vUv);

    vec2 size = r.uSource.xy;
    vec2 texel = r.uSource.zw;
    // The four coarse texels surrounding this point, addressed by their centres.
    vec2 f = vUv * size - 0.5;
    vec2 base = (floor(f) + 0.5) * texel;
    vec2 frac = fract(f);

    vec4 sum = vec4(0.0);
    float weightSum = 0.0;
    float tolerance = max(0.02 * centreDepth, 0.01);
    for (int i = 0; i < 4; ++i) {
        vec2 offset = vec2(float(i & 1), float((i >> 1) & 1));
        vec2 at = base + offset * texel;
        // Bilinear share, so a surface with no depth edge across it reconstructs exactly as
        // hardware filtering would have.
        vec2 b = mix(1.0 - frac, frac, offset);
        float w = b.x * b.y / (1.0 + abs(viewDepth(at) - centreDepth) / tolerance);
        sum += texture(uIncidentRaw, at) * w;
        weightSum += w;
    }

    // Every neighbour rejected — a one-pixel sliver whose coarse taps all belong to something else.
    // The nearest tap is the least wrong answer available, and it is what bilinear would have given.
    outIncident = weightSum > 1e-5 ? sum / weightSum : texture(uIncidentRaw, vUv);
}
