#version 450

// Contact shadows: a soft dark disc where an object meets the ground.
//
// Instanced like everything else, over a disc mesh whose texture coordinate carries the radius — centre at
// zero, rim at one — so the fragment stage can fade without knowing where the instance is.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

struct Instance {
    mat4 model;
    vec4 tint;
};

layout(set = 3, binding = 0, std430) readonly buffer Instances {
    Instance instances[];
};

layout(push_constant) uniform Push {
    mat4 uViewProjection;
    vec4 uFade;   // x = start (m), y = end (m) — a contact shadow past the fog is a smudge
    vec4 uCamPos;
};

layout(location = 0) out float vRadius;
layout(location = 1) out float vStrength;

void main() {
    Instance inst = instances[gl_InstanceIndex];
    vec4 world = inst.model * vec4(inPosition, 1.0);
    gl_Position = uViewProjection * world;
    vRadius = inUv.x;
    // Faded with distance, so the far field is not stippled with dark dots the eye reads as noise.
    float away = length(world.xyz - uCamPos.xyz);
    vStrength = inst.tint.a * (1.0 - smoothstep(uFade.x, uFade.y, away));
}
