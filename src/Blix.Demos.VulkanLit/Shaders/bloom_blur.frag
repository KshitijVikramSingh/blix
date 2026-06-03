#version 450

#include "bloom.glsl"

// Separable Gaussian blur (9-tap). Direction + texel size arrive as
// uTexelStep (push constant) — run once horizontal, once vertical.

layout(set = 0, binding = 0) uniform sampler2D uSrc;

layout(push_constant) uniform PushConstants {
    vec2 uTexelStep;  // blurDir * (1 / targetResolution)
} pc;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

void main() {
    outColor = vec4(blix_gaussianBlur9(uSrc, vUv, pc.uTexelStep), 1.0);
}
