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
vec3 blix_probeIrradiance(
    sampler2D irradianceAtlas, sampler2D depthAtlas,
    ivec3 dims, vec3 boundsMin, vec3 boundsSpan,
    vec3 worldPos, vec3 n)
{
    vec3 grid = clamp((worldPos - boundsMin) / boundsSpan, vec3(0.0), vec3(1.0)) * vec3(dims) - 0.5;
    ivec3 base = ivec3(floor(grid));
    vec3 frac = clamp(grid - vec3(base), vec3(0.0), vec3(1.0));

    vec3 sum = vec3(0.0);
    float weightSum = 0.0;

    for (int i = 0; i < 8; ++i) {
        ivec3 offset = ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        ivec3 probe = clamp(base + offset, ivec3(0), dims - 1);

        // Trilinear share of this corner.
        vec3 t = mix(1.0 - frac, frac, vec3(offset));
        float weight = t.x * t.y * t.z;

        vec3 probePos = blix_probePosition(probe, dims, boundsMin, boundsSpan);
        vec3 toProbe = probePos - worldPos;
        float dist = length(toProbe);
        vec3 dir = dist > 1e-5 ? toProbe / dist : n;

        // <b>Backface rejection.</b> A probe behind the surface has nothing to tell it, and this is
        // the cheap half of the test — smoothed rather than binary so a surface turning past a
        // probe does not pop.
        weight *= max(dot(dir, n), 0.0) * 0.5 + 0.5;

        // <b>Chebyshev visibility.</b> The probe's depth map, read in the direction of the shading
        // point, says how far its geometry is that way. If the point is further than that, a wall
        // stands between them.
        vec2 moments = texture(depthAtlas, blix_probeUv(probe, dims, -dir)).rg;
        float mean = moments.x;
        if (dist > mean) {
            float variance = max(moments.y - mean * mean, 1e-5);
            float d = dist - mean;
            float chebyshev = variance / (variance + d * d);
            // Cubed, as the paper has it: the raw ratio falls off far too gently to close a leak.
            weight *= max(chebyshev * chebyshev * chebyshev, 0.0);
        }

        if (weight <= 1e-5) continue;
        sum += texture(irradianceAtlas, blix_probeUv(probe, dims, n)).rgb * weight;
        weightSum += weight;
    }

    // Every probe rejected: the point is enclosed in a way the volume cannot describe, and zero is
    // a more honest answer than an unweighted average of the probes that just failed the test.
    return weightSum > 1e-5 ? sum / weightSum : vec3(0.0);
}
