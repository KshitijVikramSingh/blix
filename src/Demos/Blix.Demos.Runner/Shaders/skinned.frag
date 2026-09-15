#version 450

// Skinned character fragment shader: sample the albedo atlas (set 2) and apply a
// soft sun Lambert term matching the world shader's lighting.

layout(set = 2, binding = 0) uniform sampler2D uAlbedo;

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

const vec3 kSunDir = normalize(vec3(0.30, 0.85, -0.25));
const float kAmbient = 0.40;

void main() {
    vec3 albedo = texture(uAlbedo, vUv).rgb;
    float diffuse = max(dot(normalize(vNormal), kSunDir), 0.0);
    outColor = vec4(albedo * (kAmbient + (1.0 - kAmbient) * diffuse), 1.0);
}
