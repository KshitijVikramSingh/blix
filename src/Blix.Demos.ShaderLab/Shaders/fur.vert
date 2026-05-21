#version 410 core

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aTexCoord;

out vec2 textureCoordinate;
out vec3 worldNormal;
out vec3 worldPositionOut;
out float furLayer;

uniform mat4 uModel;
uniform mat4 uNormalMatrix;
uniform mat4 uView;
uniform mat4 uProjection;

uniform float uShellIndex;
uniform float uShellCount;
uniform float uFurLength;
uniform float uWindStrength;
uniform float uTime;

void main()
{
    float denom = max(uShellCount - 1.0, 1.0);
    furLayer = clamp(uShellIndex / denom, 0.0, 1.0);

    vec3 N = normalize(mat3(uNormalMatrix) * aNormal);
    vec4 baseWorld = uModel * vec4(aPosition, 1.0);

    float sway = sin(uTime * 1.7 + baseWorld.x * 5.1 + baseWorld.z * 3.7) * 0.5 + 0.5;
    vec3 wind = vec3(0.7, 0.15, -0.35) * (sway * uWindStrength * furLayer * furLayer);
    vec3 worldPosition = baseWorld.xyz + N * (furLayer * uFurLength) + wind;

    textureCoordinate = aTexCoord;
    worldNormal = N;
    worldPositionOut = worldPosition;
    gl_Position = uProjection * uView * vec4(worldPosition, 1.0);
}
