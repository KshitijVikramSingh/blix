#version 450

// Alpha-cutout fragment stage for MASK shadow casters. Samples the base-colour
// texture and discards below the glTF alpha cutoff so foliage casts leaf-shaped
// shadows. UVs are already V-canonicalised in the vertex buffer (import-time
// FlipTextureV), matching the lit pass.

layout(set = 0, binding = 0) uniform sampler2D uAlbedo;

layout(push_constant) uniform PushConstants {
    mat4 uModel;
    mat4 uCascadeViewProj;
    vec4 uAlphaParams;   // x = alphaCutoff, y = baseColorAlpha
} pc;

layout(location = 0) in vec2 vUv;

void main() {
    float a = texture(uAlbedo, vUv).a * pc.uAlphaParams.y;
    if (a < pc.uAlphaParams.x) discard;
}
