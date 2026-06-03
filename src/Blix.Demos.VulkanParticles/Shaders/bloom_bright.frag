#version 450

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
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
    float contrib = max(luma - pc.uThreshold, 0.0) / max(luma, 1e-4);
    outColor = vec4(c * contrib, 1.0);
}
