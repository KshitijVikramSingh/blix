#version 410 core

// Depth-only pass for directional shadow casting. Reads only aPosition; the
// rest of the VertexPosition3NormalTexture layout (normal, uv) is bound but
// the shader silently ignores it.

layout (location = 0) in vec3 aPosition;

uniform mat4 uModel;
uniform mat4 uLightViewProjection;

void main()
{
    gl_Position = uLightViewProjection * uModel * vec4(aPosition, 1.0);
}
