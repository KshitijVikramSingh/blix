#version 450

// The skinned caster: the same skinning as world_skinned.vert, into a cascade's depth map.
//
// <b>A posed body has to cast the shadow of the pose it is in</b>, not of its bind pose. Drawing the rest
// pose here would put a standing silhouette under a walking villager, which is worse than no shadow at all
// because it reads as a second body. So the palette is bound to this pass too and the vertex arithmetic is
// duplicated deliberately — the alternative is a shared include whose only caller-visible difference is
// which matrix it multiplies by at the end.
//
// No lean, for the reason world_skinned.vert gives; shadow_caster.vert leans its plants and this does not.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inBoneIndices;
layout(location = 4) in vec4 inBoneWeights;
layout(location = 5) in vec4 inTangent;

struct Instance {
    mat4 model;
    vec4 tint;
};

layout(std430, set = 2, binding = 0) readonly buffer Bones {
    mat4 m[];
} bones;

layout(set = 3, binding = 0, std430) readonly buffer Instances {
    Instance instances[];
};

layout(push_constant) uniform Push {
    mat4 uShadowViewProj;
    // <b>Its own block, and the same size as the leaning caster's on purpose.</b> A push block is one layout
    // per PIPELINE, not per pass, and this is its own pipeline — so the second vec4 is the bone stride here
    // where shadow_caster.vert has the wind there. Keeping the byte count equal means the cascade payload
    // buffer is one size for both, and only what is written into the last sixteen bytes differs.
    // x = bones per body; the rest is padding.
    vec4 uSkin;
};

#include "materials.glsl"

void main() {
    Instance inst = instances[gl_InstanceIndex];
    int base = gl_InstanceIndex * int(uSkin.x);
    mat4 skin = bones.m[base + int(inBoneIndices.x)] * inBoneWeights.x
              + bones.m[base + int(inBoneIndices.y)] * inBoneWeights.y
              + bones.m[base + int(inBoneIndices.z)] * inBoneWeights.z
              + bones.m[base + int(inBoneIndices.w)] * inBoneWeights.w;

    vec4 world = inst.model * (skin * vec4(inPosition, 1.0));
    gl_Position = uShadowViewProj * world;
}
