#version 450

// Soft-particle billboard vertex shader. Identical projection to particle.vert
// (the CPU already expanded each particle into a camera-facing quad); the only
// difference is the push-constant block, which carries near/far/fadeDist for
// the soft fade. The block must match particle_soft.frag byte-for-byte (Vulkan
// requires push-constant blocks to be identical across stages), even though the
// vertex stage reads only uViewProjection.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec4 inColor;
layout(location = 2) in vec2 inUv;

layout(push_constant) uniform Push {
    mat4 uViewProjection;   // 0..64
    float uNear;            // 64
    float uFar;             // 68
    float uFadeDist;        // 72
    float uSharpness;       // 76
};

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;

void main() {
    gl_Position = uViewProjection * vec4(inPosition, 1.0);
    vColor = inColor;
    vUv = inUv;
}
