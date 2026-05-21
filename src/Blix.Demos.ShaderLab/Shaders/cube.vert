#version 410 core

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aTexCoord;

out vec2 textureCoordinate;
out vec3 worldNormal;
out vec3 worldPositionOut;
out vec4 shadowCoord;

uniform mat4 uModel;
uniform mat4 uNormalMatrix;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat4 uLightViewProjection;

void main()
{
    textureCoordinate = aTexCoord;
    worldNormal = mat3(uNormalMatrix) * aNormal;
    vec4 worldPosition = uModel * vec4(aPosition, 1.0);
    worldPositionOut = worldPosition.xyz;
    shadowCoord = uLightViewProjection * worldPosition;
    gl_Position = uProjection * uView * worldPosition;
}
