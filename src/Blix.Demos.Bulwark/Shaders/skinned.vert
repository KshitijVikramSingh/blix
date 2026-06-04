#version 450

// Skinned enemy vertex shader (Bulwark M4 Gate A). Bone-palette skinning from the
// set-3 SSBO — but the palette is pre-baked into WORLD space on the CPU
// (bone = skin * model), so skinning lands the vertex directly in world. That keeps
// the push identical to cube.vert's (no per-object model matrix) and matches the
// Gate B instanced path, where each instance owns a world-space palette indexed by
// gl_InstanceIndex. Lighting/shadow outputs match cube.vert so the shared cube.frag
// shades it. Matrices are raw row-major bytes read column-major in GLSL = transpose,
// so bones[j] * v matches the engine's row-vector product (F-016).

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

// Gate A: a constant enemy tint (Gate B carries per-instance HP tint via instance data).
const vec4 kEnemyTint = vec4(0.82, 0.42, 0.30, 1.0);

void main() {
    mat4 skin = bones.m[int(inBoneIndices.x)] * inBoneWeights.x
              + bones.m[int(inBoneIndices.y)] * inBoneWeights.y
              + bones.m[int(inBoneIndices.z)] * inBoneWeights.z
              + bones.m[int(inBoneIndices.w)] * inBoneWeights.w;

    vec4 world = skin * vec4(inPosition, 1.0);   // palette is world-space (model baked in)
    gl_Position = uViewProjection * world;
    vNormal = normalize(mat3(skin) * inNormal);
    vTint = kEnemyTint;
    vWorldPos = world.xyz;
    vSunShadowCoord = uSunShadowVP * world;
}
