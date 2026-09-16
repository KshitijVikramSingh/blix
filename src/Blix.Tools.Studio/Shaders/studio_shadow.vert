#version 450

// Depth-only caster pass. The sun's view-projection is per-pass, the model is per-draw.
//
// aNormal and aColour are declared and unused; aTexCoord is read for the cutout below. That is not sloppiness: the pipeline binds a
// VertexPosition3NormalTexture buffer, and a pipeline that declares an attribute the shader has no
// input for draws correctly while the validation layers report "Vertex attribute at location 2 not
// consumed by vertex shader" on every device creation. Harmless, and noise in the one stream that
// has to stay readable for a real fault to be visible in it. Declaring them costs nothing — glslc
// keeps a declared input in the interface, so the promise and the layout agree.
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;
layout(location = 3) in vec2 aTexCoord1;
layout(location = 4) in vec4 aColour;

layout(set = 0, binding = 0) uniform ShadowFrame {
    mat4 uLightViewProjection;
};

layout(push_constant) uniform Push {
    mat4 uModel;
    // x = alpha cutoff (0 = never), y = the material's baseColorFactor.a. The fragment stage reads
    // these; the vertex stage declares them so both agree on one block.
    vec4 uCutout;         // x = alpha cutoff, y = baseColorFactor.a, z = albedo UV set
};

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec2 vUv1;

void main()
{
    // model * v, untransposed upload: see conventions §2. No transposes anywhere.
    // <b>Carried for the cutout test.</b> A caster that discards nothing would not need it, and a
    // caster that cannot discard casts a solid rectangle for a leaf.
    vUv = aTexCoord;
    vUv1 = aTexCoord1;
    gl_Position = uLightViewProjection * (uModel * vec4(aPosition, 1.0));
}
