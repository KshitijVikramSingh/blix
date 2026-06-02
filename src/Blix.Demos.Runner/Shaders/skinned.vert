#version 450

// Skinned character vertex shader (KayKit Rogue). Bone-palette skinning from the
// set-3 SSBO (same convention as VulkanLit's skinned_lit.vert), then world +
// view-projection from push constants. The character stays in the clear near-zone
// so it needs no fog; lighting is a simple sun term in the fragment stage.
//
// Matrices (palette + push) are raw row-major bytes read column-major in GLSL =
// transpose, so skin * v and uViewProjection * uModel * v match the engine's
// row-vector products.

layout(std430, set = 3, binding = 0) readonly buffer Bones {
    mat4 m[];
} bones;

layout(push_constant) uniform Push {
    mat4 uModel;
    mat4 uViewProjection;
};

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inBoneIndices;
layout(location = 4) in vec4 inBoneWeights;
layout(location = 5) in vec4 inTangent;

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vUv;

void main() {
    mat4 skin = bones.m[int(inBoneIndices.x)] * inBoneWeights.x
              + bones.m[int(inBoneIndices.y)] * inBoneWeights.y
              + bones.m[int(inBoneIndices.z)] * inBoneWeights.z
              + bones.m[int(inBoneIndices.w)] * inBoneWeights.w;

    vec4 skinnedLocal = skin * vec4(inPosition, 1.0);
    vec4 world = uModel * skinnedLocal;
    gl_Position = uViewProjection * world;

    vNormal = mat3(uModel) * (mat3(skin) * inNormal);
    vUv = inUv;
}
