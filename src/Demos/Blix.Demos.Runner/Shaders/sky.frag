#version 450

// Analytic procedural sky. Reconstructs a world-space view ray per pixel from the
// inverse view-projection, then shades a zenith→horizon→ground gradient with a
// sun disc + glow. Drawn first into the frame (depth-disabled) so the world draws
// over it; pixels with no geometry keep the sky. Matrices arrive as raw
// System.Numerics bytes (row-major) and read column-major in GLSL = transpose, so
// uInvViewProj * clip_col is the correct clip→world transform (see InstancedBatch).

layout(location = 0) in vec2 vNdc;
layout(location = 0) out vec4 outColor;

layout(push_constant) uniform Push {
    mat4 uInvViewProj;
    vec4 uCamPos;     // xyz = camera world position
    vec4 uSunDir;     // xyz = direction TOWARD the sun (normalized)
};

void main()
{
    vec4 farPoint = uInvViewProj * vec4(vNdc, 1.0, 1.0);
    vec3 world = farPoint.xyz / farPoint.w;
    vec3 ray = normalize(world - uCamPos.xyz);

    const vec3 zenith  = vec3(0.10, 0.28, 0.60);
    const vec3 horizon = vec3(0.66, 0.77, 0.88);
    const vec3 ground  = vec3(0.07, 0.08, 0.10);

    vec3 col;
    if (ray.y >= 0.0)
    {
        col = mix(horizon, zenith, pow(clamp(ray.y, 0.0, 1.0), 0.6));
    }
    else
    {
        col = mix(horizon, ground, clamp(-ray.y * 2.5, 0.0, 1.0));
    }

    // Sun: a tight bright disc, a warm glow, and a broad horizon warmth so the
    // near-horizon band the camera mostly sees reads sunny rather than gray.
    float s = max(dot(ray, normalize(uSunDir.xyz)), 0.0);
    col += vec3(1.00, 0.93, 0.76) * pow(s, 80.0) * 2.5;   // visible disc
    col += vec3(1.00, 0.80, 0.55) * pow(s, 4.0)  * 0.50;  // glow
    col += vec3(1.00, 0.85, 0.65) * pow(s, 1.2)  * 0.14;  // broad warmth

    outColor = vec4(col, 1.0);
}
