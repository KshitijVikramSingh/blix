#version 450

// Light-agnostic depth-only shadow caster. The shadow view-projection is
// supplied per-draw via push constants (alongside the model matrix) so a
// SINGLE shadow pipeline serves the sun (ortho), spot lights (perspective),
// and every point-light cube face (perspective) — the pass just pushes a
// different uShadowViewProj. No per-frame UBO needed.
//
// Push layout: mat4 uModel (offset 0), mat4 uShadowViewProj (offset 64).

layout(push_constant) uniform PushConstants {
    mat4 uModel;
    mat4 uShadowViewProj;
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

void main() {
    gl_Position = pc.uShadowViewProj * pc.uModel * vec4(inPosition, 1.0);
}
