#version 450

// Present: copy the HDR scene target to the swapchain. Passthrough for now (the
// daylight palette is already display-range); the HDR target + this pass are the
// hook for a future tonemap / bloom composite.
layout(set = 0, binding = 0) uniform sampler2D uHdr;

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

void main() {
    outColor = texture(uHdr, vUv);
}
