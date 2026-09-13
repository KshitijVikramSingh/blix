#version 450

// The caster's half of skinning. A rig that casts its REST silhouette while its mesh
// walks is the classic symptom of forgetting this one, and it is easy to miss because the
// lit pass looks perfect — the shadow is the only thing that disagrees.
//
// Pairs with lab_shadow.frag (which writes nothing) and pushes the same 64-byte block the
// unskinned caster does, so the renderer's CasterPushBytes constant covers both.

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;
layout(location = 3) in vec4 aBoneIndices;
layout(location = 4) in vec4 aBoneWeights;
layout(location = 5) in vec4 aTangent;

layout(set = 0, binding = 0) uniform ShadowFrame {
    mat4 uLightViewProjection;
};

// Same set and same fixed bound as lab_skinned.vert — one palette material is bound into
// both passes in a frame, which is safe precisely because both read the same pose.
layout(std430, set = 3, binding = 0) readonly buffer Bones {
    mat4 m[128];
} bones;

layout(push_constant) uniform Push {
    mat4 uModel;
};

void main()
{
    mat4 skin = bones.m[int(aBoneIndices.x)] * aBoneWeights.x
              + bones.m[int(aBoneIndices.y)] * aBoneWeights.y
              + bones.m[int(aBoneIndices.z)] * aBoneWeights.z
              + bones.m[int(aBoneIndices.w)] * aBoneWeights.w;

    gl_Position = uLightViewProjection * (uModel * (skin * vec4(aPosition, 1.0)));
}
