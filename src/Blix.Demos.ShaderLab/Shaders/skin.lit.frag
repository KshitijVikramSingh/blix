#version 410 core

// Skinned-mesh lit fragment shader. Same PBR core as cube.frag (via the
// pbr_core.glsl include); per-frag-specifics are the vertex TBN varyings and a
// perturbNormal that uses the interpolated TBN when the glTF provided
// TANGENT, falling back to dFdx/dFdy synthesis when not.

in vec2 textureCoordinate;
in vec3 worldNormal;
in vec3 worldTangent;
in vec3 worldBitangent;
in vec3 worldPositionOut;
in vec4 shadowCoord;
layout (location = 0) out vec4 fragColor;
layout (location = 1) out vec4 fragLuminance;
layout (location = 2) out vec4 fragNormal;

#include "pbr_core.glsl"

vec3 perturbNormal(vec3 N, vec3 worldPos, vec2 uv)
{
    vec3 tangentNormal = mix(
        vec3(0.0, 0.0, 1.0),
        texture(uNormalMap, uv).rgb * 2.0 - 1.0,
        uNormalScale);

    mat3 TBN;
    if (dot(worldTangent, worldTangent) > 1e-6)
    {
        vec3 T = normalize(worldTangent);
        vec3 B = normalize(worldBitangent);
        TBN = mat3(T, B, N);
    }
    else
    {
        TBN = cotangentFrame(N, worldPos, uv);
    }
    return normalize(TBN * tangentNormal);
}

void main()
{
    vec3 surfaceN = normalize(worldNormal);
    vec3 N = perturbNormal(surfaceN, worldPositionOut, textureCoordinate);
    PbrFragmentOutputs outputs = EvaluatePbr(N, worldPositionOut, textureCoordinate, shadowCoord);
    fragColor = outputs.color;
    fragLuminance = outputs.luminance;
    fragNormal = outputs.normal;
}
