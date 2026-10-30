#version 450
// The scene as the glass sees what is behind it, level 0 (SponzaLoop.GlassBackdrop.cs): the lit pass's colour before the frosted and thick panes are drawn, a straight copy.
#include "fullscreen.glsl"

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 1) uniform sampler2D uSrc;

void main() {
    outColor = vec4(texelFetch(uSrc, ivec2(gl_FragCoord.xy), 0).rgb, 1.0);
}
