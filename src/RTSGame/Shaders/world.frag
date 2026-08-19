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
    vec4 uFog;      // x = start (m), y = end (m), z = strength
    vec4 uShadow;   // x = texel as a fraction of the map, y = map size (m), z = penumbra, w = offset
    vec4 uLight;    // x = sun intensity, y = ambient scale, z = terminator wrap
};

// The hues stay here and the intensities do not. A colour is a decision about what kind of
// day it is and reads the same at any exposure; a magnitude is a dial nobody can measure, so
// it arrives on a slider — see RTSGame/Debug/LookTuning.cs.
const vec3 kSkyAmbient    = vec3(0.36, 0.44, 0.55);
const vec3 kGroundAmbient = vec3(0.24, 0.20, 0.15);
const vec3 kSunColor      = vec3(1.00, 0.94, 0.80);
const vec3 kFogColor      = vec3(0.62, 0.74, 0.88);

// Shared sun-shadow technique — src/Blix.Shaders/shadow.glsl.
#include "shadow.glsl"

void main() {
    vec3 n = normalize(vNormal);
    float sunDot = dot(n, normalize(uSunDir.xyz));
    float ndotl = max(sunDot, 0.0);
    float shadow = blix_sun_shadow_soft(
        uSunShadowMap, vSunShadowCoord, ndotl, uShadow.x, uShadow.z);

    // A wrapped terminator. Straight N.L puts a hard line across every curved surface at
    // exactly the angle the sun grazes it, which on low-poly geometry lands on a facet
    // boundary and reads as a crease; wrapping softens the turn without lighting anything
    // that faces away.
    float wrapped = max((sunDot + uLight.z) / (1.0 + uLight.z), 0.0);

    // Sky above, warm bounce below: a settlement is read from above, so the roofs and the
    // ground are the two surfaces that have to separate from each other.
    vec3 ambient = mix(kGroundAmbient, kSkyAmbient, n.y * 0.5 + 0.5) * uLight.y;
    vec3 lit = vTint.rgb * (ambient + kSunColor * uLight.x * wrapped * shadow);

    float dist = length(vWorldPos - uCamPos.xyz);
    float fog = smoothstep(uFog.x, uFog.y, dist) * uFog.z;
    outColor = vec4(mix(lit, kFogColor, fog), vTint.a);
}
