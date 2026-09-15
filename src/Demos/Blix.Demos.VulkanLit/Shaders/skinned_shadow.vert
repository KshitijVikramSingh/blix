#version 450

// Skinned light-agnostic shadow caster. Same push-constant shadow VP as
// shadow.vert, plus bone-palette skinning from the set-3 SSBO.
//
// Push layout: mat4 uModel (offset 0), mat4 uShadowViewProj (offset 64).

layout(push_constant) uniform PushConstants {
    mat4 uModel;
    mat4 uShadowViewProj;
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

void main() {
    mat4 skin = bones.m[int(inBoneIndices.x)] * inBoneWeights.x
              + bones.m[int(inBoneIndices.y)] * inBoneWeights.y
              + bones.m[int(inBoneIndices.z)] * inBoneWeights.z
              + bones.m[int(inBoneIndices.w)] * inBoneWeights.w;

    gl_Position = pc.uShadowViewProj * pc.uModel * skin * vec4(inPosition, 1.0);
}
