#version 410 core

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aTexCoord;

out vec3 worldNormal;
out vec3 worldPositionOut;
out vec3 viewNormal;
out vec3 viewPositionOut;

uniform mat4 uModel;
uniform mat4 uNormalMatrix;
uniform mat4 uView;
uniform mat4 uProjection;

void main()
{
    worldNormal = mat3(uNormalMatrix) * aNormal;
    vec4 worldPosition = uModel * vec4(aPosition, 1.0);
    worldPositionOut = worldPosition.xyz;
    vec4 viewPosition = uView * worldPosition;
    viewPositionOut = viewPosition.xyz;
    viewNormal = mat3(uView) * worldNormal;
    gl_Position = uProjection * viewPosition;
}
