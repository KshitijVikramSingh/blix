#version 450

// Instanced smoke. Same instance buffer (set 3) as the world pass and the same transposed-matrix
// convention, but its own pipeline: smoke is the first thing in this scene that is not opaque, and it is
// the first surface whose fourth colour channel means opacity rather than which material it is.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;

struct Instance {
    mat4 model;
    vec4 tint;
};

layout(set = 3, binding = 0, std430) readonly buffer Instances {
    Instance instances[];
};

layout(push_constant) uniform Push {
    mat4 uViewProjection;
    vec4 uCamPos;
    vec4 uSunDir;
    vec4 uSunLight;   // rgb = the sun's colour times its intensity
    vec4 uSkyLight;   // rgb = the sky's ambient times its scale
    vec4 uFog;        // x = start (m), y = end (m), z = strength
    vec4 uHaze;       // rgb = what the distance goes to
};

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec4 vTint;
layout(location = 2) out vec3 vWorldPos;

void main() {
    Instance inst = instances[gl_InstanceIndex];
    vec4 world = inst.model * vec4(inPosition, 1.0);
    gl_Position = uViewProjection * world;
    vNormal = normalize(mat3(inst.model) * inNormal);
    vTint = inst.tint;
    vWorldPos = world.xyz;
}
