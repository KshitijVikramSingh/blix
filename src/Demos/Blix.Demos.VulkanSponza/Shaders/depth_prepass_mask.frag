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
#include "texel_bin.glsl"
layout(location = 6) flat in uint vSurfaceKey;
layout(location = 7) in vec4 vClipNow;
layout(location = 8) in vec4 vClipPrev;
layout(location = 9) in vec3 vWorldMotion;
// Stage 4e: which surface this pixel shows, its motion in uv since last frame and in world space (both now minus
// then). Single-sample passes only carry these targets; under MSAA the writes have nowhere to go and are dropped.
layout(location = 1) out uint outSurfaceKey;
layout(location = 2) out vec2 outVelocity;
layout(location = 3) out vec4 outMotion;

void main() {
    vec3 n = normalize(vNormalWorld);
    vec3 shading = gl_FrontFacing ? n : -n;
    // w: the texel bins (texel_bin.glsl, texelFaceBins) of this pixel's FACE normal -- the plane of the triangle, from the world
    // position's screen derivatives (one triangle's within a quad), turned to the shading normal's side. The texels
    // are keyed by face; the interpolated normal's bin differed from it at a third of Sponza's hall.
    vec3 face = cross(dFdx(vWorldPos), dFdy(vWorldPos));
    face = dot(face, face) > 1e-20 ? normalize(face) : shading;
    if (dot(face, shading) < 0.0) face = -face;
    outNormal = vec4(shading, texelFaceBins(face));
    outSurfaceKey = vSurfaceKey;
    outVelocity = (vClipNow.xy / vClipNow.w - vClipPrev.xy / vClipPrev.w) * 0.5;
    outMotion = vec4(vWorldMotion, 0.0);

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
