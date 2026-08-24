// A guard the compiler honours: glslc ignores #pragma once, so this file and noise.glsl below both need the
// real thing — world.frag includes noise.glsl on its own account as well as getting it through here.
#ifndef RTS_VEIL_GLSL
#define RTS_VEIL_GLSL

// <b>Where the fog of war is, shared by everything that has to be hidden by it.</b>
//
// This exists for the reason lean.glsl exists, and it is the same reason twice: two shaders draw the same
// world through different push-constant layouts, so a function that read the dials out of a block could only
// ever live in one of them. The alternative is a second copy of this arithmetic, and a second copy is not a
// tidiness problem — it is the drift bug. The veil and whatever reads it have to agree about *where* the fog
// is to within a pixel, or a puff of smoke hangs in clear air over ground the cloud has covered.
//
// It returns the two densities and nothing about colour, which is the seam that makes it shareable at all.
// How a surface is hidden is the caller's business and the callers genuinely differ: world.frag mixes an
// opaque pixel toward lit cloud, while smoke is already translucent and additive, so mixing its colour would
// paint cloud-coloured smoke rather than hiding any. Smoke gives up alpha instead.
//
// The parameters, all of which the caller supplies from wherever they live in its own block:
//   mask     RG8, one texel per fog cell: red is explored, green is watched.
//   extent   the map's extent in metres. <b>Not the grid span</b> — see the offset below.
//   scouted  x = one over the grid span, y = memory density, z = deep density, w = colour drain.
//   cloud    x = one over a billow's size, y = wispiness, z = drift m/s, w = brightness (unused here).
//   air      x = scatter (unused here), y = sun glow (unused here), z = along-wind squeeze, w = gust.
//   deep     x = brightness (unused here), y = scatter share (unused here), z = solidity, w = edge falloff.
//   wind     y = the simulated clock, z = gust rate, w = bearing in radians.

#include "noise.glsl"

// Returns x = the memory layer's density, y = the deep bank's density. Both zero where nothing is hidden.
vec2 blix_rts_veil_density(
    sampler2D mask,
    vec3 worldPos,
    float extent,
    vec4 scouted,
    vec4 cloud,
    vec4 air,
    vec4 deep,
    vec4 wind)
{
    // <b>Sampled in wind-aligned coordinates, so the cloud can be a shape that has a direction.</b> Along the
    // wind and across it, with the along axis compressed — which stretches what comes out of the noise
    // downwind. Isotropic noise that merely translates reads as a texture sliding over the map however fast
    // it goes; a bank pulled out along its own motion reads as weather sweeping across it.
    vec2 heading = vec2(cos(wind.w), sin(wind.w));
    vec2 across = vec2(-heading.y, heading.x);
    float travelled = wind.y * cloud.z;
    vec2 alongAcross = vec2(dot(worldPos.xz, heading) + travelled, dot(worldPos.xz, across));
    vec2 p = vec2(alongAcross.x * air.z, alongAcross.y) * cloud.x;

    // Two octaves, the finer one carried further downwind than the broad one, which puts a parallax between
    // the layers instead of scaling one of them. On the wind's own clock, so the fog, the canopy and the
    // water's ripples agree about the weather rather than holding three opinions about it.
    float billows =
        blix_fbm2(p) * 0.62 +
        blix_fbm2(p * 2.30 + vec2(travelled * cloud.x * air.z * 1.35, 0.0)) * 0.38;

    // The gust, in bands running across the wind. A single global pulse would make the whole map breathe in
    // unison, which nothing does; a wave travelling along the wind thickens one band while the next thins.
    billows *= 1.0 + air.w * sin(wind.y * wind.z * 0.55 - alongAcross.x * cloud.x * 1.7);

    // <b>The boundary is displaced before it is read, which is what makes it seep rather than step.</b>
    // Thinning a veil with noise varies how thick it is and leaves the shape of its edge where the mask put
    // it — so a ten-metre grid stays legible as a grid however much the density wobbles, which is what
    // "blocky" meant. Warping the lookup moves the edge itself: the same fog, asked about a point a few
    // metres off, and the answer wanders in and out of the cells in fingers. Displaced in world axes from
    // noise sampled in the wind's, so the fingers it tears also lie downwind.
    vec2 warpAmount = vec2(
        blix_fbm2(p * 1.7 + vec2(11.3, 4.1)) - 0.5,
        blix_fbm2(p * 1.7 + vec2(2.7, 19.6)) - 0.5) * cloud.y * 0.9 / cloud.x;
    vec2 warp = heading * warpAmount.x + across * warpAmount.y;

    // <b>Offset by the map extent, scaled by one over the grid span, and the two are not the same length.</b>
    // Derived rather than asserted: cell i covers world [i*c - e/2, (i+1)*c - e/2) and centres at
    // (i+0.5)*c - e/2, so a texel-centre lookup wants uv = (world + e/2) / (cells*c). The grid is a ceiling
    // plus one cell, so it spans more ground than the map does — using the span for both slides the whole
    // veil half a cell, which is invisible as an offset and was written that way once already. RtsGameLoop
    // asserts this agrees with the masks, per cell, because the shader's copy cannot be read from a log.
    vec2 uv = (worldPos.xz + warp + extent * 0.5) * scouted.x;
    vec2 sampled = texture(mask, uv).rg;

    // <b>Smoothed again on the way out, because a linear ramp has a corner in it.</b> The mask is blurred on
    // the CPU and the sampler interpolates it linearly; linear interpolation is continuous in value but not
    // in slope, and it is the slope the eye reads as a facet. Two applications of the cubic ease flatten
    // those corners at both ends of the ramp.
    sampled = sampled * sampled * (3.0 - 2.0 * sampled);
    sampled = sampled * sampled * (3.0 - 2.0 * sampled);

    // <b>Two layers, not one number lerped through three tiers.</b> Sharing a scalar meant the only way to
    // make unscouted ground opaque was to drag the whole ramp and pay for it in visibility on ground already
    // scouted — which, after the opening reveal, is most of the frame. Split, the deep term is identically
    // zero on fully known ground, so its density is free to go as high as it likes.
    float known = sampled.r;
    float watched = sampled.g;
    float memory = known * (1.0 - watched) * scouted.y;
    float bank = pow(1.0 - known, deep.w) * scouted.z;
    if (memory + bank <= 0.002) return vec2(0.0);

    // The noise thins and thickens both layers on top of the warp: the warp decides where the edge is, this
    // decides how solid the middle is. At zero wispiness it is a flat sheet of exactly the two densities.
    float wisp = mix(1.0 - cloud.y, 1.0 + cloud.y, billows);
    // The deep bank curves the same field rather than sampling a second one. Below one it fills the thin
    // parts in while leaving the thick ones, so it reads as a mass that has texture instead of as the mist
    // turned up. One weather system: two independent noise fields drifting over each other never resolve
    // into a sky.
    return vec2(
        clamp(memory * wisp, 0.0, 1.0),
        clamp(bank * pow(clamp(wisp, 0.0, 2.0), deep.z), 0.0, 1.0));
}

#endif // RTS_VEIL_GLSL
