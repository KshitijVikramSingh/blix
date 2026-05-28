#version 450

// Final present: composite bloom, then exposure + ACES filmic tonemap of
// the linear HDR target. The swapchain is sRGB, so the hardware encodes
// linear→sRGB on write — we output LINEAR color here.

layout(set = 0, binding = 0) uniform sampler2D uHdr;
layout(set = 0, binding = 1) uniform sampler2D uBloom;

layout(push_constant) uniform PushConstants {
    float uExposure;
    float uBloomIntensity;
} pc;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

vec3 aces(vec3 x) {
    const float a = 2.51;
    const float b = 0.03;
    const float c = 2.43;
    const float d = 0.59;
    const float e = 0.14;
    return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

void main() {
    vec3 hdr = texture(uHdr, vUv).rgb;
    vec3 bloom = texture(uBloom, vUv).rgb;
    vec3 c = (hdr + bloom * pc.uBloomIntensity) * pc.uExposure;
    outColor = vec4(aces(c), 1.0);
}
