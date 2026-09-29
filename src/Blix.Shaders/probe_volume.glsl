#pragma once
#include "octahedral.glsl"

// Sampling an octahedral probe volume, with the visibility test that stops it leaking.
//
// <b>Nearest-probe sampling leaks, and the leak is coloured.</b> A surface whose nearest probe sits
// on the far side of a wall receives that probe's light — so in Sponza a wall took colour from the
// curtains behind it and from a tree it cannot see. At probe spacings over a metre against
// architectural walls that is not an edge case, it is the common case.
//
// The fix is the other half of DDGI (Majercik et al. 2019): each probe stores, per direction, the
// MEAN and MEAN-SQUARE distance to whatever its rays hit. A shading point asks each of the eight
// probes around it "how far is your geometry, in my direction" and compares against how far away
// the probe actually is. A probe behind a wall reports a much shorter distance than it is away,
// and its contribution is rejected.
//
// Chebyshev rather than a hard depth test, because probe depth is a low-resolution average: a hard
// comparison turns every reconstruction error into a black seam, and the variance term says how
// much to trust the mean. This is the same reason variance shadow maps exist.

#ifndef BLIX_PROBE_TILE
#define BLIX_PROBE_TILE     8
#define BLIX_PROBE_INTERIOR 6
#endif

// Where a probe's tile sits in the atlas, in texels.
vec2 blix_probeTile(ivec3 probe, ivec3 dims) {
    return vec2(probe.x, probe.y + probe.z * dims.y) * float(BLIX_PROBE_TILE);
}

// A direction inside one probe's tile, as an atlas UV. The +1 and the interior scale keep the
// sample inside the border ring, which exists so this fetch can be bilinear at all.
vec2 blix_probeUv(ivec3 probe, ivec3 dims, vec3 dir) {
    vec2 oct = blix_octEncode(normalize(dir)) * 0.5 + 0.5;
    vec2 texel = blix_probeTile(probe, dims) + 1.0 + oct * float(BLIX_PROBE_INTERIOR);
    vec2 atlas = vec2(dims.x, dims.y * dims.z) * float(BLIX_PROBE_TILE);
    return texel / atlas;
}

vec3 blix_probePosition(ivec3 probe, ivec3 dims, vec3 boundsMin, vec3 boundsSpan) {
    return boundsMin + (vec3(probe) + 0.5) * boundsSpan / vec3(dims);
}

/// Irradiance at `worldPos` for a surface facing `n`, blended over the eight surrounding probes and
/// weighted so that probes which cannot see the point contribute nothing.
/// The same lookup, reporting how much of the eight-probe blend actually survived the visibility
/// test. `confidence` is the summed weight: 1 where all eight probes agree they can see the point,
/// small where most were rejected, and exactly 0 where every one was — the case the fallback below
/// covers. Nothing about the returned colour says which of those happened, and the difference
/// matters: a fallback result carries NO occlusion, because the whole point of the fallback is that
/// the test rejected everything. A surface lit by it is lit by an unweighted nearest probe.
/// The normal bias every probe query applies before it decides anything.
///
/// A quarter of a cell: enough to lift a point off the wall it sits on, far short of moving the
/// sample into the next room. Shared because a metric that biases differently from the lookup is
/// measuring a point the lookup never asked about.
vec3 blix_probeBiasedPos(vec3 worldPos, vec3 n, ivec3 dims, vec3 boundsSpan)
{
    vec3 cellSize = boundsSpan / vec3(dims);
    return worldPos + n * (0.25 * max(max(cellSize.x, cellSize.y), cellSize.z));
}

/// Which lattice corners a point interpolates between, and each one's share.
///
/// <b>Eight for trilinear, four for Kuhn's tetrahedral split — and shared, which is the point.</b>
/// The leak metric first shipped with its own eight-corner loop, so both reconstruction arms
/// measured the trilinear weighting and came back identical to three significant figures. Identical
/// numbers from two arms that differ by half their fetches is not a result, it is an instrument
/// reporting on something other than the setting being changed.
int blix_probeCorners(vec3 frac, bool tetrahedral, out ivec3 offsets[8], out float shares[8])
{
    if (!tetrahedral) {
        for (int i = 0; i < 8; ++i) {
            ivec3 offset = ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
            vec3 t = mix(1.0 - frac, frac, vec3(offset));
            offsets[i] = offset;
            shares[i] = t.x * t.y * t.z;
        }
        return 8;
    }

    // Sorted-permutation barycentric: w = (1 - a, a - b, b - c, c) for fractions sorted a>=b>=c,
    // with each successive vertex adding one to the axis whose fraction came next.
    bvec3 o = bvec3(frac.x >= frac.y, frac.y >= frac.z, frac.x >= frac.z);
    ivec3 ax;
    if (o.x && o.y)                 ax = ivec3(0, 1, 2);
    else if (o.x && !o.y && o.z)    ax = ivec3(0, 2, 1);
    else if (!o.z)                  ax = ivec3(2, 0, 1);
    else if (!o.x && o.y)           ax = ivec3(1, 0, 2);
    else if (!o.x && !o.y && o.z)   ax = ivec3(1, 2, 0);
    else                            ax = ivec3(2, 1, 0);
    float f0 = frac[ax.x], f1 = frac[ax.y], f2 = frac[ax.z];
    vec4 tetW = vec4(1.0 - f0, f0 - f1, f1 - f2, f2);
    ivec3 acc = ivec3(0);
    offsets[0] = acc;
    acc[ax.x] += 1; offsets[1] = acc;
    acc[ax.y] += 1; offsets[2] = acc;
    acc[ax.z] += 1; offsets[3] = acc;
    for (int i = 0; i < 4; ++i) shares[i] = tetW[i];
    return 4;
}

// The sampling functions, in both forms: combined (sampler2D) and separate (texture2D + one sampler).
// See sampling_form.glsl for why both exist and how the two are spelled.
#include "sampling_form.glsl"
#include "probe_volume.sampled.glsl"
#define BLIX_SAMPLING_SEPARATE
#include "sampling_form.glsl"
#include "probe_volume.sampled.glsl"
#undef BLIX_SAMPLING_SEPARATE
#include "sampling_form.glsl"
