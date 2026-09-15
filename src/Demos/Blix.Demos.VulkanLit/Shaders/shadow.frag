#version 450

// Depth-only pass: no color output. The depth attachment captures
// gl_FragDepth (built-in, set by the rasterizer from gl_Position.z/w).
// The engine still requires a fragment stage on every graphics pipeline,
// so this stub exists to satisfy that contract.

void main() {}
