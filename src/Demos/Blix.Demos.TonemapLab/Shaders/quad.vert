#version 450

#include "fullscreen.glsl"

// Fullscreen triangle from gl_VertexIndex; no vertex inputs (FullscreenPass.Layout declares none).
layout(location = 0) out vec2 vUv;

void main() {
    gl_Position = blix_fullscreenTriangle(gl_VertexIndex, vUv);
}
