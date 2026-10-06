#version 450

// Lit vertex shader for VulkanSponza. Static-mesh path on the
// VertexPosition3NormalTangentTexture layout (48-byte stride): position,
// normal, tangent (vec4 xyz + w handedness), uv. Forwards a real world-space
// tangent frame to the fragment shader for normal mapping. Per-frame UBO in
// set 0; the placement's world matrix comes from set 3 (instances.glsl).

// Declare only the matrix this stage reads. The shared block's remaining layout is owned and
// validated by the fragment-stage interface rather than duplicated here.
#include "frame.glsl"
#include "instances.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;   // xyz = tangent dir, w = handedness
layout(location = 3) in vec2 inUv;

layout(location = 0) out vec3 vNormalWorld;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec3 vWorldPos;
layout(location = 3) out vec3 vTangentWorld;
layout(location = 4) out float vTangentSign;
// Stage 4e, for the pre-pass: the surface this is, and where this vertex is now and was last frame (un-jittered
// clip space; the fragment divides, so the velocity is exact per pixel rather than interpolated after division).
layout(location = 6) flat out uint vSurfaceKey;
layout(location = 7) out vec4 vClipNow;
layout(location = 8) out vec4 vClipPrev;
// And its motion in world space (now minus then): what a consumer needs to find where a surface point was without
// asking how it moved. Affine in the position, so interpolation is exact for rigid motion; a still row holds the same
// transform twice and writes exactly zero.
layout(location = 9) out vec3 vWorldMotion;

// gl_Position must be bit-identical to the depth pre-pass (which reuses this
// vertex shader) so the lit pass's LessEqual depth test matches the pre-pass
// depth exactly — no precision-mismatch holes.
invariant gl_Position;

void main() {
    mat4 model = instanceWorld();
    vec4 world = model * vec4(inPosition, 1.0);
    gl_Position = frame.uViewProjection * world;
    // The model's linear part carries normals and tangents, which is exact for rotation and uniform
    // scale (the fragment stage normalises). The host counts placements with non-uniform scale at load.
    mat3 m = mat3(model);
    vNormalWorld = m * inNormal;
    vTangentWorld = m * inTangent.xyz;
    vTangentSign = inTangent.w;
    vUv = inUv;
    vWorldPos = world.xyz;
    vSurfaceKey = instanceSurfaceKey();
    vClipNow = frame.uViewProjUnjittered * world;
    vec4 previousWorld = instancePreviousWorld() * vec4(inPosition, 1.0);
    vClipPrev = frame.uPrevViewProjUnjittered * previousWorld;
    vWorldMotion = world.xyz - previousWorld.xyz;
}
