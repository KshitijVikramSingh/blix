#version 450

// Passthrough sample of the offscreen hdr.

layout(set = 0, binding = 0) uniform sampler2D uOffscreen;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

void main() {
    outColor = texture(uOffscreen, vUv);
}
