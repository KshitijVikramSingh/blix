#version 450

// --rtgi-temporal (SponzaLoop.RtgiTemporal.cs): this frame's accumulated light and its state copied for the next frame's
// rtgi_temporal.frag (the graph's history read is of the target as the last frame left it: the pass that reads it cannot
// also write it).

#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outHistory;
layout(location = 1) out vec4 outHistoryState;
layout(location = 2) out vec4 outFastHistory;

layout(set = 0, binding = 0) uniform RtgiHistory { vec4 uUnused; } u;
layout(set = 0, binding = 1) uniform sampler2D uIncident;
layout(set = 0, binding = 2) uniform sampler2D uState;
layout(set = 0, binding = 3) uniform sampler2D uFast;

void main() {
    ivec2 q = ivec2(gl_FragCoord.xy);
    outHistory = texelFetch(uIncident, q, 0) + u.uUnused * 0.0;
    outHistoryState = texelFetch(uState, q, 0);
    outFastHistory = texelFetch(uFast, q, 0);
}
