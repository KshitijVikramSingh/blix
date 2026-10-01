#version 450

// Instanced skinned shadow caster (Bulwark M4 Gate B) — depth-only. Same per-instance
// world-space palette as skinned_instanced.vert (the engine's set-3 bone block, uJointCount mat4s at
// gl_InstanceIndex * uJointCount), but transformed by the sun's shadow view-projection
// so the enemies' shadows track their walk. No colour output (shadow_caster.frag is
// the empty stub). Same set-3 layout as the scene shader, so they share one palette
// material.

#include "skinning.glsl"

layout(push_constant) uniform Push { mat4 uShadowViewProj; int uJointCount; };

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inBoneIndices;
layout(location = 4) in vec4 inBoneWeights;
layout(location = 5) in vec4 inTangent;

void main() {
    mat4 skin = blix_skin(inBoneIndices, inBoneWeights, gl_InstanceIndex * uJointCount);
    gl_Position = uShadowViewProj * (skin * vec4(inPosition, 1.0));
}
