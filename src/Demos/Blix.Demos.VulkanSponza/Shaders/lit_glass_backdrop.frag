#version 450
// A frosted or thick pane (SponzaLoop.GlassBackdrop.cs): one draw that REPLACES the pixel with what is behind it -- refracted through a solid's thickness and blurred by the
// pane's roughness, read from the scene-behind chain -- times what the glass lets through, plus the pane's reflection (lit_body.glsl, LIT_GLASS_BACKDROP).
#define LIT_GLASS_BACKDROP
#include "lit_body.glsl"
