#version 450

// Depth pre-pass fragment stage for OPAQUE geometry — no colour output, depth
// comes from the rasterizer. Paired with lit.vert so gl_Position (and thus the
// written depth) is bit-identical to the lit pass, which then runs LessEqual +
// no-write to shade only the front-most fragment (overdraw killed).
//
// lit.vert forwards several varyings; this consumes exactly one of them.
//
// <b>It is not an empty fragment any more, and the reason is worth stating.</b> The half-resolution
// incident field reconstructed its normal from the depth buffer, which measured 5.31 mean sRGB
// against the lit pass — while the normal MAP's whole contribution to the ambient measured 0.90. So
// the error was never missing detail, it was that a depth-derived normal is wrong wherever depth is
// not a smooth height field: the canopy's thousands of disconnected silhouettes, double-sided
// curtains whose back faces the lit pass flips and depth cannot, and grazing angles.
//
// This pass already rasterises every one of those surfaces and already has the interpolated normal
// in hand. Writing it costs one target on a pass that was depth-only, and hands the field the
// answer instead of an inference. The normal MAP is deliberately not applied here: it would mean
// sampling and TBN-transforming in the pre-pass as well as the lit pass, to recover the 0.90.

layout(location = 0) in vec3 vNormalWorld;

layout(location = 0) out vec4 outNormal;

void main() {
    // Flipped for back faces exactly as lit.frag does it, which is most of the point for the
    // curtains: a two-sided sheet lit from behind needs the hemisphere it actually faces, and that
    // is the one piece of information a depth buffer fundamentally cannot carry.
    vec3 n = normalize(vNormalWorld);
    outNormal = vec4(gl_FrontFacing ? n : -n, 1.0);
}
