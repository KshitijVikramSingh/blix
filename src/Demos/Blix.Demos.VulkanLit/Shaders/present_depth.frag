#version 450

// Debug depth visualizer. Samples a depth target's .r channel and shows
// it as grayscale. The sun shadow map (orthographic) reads as a clean
// linear gradient; the scene depth (perspective) is heavily non-linear so
// we apply a contrast curve to pull the mostly-near-1.0 values into a
// visible range — enough to confirm depth is being written, not a precise
// readout.

layout(set = 0, binding = 0) uniform sampler2D uDepth;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

void main() {
    float d = texture(uDepth, vUv).r;
    // Gamma-ish curve so perspective depth (clustered near 1.0) spreads
    // out. Harmless on linear ortho depth — just darkens slightly.
    float shaped = pow(clamp(d, 0.0, 1.0), 16.0);
    outColor = vec4(vec3(1.0 - shaped), 1.0);
}
