#version 450

// Depth/normal pre-pass fragment stage for OPAQUE geometry. Depth comes from the rasterizer;
// geometric world normal is the sole colour output. Paired with lit.vert so gl_Position and the
// written depth are bit-identical to the LessEqual, no-write lit pass.
//
// lit.vert forwards several varyings; this consumes exactly one of them.
//
// The normal target supplies the half-resolution incident field with rasterized surface orientation,
// including flipped back faces. Depth-derived normals measured 5.31 mean sRGB from the inline path,
// while omitting the normal map accounts for 0.90; applying the map here would duplicate its texture
// sampling and TBN work in both passes for the smaller term.

layout(location = 0) in vec3 vNormalWorld;

layout(location = 0) out vec4 outNormal;

void main() {
    // Match lit.frag's back-face convention; depth alone cannot recover the facing hemisphere of a
    // two-sided sheet.
    vec3 n = normalize(vNormalWorld);
    outNormal = vec4(gl_FrontFacing ? n : -n, 1.0);
}
