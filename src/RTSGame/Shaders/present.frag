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
    vec4 uNight;   // x = how much the eye is allowed to go scotopic
};

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

#include "tonemap.glsl"

// <b>The Purkinje shift: what the eye does in the dark, rather than what the light does.</b> Below about
// a hundredth of daylight the cones stop answering and the rods take over, and rods have no colour and peak
// further into the blue — which is why a moonlit field looks blue-grey when the light falling on it is
// nearly white, and why deep shadow at noon looks blue too.
//
// Per pixel rather than per frame, because that is what actually happens: rod dominance is local, so the
// dark parts of a bright scene lose their colour while the lit parts keep it. A whole-frame version would
// drain a sunlit meadow because a corner of it was in shadow.
//
// This is a correction to the *observer*, so it belongs here and not in the world shader. The light in the
// scene is what it is; this is the eye receiving it.
vec3 blix_scotopic(vec3 hdr, float amount) {
    float luma = dot(hdr, vec3(0.2126, 0.7152, 0.0722));
    // Rods have it entirely below 0.02 and nothing above 0.30, which is roughly six stops and about the
    // width of the real mesopic range.
    float rods = 1.0 - smoothstep(0.02, 0.30, luma);
    // Blue-shifted grey at the same luminance, so this changes hue and not exposure — a night that also
    // goes dark is two effects and only one of them is this one.
    vec3 rodResponse = vec3(luma) * vec3(0.74, 0.88, 1.26);
    return mix(hdr, rodResponse, rods * amount);
}

void main() {
    vec3 hdr = texture(uHdr, vUv).rgb * uGrade.x;
    hdr = blix_scotopic(hdr, uNight.x);
    hdr = blix_saturate(hdr, uGrade.z);
    vec3 mapped = blix_tonemap(hdr, uGrade.y);
    outColor = vec4(blix_contrast(mapped, uGrade.w), 1.0);
}
