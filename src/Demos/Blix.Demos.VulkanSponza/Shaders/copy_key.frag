#version 450
// The surface-key target kept for the next frame (stage 4e): a copy at the end of the frame, after every reader,
// so the next frame's TAA and screen probes can ask which surface a pixel showed one frame ago.
#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out uint outKey;

layout(set = 0, binding = 1) uniform usampler2D uSource;

void main() {
    outKey = texelFetch(uSource, ivec2(gl_FragCoord.xy), 0).r;
}
