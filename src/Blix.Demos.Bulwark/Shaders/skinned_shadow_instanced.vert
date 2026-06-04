#version 450

// Instanced skinned shadow caster (Bulwark M4 Gate B) — depth-only. Same per-instance
// world-space palette as skinned_instanced.vert (set 3, BONE_COUNT mat4s at
// gl_InstanceIndex * BONE_COUNT), but transformed by the sun's shadow view-projection
// so the enemies' shadows track their walk. No colour output (shadow_caster.frag is
// the empty stub). Same set-3 layout as the scene shader, so they share one palette
// material.

#define BONE_COUNT 15

layout(std430, set = 3, binding = 0) readonly buffer Bones { mat4 m[]; } bones;

layout(push_constant) uniform Push { mat4 uShadowViewProj; };

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inBoneIndices;
layout(location = 4) in vec4 inBoneWeights;
layout(location = 5) in vec4 inTangent;

void main() {
    int base = gl_InstanceIndex * BONE_COUNT;
    mat4 skin = bones.m[base + int(inBoneIndices.x)] * inBoneWeights.x
              + bones.m[base + int(inBoneIndices.y)] * inBoneWeights.y
              + bones.m[base + int(inBoneIndices.z)] * inBoneWeights.z
              + bones.m[base + int(inBoneIndices.w)] * inBoneWeights.w;
    gl_Position = uShadowViewProj * (skin * vec4(inPosition, 1.0));
}
