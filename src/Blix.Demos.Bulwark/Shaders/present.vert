#version 450

// Fullscreen-triangle present. Positions from gl_VertexIndex; vertex inputs declared
// for pipeline-layout compatibility and ignored.
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

layout(location = 0) out vec2 vUv;

void main() {
    vec2 pos[3] = vec2[3](vec2(-1.0, -1.0), vec2(3.0, -1.0), vec2(-1.0, 3.0));
    vec2 uv[3]  = vec2[3](vec2(0.0, 0.0), vec2(2.0, 0.0), vec2(0.0, 2.0));
    vUv = uv[gl_VertexIndex];
    gl_Position = vec4(pos[gl_VertexIndex], 0.0, 1.0);
}
