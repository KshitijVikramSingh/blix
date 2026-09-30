#version 450

#include "fullscreen.glsl"

// The sky: a fullscreen triangle pinned to the far plane. The stage draws it first in the lit pass
// with depth writes off, so it lands exactly where nothing else is, and inside the multisampled
// target, so a silhouette resolves against the sky rather than against a black clear.
layout(location = 0) out vec2 vNdc;

void main()
{
    vec2 uv;
    gl_Position = blix_fullscreenTriangle(gl_VertexIndex, uv);
    vNdc = gl_Position.xy / gl_Position.w;
    gl_Position = vec4(vNdc, 1.0, 1.0);
}
