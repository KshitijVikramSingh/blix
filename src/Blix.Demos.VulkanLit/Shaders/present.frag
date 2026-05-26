#version 450

// Final present: exposure + ACES filmic tonemap of the linear HDR target.
// The swapchain is sRGB (B8G8R8A8Srgb), so the hardware encodes linear→sRGB
// on write — we output LINEAR color here (no manual gamma).

layout(set = 0, binding = 0) uniform sampler2D uHdr;

layout(push_constant) uniform PushConstants {
    float uExposure;
} pc;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

// Narkowicz ACES filmic approximation — cheap, good highlight rolloff.
vec3 aces(vec3 x) {
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main() {
    vec3 hdr = texture(uHdr, vUv).rgb * pc.uExposure;
    outColor = vec4(aces(hdr), 1.0);
}
