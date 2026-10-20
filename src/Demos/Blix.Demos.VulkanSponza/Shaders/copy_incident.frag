#version 450
// The incident light and its gradient kept for the next frame (SponzaLoop.Clipmap.cs, incident reuse): a copy after the
// lit pass, so the next frame's incident pass can reproject what each pixel was given one frame ago.
#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outIncident;
layout(location = 1) out vec4 outGradient;

layout(set = 0, binding = 1) uniform sampler2D uIncident;
layout(set = 0, binding = 2) uniform sampler2D uGradient;

void main() {
    ivec2 p = ivec2(gl_FragCoord.xy);
    outIncident = texelFetch(uIncident, p, 0);
    outGradient = texelFetch(uGradient, p, 0);
}
