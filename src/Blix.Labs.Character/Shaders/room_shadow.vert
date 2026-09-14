#version 450

// Depth-only caster. No push block at all: the room does not move, so the only thing this stage
// needs is the sun's view-projection, and it is per-pass.
//
// aNormal and aTexCoord are declared and unused — the pipeline binds a VertexPosition3NormalTexture
// buffer, and an attribute the shader has no input for makes the validation layers report it on
// every device creation. Noise in the one stream that has to stay readable.
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;

layout(set = 0, binding = 0) uniform ShadowFrame {
    mat4 uLightViewProjection;
};

void main()
{
    gl_Position = uLightViewProjection * vec4(aPosition, 1.0);
}
