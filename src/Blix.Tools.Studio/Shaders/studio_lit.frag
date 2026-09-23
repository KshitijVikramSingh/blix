#version 450

// A lean physically-based direct-lighting pass built ENTIRELY on the engine's shared
// GLSL library — no local copies of a BRDF or a shadow lookup. That is the point of
// the lab as much as the picture is: if blix_cookTorranceBrdf or blix_sun_shadow
// changes, this moves with it.
#include "pbr.glsl"
#include "shadow.glsl"
#include "ibl.glsl"

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    // One fitted light view-projection per cascade, near to far.
    mat4 uCascadeVP0;
    mat4 uCascadeVP1;
    mat4 uCascadeVP2;
    vec4 uCameraPosition;
    // xyz = each cascade's far bound along the view, in metres. w = paint the cascades instead of
    // shading, which is the only instrument that says whether three passes are doing three
    // cascades' work.
    vec4 uCascadeSplits;
    // xyz = one texel as a fraction of each cascade's own map. Per cascade because the PCF radius
    // is measured in texels and the three maps need not be the same size.
    vec4 uCascadeTexels;
    // The camera's unit forward. Cascade selection is by view DEPTH — dot(world - eye, forward) —
    // not by distance to the eye: at equal depth a fragment at the edge of a wide frustum is
    // further away than one in the centre, and picking by radius puts them in different cascades,
    // which shows as an arc across the picture.
    vec4 uCameraForward;
    vec4 uSunDirection;
    vec4 uSunColour;
    // x = prefilter mip ceiling (mip count - 1), FROM THE BAKE — see ibl.glsl for why this is a
    // value and not a constant. y = 1 when the environment probe is real, 0 when it is the 1x1
    // stand-in and the ambient should stay flat.
    vec4 uEnvironment;
};



// Inline per-draw textures belong to set 1; set 2 is reserved for MaterialHandle bindings. Every
// draw using this program, including Studio's ground, must bind every declared set-1 texture.
layout(set = 1, binding = 0) uniform sampler2D uCascade0;
layout(set = 1, binding = 1) uniform sampler2D uAlbedo;
layout(set = 1, binding = 5) uniform sampler2D uCascade1;
layout(set = 1, binding = 6) uniform sampler2D uCascade2;

// Environment baked once from StudioLook's authored sun. Identity stand-ins keep bindings complete
// when IBL is disabled.
layout(set = 1, binding = 2) uniform samplerCube uIrradiance;
layout(set = 1, binding = 3) uniform samplerCube uPrefilteredEnv;
layout(set = 1, binding = 4) uniform sampler2D uBrdfLut;

layout(push_constant) uniform Push {
    mat4 uModel;
    vec4 uBaseColour;
    vec4 uMaterial;
    // glTF lets each texture select its own TEXCOORD set.
    vec4 uExtra;          // x = albedo UV set
};

layout(location = 0) in vec3 vWorld;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;
// glTF COLOR_0 multiplies base colour. White is the identity; asset-specific use as baked occlusion
// does not change the attribute's material meaning.
layout(location = 3) in vec4 vColour;
layout(location = 4) in vec2 vUv1;

layout(location = 0) out vec4 outColour;

void main()
{
    vec3 N = normalize(vNormal);
    vec3 V = normalize(uCameraPosition.xyz - vWorld);
    vec3 L = normalize(uSunDirection.xyz);

    float ndotl = max(dot(N, L), 0.0);

    // The shared shadow path receives world metres per texel; it derives sampling texels itself.
    int cascade;
    float shadow = blix_sun_shadow_cascaded(
        uCascade0, uCascade1, uCascade2,
        uCascadeVP0, uCascadeVP1, uCascadeVP2,
        uCascadeTexels.xyz,
        vWorld, N, ndotl, 1.5, gl_FragCoord.xy,
        cascade);

    float metallic = clamp(uMaterial.x, 0.0, 1.0);
    float roughness = clamp(uMaterial.y, 0.04, 1.0);
    // A zero cutoff lets OPAQUE and MASK share this pipeline. Alpha is texture × baseColorFactor.a ×
    // vertex alpha, per glTF. `discard` prevents early-Z for the whole shader; Studio accepts that
    // cost for its small subjects rather than multiplying pipeline variants. Revisit for large views.
    vec2 uvAlbedo = uExtra.x > 0.5 ? vUv1 : vUv;
    if (uMaterial.w > 0.0 && texture(uAlbedo, uvAlbedo).a * uBaseColour.a * vColour.a < uMaterial.w) discard;

    vec3 albedo = uBaseColour.rgb * texture(uAlbedo, uvAlbedo).rgb * vColour.rgb;

    vec3 F0 = mix(vec3(0.04), albedo, metallic);
    vec3 direct = blix_cookTorranceBrdf(N, V, L, albedo, F0, metallic, roughness)
                * uSunColour.rgb * ndotl * shadow;

    // The same ambient-strength control scales directional IBL or the explicit flat fallback.
    vec3 ambient;
    if (uEnvironment.y > 0.5)
    {
        ambient = blix_iblAmbient(
            N, V, albedo, metallic, roughness, F0,
            uIrradiance, uPrefilteredEnv, uBrdfLut, uEnvironment.x) * uSunColour.a;
    }
    else
    {
        ambient = albedo * uSunColour.a;
    }

    // Output the same composed alpha used by MASK; opaque pipelines ignore this channel.
    vec3 lit = direct + ambient;

    // Cascade diagnostics replace shading with a flat band; shadow remains as brightness.
    if (uCascadeTexels.w > 0.5) lit = blix_cascade_tint(cascade) * mix(0.35, 1.0, shadow);

    outColour = vec4(lit, texture(uAlbedo, uvAlbedo).a * uBaseColour.a * vColour.a);
}
