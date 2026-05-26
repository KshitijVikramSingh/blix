#version 450

// Fullscreen-triangle invert pass. Identical vertex shape to present.vert
// (three positions from gl_VertexIndex clipping to a fullscreen triangle).
// Vertex inputs declared for engine-pipeline-layout compatibility but
// unused — gl_VertexIndex drives everything.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;

layout(location = 0) out vec2 vUv;

void main() {
    vec2 positions[3] = vec2[3](
        vec2(-1.0, -1.0),
        vec2( 3.0, -1.0),
        vec2(-1.0,  3.0)
    );
    vec2 uvs[3] = vec2[3](
        vec2(0.0, 0.0),
        vec2(2.0, 0.0),
        vec2(0.0, 2.0)
    );
    gl_Position = vec4(positions[gl_VertexIndex], 0.0, 1.0);
    vUv = uvs[gl_VertexIndex];
}
