#version 450

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
};

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec4 vTint;

void main() {
    Instance inst = instances[gl_InstanceIndex];
    gl_Position = uViewProjection * inst.model * vec4(inPosition, 1.0);
    vNormal = normalize(mat3(inst.model) * inNormal);
    vTint = inst.tint;
}
