#version 450

// Straight passthrough sample of the offscreen render. The half-resolution
// upscale to swapchain size gives a slight softening — the visual proof
// that an intermediate buffer is in the pipeline rather than direct
// swapchain rendering.

layout(set = 0, binding = 0) uniform sampler2D uOffscreen;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

void main() {
    outColor = texture(uOffscreen, vUv);
}
