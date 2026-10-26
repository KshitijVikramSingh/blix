#version 450
// A transmissive pane's first draw: the scene behind it, filtered (blended Multiply) by what the glass lets through -- (1 - F) T, the base colour, the volume's
// attenuation (lit_body.glsl, LIT_GLASS_FILTER).
#define LIT_GLASS_FILTER
#include "lit_body.glsl"
