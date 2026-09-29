// sheen.glsl's sampling functions, written once for both sampling forms.
//
// Included twice by sheen.glsl, after sampling_form.glsl has spelled one form and then the other:
// never include it directly, and never give it #pragma once, which would drop the second form.
// BLIX_TEX2D/BLIX_TEX3D are the texture parameter types, BLIX_SAMPLER_PARAM and BLIX_SAMPLER_ARG
// add the one sampler the separate form takes, and BLIX_S2D/BLIX_S3D spell a tap.

float blix_sheenAlbedo(BLIX_TEX2D sheenLut BLIX_SAMPLER_PARAM, float NdotV, float sheenRoughness)
{
    return texture(BLIX_S2D(sheenLut), vec2(clamp(NdotV, 0.0, 1.0), clamp(sheenRoughness, 0.0, 1.0))).r;
}
