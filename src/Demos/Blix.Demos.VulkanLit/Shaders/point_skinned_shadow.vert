#version 450

// Skinned point-light cube shadow caster. Same push layout + world-position
// output as point_shadow.vert, plus bone-palette skinning. Pairs with
// point_shadow.frag.

layout(push_constant) uniform PushConstants {
    mat4 uModel;
    mat4 uFaceViewProj;
    vec4 uLightPosFar;
} pc;

layout(std430, set = 3, binding = 0) readonly buffer Bones {
    mat4 m[];
} bones;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inBoneIndices;
layout(location = 4) in vec4 inBoneWeights;
layout(location = 5) in vec4 inTangent;

layout(location = 0) out vec3 vWorld;

void main() {
    mat4 skin = bones.m[int(inBoneIndices.x)] * inBoneWeights.x
              + bones.m[int(inBoneIndices.y)] * inBoneWeights.y
              + bones.m[int(inBoneIndices.z)] * inBoneWeights.z
              + bones.m[int(inBoneIndices.w)] * inBoneWeights.w;

    vec4 world = pc.uModel * skin * vec4(inPosition, 1.0);
    vWorld = world.xyz;
    gl_Position = pc.uFaceViewProj * world;
}
