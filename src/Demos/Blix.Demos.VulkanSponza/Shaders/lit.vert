#version 450

// Lit vertex shader for VulkanSponza. Static-mesh path on the
// VertexPosition3NormalTangentTexture layout (48-byte stride): position,
// normal, tangent (vec4 xyz + w handedness), uv. Forwards a real world-space
// tangent frame to the fragment shader for normal mapping. Per-frame UBO in
// set 0; per-draw model matrix rides a push constant.

// Declare only the matrix this stage reads. The shared block's remaining layout is owned and
// validated by the fragment-stage interface rather than duplicated here.
layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
} frame;

layout(push_constant) uniform PushConstants {
    mat4 uModel;
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;   // xyz = tangent dir, w = handedness
layout(location = 3) in vec2 inUv;

layout(location = 0) out vec3 vNormalWorld;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec3 vWorldPos;
layout(location = 3) out vec3 vTangentWorld;
layout(location = 4) out float vTangentSign;

// gl_Position must be bit-identical to the depth pre-pass (which reuses this
// vertex shader) so the lit pass's LessEqual depth test matches the pre-pass
// depth exactly — no precision-mismatch holes.
invariant gl_Position;

void main() {
    vec4 world = pc.uModel * vec4(inPosition, 1.0);
    gl_Position = frame.uViewProjection * world;
    mat3 m = mat3(pc.uModel);
    vNormalWorld = m * inNormal;
    vTangentWorld = m * inTangent.xyz;
    vTangentSign = inTangent.w;
    vUv = inUv;
    vWorldPos = world.xyz;
}
