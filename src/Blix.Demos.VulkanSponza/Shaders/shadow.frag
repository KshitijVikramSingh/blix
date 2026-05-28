#version 450

// Depth-only fragment stage for the sun shadow pass. The shadow render pass
// has no color attachment, so the rasterizer writes gl_FragDepth implicitly.
//
// Alpha-cutout discard for MASK foliage (potted plants / ivy / cypress / the
// curtains) so they cast leaf/fabric-shaped shadows instead of solid blobs.
// uAlphaParams.x is the alpha cutoff (0 for OPAQUE materials, so the discard
// is a no-op there — opaque primitives cast solid occluders without sampling
// the texture meaningfully). uAlphaParams.y carries baseColorFactor.a (glTF:
// effective alpha = sampledAlpha × factorAlpha).

layout(set = 0, binding = 0) uniform sampler2D uAlbedo;

layout(push_constant) uniform PushConstants {
    mat4 uModel;
    mat4 uCascadeViewProj;
    vec4 uAlphaParams;   // x = alphaCutoff, y = baseColorAlpha
} pc;

layout(location = 0) in vec2 vUv;

void main() {
    float cutoff = pc.uAlphaParams.x;
    if (cutoff > 0.0) {
        float a = texture(uAlbedo, vUv).a * pc.uAlphaParams.y;
        if (a < cutoff) discard;
    }
}
