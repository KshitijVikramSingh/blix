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

layout(push_constant) uniform Push {
    mat4 uViewProjection;
    vec4 uCamPos;
    vec4 uSunDir;
    vec4 uSunLight;
    vec4 uSkyLight;
    vec4 uFog;
    vec4 uHaze;
};

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
    outColor = vec4(mix(lit, uHaze.rgb, fog), vTint.a * soft);
}
