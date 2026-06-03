#pragma once

// Fullscreen-triangle generator. A vertex shader binds a dummy 3-vertex buffer
// (never sampled) and synthesises geometry from gl_VertexIndex: three vertices
// of one oversized triangle that, after clipping, covers the whole framebuffer
// -- cheaper and seam-free vs a two-triangle quad. The matching CPU primitive is
// Blix.Render.FullscreenPass (it owns the dummy buffer + draw).
//
// Vulkan clip space is +Y-down and the UV origin is top-left, so UV (0,0) pulls
// the framebuffer's first row.

// The three clip-space corners. vertexIndex is gl_VertexIndex (0..2).
vec2 blix_fullscreenTriangleNdc(int vertexIndex)
{
    vec2 corners[3] = vec2[3](
        vec2(-1.0, -1.0),
        vec2( 3.0, -1.0),
        vec2(-1.0,  3.0));
    return corners[vertexIndex];
}

// Convenience for post passes: the clip-space position at z = 0 (w = 1) plus the
// matching top-left-origin UV (0..2 across the oversized triangle, clipping to
// 0..1 over the visible screen). A sky/background pass that needs a different
// depth or a reconstructed ray uses blix_fullscreenTriangleNdc directly instead.
vec4 blix_fullscreenTriangle(int vertexIndex, out vec2 uv)
{
    vec2 uvs[3] = vec2[3](
        vec2(0.0, 0.0),
        vec2(2.0, 0.0),
        vec2(0.0, 2.0));
    uv = uvs[vertexIndex];
    return vec4(blix_fullscreenTriangleNdc(vertexIndex), 0.0, 1.0);
}
