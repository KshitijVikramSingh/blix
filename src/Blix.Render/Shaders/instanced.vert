#version 450

// Default instanced mesh vertex stage. One mesh (position + normal) drawn N
// times; each instance pulls its world transform + tint from a per-instance
// storage buffer indexed by gl_InstanceIndex (the Vulkan-GLSL built-in). The
// view-projection rides a push constant shared by the whole batch.
//
// This is InstancedBatch's turnkey shader. Callers that need a richer material
// (lighting, fog, shadows, texturing) supply their own shader to InstancedBatch
// instead — the per-instance SSBO contract (set 3, binding 0, the Instance
// struct below) is all that's fixed.
//
// Matrices arrive as raw System.Numerics.Matrix4x4 bytes (row-major). GLSL reads
// a mat4 column-major, so each lands as its transpose — which makes
// `uViewProj * model * v` equal the row-vector product the engine computes on the
// CPU. mat3(model) is then the column-convention rotation, correct for normals
// under rotation + uniform scale.

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
    vec4 worldPos = inst.model * vec4(inPosition, 1.0);
    gl_Position = uViewProjection * worldPos;
    vNormal = normalize(mat3(inst.model) * inNormal);
    vTint = inst.tint;
}
