#version 450

// Fullscreen-triangle sky. Positions come from gl_VertexIndex (the dummy vertex inputs are
// declared for pipeline-layout compatibility and ignored), at the far plane (z = w = 1).
// The interpolated NDC goes to the fragment stage, which unprojects it to a per-pixel
// world-space view ray.
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

layout(location = 0) out vec2 vNdc;

void main() {
    vec2 corners[3] = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
    vNdc = corners[gl_VertexIndex];
    gl_Position = vec4(corners[gl_VertexIndex], 1.0, 1.0);
}
