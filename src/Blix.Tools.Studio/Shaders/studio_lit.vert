#version 450

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;
// Baked ambient occlusion on the nature kit, white everywhere else. UByte4Norm, so the
// hardware hands it over already expanded to 0..1.
layout(location = 3) in vec4 aColour;

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    mat4 uSunViewProjection;
    vec4 uCameraPosition;
    vec4 uSunDirection;   // xyz = direction TOWARD the sun, w unused
    vec4 uSunColour;      // rgb = radiance, a = ambient strength
};

layout(push_constant) uniform Push {
    mat4 uModel;
    vec4 uBaseColour;
    vec4 uMaterial;       // x = metallic, y = roughness
};

layout(location = 0) out vec3 vWorld;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;
layout(location = 3) out vec4 vColour;

void main()
{
    vec4 world = uModel * vec4(aPosition, 1.0);
    vWorld = world.xyz;

    // Uniform scale only in this lab, so the upper 3x3 is enough and no inverse
    // transpose is needed. Stated rather than assumed: non-uniform scale would shear
    // these normals.
    vNormal = normalize(mat3(uModel) * aNormal);

    vUv = aTexCoord;
    vColour = aColour;
    gl_Position = uViewProjection * world;
}
