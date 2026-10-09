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
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outNormal;
#include "texel_bin.glsl"
layout(location = 6) flat in uint vSurfaceKey;
layout(location = 7) in vec4 vClipNow;
layout(location = 8) in vec4 vClipPrev;
layout(location = 9) in vec3 vWorldMotion;
// Stage 4e: which surface this pixel shows, its motion in uv since last frame and in world space (both now minus
// then). Single-sample passes only carry these targets; under MSAA the writes have nowhere to go and are dropped.
layout(location = 1) out uint outSurfaceKey;
layout(location = 2) out vec2 outVelocity;
layout(location = 3) out vec4 outMotion;

void main() {
    // Match lit.frag's back-face convention; depth alone cannot recover the facing hemisphere of a
    // two-sided sheet.
    vec3 n = normalize(vNormalWorld);
    vec3 shading = gl_FrontFacing ? n : -n;
    // w: the texel bins (texel_bin.glsl, texelFaceBins) of this pixel's FACE normal -- the plane of the triangle, from the world
    // position's screen derivatives (one triangle's within a quad), turned to the shading normal's side. The texels
    // are keyed by face; the interpolated normal's bin differed from it at a third of Sponza's hall.
    vec3 face = cross(dFdx(vWorldPos), dFdy(vWorldPos));
    face = dot(face, face) > 1e-20 ? normalize(face) : shading;
    if (dot(face, shading) < 0.0) face = -face;
    outNormal = vec4(shading, texelFaceBins(face));
    outSurfaceKey = vSurfaceKey;
    outVelocity = (vClipNow.xy / vClipNow.w - vClipPrev.xy / vClipPrev.w) * 0.5;
    outMotion = vec4(vWorldMotion, 0.0);
}
