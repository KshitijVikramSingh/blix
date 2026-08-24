#version 450

// A puff of smoke, which is a sphere pretending not to have an edge.
//
// <b>The one trick that makes this work is fading the alpha at the silhouette</b>, and how hard it fades is
// the difference between bubbly and wispy. A translucent sphere drawn plainly reads as a bubble: it is at
// its most opaque exactly where its outline is, because that is where the eye finds a hard boundary. Real
// smoke is a volume, and a volume seen edge-on is thin — so opacity falls off with how far the surface is
// turned away from the viewer.
//
// The first version faded over the outer half of the sphere, which removed the hard rim and still left a
// recognisable ball. Reported as bubbly, correctly. It now fades over nearly all of it and squares the
// result, so only a small cap facing the viewer carries any opacity at all and the edges go to nothing over
// a long way — which is what a wisp is. The visible mass of a puff drops by most of itself, so the plume
// gets it back from more puffs at lower opacity rather than from fewer solid ones.
//
// Lit by the sky rather than by the sun, because that is what smoke does: it is optically thin, so it
// carries the ambient light of the whole hemisphere and only a little of the direct beam. The exception is
// worth the two lines it costs — a plume with a low sun behind it glows, which is the single most
// recognisable thing about woodsmoke at dusk and free here, since the same dot product already exists in
// the world shader for light through a leaf.

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec4 vTint;
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

// What the player has scouted, and what they are watching. Same texture the world shader reads.
layout(set = 0, binding = 0) uniform sampler2D uScoutedMap;

layout(push_constant) uniform Push {
    mat4 uViewProjection;
    vec4 uCamPos;
    vec4 uSunDir;
    vec4 uSunLight;
    vec4 uSkyLight;
    vec4 uFog;
    vec4 uHaze;     // rgb = what the distance goes to, w = the map extent, which the veil needs
    // The fog of war's dials, laid out exactly as world.frag has them so one shared function can read both.
    vec4 uScouted;
    vec4 uVeil;
    vec4 uVeilAir;
    vec4 uVeilDeep;
    vec4 uWind;     // y = the simulated clock, z = gust rate, w = bearing
};

// Where the fog of war is — shared with world.frag rather than reimplemented, because a puff hanging in
// clear air over ground the cloud has covered is exactly what a second copy of that arithmetic buys.
#include "veil.glsl"

void main() {
    vec3 n = normalize(vNormal);
    vec3 toEye = normalize(uCamPos.xyz - vWorldPos);
    vec3 toSun = normalize(uSunDir.xyz);

    // Thin over nearly all of it, with only a small cap facing the viewer carrying any weight.
    float soft = smoothstep(0.0, 0.92, dot(n, toEye));
    soft *= soft;

    // Sky light, a little of the beam, and the glow of a sun behind it.
    float wrapped = 0.5 + 0.5 * dot(n, toSun);
    float behind = pow(max(dot(-toEye, toSun), 0.0), 3.0);
    vec3 lit = vTint.rgb * (uSkyLight.rgb * 0.95 + uSunLight.rgb * (0.28 * wrapped + 0.55 * behind));

    float dist = length(vWorldPos - uCamPos.xyz);
    float fog = smoothstep(uFog.x, uFog.y, dist) * uFog.z;

    // <b>Hidden by giving up alpha, not by mixing toward the cloud.</b> Smoke was the last thing on the map
    // still escaping the veil — water and lit windows were caught earlier, and this is the same class of leak
    // with the worst payload: a chimney is a settlement's position, and a plume is visible from much further
    // than the building under it. Alpha rather than colour because this pipeline blends: mixing cloud into it
    // would paint cloud-coloured smoke over the fog rather than take any smoke away.
    //
    // Both layers count, and they multiply rather than add — two things each letting some light past is what
    // a product means, and a sum would go opaque early and clip.
    vec2 density = blix_rts_veil_density(
        uScoutedMap, vWorldPos, uHaze.w, uScouted, uVeil, uVeilAir, uVeilDeep, uWind);
    float through = (1.0 - density.x) * (1.0 - density.y);

    outColor = vec4(mix(lit, uHaze.rgb, fog), vTint.a * soft * through);
}
