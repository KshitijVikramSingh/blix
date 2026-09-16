#version 450

// A lean physically-based direct-lighting pass built ENTIRELY on the engine's shared
// GLSL library — no local copies of a BRDF or a shadow lookup. That is the point of
// the lab as much as the picture is: if blix_cookTorranceBrdf or blix_sun_shadow
// changes, this moves with it.
#include "pbr.glsl"
#include "shadow.glsl"

layout(set = 0, binding = 0) uniform Frame {
    mat4 uViewProjection;
    mat4 uSunViewProjection;
    vec4 uCameraPosition;
    vec4 uSunDirection;
    vec4 uSunColour;
};



// Per-pass shadow map and per-part albedo, both in set 1.
//
// <b>Set 1 rather than set 2, and that distinction cost an hour.</b> Set 2 is the engine's
// per-MATERIAL set, bound only through a MaterialHandle; a ShaderTextureBinding goes down the
// inline per-draw path, which fills sets 0 and 1. Declaring uAlbedo at set 2 and passing it
// inline left the pipeline statically using a set nothing bound — a SIGSEGV with no managed
// exception, named in one line by the validation layers and by nothing else.
//
// The second failure looked exactly like an engine gap and was mine too: after moving to set 1
// binding 1, validation reported uAlbedo "used in draw but never updated". Reflection had it,
// the layout had it, the model draws passed it — but the lab's own GROUND goes through this
// same pipeline and was still passing only the shadow map. Every draw on a pipeline must bind
// every texture its shader declares, including the ones it does not care about.
layout(set = 1, binding = 0) uniform sampler2D uSunShadowMap;
layout(set = 1, binding = 1) uniform sampler2D uAlbedo;

layout(push_constant) uniform Push {
    mat4 uModel;
    vec4 uBaseColour;
    vec4 uMaterial;
};

layout(location = 0) in vec3 vWorld;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;
// The glTF COLOR_0 attribute: a multiplier on base colour. Multiplied, not added, which is what
// makes white the correct default.
//
// <b>It goes into ALBEDO, and the route here went the wrong way once.</b> On this tree's nature kit
// the channel is greyscale baked occlusion — a blade dark where it meets the ground, bark dark in
// its crevices — so it was moved to modulate the ambient term alone, on the reasoning that
// occlusion has no authority over a direct ray from the sun. That reasoning is sound about
// OCCLUSION and wrong about this ATTRIBUTE, and Khronos's own BoxVertexColors said so: it is the
// reference test for vertex colour, and against an ambient term of 0.06 it rendered WHITE instead
// of red, green and blue. COLOR_0 is a base-colour multiplier; the kit's use of it as occlusion is
// an authoring convention, not what the attribute means.
//
// The consequence is deliberate and worth stating, because it looks like a regression. The kit's
// blade bases are authored at COLOR_0 ≈ 0.008, so they multiply their albedo to nearly nothing and
// render near-black. That is this asset being drawn as it is written, not the renderer failing.
layout(location = 3) in vec4 vColour;

layout(location = 0) out vec4 outColour;

void main()
{
    vec3 N = normalize(vNormal);
    vec3 V = normalize(uCameraPosition.xyz - vWorld);
    vec3 L = normalize(uSunDirection.xyz);

    float ndotl = max(dot(N, L), 0.0);

    // Push the sample point off the surface along the normal before projecting into
    // light space — the engine's own remedy for shadow acne, sized in shadow texels.
    vec3 biased = blix_shadow_normal_offset(vWorld, N, ndotl, 4.0 / 2048.0, 1.5);
    vec4 lightSpace = uSunViewProjection * vec4(biased, 1.0);
    float shadow = blix_sun_shadow(uSunShadowMap, lightSpace, ndotl);

    float metallic = clamp(uMaterial.x, 0.0, 1.0);
    float roughness = clamp(uMaterial.y, 0.04, 1.0);
    vec3 albedo = uBaseColour.rgb * texture(uAlbedo, vUv).rgb * vColour.rgb;

    vec3 F0 = mix(vec3(0.04), albedo, metallic);
    vec3 direct = blix_cookTorranceBrdf(N, V, L, albedo, F0, metallic, roughness)
                * uSunColour.rgb * ndotl * shadow;

    // A flat ambient term standing in for image-based lighting, which this lab
    // deliberately does not carry — VulkanLit is where IBL lives. It picks up the vertex colour
    // through `albedo` above, so it is not applied twice here.
    vec3 ambient = albedo * uSunColour.a;

    outColour = vec4(direct + ambient, 1.0);
}
