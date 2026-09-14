#version 450

// Direct sun lighting on the engine's shared GLSL library, same as the toolchain lab's — no local
// copy of a BRDF or a shadow lookup, so this moves when Blix.Shaders moves.
#include "pbr.glsl"
#include "shadow.glsl"

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    mat4 uSunViewProjection;
    vec4 uCameraPosition;
    vec4 uSunDirection;
    vec4 uSunColour;
};

layout(set = 1, binding = 0) uniform sampler2D uSunShadowMap;

// Declared by the FRAGMENT stage alone. The vertex stage needs nothing per-draw, and a push block
// declared in one stage has one range — which is one fewer pair of byte-identical declarations to
// keep in step. The lit pair in the toolchain lab has to declare the same block twice because both
// stages read it, and widening one of them is how a 112-byte payload gets measured against 208.
layout(push_constant) uniform Push {
    vec4 uBaseColour;
    vec4 uMaterial;       // x = metallic, y = roughness, z = slope tint 0..1
};

layout(location = 0) in vec3 vWorld;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;

layout(location = 0) out vec4 outColour;

// SLOPE, SHOWN. The room's whole subject is which surfaces a body can stand on, and that is a
// property of the normal rather than of the part — so it is drawn from the same normal the sweep
// will read. Continuous rather than thresholded on purpose: the walkable limit is policy stage R-D
// has not taken yet, and painting a threshold now would bake a guess into the instrument that is
// supposed to find it. A mis-wound face reads as a wall lying flat or a floor standing up.
vec3 slopeTint(vec3 n)
{
    float slope = degrees(acos(clamp(n.y, -1.0, 1.0)));
    // Saturated at the source: the present pass runs ACES over this like anything else, and a
    // filmic curve pulls mid values toward each other — flat greys are exactly what it is for.
    // A gauge has to survive its own tonemap.
    vec3 flat_ = vec3(0.06, 0.95, 0.20);
    vec3 mid   = vec3(1.00, 0.62, 0.00);
    vec3 steep = vec3(0.98, 0.06, 0.04);
    return slope < 45.0
        ? mix(flat_, mid, slope / 45.0)
        : mix(mid, steep, clamp((slope - 45.0) / 45.0, 0.0, 1.0));
}

void main()
{
    vec3 N = normalize(vNormal);
    vec3 V = normalize(uCameraPosition.xyz - vWorld);
    vec3 L = normalize(uSunDirection.xyz);

    float ndotl = max(dot(N, L), 0.0);

    vec3 biased = blix_shadow_normal_offset(vWorld, N, ndotl, 4.0 / 2048.0, 1.5);
    vec4 lightSpace = uSunViewProjection * vec4(biased, 1.0);
    float shadow = blix_sun_shadow(uSunShadowMap, lightSpace, ndotl);

    float metallic = clamp(uMaterial.x, 0.0, 1.0);
    float roughness = clamp(uMaterial.y, 0.04, 1.0);
    vec3 albedo = uBaseColour.rgb;

    vec3 F0 = mix(vec3(0.04), albedo, metallic);
    vec3 direct = blix_cookTorranceBrdf(N, V, L, albedo, F0, metallic, roughness)
                * uSunColour.rgb * ndotl * shadow;

    vec3 ambient = albedo * uSunColour.a;

    // THE TINT REPLACES THE SHADING RATHER THAN COLOURING IT. The first cut fed it in as an albedo,
    // which meant the same slope came out a different colour in shadow than in sun — and a gauge
    // whose reading depends on where the light is is not a gauge. Read off a capture: the dome and
    // the ledge's flat top were the same value and could not be told apart.
    //
    // The cost is that shape goes with the shading: at 1.0 the room is a flat map. That is what the
    // slider is for — 0 is the picture, 1 is the measurement, and the interesting place is usually
    // one end or the other rather than the middle.
    vec3 lit = direct + ambient;
    outColour = vec4(mix(lit, slopeTint(N) * 1.25, clamp(uMaterial.z, 0.0, 1.0)), 1.0);
}
