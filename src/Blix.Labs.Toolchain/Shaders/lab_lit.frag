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

layout(set = 1, binding = 0) uniform sampler2D uSunShadowMap;

// Per-material albedo. The lab drew base-colour FACTORS only until now, so an asset's
// textures were imported, held in memory and never sampled — the tank rendered as flat
// green paint with its markings and panel lines missing, which looks plausible enough to
// not notice.
// <b>No albedo sampler yet, and the reason is an engine gap rather than a lab decision.</b>
// A per-part base-colour texture has two routes and neither works today:
//
//   set 2 via a MaterialHandle — the engine's per-material set. Passing a material AND an
//     inline texture list together is a combination nothing in the tree exercises
//     (VulkanHello uses a material with no inline textures, TankArena inline textures with
//     no material), and the draw dies with "statically uses descriptor set 2, but all sets
//     0 to 2 are not compatible" and a SIGSEGV with no managed exception.
//
//   set 1 binding 1 alongside the shadow map — reflection sees it and the layout carries
//     it, but the inline path never writes that descriptor: "uAlbedo is being used in draw
//     but has never been updated via vkUpdateDescriptorSets".
//
// So a second sampled texture in one draw is the gap. LabModel already uploads the
// textures and holds the handles; this is one line once the binding path takes them.

layout(push_constant) uniform Push {
    mat4 uModel;
    vec4 uBaseColour;
    vec4 uMaterial;
};

layout(location = 0) in vec3 vWorld;
layout(location = 1) in vec3 vNormal;
layout(location = 2) in vec2 vUv;

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
    vec3 albedo = uBaseColour.rgb;

    vec3 F0 = mix(vec3(0.04), albedo, metallic);
    vec3 direct = blix_cookTorranceBrdf(N, V, L, albedo, F0, metallic, roughness)
                * uSunColour.rgb * ndotl * shadow;

    // A flat ambient term standing in for image-based lighting, which this lab
    // deliberately does not carry — VulkanLit is where IBL lives.
    vec3 ambient = albedo * uSunColour.a;

    outColour = vec4(direct + ambient, 1.0);
}
