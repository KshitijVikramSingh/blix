#version 450

// <b>Instanced skeletal skinning: one draw, N bodies, each in its own pose.</b>
//
// The palette holds every body's bones end to end and a body finds its slice by instance index, so nothing
// per-body has to be bound or pushed — which is what makes this an instanced draw rather than N draws. It is
// also why the readability proof and the "proper" path turned out to be the same code: indexing by
// gl_InstanceIndex costs nothing over indexing by zero.
//
// Outputs are byte-identical to world.vert's so world.frag is reused unchanged: a body is lit, fogged,
// shadowed and veiled by exactly the rules everything else in the settlement obeys. That is the point —
// a second lighting path for people would drift from the first within a session.
//
// Matrices (palette, instance and push) arrive as raw row-major System.Numerics bytes read column-major in
// GLSL, which is a transpose, so skin * v then model * v matches the engine's row-vector products. Skin
// first: the palette is mesh-space, the model matrix is what puts the mesh in the world.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
// The asset's own texture coordinates. Named vGround downstream because that is what the terrain puts here
// and world.frag reads one name; for a body nothing reads it.
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec4 inBoneIndices;
layout(location = 4) in vec4 inBoneWeights;
layout(location = 5) in vec4 inTangent;

struct Instance {
    mat4 model;
    vec4 tint;
};

// Set 2, because set 0 is the world's textures and set 3 is the instance buffer. One palette for every body
// on screen; see uSkin.x for the stride.
layout(std430, set = 2, binding = 0) readonly buffer Bones {
    mat4 m[];
} bones;

layout(set = 3, binding = 0, std430) readonly buffer Instances {
    Instance instances[];
};

// The push block, shared with every other world stage — Shaders/world_push.glsl.
#include "world_push.glsl"

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec4 vTint;
layout(location = 2) out vec3 vWorldPos;
layout(location = 3) out vec2 vGround;

#include "materials.glsl"

void main() {
    Instance inst = instances[gl_InstanceIndex];

    // This body's own slice of the shared palette.
    int base = gl_InstanceIndex * int(uSkin.x);
    mat4 skin = bones.m[base + int(inBoneIndices.x)] * inBoneWeights.x
              + bones.m[base + int(inBoneIndices.y)] * inBoneWeights.y
              + bones.m[base + int(inBoneIndices.z)] * inBoneWeights.z
              + bones.m[base + int(inBoneIndices.w)] * inBoneWeights.w;

    vec4 posed = skin * vec4(inPosition, 1.0);
    vec4 world = inst.model * posed;

    // <b>No wind lean.</b> world.vert leans everything it draws by height above its own root, which is right
    // for wheat and trees and absurd for a person: a villager is not a plant and must not sway with the gusts.
    gl_Position = uViewProjection * world;
    vNormal = normalize(mat3(inst.model) * (mat3(skin) * inNormal));
    vTint = inst.tint;
    vWorldPos = world.xyz;
    vGround = inUv;
}
