#version 450

// Default instanced fragment shader: per-instance tint with a fixed-direction
// Lambert/ambient term so instanced geometry reads as 3D. Intentionally minimal —
// callers wanting fog/shadows/texturing provide their own shader to InstancedBatch.

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec4 vTint;

layout(location = 0) out vec4 outColor;

const vec3 kSunDir = normalize(vec3(0.4, 0.9, 0.3));
const float kAmbient = 0.3;

void main() {
    vec3 n = normalize(vNormal);
    float diffuse = max(dot(n, kSunDir), 0.0);
    float shade = kAmbient + (1.0 - kAmbient) * diffuse;
    outColor = vec4(vTint.rgb * shade, vTint.a);
}
