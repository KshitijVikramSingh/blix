#version 450

// Instanced skinned enemy (Bulwark M4 Gate B) — ONE draw for the whole crowd.
// Each instance's world-space bone palette is uJointCount mat4s starting at
// gl_InstanceIndex * uJointCount in the engine's set-3 bone block (skinning.glsl). The palette is pre-baked
// into WORLD space on the CPU (skin × model per enemy), so there's no per-instance
// model — the instance is fully described by its palette slot. Lighting/shadow
// outputs match cube.vert so the shared cube.frag shades it shadow-aware.
//
// The stride is the skin's JOINT count (BonePaletteSet.JointCount), pushed per draw (Model.CreateBoneBuffers
// sizes the block), so the shader serves a skin of any size. Not the skeleton's bone count. The row-major bytes read column-major in GLSL = transpose,
// so skin * v is the row-vector product (F-016).

#include "skinning.glsl"

layout(push_constant) uniform Push {
    mat4 uViewProjection;
    vec4 uCamPos;
    vec4 uSunDir;
    mat4 uSunShadowVP;
    int uJointCount;
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
    mat4 skin = blix_skin(inBoneIndices, inBoneWeights, gl_InstanceIndex * uJointCount);

    vec4 world = skin * vec4(inPosition, 1.0);
    gl_Position = uViewProjection * world;
    vNormal = normalize(mat3(skin) * inNormal);
    vTint = kEnemyTint;
    vWorldPos = world.xyz;
    vSunShadowCoord = uSunShadowVP * world;
}
