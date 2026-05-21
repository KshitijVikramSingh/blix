#version 410 core

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aTexCoord;

out vec3 worldPosition;
out vec3 worldNormal;
out vec2 texCoord;
out vec4 shadowCoord;

uniform mat4 uModel;
uniform mat4 uNormalMatrix;
uniform mat4 uView;
uniform mat4 uProjection;
uniform mat4 uLightViewProjection;

void main()
{
    vec4 world = uModel * vec4(aPosition, 1.0);
    worldPosition = world.xyz;
    worldNormal = normalize(mat3(uNormalMatrix) * aNormal);
    texCoord = aTexCoord;
    shadowCoord = uLightViewProjection * world;
    gl_Position = uProjection * uView * world;
}
