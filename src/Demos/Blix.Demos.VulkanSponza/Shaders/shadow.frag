#version 450

// Depth-only fragment stage for OPAQUE shadow casters. No color attachment and
// no texture sampling — the rasterizer writes gl_FragDepth implicitly and the
// program declares no descriptor sets, so these draws cost zero transient
// descriptor allocations.

void main() {
}
