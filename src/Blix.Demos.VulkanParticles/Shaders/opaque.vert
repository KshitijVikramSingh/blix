#version 450

// Opaque backdrop (ground + blocks) for the particle showcase. The geometry
// exists so the soft-particle fade has something to fade against — particles
// dissolve into these surfaces instead of clipping through them. Lambert + a
// fixed sun in the fragment stage; albedo per-draw via the push constant.
//
// Push: mat4 uModel (0) + mat4 uViewProj (64) + vec4 uAlbedo (128) = 144 bytes.
// uAlbedo is fragment-only but the block must be declared identically in both
// stages (Vulkan push-constant rule).

layout(push_constant) uniform Push {
    mat4 uModel;
    mat4 uViewProj;
    vec4 uAlbedo;
};

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec2 inUv;

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec3 vWorldPos;

void main() {
    vec4 world = uModel * vec4(inPosition, 1.0);
    gl_Position = uViewProj * world;
    // Demo models are rigid + uniformly scaled, so the upper 3x3 suffices.
    vNormal = normalize(mat3(uModel) * inNormal);
    vWorldPos = world.xyz;
}
