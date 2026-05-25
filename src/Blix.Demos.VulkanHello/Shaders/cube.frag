#version 450

layout(location = 0) in vec4 vColor;
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
    vec3 lit = vColor.rgb * (0.18 + 0.82 * NdotL);
    outColor = vec4(lit, vColor.a);
}
