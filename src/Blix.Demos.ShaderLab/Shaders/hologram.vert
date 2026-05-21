#version 410 core

layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aNormal;
layout (location = 2) in vec2 aTexCoord;

out vec3 worldNormal;
out vec3 worldPositionOut;
out vec3 objectPositionOut;
out float glitchMask;

uniform mat4 uModel;
uniform mat4 uNormalMatrix;
uniform mat4 uView;
uniform mat4 uProjection;
uniform float uTime;
uniform float uGlitchAmount;

float hash(float n)
{
    return fract(sin(n) * 43758.5453123);
}

void main()
{
    vec3 objectPosition = aPosition;
    float band = floor((objectPosition.y + uTime * 0.8) * 18.0);
    float glitch = (hash(band) * 2.0 - 1.0) * uGlitchAmount;
    objectPosition.x += glitch * 0.055;
    objectPosition.z += sin(uTime * 7.0 + objectPosition.y * 21.0) * uGlitchAmount * 0.025;

    vec4 worldPosition = uModel * vec4(objectPosition, 1.0);
    worldPositionOut = worldPosition.xyz;
    objectPositionOut = objectPosition;
    worldNormal = normalize(mat3(uNormalMatrix) * aNormal);
    glitchMask = abs(glitch);

    gl_Position = uProjection * uView * worldPosition;
}
