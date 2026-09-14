#version 450

// The skinned caster, reusing room_shadow.frag — which declares no push block and no bindings, so
// this stage is free to declare exactly what it needs and nothing has to be shaped to match it.
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;
layout(location = 3) in vec4 aBoneIndices;
layout(location = 4) in vec4 aBoneWeights;
layout(location = 5) in vec4 aTangent;

layout(set = 0, binding = 0) uniform ShadowFrame {
    mat4 uLightViewProjection;
};

layout(set = 1, binding = 0) uniform Skinned {
    mat4 uModel;
};

layout(set = 3, binding = 0) readonly buffer Bones {
    mat4 uBones[128];
};

void main()
{
    mat4 skin =
        uBones[int(aBoneIndices.x)] * aBoneWeights.x +
        uBones[int(aBoneIndices.y)] * aBoneWeights.y +
        uBones[int(aBoneIndices.z)] * aBoneWeights.z +
        uBones[int(aBoneIndices.w)] * aBoneWeights.w;

    gl_Position = uLightViewProjection * (uModel * (skin * vec4(aPosition, 1.0)));
}
