#version 450

// The skinned vertex stage, reusing room_lit.frag unchanged — skinning is a vertex-stage concern,
// so there is one more vertex shader here and no new fragment shader at all.
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;
layout(location = 3) in vec4 aBoneIndices;
layout(location = 4) in vec4 aBoneWeights;
layout(location = 5) in vec4 aTangent;

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    mat4 uSunViewProjection;
    vec4 uCameraPosition;
    vec4 uSunDirection;
    vec4 uSunColour;
};

// <b>The model matrix is a per-DRAW UNIFORM, not a push constant, and that is not a style choice.</b>
// room_lit.frag declares a 32-byte push block (base colour + material) and this stage reuses that
// fragment stage. Adding a mat4 to the push here would make the two stages reflect DIFFERENT push
// ranges over the same bytes — the emit path sums them and the payload gets measured against the
// wrong total, which is the trap the toolchain lab's instancing work documented in as many words.
//
// Sets 0-1 became genuinely per-draw when the uniform arena landed (the character arc's prologue),
// so a matrix that changes per draw belongs here now and would have raced here before.
layout(set = 1, binding = 1) uniform Skinned {
    mat4 uModel;
};

// Fixed-size because spirv-cross reflects an unbounded array as block_size 0, and MaterialBindings
// needs a size to allocate. Short writes are legal: a 41-bone rig uploads 2,624 of these bytes.
layout(set = 3, binding = 0) readonly buffer Bones {
    mat4 uBones[128];
};

layout(location = 0) out vec3 vWorld;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;

void main()
{
    // The palette is InverseBindPose * world per bone, so this is the standard four-weight blend.
    mat4 skin =
        uBones[int(aBoneIndices.x)] * aBoneWeights.x +
        uBones[int(aBoneIndices.y)] * aBoneWeights.y +
        uBones[int(aBoneIndices.z)] * aBoneWeights.z +
        uBones[int(aBoneIndices.w)] * aBoneWeights.w;

    vec4 posed = skin * vec4(aPosition, 1.0);
    vec4 world = uModel * posed;

    vWorld = world.xyz;
    vNormal = normalize(mat3(uModel) * (mat3(skin) * aNormal));
    vUv = aTexCoord;
    gl_Position = uViewProjection * world;
}
