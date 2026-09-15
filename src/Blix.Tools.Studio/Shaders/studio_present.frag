#version 450

#include "tonemap.glsl"

layout(set = 0, binding = 0) uniform sampler2D uScene;

// <b>The scene's depth, carried across to the swapchain.</b> This pass blits a finished
// picture, so without it the swapchain's depth buffer is empty — and debug geometry drawn
// there afterwards tests against nothing and floats in front of the world, which reads as
// disorienting rather than as a bug. Writing gl_FragDepth costs early-Z on a fullscreen
// triangle that has nothing to reject.
layout(set = 0, binding = 1) uniform sampler2D uSceneDepth;

layout(set = 1, binding = 0) uniform Present {
    vec4 uParams;   // x = exposure, y = tonemap mode
};

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColour;

void main()
{
    vec3 hdr = texture(uScene, vUv).rgb * uParams.x;
    outColour = vec4(blix_tonemap(hdr, uParams.y), 1.0);
    gl_FragDepth = texture(uSceneDepth, vUv).r;
}
