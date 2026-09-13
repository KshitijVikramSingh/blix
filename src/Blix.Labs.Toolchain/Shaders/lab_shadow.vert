#version 450

// Depth-only caster pass. The sun's view-projection is per-pass, the model is per-draw.
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;

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
