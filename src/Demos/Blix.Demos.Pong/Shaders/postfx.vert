#version 450

#include "fullscreen.glsl"

// Fullscreen-triangle present (same shape as the Vulkan demos' present.vert):
// the triangle is generated from gl_VertexIndex by blix_fullscreenTriangle;
// vertex inputs are declared only for pipeline-layout compatibility and ignored.
// UV origin is top-left, matching the offscreen image's row order (the
// SpriteBatch rendered it with a top-left-origin ortho).
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;

layout(location = 0) out vec2 vScreenUV;

void main()
{
    gl_Position = blix_fullscreenTriangle(gl_VertexIndex, vScreenUV);
}
