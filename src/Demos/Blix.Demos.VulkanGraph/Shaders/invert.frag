#version 450

// Color invert pass: outColor = 1.0 − sample. Visible proof of multi-pass
// graph dependency — if the Read edge to hdr is wired correctly, the
// scene pass's output shows up inverted on the swapchain. If the edge is
// broken, the present pass shows garbage / black / the previous frame's
// content.

layout(set = 0, binding = 0) uniform sampler2D uHdr;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

void main() {
    vec3 hdr = texture(uHdr, vUv).rgb;
    outColor = vec4(1.0 - hdr, 1.0);
}
