#version 410 core

// Depth-only pass for directional shadow casting. Passes UV through so the
// fragment shader can do alpha-cutout discard on MASK materials (foliage,
// fabric); without that, leaves cast solid-rectangle shadows because the
// rasterizer writes depth for every triangle pixel regardless of alpha.

layout (location = 0) in vec3 aPosition;
layout (location = 2) in vec2 aTexCoord;

out vec2 vTexCoord;

uniform mat4 uModel;
uniform mat4 uLightViewProjection;

void main()
{
    vTexCoord = aTexCoord;
    gl_Position = uLightViewProjection * uModel * vec4(aPosition, 1.0);
}
