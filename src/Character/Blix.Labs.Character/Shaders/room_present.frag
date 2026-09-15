#version 450

#include "tonemap.glsl"

layout(set = 0, binding = 0) uniform sampler2D uScene;

// The scene's depth carried onto the swapchain, so the debug pass that draws after this can
// depth-test against the room. Without it every gizmo floats in front of the world — and in this
// lab the gizmos ARE the subject: a contact normal drawn through a wall says nothing about which
// side of the wall it is on.
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
