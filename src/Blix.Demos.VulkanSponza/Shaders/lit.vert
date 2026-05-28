#version 450

// Scaffold lit vertex shader for VulkanSponza. Static-mesh path only
// (VertexPosition3NormalTexture, 32-byte stride). Per-frame UBO carries
// the camera + sun in set 0; per-draw model matrix rides a push
// constant; per-material textures bind into set 2 (per F-002 / Vector A
// lifetime convention).
//
// Deliberately minimal: no skinning, no shadow VPs, no IBL — those land
// when the scaffold demo matures into a real Sponza-Modern Vulkan port.

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    vec3 uSunDirection;
    float uSunIntensity;
    vec3 uAmbientColor;
    float uAmbientIntensity;
} frame;

layout(push_constant) uniform PushConstants {
    mat4 uModel;
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

layout(location = 0) out vec3 vNormalWorld;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec3 vWorldPos;

void main() {
    vec4 world = pc.uModel * vec4(inPosition, 1.0);
    gl_Position = frame.uViewProjection * world;
    vNormalWorld = mat3(pc.uModel) * inNormal;
    vUv = inUv;
    vWorldPos = world.xyz;
}
