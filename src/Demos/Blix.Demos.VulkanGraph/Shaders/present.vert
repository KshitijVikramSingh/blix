#version 450

#include "fullscreen.glsl"

// Fullscreen-triangle present. Vertex inputs are declared for pipeline-layout
// compatibility (the engine's VertexLayout requires a binding) but ignored --
// the triangle is generated from gl_VertexIndex by blix_fullscreenTriangle.
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;

layout(location = 0) out vec2 vUv;

void main() {
    gl_Position = blix_fullscreenTriangle(gl_VertexIndex, vUv);
}
