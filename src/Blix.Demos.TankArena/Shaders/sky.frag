#version 450

// Analytic procedural sky (clean daylight). Reconstructs a world-space view ray
// per pixel from the inverse view-projection, then shades a zenith->horizon->ground
// gradient plus a sun disc + glow. Drawn first (depth-disabled) so the world draws
// over it. Matrices arrive as raw System.Numerics bytes (row-major) read
// column-major in GLSL = transpose, so uInvViewProj * clip_col is correct.

layout(location = 0) in vec2 vNdc;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform Push {
    mat4 uInvViewProj;
    vec4 uCamPos;     // xyz = camera world position
    vec4 uSunDir;     // xyz = direction TOWARD the sun (normalized)
};

void main() {
    vec4 farPoint = uInvViewProj * vec4(vNdc, 1.0, 1.0);
    vec3 world = farPoint.xyz / farPoint.w;
    vec3 ray = normalize(world - uCamPos.xyz);

    const vec3 zenith  = vec3(0.18, 0.42, 0.78);
    const vec3 horizon = vec3(0.72, 0.84, 0.94);
    const vec3 ground  = vec3(0.30, 0.34, 0.36);

    vec3 col = ray.y >= 0.0
        ? mix(horizon, zenith, pow(clamp(ray.y, 0.0, 1.0), 0.55))
        : mix(horizon, ground, clamp(-ray.y * 3.0, 0.0, 1.0));

    float s = max(dot(ray, normalize(uSunDir.xyz)), 0.0);
    col += vec3(1.00, 0.95, 0.80) * pow(s, 120.0) * 3.0;   // disc
    col += vec3(1.00, 0.86, 0.62) * pow(s, 5.0)  * 0.45;   // glow
    col += vec3(1.00, 0.90, 0.72) * pow(s, 1.3)  * 0.10;   // broad warmth

    outColor = vec4(col, 1.0);
}
