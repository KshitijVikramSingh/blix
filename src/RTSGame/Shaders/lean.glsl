#pragma once

// The wind lean, shared by the scene shader and the shadow caster — RTSGame/Shaders.
// Requires materials.glsl (isPlant) to have been included first.

// <b>Wind, as a lean rather than as an animation.</b> Nothing in this scene has ever moved except the
// people, which is most of why a still frame of it reads as a diorama: a wood with eleven thousand trees in
// it and not a leaf stirring is uncanny in a way no amount of shading fixes.
//
// Three decisions keep it restrained, which is the whole brief:
//
// It hinges at the ground. The displacement is scaled by how far the vertex is above its own instance's
// origin, so a trunk barely moves, a canopy leans, and nothing detaches from the earth it is planted in.
// Height enters as a square root rather than linearly, because a tree is stiffer than a blade of grass —
// linear would put the same degree of tilt on an eight-metre pine as on a hand of wheat, which at a
// believable amplitude for the pine is imperceptible on the wheat and at a believable one for the wheat
// throws the pine across the map.
//
// <b>It leans downwind, and only breathes across it.</b> The first version put equal amplitude on two
// axes at right angles, which makes a crown orbit — and an orbit reads as swinging rather than as weather,
// because nothing in wind goes round. Reported immediately as violent, and it was the shape rather than the
// size: most of the displacement is now a steady lean along the wind's own bearing, and what oscillates is
// a fraction of it plus a smaller sway across. A tree that is mostly just *leaning* looks windy while
// hardly moving at all, which is the cheapest calm there is.
//
// It is out of phase with itself. The phase carries a world-space term, so neighbours lean at different
// moments and a wood ripples instead of pulsing.
//
// It gusts, on a slow spatial wave, because steady wind reads as a machine. The gust never reaches zero —
// dead calm in one patch while the next one moves is a stranger artefact than a little wind everywhere.
//
// And nothing may travel more than a fraction of its own height, which is what the square root alone did
// not give. Root-of-height makes small plants move relatively *more* than tall ones — 8 cm on a 60 cm tuft
// is a seventh of it, and a whole field of grass shifting by a seventh shimmers. The cap is the rule a
// person would state if asked: a plant bends, it does not walk.
// <b>Wind arrives as a parameter, not from the push block, and that is the whole reason this file exists.</b>
// The scene shader and the shadow caster have different push layouts — the caster takes a matrix and this
// vec4 and nothing else — so a function reading the wind straight out of a push block could only ever live
// in one of them. It lived in world.vert, which meant a tree leaned and its shadow did not: the tree swayed
// and a still shadow sat under it, which reads as a shadow detached from its trunk and shimmers as the gust
// passes. A caster and its receiver have to agree about where the geometry is, and the only way to
// guarantee that is for both of them to call the same function.
//
// wind: x = lean at a metre up, y = the clock in simulated seconds, z = gust rate, w = bearing in radians.
vec3 blix_rts_lean(vec3 worldPos, float baseY, float carried, vec4 wind) {
    if (!isPlant(carried) || wind.x <= 0.0) return worldPos;
    float above = max(worldPos.y - baseY, 0.0);
    float t = wind.y;
    // A slow travelling wave, so a gust arrives somewhere before it arrives everywhere.
    float gust = 0.55 + 0.45 * sin(t * wind.z + (worldPos.x + worldPos.z * 0.7) * 0.02);
    float phase = t * 0.8 + worldPos.x * 0.23 + worldPos.z * 0.19;
    float amount = min(wind.x * gust * sqrt(above), above * 0.07);
    vec2 downwind = vec2(sin(wind.w), cos(wind.w));
    vec2 across = vec2(downwind.y, -downwind.x);
    vec2 offset = downwind * (0.62 + 0.38 * sin(phase)) + across * (0.22 * cos(phase * 0.83));
    return worldPos + vec3(offset.x, 0.0, offset.y) * amount;
}
