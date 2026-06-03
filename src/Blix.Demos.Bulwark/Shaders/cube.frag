#version 450

// Bulwark Gate A — per-instance tint with a fixed-direction Lambert + ambient
// term so the grid tiles, towers, and hover ghost read as 3D.

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec4 vTint;

layout(location = 0) out vec4 outColor;

const vec3 kSunDir = normalize(vec3(0.4, 0.9, 0.3));
const float kAmbient = 0.35;

void main() {
    float diffuse = max(dot(normalize(vNormal), kSunDir), 0.0);
    outColor = vec4(vTint.rgb * (kAmbient + (1.0 - kAmbient) * diffuse), vTint.a);
}
