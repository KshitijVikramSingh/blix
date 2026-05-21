#version 410 core

// Fullscreen sky pass: takes a unit-NDC quad and outputs the world-space view
// direction through each fragment. Outputs gl_Position.z = w so the rasterised
// depth is 1.0 (far plane); the pipeline uses LessEqual depth + no-write so
// the sky only fills pixels nothing else covered.

layout (location = 0) in vec2 aPosition;
layout (location = 1) in vec2 aTexCoord;

out vec3 viewDir;

uniform mat4 uInvViewProj;
uniform vec3 uCameraPosition;

void main()
{
    // Unproject the NDC point at the far plane to world space.
    vec4 farClip = vec4(aPosition, 1.0, 1.0);
    vec4 farWorldH = uInvViewProj * farClip;
    vec3 farWorld = farWorldH.xyz / farWorldH.w;
    viewDir = farWorld - uCameraPosition;
    gl_Position = vec4(aPosition, 1.0, 1.0);
}
