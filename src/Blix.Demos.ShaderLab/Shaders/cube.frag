#version 410 core

// Static-mesh lit fragment shader. Per-frag-specifics: varyings, MRT outputs,
// and the dFdx/dFdy synthesised tangent frame for normal mapping. Everything
// else (PBR BRDF, lighting, IBL, shadow sampling) is shared with skin.lit.frag
// via pbr_core.glsl.

in vec2 textureCoordinate;
in vec3 worldNormal;
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
    return normalize(cotangentFrame(N, worldPos, uv) * tangentNormal);
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
