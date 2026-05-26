#version 450

// Skinned lit vertex shader. Adds bone-palette skinning at set 3 binding 0
// (per-draw lifetime tier — too large for push constants, so it lives in
// a per-draw SSBO). Rest of the pipeline matches lit.vert.

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    vec3 uSunDirection;
    float uSunIntensity;
    vec3 uAmbientColor;
    float uAmbientIntensity;
    mat4 uSunShadowVP;
} frame;

// Set 3 = per-draw. Bone palette in a readonly SSBO (rest-pose → animated-
// pose per bone). std430 layout: tightly-packed mat4 array, 64-byte stride,
// matrices column-major. Length is implicit from the buffer binding size.
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
layout(location = 5) in vec4 inTangent;  // unused for now (room for normal-mapping later)

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec4 vShadowCoord;

void main() {
    // Linear blend skinning: skin = Σ weight_j × bones[index_j].
    // Weights are expected normalized; the importer (GltfImporter) makes
    // sure of that. Indices come in as float4 because Vulkan vertex
    // attributes are typed Float* (an iuvec4 path would need a separate
    // attribute format — float bitcasting is the simpler convention the
    // engine already uses for skinned geometry).
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
    vShadowCoord = frame.uSunShadowVP * world;
}
