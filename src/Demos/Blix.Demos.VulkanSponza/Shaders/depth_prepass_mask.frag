#version 450

// Depth pre-pass fragment stage for MASK (alpha-cutout) geometry. Discards
// below the cutoff so foliage writes leaf-shaped depth, exactly matching the
// lit pass's discard — otherwise the pre-pass would write a solid quad of
// depth and occlude the scene behind the leaf's transparent parts.
//
// Set 2 mirrors the lit material layout (so the same material descriptor set
// binds); only the base-colour factor, alpha cutoff, and albedo are used.

layout(set = 2, binding = 0) uniform Material {
    vec4 uBaseColorFactor;
    vec4 uEmissiveFactor;
    vec4 uMaterialParams;  // x = alphaCutoff
    vec4 uMaterialParams2;
} mat;
layout(set = 2, binding = 1) uniform sampler2D uAlbedo;

layout(location = 1) in vec2 vUv;

void main() {
    float a = texture(uAlbedo, vUv).a * mat.uBaseColorFactor.w;
    if (a < mat.uMaterialParams.x) discard;
}
