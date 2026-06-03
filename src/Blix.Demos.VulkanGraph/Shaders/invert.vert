#version 450

#include "fullscreen.glsl"

// Fullscreen-triangle invert pass. Same vertex shape as present.vert.
// Vertex inputs declared for engine-pipeline-layout compatibility but
// unused -- blix_fullscreenTriangle drives everything from gl_VertexIndex.
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;

layout(location = 0) out vec2 vUv;

void main() {
    gl_Position = blix_fullscreenTriangle(gl_VertexIndex, vUv);
}
