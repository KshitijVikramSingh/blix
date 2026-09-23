#version 450

// Edge-aware upsample of the incident-light field, from incidentScale to full resolution.
//
// Resolve separately because the gather samples scene depth to identify matching coarse neighbours;
// the lit pass simultaneously uses that image as its depth attachment and cannot sample it without
// a feedback loop. The lit pass therefore consumes one already-resolved full-resolution texel.
//
// The cost of that choice is one full-res RGBA16F written and read — the same round trip the
// ambient buffer already pays, and small against the two volume reconstructions per pixel it
// replaces.
//
// Gather the four bilinear neighbours explicitly and guide them by relative depth. Hardware
// bilinear filtering would mix bounced colour and sky visibility across silhouettes; relative depth
// distinguishes a 5 cm discontinuity nearby from the same separation at long range.
//
// Smooth weights avoid exposing the coarse grid when neighbouring fine pixels cross a hard accept
// threshold.

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;

layout(set = 0, binding = 0) uniform Resolve {
    mat4 uInvProjection;
    vec4 uSource;      // xy = the coarse field's size in pixels, zw = 1/size
} r;

layout(set = 0, binding = 1) uniform sampler2D uIncidentRaw;
layout(set = 0, binding = 2) uniform sampler2D uSceneDepth;
// Geometric normal is the second reconstruction guide. Depth continuity alone admits neighbouring
// taps from differently oriented surfaces at corners, arches, and grazing angles; the recorded
// depth-only resolve reached 9.17 mean sRGB at the chair view versus 1.29 on the square-on orbit.
// The full-resolution pre-pass normal rejects those taps with one additional fetch each.
layout(set = 0, binding = 4) uniform sampler2D uPrepassNormal;

// View-space depth (positive, metres) — the scale the tolerance is relative to.
float viewDepth(vec2 uv) {
    float raw = texture(uSceneDepth, uv).r;
    vec4 view = r.uInvProjection * vec4(uv * 2.0 - 1.0, raw, 1.0);
    return -(view.z / view.w);
}

void main() {
    float centreDepth = viewDepth(vUv);
    vec3 centreNormal = texture(uPrepassNormal, vUv).xyz;
    float centreLen = length(centreNormal);
    centreNormal = centreLen > 1e-3 ? centreNormal / centreLen : vec3(0.0);

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

        // Smooth in the same way and for the same reason the depth term is: a hard accept draws the
        // coarse grid wherever the threshold happens to fall. The eighth power keeps a flat wall's
        // four taps at essentially full weight while a tap 30 degrees away keeps about a fifth.
        vec3 tapNormal = texture(uPrepassNormal, at).xyz;
        float tapLen = length(tapNormal);
        if (centreLen > 1e-3 && tapLen > 1e-3) {
            float align = max(dot(centreNormal, tapNormal / tapLen), 0.0);
            float a2 = align * align;
            w *= a2 * a2 * a2 * a2;
        }

        sum += texture(uIncidentRaw, at) * w;
        weightSum += w;
    }

    // Every neighbour rejected — a one-pixel sliver whose coarse taps all belong to something else.
    // The nearest tap is the least wrong answer available, and it is what bilinear would have given.
    outIncident = weightSum > 1e-5 ? sum / weightSum : texture(uIncidentRaw, vUv);
}
