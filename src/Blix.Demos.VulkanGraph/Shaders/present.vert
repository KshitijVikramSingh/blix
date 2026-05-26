#version 450

// Fullscreen-triangle present shader. Vertex inputs exist for pipeline-
// layout compatibility (the engine's VertexLayout requires at least one
// binding) but are ignored — positions are generated from gl_VertexIndex
// so we never read inPosition / inUv. Three vertices produce one giant
// triangle that's larger than the framebuffer; clipping reduces it to a
// fullscreen quad.
//
// Vulkan clip-space: +Y goes DOWN, so (-1,-1) is top-left and (3,-1)
// extends right past the screen. UV origin is top-left to match Vulkan's
// framebuffer pixel order, so sample UV (0,0) pulls from the offscreen
// image's first row (which we wrote with the cube's top).

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
