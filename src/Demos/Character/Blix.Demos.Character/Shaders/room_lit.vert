#version 450

// The room is already in world space, so there is no model matrix anywhere in this lab — the
// vertices the collider was built from are the vertices this draws. A model transform would be
// the one place the picture and the physics could drift apart, and stage R-A exists to make that
// impossible rather than unlikely.
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aTexCoord;

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    mat4 uSunViewProjection;
    vec4 uCameraPosition;
    vec4 uSunDirection;   // xyz = direction TOWARD the sun
    vec4 uSunColour;      // rgb = radiance, a = ambient strength
};

layout(location = 0) out vec3 vWorld;
layout(location = 1) out vec3 vNormal;
layout(location = 2) out vec2 vUv;

void main()
{
    vWorld = aPosition;
    vNormal = aNormal;
    vUv = aTexCoord;
    gl_Position = uViewProjection * vec4(aPosition, 1.0);
}
