#version 450
// A level of the scene-behind chain from the one above it (SponzaLoop.GlassBackdrop.cs): a 4x4-texel tent of the source by four bilinear taps, so a rough pane's blur is
// a smooth function of its roughness, not a ladder of box filters.
#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 1) uniform sampler2D uSrc;

void main() {
    vec2 texel = 1.0 / vec2(textureSize(uSrc, 0));
    vec3 sum = texture(uSrc, vUv + texel * vec2(-0.5, -0.5)).rgb + texture(uSrc, vUv + texel * vec2(0.5, -0.5)).rgb
             + texture(uSrc, vUv + texel * vec2(-0.5, 0.5)).rgb + texture(uSrc, vUv + texel * vec2(0.5, 0.5)).rgb;
    outColor = vec4(sum * 0.25, 1.0);
}
