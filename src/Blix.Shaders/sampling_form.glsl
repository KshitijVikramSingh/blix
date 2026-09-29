// How a Blix.Shaders sampling function spells its textures, in one of two forms.
//
// No #pragma once, on purpose: it is included once per form, and each inclusion respells the
// macros below. Libraries use it; a shader that only calls them never needs to.
//
// --- WHY THERE ARE TWO FORMS ---------------------------------------------------------------------
//
// A texture reaches a shader one of two ways, and they sample identically:
//
//   combined   sampler2D uMap                          image and sampling state in one descriptor
//   separate   texture2D uMap + sampler uLinearClamp   image and state apart; the sampler is shared
//
// texture(sampler2D(uMap, uLinearClamp), uv) compiles to the same sample instruction as
// texture(uMap, uv) on a combined sampler holding that state. What differs is descriptor
// accounting: Vulkan counts a combined sampler against the per-stage SAMPLER limit, 16 on MoltenVK,
// and a separate image only against sampled images (256), with one sampler shared by all of them. A
// stage reading more than about sixteen textures needs the separate form; everything else is free
// to keep the combined one. See docs/renderer.md, "Separate images and samplers".
//
// --- WHY A LIBRARY FUNCTION IS WRITTEN ONCE AND INCLUDED TWICE ----------------------------------
//
// GLSL builds sampler2D(t, s) only where it is sampled: it cannot be passed to a function. So a
// function taking sampler2D cannot be handed a separate image, and neither form can call the other
// (a sampler2D cannot be split back into image and state). The sampling functions therefore exist
// as an overload pair, differing only in their parameters and in how each tap is spelled, and each
// library writes the body once, in <lib>.sampled.glsl, against these macros:
//
//   BLIX_TEX2D, BLIX_TEX3D       the texture parameter type: sampler2D / texture2D, and 3D alike
//   BLIX_SAMPLER_PARAM           after the last texture parameter: nothing, or ", sampler blixSampler"
//   BLIX_SAMPLER_ARG             after the last texture argument in a call between sampling functions
//   BLIX_S2D(t), BLIX_S3D(t)     a tap's first argument: t, or sampler2D(t, blixSampler)
//
// The separate overload takes ONE sampler for every texture it reads, after the textures:
//
//   blix_sun_shadow_cascaded(uCascade0, uCascade1, uCascade2, vp0, ...)                  combined
//   blix_sun_shadow_cascaded(uCascade0, uCascade1, uCascade2, uLinearClamp, vp0, ...)    separate
//
// The library that includes the body twice ends by including this file once more without
// BLIX_SAMPLING_SEPARATE, so what it leaves behind is the combined spelling.

#undef BLIX_TEX2D
#undef BLIX_TEX3D
#undef BLIX_SAMPLER_PARAM
#undef BLIX_SAMPLER_ARG
#undef BLIX_S2D
#undef BLIX_S3D

#ifdef BLIX_SAMPLING_SEPARATE
#define BLIX_TEX2D texture2D
#define BLIX_TEX3D texture3D
#define BLIX_SAMPLER_PARAM , sampler blixSampler
#define BLIX_SAMPLER_ARG , blixSampler
#define BLIX_S2D(t) sampler2D(t, blixSampler)
#define BLIX_S3D(t) sampler3D(t, blixSampler)
#else
#define BLIX_TEX2D sampler2D
#define BLIX_TEX3D sampler3D
#define BLIX_SAMPLER_PARAM
#define BLIX_SAMPLER_ARG
#define BLIX_S2D(t) t
#define BLIX_S3D(t) t
#endif
