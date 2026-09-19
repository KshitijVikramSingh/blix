#pragma once
#ifndef BLIX_FROXEL_GLSL
#define BLIX_FROXEL_GLSL

// The froxel grid's depth distribution — one definition, because its two readers must agree
// exactly.
//
// The compute pass uses it to place a slice's sample point; the lit and sky passes use it in
// reverse, to find where a fragment at a given distance falls in the grid. They are the same curve
// read in opposite directions, and if they ever disagree the fog sits at the wrong distance from
// everything — which looks like a tuning problem rather than a bug. That is why it does not get to
// live in two files.
//
// <b>Quadratic, not linear.</b> Linear slices spend the same world thickness on the first metre as
// on the sixtieth, and perspective means the first metre is most of what fills the screen: the
// near field was quantised into steps a trilinear fetch could not hide, while the far field held
// resolution nothing could see. At t^2 and 48 slices out to 60 m the first slice is 1.3 cm and the
// last is 2.5 m, and the far end is where the medium is smooth anyway.
//
// The exponent is a constant on purpose. It trades near resolution against far resolution at a
// fixed slice count; a dial on it would be a dial on where the fog is allowed to be wrong.
#define BLIX_FROXEL_CURVE 2.0

/// World distance for a normalised grid coordinate (the froxel texture's w axis, 0..1).
float blix_froxelSliceDistance(float t, float farDist)
{
    return farDist * pow(clamp(t, 0.0, 1.0), BLIX_FROXEL_CURVE);
}

/// The inverse: the grid coordinate to sample for a fragment at `dist` metres.
float blix_froxelSliceCoord(float dist, float farDist)
{
    return pow(clamp(dist / max(farDist, 1e-4), 0.0, 1.0), 1.0 / BLIX_FROXEL_CURVE);
}

#endif // BLIX_FROXEL_GLSL
