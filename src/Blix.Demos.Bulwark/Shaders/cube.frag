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

// Shared single-tap sun-shadow technique (the look-free, fiddly bit) — see
// src/Blix.Shaders/shadow.glsl. The daylight mood (ambient/sun/fog colours above)
// stays local to this demo.
#include "shadow.glsl"

void main() {
    vec3 n = normalize(vNormal);
    float ndotl = max(dot(n, normalize(uSunDir.xyz)), 0.0);
    float shadow = blix_sun_shadow(uSunShadowMap, vSunShadowCoord, ndotl);

    vec3 ambient = mix(kGroundAmbient, kSkyAmbient, n.y * 0.5 + 0.5);
    vec3 lit = vTint.rgb * (ambient + kSunColor * ndotl * 0.9 * shadow);

    float dist = length(vWorldPos - uCamPos.xyz);
    float fog = smoothstep(kFogStart, kFogEnd, dist);
    outColor = vec4(mix(lit, kFogColor, fog), vTint.a);
}
