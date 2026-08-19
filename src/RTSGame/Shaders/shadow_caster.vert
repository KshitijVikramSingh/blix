#version 450

// Instanced depth-only shadow caster. Reads each instance's model from the InstanceBuffer
// (set 3) and transforms it by the sun's shadow view-projection. No colour output — the
// depth attachment captures gl_Position.z/w.

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
    mat4 uShadowViewProj;
};

void main() {
    gl_Position = uShadowViewProj * instances[gl_InstanceIndex].model * vec4(inPosition, 1.0);
}
