#version 450

// Daylight world shading for the greybox kingdom: warm directional sun gated by a single
// sun shadow map, hemispheric ambient (sky above, warm ground bounce below), and aerial
// perspective toward the horizon. Output is HDR-linear; the present pass tonemaps.
//
// The values here are chosen against a tonemap rather than against the screen. A sun of
// 2.35 and an ambient of 0.30 puts a lit mid-grey surface a little above 1.0 and a shaded
// one near 0.15, which is roughly a two-and-a-half stop separation — enough that a shadow
// reads as shade rather than as a darker shade of the same paint. Written straight to the
// swapchain those numbers would clip; that is the point of having somewhere to put them.

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
    vec4 uFog;
};

const vec3 kSkyAmbient    = vec3(0.30, 0.37, 0.48);
const vec3 kGroundAmbient = vec3(0.20, 0.16, 0.12);
const vec3 kSunColor      = vec3(1.00, 0.93, 0.78);
const vec3 kFogColor      = vec3(0.62, 0.74, 0.88);
const float kSunIntensity = 2.35;

// Shared single-tap sun-shadow technique — src/Blix.Shaders/shadow.glsl.
#include "shadow.glsl"

void main() {
    vec3 n = normalize(vNormal);
    float ndotl = max(dot(n, normalize(uSunDir.xyz)), 0.0);
    float shadow = blix_sun_shadow(uSunShadowMap, vSunShadowCoord, ndotl);

    // Sky above, warm bounce below, and a little extra light on anything facing up: a
    // settlement is read from above, so the roofs and the ground are the surfaces that
    // have to separate from each other.
    vec3 ambient = mix(kGroundAmbient, kSkyAmbient, n.y * 0.5 + 0.5);
    vec3 lit = vTint.rgb * (ambient + kSunColor * kSunIntensity * ndotl * shadow);

    float dist = length(vWorldPos - uCamPos.xyz);
    float fog = smoothstep(uFog.x, uFog.y, dist) * 0.72;
    outColor = vec4(mix(lit, kFogColor, fog), vTint.a);
}
