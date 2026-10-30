#version 450
// A transmissive pane's second draw: what it adds on top (blended Additive) -- the sky's reflection by its Fresnel share, and the base's remainder where
// transmission is under 1 (lit_body.glsl, LIT_GLASS_REFLECT).
#define LIT_GLASS_REFLECT
#include "lit_body.glsl"
