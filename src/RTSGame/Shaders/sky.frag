#version 450

// Analytic procedural sky. Reconstructs a world-space view ray per pixel from the inverse
// view-projection, then shades a zenith-to-horizon gradient plus a sun disc and glow.
// Drawn first with depth disabled so the world draws over it.
//
// It matters more than a backdrop usually does here: an RTS camera looking down at a plain
// sees the sky only in the top strip of the screen, and that strip is the whole of the
// horizon reference. A flat clear colour is why an untextured plain reads as a diagram.

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

    const vec3 zenith  = vec3(0.16, 0.38, 0.80);
    const vec3 horizon = vec3(0.64, 0.78, 0.92);
    const vec3 ground  = vec3(0.36, 0.38, 0.34);

    vec3 col = ray.y >= 0.0
        ? mix(horizon, zenith, pow(clamp(ray.y, 0.0, 1.0), 0.50))
        : mix(horizon, ground, clamp(-ray.y * 3.0, 0.0, 1.0));

    float s = max(dot(ray, normalize(uSunDir.xyz)), 0.0);
    col += vec3(1.00, 0.95, 0.80) * pow(s, 160.0) * 4.0;   // disc
    col += vec3(1.00, 0.86, 0.62) * pow(s, 6.0)  * 0.50;   // glow
    col += vec3(1.00, 0.90, 0.72) * pow(s, 1.3)  * 0.12;   // broad warmth

    outColor = vec4(col, 1.0);
}
