#version 450

// Daylight world shading: warm directional sun + hemispheric ambient, gated by a
// single-tap sun shadow map (no PCF — clean enough for low-poly), then distance fog
// toward the sky horizon.

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec4 vTint;
layout(location = 2) in vec3 vWorldPos;
layout(location = 3) in vec4 vSunShadowCoord;

layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D uSunShadowMap;

layout(push_constant) uniform Push {
    mat4 uViewProjection;
    vec4 uCamPos;
    vec4 uSunDir;
    mat4 uSunShadowVP;
};

const vec3 kSkyAmbient    = vec3(0.45, 0.55, 0.66);
const vec3 kGroundAmbient = vec3(0.24, 0.21, 0.18);
const vec3 kSunColor      = vec3(1.00, 0.96, 0.84);
const vec3 kFogColor      = vec3(0.72, 0.84, 0.94);
const float kFogStart     = 38.0;
const float kFogEnd       = 130.0;

// Single-tap sun shadow: NDC->UV (Vulkan: Z already [0,1], Y-flip baked into the
// shadow ortho), with an angle-dependent bias to avoid acne. 1 = lit, 0 = shadowed.
float sunShadow(vec4 coord, float ndotl) {
    vec3 ndc = coord.xyz / coord.w;
    vec2 uv = ndc.xy * 0.5 + 0.5;
    float current = ndc.z;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0 || current < 0.0 || current > 1.0) {
        return 1.0;
    }
    float bias = mix(0.004, 0.0006, ndotl);
    float closest = texture(uSunShadowMap, uv).r;
    return (current - bias > closest) ? 0.0 : 1.0;
}

void main() {
    vec3 n = normalize(vNormal);
    float ndotl = max(dot(n, normalize(uSunDir.xyz)), 0.0);
    float shadow = sunShadow(vSunShadowCoord, ndotl);

    vec3 ambient = mix(kGroundAmbient, kSkyAmbient, n.y * 0.5 + 0.5);
    vec3 lit = vTint.rgb * (ambient + kSunColor * ndotl * 0.9 * shadow);

    float dist = length(vWorldPos - uCamPos.xyz);
    float fog = smoothstep(kFogStart, kFogEnd, dist);
    outColor = vec4(mix(lit, kFogColor, fog), vTint.a);
}
