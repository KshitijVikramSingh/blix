#version 450

// Runner's instanced world vertex shader. Same per-instance SSBO contract as
// InstancedBatch (set 3, binding 0, the Instance struct), but the runner owns
// this shader so it can carry the extra push data its fragment stage needs
// (camera position + fog) — fog is a runner/material concern, not the engine's.

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
    vec4 uCamPos;      // xyz = camera world position
    vec4 uFogColor;    // rgb = haze colour
    vec4 uFogParams;   // x = density, y = start distance
};

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec4 vTint;
layout(location = 2) out vec3 vWorldPos;

void main() {
    Instance inst = instances[gl_InstanceIndex];
    vec4 worldPos = inst.model * vec4(inPosition, 1.0);
    gl_Position = uViewProjection * worldPos;
    vNormal = normalize(mat3(inst.model) * inNormal);
    vTint = inst.tint;
    vWorldPos = worldPos.xyz;
}
