#version 410 core

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aTexCoord;
layout (location = 3) in vec4 aBoneIndices;
layout (location = 4) in vec4 aBoneWeights;
layout (location = 5) in vec4 aTangent;     // XYZ tangent, W = +/-1 bitangent sign

out vec2 textureCoordinate;
out vec3 worldNormal;
out vec3 worldTangent;
out vec3 worldBitangent;
out vec3 worldPositionOut;
out vec4 shadowCoord;

uniform mat4 uModel;
uniform mat4 uNormalMatrix;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat4 uLightViewProjection;
uniform mat4 uBones[64];

// Skinned variant pairing with skin.lit.frag. Outputs per-vertex tangent/bitangent
// alongside the standard varyings; the fragment shader uses these for the TBN
// instead of synthesising from screen-space derivatives. Tangents arrive in
// mesh-local space (per glTF), get skinned through the bone palette like the
// normal does, then transformed to world space by uNormalMatrix.
//
// If aTangent is zero (the importer's sentinel for "this glTF didn't provide
// TANGENT"), worldTangent and worldBitangent come out as zero too; the fragment
// shader detects this and falls back to derivative synthesis.
void main()
{
    mat4 skinMatrix =
        aBoneWeights.x * uBones[int(aBoneIndices.x)] +
        aBoneWeights.y * uBones[int(aBoneIndices.y)] +
        aBoneWeights.z * uBones[int(aBoneIndices.z)] +
        aBoneWeights.w * uBones[int(aBoneIndices.w)];

    vec4 skinnedPosition = skinMatrix * vec4(aPosition, 1.0);
    vec3 skinnedNormal   = mat3(skinMatrix) * aNormal;
    vec3 skinnedTangent  = mat3(skinMatrix) * aTangent.xyz;

    textureCoordinate = aTexCoord;
    worldNormal = mat3(uNormalMatrix) * skinnedNormal;
    worldTangent = mat3(uNormalMatrix) * skinnedTangent;
    // Bitangent = cross(N, T) * sign, all in world space. Done after world
    // transformation so handedness is preserved.
    worldBitangent = cross(worldNormal, worldTangent) * aTangent.w;

    vec4 worldPosition = uModel * skinnedPosition;
    worldPositionOut = worldPosition.xyz;
    shadowCoord = uLightViewProjection * worldPosition;
    gl_Position = uProjection * uView * worldPosition;
}
