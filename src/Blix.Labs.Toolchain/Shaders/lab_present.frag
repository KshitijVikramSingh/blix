#version 450

#include "tonemap.glsl"

layout(set = 0, binding = 0) uniform sampler2D uScene;

layout(set = 1, binding = 0) uniform Present {
    vec4 uParams;   // x = exposure, y = tonemap mode
};

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColour;

void main()
{
    vec3 hdr = texture(uScene, vUv).rgb * uParams.x;
    outColour = vec4(blix_tonemap(hdr, uParams.y), 1.0);
}
