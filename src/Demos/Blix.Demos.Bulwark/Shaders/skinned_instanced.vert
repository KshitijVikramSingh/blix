#version 450

// Instanced skinned enemy (Bulwark M4 Gate B) — ONE draw for the whole crowd.
// Each instance's world-space bone palette is BONE_COUNT mat4s starting at
// gl_InstanceIndex * BONE_COUNT in the shared set-3 SSBO. The palette is pre-baked
// into WORLD space on the CPU (skin × model per enemy), so there's no per-instance
// model — the instance is fully described by its palette slot. Lighting/shadow
// outputs match cube.vert so the shared cube.frag shades it shadow-aware.
//
// BONE_COUNT is fixed to the enemy skeleton (the loader asserts it); the row-major
// bytes read column-major in GLSL = transpose, so bones[j] * v is the row-vector
// product (F-016).

#define BONE_COUNT 15

layout(std430, set = 3, binding = 0) readonly buffer Bones { mat4 m[]; } bones;

layout(push_constant) uniform Push {
    mat4 uViewProjection;
    vec4 uCamPos;
    vec4 uSunDir;
    mat4 uSunShadowVP;
};

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inBoneIndices;
layout(location = 4) in vec4 inBoneWeights;
layout(location = 5) in vec4 inTangent;

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec4 vTint;
layout(location = 2) out vec3 vWorldPos;
layout(location = 3) out vec4 vSunShadowCoord;

const vec4 kEnemyTint = vec4(0.82, 0.42, 0.30, 1.0);   // Gate B: constant (HP tint = polish)

void main() {
    int base = gl_InstanceIndex * BONE_COUNT;
    mat4 skin = bones.m[base + int(inBoneIndices.x)] * inBoneWeights.x
              + bones.m[base + int(inBoneIndices.y)] * inBoneWeights.y
              + bones.m[base + int(inBoneIndices.z)] * inBoneWeights.z
              + bones.m[base + int(inBoneIndices.w)] * inBoneWeights.w;

    vec4 world = skin * vec4(inPosition, 1.0);
    gl_Position = uViewProjection * world;
    vNormal = normalize(mat3(skin) * inNormal);
    vTint = kEnemyTint;
    vWorldPos = world.xyz;
    vSunShadowCoord = uSunShadowVP * world;
}
