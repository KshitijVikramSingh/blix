#version 450

// Set 0 = per-frame (camera/projection only after the uModel migration
// to push constants per Vector A 2e).
layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
} frame;

// Per-draw model matrix via push constants — the "set 3 lifetime tier"
// realized as a push range. 64 bytes (one mat4), vertex stage only.
layout(push_constant) uniform PushConstants {
    mat4 uModel;
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec3 vWorldPos;

void main() {
    vec4 world = pc.uModel * vec4(inPosition, 1.0);
    gl_Position = frame.uViewProjection * world;
    vUv = inUv;
    vWorldPos = world.xyz;
}
