#version 410 core

layout (location = 0) in vec3 aPosition;
layout (location = 3) in vec4 aBoneIndices;
layout (location = 4) in vec4 aBoneWeights;

uniform mat4 uModel;
uniform mat4 uLightViewProjection;
uniform mat4 uBones[64];

// Skinned shadow caster. Same skinning sum as skin.lit.vert but only the position
// matters -- depth is the only fragment output. Pairs with shadow.frag (or any
// trivial fragment that just writes gl_FragDepth implicitly). Vertex attribute
// locations 0, 3, 4 match the skinned vertex layout exactly; locations 1 (normal)
// and 2 (texcoord) are present in the buffer but not declared here -- OpenGL
// leaves them unused, no error.
void main()
{
    mat4 skinMatrix =
        aBoneWeights.x * uBones[int(aBoneIndices.x)] +
        aBoneWeights.y * uBones[int(aBoneIndices.y)] +
        aBoneWeights.z * uBones[int(aBoneIndices.z)] +
        aBoneWeights.w * uBones[int(aBoneIndices.w)];

    vec4 skinnedPosition = skinMatrix * vec4(aPosition, 1.0);
    gl_Position = uLightViewProjection * uModel * skinnedPosition;
}
