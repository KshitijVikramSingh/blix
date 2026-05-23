#version 410 core

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aTexCoord;

out vec3 worldPosition;
out vec3 worldNormal;
out vec2 texCoord;
// View-space depth (positive going forward) for the lit frag's cascade
// selection. Saves having to invert the projection or sample depth in the
// frag shader -- we already have it here for free.
out float viewDepth;

uniform mat4 uModel;
uniform mat4 uNormalMatrix;
uniform mat4 uView;
uniform mat4 uProjection;

void main()
{
    vec4 world = uModel * vec4(aPosition, 1.0);
    worldPosition = world.xyz;
    worldNormal = normalize(mat3(uNormalMatrix) * aNormal);
    texCoord = aTexCoord;
    vec4 viewPos = uView * world;
    viewDepth = -viewPos.z;  // -z because camera looks down -Z in OpenGL view
    gl_Position = uProjection * viewPos;
}
