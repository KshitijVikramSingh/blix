#version 450

// Depth-only pass: no colour output. The engine requires a fragment stage on
// every graphics pipeline, so this stub satisfies that while the depth
// attachment captures gl_FragDepth (set by the rasterizer from gl_Position).

void main() {}
