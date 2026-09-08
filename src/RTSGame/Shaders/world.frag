#version 450

// Daylight world shading for everything that is not a skinned body: terrain, buildings, props, canopy.
// The shading itself lives in world_shade.glsl, shared with world_skinned.frag so a villager is lit by
// exactly the rules its village is.

#include "world_varyings.glsl"

// This stage has no texture: an instance's tint IS its colour, which is what the whole flat-shaded art
// direction rests on.
vec4 rts_surface_tint() {
    return vTint;
}

#include "world_shade.glsl"
