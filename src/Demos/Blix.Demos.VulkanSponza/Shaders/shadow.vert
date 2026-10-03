#version 450

// Depth-only OPAQUE shadow caster. No textures, so opaque casters (the bulk of the
// scene) allocate zero per-draw transient descriptor sets; the only set is 3, the
// scene's placements (instances.glsl), a material bound as it is and never allocated
// per draw. MASK foliage uses shadow_mask.{vert,frag} instead for alpha cutout.
//
// Push layout (64 bytes, Vertex): mat4 uCascadeViewProj (0).

#include "instances.glsl"

layout(push_constant) uniform PushConstants {
    mat4 uCascadeViewProj;
} pc;

// Only position is needed; the VB is the tangent layout but unused
// attributes (normal/tangent/uv) don't need shader declarations.
layout(location = 0) in vec3 inPosition;

void main() {
    gl_Position = pc.uCascadeViewProj * instanceWorld() * vec4(inPosition, 1.0);
}
