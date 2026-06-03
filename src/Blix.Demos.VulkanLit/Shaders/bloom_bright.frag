#version 450

#include "bloom.glsl"

// Bloom bright-pass: keep only the over-threshold portion of the HDR scene.
// Run at quarter resolution (the target is smaller than uHdr), so this also
// downsamples. Output feeds the separable blur chain.

layout(set = 0, binding = 0) uniform sampler2D uHdr;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

const float kThreshold = 1.0;

void main() {
    vec3 c = texture(uHdr, vUv).rgb;
    outColor = vec4(blix_bloomThreshold(c, kThreshold), 1.0);
}
