#version 450

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    vec3 uSunDirection;
    float uSunIntensity;
    vec3 uAmbientColor;
    float uAmbientIntensity;
    mat4 uSunShadowVP;
} frame;

// Set 1 = per-pass: sun shadow map. Set 1 is the lifetime tier for
// data that varies per render-pass but stays constant across draws
// inside the pass (see docs/vulkan-reshape-shaderlab-target.md).
layout(set = 1, binding = 0) uniform sampler2D uSunShadowMap;

// Set 2 = per-material.
layout(set = 2, binding = 0) uniform LitMaterial {
    vec4 uTint;
} mat;
layout(set = 2, binding = 1) uniform sampler2D uAlbedo;

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec4 vShadowCoord;

layout(location = 0) out vec4 outColor;

float sampleSunShadow(vec4 coord, float NdotL) {
    // Perspective divide → light-space NDC.
    vec3 ndc = coord.xyz / coord.w;

    // Vulkan clip-space y is +down; framebuffer UV is also +down, so
    // NDC.xy * 0.5 + 0.5 maps directly without a flip. Vulkan depth is
    // [0,1] in NDC (different from OpenGL's [-1,+1]), so currentDepth =
    // ndc.z directly.
    vec2 shadowUv = ndc.xy * 0.5 + 0.5;
    float currentDepth = ndc.z;

    // Out-of-frustum fragments: treat as lit. ClampToEdge sampling would
    // otherwise smear the nearest border pixel's depth across the entire
    // out-of-frustum region.
    if (shadowUv.x < 0.0 || shadowUv.x > 1.0 ||
        shadowUv.y < 0.0 || shadowUv.y > 1.0 ||
        currentDepth < 0.0 || currentDepth > 1.0) {
        return 1.0;
    }

    // Slope-scaled bias: surfaces nearly perpendicular to the light
    // (NdotL → 0) need more bias to avoid self-shadowing acne.
    float bias = mix(0.005, 0.0005, NdotL);
    float sampledDepth = texture(uSunShadowMap, shadowUv).r;
    return (currentDepth - bias > sampledDepth) ? 0.0 : 1.0;
}

void main() {
    vec3 N = normalize(vNormal);
    vec3 L = -normalize(frame.uSunDirection);
    float NdotL = max(dot(N, L), 0.0);

    float shadow = sampleSunShadow(vShadowCoord, NdotL);

    vec3 albedo = texture(uAlbedo, vUv).rgb * mat.uTint.rgb;
    vec3 ambient = frame.uAmbientColor * frame.uAmbientIntensity;
    vec3 lit = albedo * (ambient + NdotL * frame.uSunIntensity * shadow);
    outColor = vec4(lit, mat.uTint.a);
}
