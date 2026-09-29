#version 450

// The environment the stage is lit by, drawn behind it. The irradiance and specular maps the lit
// shader reads are baked from this same cube, so what is behind the subject is what is lighting it.
layout(set = 0, binding = 0) uniform Sky {
    // The inverse of the view-projection the geometry is drawn with, so a pixel's ray is recovered
    // through the same conventions that placed the geometry, whatever they are.
    mat4 uInverseViewProjection;
    vec4 uCameraPosition;
};

layout(set = 1, binding = 0) uniform samplerCube uSky;

layout(location = 0) in vec2 vNdc;
layout(location = 0) out vec4 outColour;

void main()
{
    vec4 far = uInverseViewProjection * vec4(vNdc, 1.0, 1.0);
    vec3 direction = normalize(far.xyz / far.w - uCameraPosition.xyz);
    outColour = vec4(textureLod(uSky, direction, 0.0).rgb, 1.0);
}
