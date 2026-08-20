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
    vec4 uHaze;     // x = desaturation with distance, y = how much haze glows toward the sun
};

// The hues stay here and the intensities do not. A colour is a decision about what kind of
// day it is and reads the same at any exposure; a magnitude is a dial nobody can measure, so
// it arrives on a slider — see RTSGame/Debug/LookTuning.cs.
const vec3 kSkyAmbient    = vec3(0.36, 0.44, 0.55);
const vec3 kGroundAmbient = vec3(0.24, 0.20, 0.15);
const vec3 kSunColor      = vec3(1.00, 0.94, 0.80);
// Two haze colours rather than one, because air lit from behind and air lit from in front are not the
// same colour. Away from the sun it is the cold scatter of the sky; toward it, the warm glow of the same
// air with the sun behind it. One dot product picks between them, which is the cheapest half of real
// atmospheric scattering and most of what makes a low sun read as a time of day.
const vec3 kHazeAway      = vec3(0.60, 0.70, 0.84);
const vec3 kHazeToward    = vec3(0.92, 0.82, 0.66);

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

    vec3 toFragment = vWorldPos - uCamPos.xyz;
    float dist = length(toFragment);
    float fog = smoothstep(uFog.x, uFog.y, dist) * uFog.z;

    // <b>Distance takes colour before it takes value.</b> A straight mix toward one colour fades a scene
    // evenly, which flattens it — a far forest went pale and stayed just as green. Air scatters short
    // wavelengths and drains saturation first, which is why a distant hillside reads grey-blue rather than
    // as bright green seen through milk, and why draining it here makes the settlement stand out of its own
    // landscape without touching the settlement.
    float drained = fog * uHaze.x;
    float luma = dot(lit, vec3(0.2126, 0.7152, 0.0722));
    lit = mix(lit, vec3(luma), drained);

    // Which way the haze is lit. Looking toward the sun the air between here and there glows; looking away
    // it is the cold scatter of the sky.
    float towardSun = max(dot(normalize(toFragment), normalize(uSunDir.xyz)), 0.0);
    vec3 hazeColor = mix(kHazeAway, kHazeToward, towardSun * uHaze.y);

    outColor = vec4(mix(lit, hazeColor, fog), vTint.a);
}
