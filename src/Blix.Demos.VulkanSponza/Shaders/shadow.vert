#version 450

// Depth-only OPAQUE shadow caster. Push-only — no descriptor sets — so opaque
// casters (the bulk of the scene) allocate zero per-draw transient descriptor
// sets. MASK foliage uses shadow_mask.{vert,frag} instead for alpha cutout.
//
// Push layout (128 bytes, Vertex): mat4 uModel (0), mat4 uCascadeViewProj (64).

layout(push_constant) uniform PushConstants {
    mat4 uModel;
    mat4 uCascadeViewProj;
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

void main() {
    gl_Position = pc.uCascadeViewProj * pc.uModel * vec4(inPosition, 1.0);
}
