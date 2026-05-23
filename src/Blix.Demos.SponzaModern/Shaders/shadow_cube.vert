#version 410 core

// Depth-only vertex shader for point-light cube shadow casting. Same vertex
// layout as the directional shadow pass; we just need world position in the
// fragment shader so it can write linear distance-to-light instead of the
// usual nonlinear projection depth.

layout (location = 0) in vec3 aPosition;

uniform mat4 uModel;
uniform mat4 uLightViewProjection;   // per-face: 90deg persp * lookAt(lightPos, lightPos + faceDir)

out vec3 vWorldPosition;

void main()
{
    vec4 wp = uModel * vec4(aPosition, 1.0);
    vWorldPosition = wp.xyz;
    gl_Position = uLightViewProjection * wp;
}
