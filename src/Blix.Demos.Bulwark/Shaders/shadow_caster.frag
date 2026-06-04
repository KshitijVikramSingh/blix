#version 450

// Depth-only: no colour output. The depth attachment captures gl_FragDepth from
// the rasterizer. The engine requires a fragment stage on every graphics pipeline,
// so this stub satisfies that.
void main() {}
