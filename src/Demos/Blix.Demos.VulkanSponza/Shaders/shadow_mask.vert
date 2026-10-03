#version 450

// Depth-only ALPHA-CUTOUT shadow caster for MASK foliage (potted plants, ivy,
// curtains). Passes UV to the fragment stage so leaves cast leaf-shaped
// shadows. Only MASK primitives use this pipeline; opaque casters use the
// push-only shadow.{vert,frag}.
//
// Push layout (80 bytes, Vertex|Fragment):
//   mat4 uCascadeViewProj (0), vec4 uAlphaParams (64).
// The placement's world matrix comes from set 3 (instances.glsl).

#include "instances.glsl"

layout(push_constant) uniform PushConstants {
    mat4 uCascadeViewProj;
    vec4 uAlphaParams;   // x = alphaCutoff, y = baseColorAlpha
} pc;

// Tangent layout: position at 0, uv at 3 (normal/tangent unused here).
layout(location = 0) in vec3 inPosition;
layout(location = 3) in vec2 inUv;

layout(location = 0) out vec2 vUv;

void main() {
    vUv = inUv;
    gl_Position = pc.uCascadeViewProj * instanceWorld() * vec4(inPosition, 1.0);
}
