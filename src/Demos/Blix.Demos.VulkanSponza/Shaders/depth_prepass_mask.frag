#version 450

// Depth pre-pass fragment stage for MASK (alpha-cutout) geometry. Discards
// below the cutoff so foliage writes leaf-shaped depth, exactly matching the
// lit pass's discard — otherwise the pre-pass would write a solid quad of
// depth and occlude the scene behind the leaf's transparent parts.
//
// Set 2 mirrors the lit material layout (so the same material descriptor set
// binds); only the base-colour factor, alpha cutoff, and albedo are used.

#include "coverage.glsl"

layout(set = 2, binding = 0) uniform Material {
    vec4 uBaseColorFactor;
    vec4 uEmissiveFactor;
    vec4 uMaterialParams;  // x = alphaCutoff
    vec4 uMaterialParams2;
} mat;
layout(set = 2, binding = 1) uniform sampler2D uAlbedo;

// See depth_prepass.frag for why this pass writes a normal at all.
layout(location = 0) in vec3 vNormalWorld;
layout(location = 1) in vec2 vUv;
// World position, for the layer hash. lit.vert already writes it; a fragment stage may consume a
// subset of what the vertex stage produces, so this costs nothing new.
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outNormal;

void main() {
    vec3 n = normalize(vNormalWorld);
    outNormal = vec4(gl_FrontFacing ? n : -n, 1.0);

    float a = texture(uAlbedo, vUv).a * mat.uBaseColorFactor.w;
    if (a < mat.uMaterialParams.x) discard;

    // Match the lit pass's sample mask and layer hash exactly. Otherwise depth survives in samples
    // where leaf colour is discarded and prevents background layers from filling the canopy.
    // uMaterialParams2.w carries the sample-count/policy because this pass has no frame block.
    float coverage = clamp((a - mat.uMaterialParams.x) / max(fwidth(a), 1e-5) + 0.5, 0.0, 1.0);
    float policy = mat.uMaterialParams2.w;
    int samples = int(policy);
    if (policy > 0.0 && policy < 1.5) {
        // Single sample: the lit pass resolves coverage with a hashed discard, so this must make
        // the SAME decision or the pre-pass writes depth for a fragment the lit pass throws away
        // and the canopy fills with holes of solid depth.
        if (!blix_hashedAlphaKeeps(coverage, blix_layerHash(vWorldPos))) discard;
        gl_SampleMask[0] = 1;
    } else if (samples < 2) {
        gl_SampleMask[0] = 1;   // one sample, plain binary: the discard above is the whole test
    } else {
        gl_SampleMask[0] = blix_coverageMask(coverage, samples, blix_layerHash(vWorldPos));
    }
}
