#version 450

// Depth-only caster pass. The sun's view-projection is per-pass, the model is per-draw.
//
// aNormal and aTexCoord are declared and unused. That is not sloppiness: the pipeline binds a
// VertexPosition3NormalTexture buffer, and a pipeline that declares an attribute the shader has no
// input for draws correctly while the validation layers report "Vertex attribute at location 2 not
// consumed by vertex shader" on every device creation. Harmless, and noise in the one stream that
// has to stay readable for a real fault to be visible in it. Declaring them costs nothing — glslc
// keeps a declared input in the interface, so the promise and the layout agree.
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;

layout(set = 0, binding = 0) uniform ShadowFrame {
    mat4 uLightViewProjection;
};

layout(push_constant) uniform Push {
    mat4 uModel;
};

void main()
{
    // model * v, untransposed upload: see conventions §2. No transposes anywhere.
    gl_Position = uLightViewProjection * (uModel * vec4(aPosition, 1.0));
}
