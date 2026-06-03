#version 450

// Instanced world vertex shader. Reads per-instance transform/tint from the
// InstanceBuffer (set 3) by gl_InstanceIndex. Push = view-projection + camera pos
// + sun dir (shared with the fragment stage for lighting + fog). Matrices arrive
// as raw row-major bytes, read column-major in GLSL = transpose, so
// uViewProjection * model * v matches the engine's row-vector product.

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
