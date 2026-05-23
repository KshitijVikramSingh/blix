#version 410 core

// Fullscreen-triangle vertex shader for the composite/tonemap pass. The
// mesh is the same fullscreen quad the skybox uses (NDC -1..1 with UVs
// 0..1); we just pass position straight through to NDC and the UV to the
// frag shader.

layout (location = 0) in vec2 aPosition;
layout (location = 1) in vec2 aTexCoord;

out vec2 vUv;

void main()
{
    gl_Position = vec4(aPosition, 0.0, 1.0);
    vUv = aTexCoord;
}
