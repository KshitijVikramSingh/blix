#version 450

#include "bloom.glsl"

// Bloom bright-pass: keep only the over-threshold portion of the HDR scene.
// Run at quarter resolution (the target is smaller than uHdr), so this also
// downsamples. Output feeds the separable blur chain.

layout(set = 0, binding = 0) uniform sampler2D uHdr;

// Threshold as a push constant so the demo can tune the bloom knee live.
layout(push_constant) uniform PushConstants {
    float uThreshold;
} pc;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

void main() {
    vec3 c = texture(uHdr, vUv).rgb;
    outColor = vec4(blix_bloomThreshold(c, pc.uThreshold), 1.0);
}
