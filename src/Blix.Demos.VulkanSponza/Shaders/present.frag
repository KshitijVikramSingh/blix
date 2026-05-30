#version 450

// Scaffold present: sample HDR, apply exposure, Reinhard tonemap, output
// to sRGB swapchain. Reinhard (not ACES) keeps the scaffold self-
// contained — when the engine shader library settles, swap to whichever
// operator the Sponza port wants.

layout(set = 0, binding = 0) uniform sampler2D uHdr;

layout(push_constant) uniform PushConstants {
    float uExposure;
} pc;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

void main() {
    vec3 hdr = texture(uHdr, vUv).rgb * pc.uExposure;
    vec3 mapped = hdr / (hdr + vec3(1.0));
    outColor = vec4(mapped, 1.0);
}
