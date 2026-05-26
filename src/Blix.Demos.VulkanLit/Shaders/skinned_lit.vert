#version 450

// Skinned lit vertex shader. Same per-frame UBO + varyings as lit.vert,
// plus bone-palette skinning from the set-3 SSBO. Pairs with lit.frag.

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    vec3 uSunDirection;
    float uSunIntensity;
    vec3 uAmbientColor;
    float uAmbientIntensity;
    mat4 uSunShadowVP;
    mat4 uSpot0ViewProj;
    vec4 uSpot0PosRange;
    vec4 uSpot0DirCosInner;
    vec4 uSpot0ColorCosOuter;
    mat4 uSpot1ViewProj;
    vec4 uSpot1PosRange;
    vec4 uSpot1DirCosInner;
    vec4 uSpot1ColorCosOuter;
    vec4 uPointPosFar;
    vec4 uPointColorRange;
    vec4 uLightEnable;
} frame;

layout(std430, set = 3, binding = 0) readonly buffer Bones {
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

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec4 vSunShadowCoord;
layout(location = 3) out vec3 vWorldPos;

void main() {
    mat4 skin = bones.m[int(inBoneIndices.x)] * inBoneWeights.x
              + bones.m[int(inBoneIndices.y)] * inBoneWeights.y
              + bones.m[int(inBoneIndices.z)] * inBoneWeights.z
              + bones.m[int(inBoneIndices.w)] * inBoneWeights.w;

    vec4 skinnedLocal = skin * vec4(inPosition, 1.0);
    vec3 skinnedNormal = mat3(skin) * inNormal;

    vec4 world = pc.uModel * skinnedLocal;
    gl_Position = frame.uViewProjection * world;

    vNormal = mat3(pc.uModel) * skinnedNormal;
    vUv = inUv;
    vWorldPos = world.xyz;
    vSunShadowCoord = frame.uSunShadowVP * world;
}
