#version 450

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec4 vTint;

layout(location = 0) out vec4 outColor;

const vec3 kSunDirection = normalize(vec3(0.45, 0.90, 0.35));
const vec3 kSunColor = vec3(1.00, 0.94, 0.82);
const vec3 kSkyAmbient = vec3(0.42, 0.50, 0.58);

void main() {
    float diffuse = max(dot(normalize(vNormal), kSunDirection), 0.0);
    vec3 light = kSkyAmbient + kSunColor * diffuse * 0.72;
    outColor = vec4(vTint.rgb * light, vTint.a);
}
