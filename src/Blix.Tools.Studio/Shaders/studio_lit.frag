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
    mat4 uSunViewProjection;
    vec4 uCameraPosition;
    vec4 uSunDirection;
    vec4 uSunColour;
    // x = prefilter mip ceiling (mip count - 1), FROM THE BAKE — see ibl.glsl for why this is a
    // value and not a constant. y = 1 when the environment probe is real, 0 when it is the 1x1
    // stand-in and the ambient should stay flat.
    vec4 uEnvironment;
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

// The environment, baked once from the house style's own sun — see StudioLook.
//
// <b>Every draw on this pipeline must bind all three, including the ones it does not care
// about.</b> That is the rule the uAlbedo comment above was written about, and these are three
// more chances to break it: the stage's own GROUND goes down this pipeline too.
layout(set = 1, binding = 2) uniform samplerCube uIrradiance;
layout(set = 1, binding = 3) uniform samplerCube uPrefilteredEnv;
layout(set = 1, binding = 4) uniform sampler2D uBrdfLut;

layout(push_constant) uniform Push {
    mat4 uModel;
    vec4 uBaseColour;
    vec4 uMaterial;
    // <b>Which TEXCOORD set the albedo samples.</b> glTF lets every texture on a material name its
    // own set, and a reader that assumes 0 does not fail on a material naming 1 — it samples the
    // wrong coordinates and draws something merely wrong.
    vec4 uExtra;          // x = albedo UV set
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
layout(location = 4) in vec2 vUv1;

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
    // <b>Cutout, before any shading is paid for.</b> uMaterial.w is the alpha cutoff and ZERO MEANS
    // NEVER, which is what lets OPAQUE and MASK share one pipeline: a cutout is a comparison the
    // fragment stage can make, where blending is pipeline state and cannot be.
    //
    // Alpha is the sampled texture's, times the material's baseColorFactor.a, times the vertex
    // colour's. All three are part of it per the glTF material model, and dropping any of them
    // makes a material that dims its own alpha look identical to one that does not.
    //
    // <b>A shader containing `discard` cannot be early-Z tested, even on a fragment that does not
    // take the branch.</b> Measured, after first blaming the expression: removing this line restores
    // byte-for-byte identity with the pre-cutout captures, and rewriting the arithmetic around it
    // does not. So every opaque draw on this stage now resolves depth late, which moves between 1
    // and 60 pixels of a 3.7M-pixel capture — silhouette fragments where a different one wins.
    //
    // Accepted rather than split into a MASK pipeline variant. The stage draws a handful of objects,
    // so early-Z buys it little, and a variant here is the first tile of the permutation matrix this
    // arc said it would not build: mask and non-mask times skinned and static times single- and
    // double-sided. If the studio ever draws enough for early-Z to matter, that is the fix, and this
    // comment is the reason it was not taken now.
    vec2 uvAlbedo = uExtra.x > 0.5 ? vUv1 : vUv;
    if (uMaterial.w > 0.0 && texture(uAlbedo, uvAlbedo).a * uBaseColour.a * vColour.a < uMaterial.w) discard;

    vec3 albedo = uBaseColour.rgb * texture(uAlbedo, uvAlbedo).rgb * vColour.rgb;

    vec3 F0 = mix(vec3(0.04), albedo, metallic);
    vec3 direct = blix_cookTorranceBrdf(N, V, L, albedo, F0, metallic, roughness)
                * uSunColour.rgb * ndotl * shadow;

    // <b>Image-based ambient, from a probe baked out of this stage's own sun.</b> The flat term
    // this replaces — `albedo * ambientStrength` — lit every surface identically whatever it faced,
    // which is exactly the information a person is looking for when they turn a model around.
    //
    // uSunColour.a is still the ambient strength and still scales the whole term, so the knob a
    // caller already had means the same thing it meant before; what changed is what it scales.
    //
    // The branch is on the PROBE, not on taste: with IBL off the stage binds a 1x1 stand-in, and
    // integrating a single texel through the split-sum would be a slower way of computing a flat
    // term that is also wrong. Uniform across the draw, so it costs nothing.
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

    // <b>Alpha reaches the output, for the pipeline that blends.</b> It is the product of the
    // texture's, the material's baseColorFactor.a and the vertex colour's — the same three the
    // cutout tests, because glTF says alpha is all three whatever the mode does with it. On an
    // opaque pipeline blending is off and this channel is ignored.
    outColour = vec4(direct + ambient, texture(uAlbedo, uvAlbedo).a * uBaseColour.a * vColour.a);
}
