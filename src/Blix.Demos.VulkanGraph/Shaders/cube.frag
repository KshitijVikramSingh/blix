#version 450

// Set 2 = per-material (engine convention from
// docs/vulkan-reshape-shaderlab-target.md).
layout(set = 2, binding = 0) uniform CubeMaterial {
    vec4 uTint;
} mat;
layout(set = 2, binding = 1) uniform sampler2D uAlbedo;

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

const vec3 lightDir = normalize(vec3(0.55, 1.0, 0.45));

void main() {
    // Per-fragment face normal from screen-space derivatives of world
    // position. In Vulkan the framebuffer Y axis points DOWN, so dFdy
    // gives the derivative in the screen-down direction. cross(dFdx,
    // dFdy) ends up pointing INTO the surface (opposite of OpenGL where
    // screen Y is up). Swap the cross order so the normal points outward
    // for front-facing triangles, matching the standard "light dot
    // normal" lighting convention. See docs/vulkan-friction.md F-014.
    vec3 dx = dFdx(vWorldPos);
    vec3 dy = dFdy(vWorldPos);
    vec3 N = normalize(cross(dy, dx));

    float NdotL = max(dot(N, lightDir), 0.0);
    vec3 albedo = texture(uAlbedo, vUv).rgb * mat.uTint.rgb;
    vec3 lit = albedo * (0.18 + 0.82 * NdotL);
    outColor = vec4(lit, mat.uTint.a);
}
