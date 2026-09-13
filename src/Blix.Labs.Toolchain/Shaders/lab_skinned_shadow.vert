#version 450

// The caster's half of skinning, instanced. A rig that casts its REST silhouette while its
// mesh walks is the classic symptom of forgetting this one, and it is easy to miss because
// the lit pass looks perfect — the shadow is the only thing that disagrees.
//
// Same set-3 layout and the same per-instance stride as lab_skinned.vert, so both passes bind
// ONE palette material in a frame and read the same poses out of it. Two layouts here would be
// two chances for the shadow to disagree with the body.

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;
layout(location = 3) in vec4 aBoneIndices;
layout(location = 4) in vec4 aBoneWeights;
layout(location = 5) in vec4 aTangent;

layout(set = 0, binding = 0) uniform ShadowFrame {
    mat4 uLightViewProjection;
};

// 1024 = LabRig.MaxBones (128) x LabRig.MaxInstances (8). The literal is here because the
// build's SPIR-V target does not pass -D, so this cannot be a define — which means the number
// lives in two files, and that is precisely what the probe checks: it reads the reflected
// block size back and fails non-zero if it stops matching the C# constants.
layout(std430, set = 3, binding = 0) readonly buffer Bones {
    mat4 m[1024];
} bones;

// <b>Sixteen bytes, and no model matrix.</b> The unskinned caster pushes a mat4 because it has
// to place its object; this one does not, because each instance's placement is already baked
// into its palette. What it does need is the per-instance stride, and lab_shadow.frag declares
// no push block at all — so unlike the lit pair, this block is free to be exactly what the
// stage uses rather than shaped to match a fragment stage.
//
// The stride is NOT smuggled into a spare matrix element. A matrix element that secretly holds
// a count is a lie about what the value is, and the next reader deserves better than finding an
// integer in M14.
layout(push_constant) uniform Push {
    vec4 uSkin;           // x = bones per instance
};

void main()
{
    int base = gl_InstanceIndex * int(uSkin.x);

    mat4 skin = bones.m[base + int(aBoneIndices.x)] * aBoneWeights.x
              + bones.m[base + int(aBoneIndices.y)] * aBoneWeights.y
              + bones.m[base + int(aBoneIndices.z)] * aBoneWeights.z
              + bones.m[base + int(aBoneIndices.w)] * aBoneWeights.w;

    // World-space palette, so there is nothing between the skin and the sun's projection.
    gl_Position = uLightViewProjection * (skin * vec4(aPosition, 1.0));
}
