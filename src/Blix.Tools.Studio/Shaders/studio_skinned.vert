#version 450

// studio_lit.vert with a bone palette in front of it, and N of them. Pairs with studio_lit.frag
// unchanged — the fragment stage never learns that the vertices moved, which is the whole
// reason skinning is a vertex-stage concern and not a material one.
//
// <b>Always instanced, even for one body.</b> There is no separate single-rig shader: drawing
// one rig is drawing an instance count of one, through this same line of code. A second,
// simpler path for the common case is how "it works with one and breaks with three" becomes
// possible, and the lab exists to make the three-body case visible rather than to special-case
// around it.

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;
layout(location = 3) in vec4 aBoneIndices;
layout(location = 4) in vec4 aBoneWeights;
layout(location = 5) in vec4 aTangent;      // w is the bitangent's handedness, per glTF

// The PREFIX this stage reads, and nothing after it. studio_lit.frag's Frame block gained three
// cascade matrices at offset 64 and this one did not, so it still described the pre-cascade
// layout: uSunViewProjection where the fragment has uCascadeVP0, and uCameraPosition at 128 where
// the fragment has uCascadeVP1. Nothing drew wrong, because the only member read here is
// uViewProjection at offset 0 — the four below were declared and never used, which is precisely
// why it survived. Declaring the prefix is what studio_lit.vert does, and makes the two stages
// describe one block again.
layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
};

// The palettes: the engine's bone block at set 3 (skinning.glsl), sized here. Reflection reads an
// unsized array as a zero-byte buffer, and the bone buffer is allocated at the reflected size, so the
// capacity is a literal: 1024 = StudioRig.MaxBones (128) x StudioRig.MaxInstances (8). Blix.Test.Studio
// reads the reflected block size and fails if it stops matching the C# constants; StudioRig rejects an
// asset whose skin cannot fit before allocating its GPU resources. A short write is legal, so a small
// rig uploads only its live palettes and the tail is never read.
#define BLIX_BONE_CAPACITY 1024
#include "skinning.glsl"

layout(push_constant) uniform Push {
    mat4 uModel;
    vec4 uBaseColour;
    vec4 uMaterial;       // x = metallic, y = roughness, z = bones per instance
    // Declared so this stage's block matches studio_lit.frag's. A vertex and fragment stage sharing
    // a program must agree on the push block; when they disagree the reflected total is their SUM,
    // which is how a 112-byte payload came to meet a pipeline declaring 208.
    vec4 uExtra;
    vec4 uEmission;
};

layout(location = 0) out vec3 vWorld;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;
// White: the skinned layout carries no colour attribute and a character has no baked occlusion to
// carry. Written anyway because this stage shares studio_lit.frag, and a varying the fragment
// stage reads but no vertex stage writes is undefined — it would read as whatever was in the
// register, which is a bug that looks like a lighting bug.
layout(location = 3) out vec4 vColour;
layout(location = 4) out vec2 vUv1;
layout(location = 5) out vec4 vTangent;

void main()
{
    // <b>The stride comes from the draw, not from a #define.</b> uMaterial.z carries the RIG's
    // bone count, so a shader compiled once serves a 15-bone robot and a 41-bone rogue, and the
    // packing on the CPU cannot disagree with the reading here — the number travels with the data.
    // That is the one thing this does differently from the two consumers it copies from.
    //
    // It rides in uMaterial's spare .z because the push block has to stay byte-identical to the
    // one studio_lit.frag declares. Give this stage a wider block and the two stages reflect
    // different push ranges; the emit path sums range sizes and would then expect 208 bytes for a
    // 112-byte payload and refuse the draw. The alternative — a second fragment shader existing
    // for one float — is worse than a documented use of a slot the frag ignores.
    int base = gl_InstanceIndex * int(uMaterial.z);

    // The linear-blend skinning sum. Weights come normalised out of the importer; a rig whose
    // weights do not sum to one shrinks toward the origin, which is a thing the lab's skeleton
    // overlay makes visible (mesh drifts, bones do not).
    mat4 skin = blix_skin(aBoneIndices, aBoneWeights, base);

    // <b>uModel is identity on every instanced draw, and the multiply stays.</b> Each instance's
    // placement is baked into its palette on the CPU (Bulwark's shape — a world-space palette and
    // no instance buffer), because a per-draw push constant cannot vary per instance and there is
    // no compose order that lets a shared uModel sit between the skin and a per-instance placement.
    //
    // Kept rather than deleted so this stage still reflects the whole 96-byte push block that
    // studio_lit.frag declares. Drop it and glslc strips uModel from the vertex stage's reflection,
    // the two stages report different push ranges, and the emit path — which sums range sizes —
    // expects 128 bytes for a 96-byte payload and refuses the draw. One dead multiply per vertex
    // against a binding model that stays honest.
    vec4 posed = skin * vec4(aPosition, 1.0);
    vec4 world = uModel * posed;
    vWorld = world.xyz;

    // Through the skin matrix as well as the model — a bone's rotation turns its normals.
    // Uniform scale only, in this lab as in the unskinned path, so the upper 3x3 suffices.
    vNormal = normalize(mat3(uModel) * (mat3(skin) * aNormal));
    // The tangent turns with the bone exactly as the normal does; handedness rides along.
    vTangent = vec4(normalize(mat3(uModel) * (mat3(skin) * aTangent.xyz)), aTangent.w);

    vUv = aTexCoord;
    vColour = vec4(1.0);
    vUv1 = aTexCoord;   // the skinned layout carries one UV set
    gl_Position = uViewProjection * world;
}
