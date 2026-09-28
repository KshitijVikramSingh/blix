#version 450

#include "fullscreen.glsl"

layout(location = 0) out vec2 vUv;

void main()
{
    gl_Position = blix_fullscreenTriangle(gl_VertexIndex, vUv);
}
