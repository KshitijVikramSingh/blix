#version 450
// Opaque materials without sheen or diffuse transmission, on a frame with no debug view: the early depth test and
// none of what they never run (lit_body.glsl, LIT_LEAN).
#define LIT_OPAQUE
#define LIT_LEAN
#include "lit_body.glsl"
