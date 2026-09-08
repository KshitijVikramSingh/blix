#version 450

// The skinned body's fragment stage: the same shading as everything else, with a character's albedo
// texture multiplied into its tint. §170.
//
// Why this stage exists at all rather than a binding added to world.frag: a texture bound to the shared
// stage would have to be bound by every terrain and prop draw too, each supplying a white one-pixel stand-in
// — a binding that exists for one consumer and is humoured by a dozen. A second thin stage over one shared
// include is the smaller change and it keeps the lighting single.

#include "world_varyings.glsl"

// <b>Set 0 binding 5, appended after the maps the plain stage declares.</b> Bound per primitive, because a
// character is several materials — cloth here, skin there — and each carries its own image.
layout(set = 0, binding = 5) uniform sampler2D uBodyAlbedo;

// The tint carries the material class in alpha and, for a body, white in rgb; the picture is in the
// texture. Sampled at the asset's own UVs, which ride through the vertex stage as vGround.
vec4 rts_surface_tint() {
    vec3 picture = texture(uBodyAlbedo, vGround).rgb;
    return vec4(vTint.rgb * picture, vTint.a);
}

#include "world_shade.glsl"
