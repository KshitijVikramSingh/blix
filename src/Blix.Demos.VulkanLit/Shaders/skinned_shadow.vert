#version 450

// Skinned shadow caster. Applies the same bone-palette skinning as
// skinned_lit.vert but only writes depth (no varyings, no color output).

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    vec3 uSunDirection;
    float uSunIntensity;
    vec3 uAmbientColor;
    float uAmbientIntensity;
    mat4 uSunShadowVP;
} frame;

layout(set = 3, binding = 0) readonly buffer Bones {
    mat4 m[];
} bones;

layout(push_constant) uniform PushConstants {
    mat4 uModel;
} pc;

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

    gl_Position = frame.uSunShadowVP * pc.uModel * skin * vec4(inPosition, 1.0);
}
