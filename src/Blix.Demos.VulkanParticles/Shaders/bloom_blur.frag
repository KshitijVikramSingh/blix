#version 450

// Separable Gaussian blur (9-tap). Direction + texel size arrive as
// uTexelStep (push constant) — run once horizontal, once vertical.

layout(set = 0, binding = 0) uniform sampler2D uSrc;

layout(push_constant) uniform PushConstants {
    vec2 uTexelStep;  // blurDir * (1 / targetResolution)
} pc;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

void main() {
    float w[5] = float[](0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216);
    vec3 sum = texture(uSrc, vUv).rgb * w[0];
    for (int i = 1; i < 5; i++) {
        sum += texture(uSrc, vUv + pc.uTexelStep * float(i)).rgb * w[i];
        sum += texture(uSrc, vUv - pc.uTexelStep * float(i)).rgb * w[i];
    }
    outColor = vec4(sum, 1.0);
}
