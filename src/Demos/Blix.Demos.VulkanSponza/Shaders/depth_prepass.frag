#version 450

// Depth pre-pass fragment stage for OPAQUE geometry — no colour output, depth
// comes from the rasterizer. Paired with lit.vert so gl_Position (and thus the
// written depth) is bit-identical to the lit pass, which then runs LessEqual +
// no-write to shade only the front-most fragment (overdraw killed).
//
// lit.vert forwards several varyings; an empty fragment simply ignores them.

void main() { }
