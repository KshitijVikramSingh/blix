#version 450

// Depth-only directional shadow caster for the cascaded sun shadow maps.
// One pipeline serves all cascades; the per-cascade light view-projection
// rides a push constant alongside the model matrix, so the cascade loop in
// OnRender just pushes a different uCascadeViewProj per pass (mirrors the
// VulkanLit shadow caster).
//
// Push layout (144 bytes, Vertex|Fragment):
//   mat4 uModel           (0)
//   mat4 uCascadeViewProj (64)
//   vec4 uAlphaParams     (128) — fragment-only (x=alphaCutoff, y=baseColorAlpha)
// The frag stage reads uAlphaParams for MASK-foliage cutout; the vert stage
// only touches uModel + uCascadeViewProj but declares the whole block so the
// single push range spanning both stages stays consistent.

layout(push_constant) uniform PushConstants {
    mat4 uModel;
    mat4 uCascadeViewProj;
    vec4 uAlphaParams;
} pc;

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

layout(location = 0) out vec2 vUv;

void main() {
    vUv = inUv;
    gl_Position = pc.uCascadeViewProj * pc.uModel * vec4(inPosition, 1.0);
}
