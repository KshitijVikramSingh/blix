#version 450

// Present: expose, grade and tonemap the HDR scene onto the swapchain.
//
// The swapchain is an sRGB surface, so this writes LINEAR and the present surface does the
// encode. Grading happens in HDR before the curve, which is the order the shared library
// asks for: a saturation lift applied after tonemapping is a lift applied to values that
// have already been clipped.

layout(set = 0, binding = 0) uniform sampler2D uHdr;

layout(push_constant) uniform Push {
    vec4 uGrade;   // x = exposure, y = tonemap mode, z = saturation, w = contrast
};

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

#include "tonemap.glsl"

void main() {
    vec3 hdr = texture(uHdr, vUv).rgb * uGrade.x;
    hdr = blix_saturate(hdr, uGrade.z);
    vec3 mapped = blix_tonemap(hdr, uGrade.y);
    outColor = vec4(blix_contrast(mapped, uGrade.w), 1.0);
}
