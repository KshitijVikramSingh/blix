// skinning.glsl — the bone palette a skinned vertex shader reads, and the linear-blend sum over it.
//
// The block is a convention (conventions §10): set 3, binding 0, std430, one mat4 per bone, and for N
// bodies N palettes back to back, each BoneCount long. Blix.BoneBuffers writes it; a skinned program
// that includes this reads it. Palettes are row-vector matrices uploaded untransposed (conventions §2),
// so `skin * v` here is `v_row * skin` on the CPU.
//
// The array is unsized: how many palettes there are is the model's, not the shader's. Model.CreateBoneBuffers
// sizes each skin's buffer (bones x bodies x 64 bytes) when it makes the material, so one program serves
// a rig of any size, up to what the device binds (maxStorageBufferRange, which the device checks).
#ifndef BLIX_SKINNING_GLSL
#define BLIX_SKINNING_GLSL

layout(std430, set = 3, binding = 0) readonly buffer BlixBones { mat4 m[]; } blix_bones;

// The skinning matrix for one vertex: its four joints (indices, as floats) and weights, in the palette
// that starts at `base` — 0 for a single body, gl_InstanceIndex * boneCount for an instanced one.
mat4 blix_skin(vec4 joints, vec4 weights, int base)
{
    return blix_bones.m[base + int(joints.x)] * weights.x
         + blix_bones.m[base + int(joints.y)] * weights.y
         + blix_bones.m[base + int(joints.z)] * weights.z
         + blix_bones.m[base + int(joints.w)] * weights.w;
}

#endif
