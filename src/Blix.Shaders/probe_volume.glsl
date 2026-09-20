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
/// <summary>Is there geometry between two points, by marching the occupancy grid?</summary>
///
/// <b>The ground truth the Chebyshev test approximates, and at this probe density it is cheap.</b>
/// A probe is about 0.8 m away and an occupancy cell is 0.145 m, so a line-of-sight check between a
/// shading point and its probe is five or six steps of a DDA through a texture already resident.
/// The statistical test exists because that used to be unaffordable at coarse probe spacing; it is
/// worth knowing what it costs and what it gets wrong now that it is not.
///
/// Used by the leak metric below. Whether it should REPLACE the Chebyshev test rather than measure
/// it is the obvious next question and is deliberately not answered here.
float blix_probeOccluded(sampler3D occupancy, ivec3 occDims, vec3 boundsMin, vec3 boundsSpan,
                         vec3 from, vec3 to)
{
    vec3 d = to - from;
    float len = length(d);
    if (len < 1e-4) return 0.0;
    vec3 cell = boundsSpan / vec3(occDims);
    int steps = int(clamp(len / min(min(cell.x, cell.y), cell.z), 1.0, 24.0));
    // Endpoints excluded: the surface the point sits on, and the probe's own cell, are both allowed
    // to be solid without meaning the path between them is blocked.
    for (int i = 1; i < steps; ++i) {
        vec3 p = from + d * (float(i) / float(steps));
        vec3 uvw = clamp((p - boundsMin) / boundsSpan, vec3(0.0), vec3(1.0));
        if (texture(occupancy, uvw).r > 0.5) return 1.0;
    }
    return 0.0;
}

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

/// What one probe's vote is worth: the facing term and the Chebyshev visibility test.
///
/// <b>Extracted so the leak metric cannot drift from the thing it measures.</b> A diagnostic that
/// re-implements the weighting it is auditing eventually audits a different weighting — this repo
/// has been bitten by exactly that shape twice tonight, once in a path tracer and once in a census.
float blix_probeVote(sampler2D depthAtlas, sampler3D occupancy, ivec3 dims, ivec3 occDims,
                     vec3 boundsMin, vec3 boundsSpan, ivec3 probe,
                     vec3 probePos, vec3 worldPos, vec3 n, float occStrength, out vec3 dir)
{
    vec3 toProbe = probePos - worldPos;
    float dist = length(toProbe);
    dir = dist > 1e-5 ? toProbe / dist : n;

    // <b>Backface rejection, SQUARED so that it actually rejects.</b> The first form here was
    // max(dot, 0) * 0.5 + 0.5, which ranges 0.5 to 1 and therefore never culls anything — a probe
    // directly behind the surface still got half a vote, and every one of the texture fetches below
    // ran for all eight probes. Squaring the half-angle term takes a probe at dot = -1 to exactly
    // zero, which is both the correct weight and the thing that lets the caller skip its fetches.
    float facing = dot(dir, n) * 0.5 + 0.5;
    float weight = facing * facing;
    // Returned BEFORE the fetches, not after. The early-out used to sit below them, so it saved the
    // arithmetic and paid for the bandwidth anyway.
    if (weight <= 1e-4) return 0.0;

    // <b>Chebyshev visibility.</b> The probe's depth map, read in the direction of the shading
    // point, says how far its geometry is that way. If the point is further than that, a wall
    // stands between them.
    // <b>.b carries a reachability flag the injector writes, and this deliberately ignores it.</b>
    // Rejecting probes that light cannot reach is an obvious idea and it was built and measured:
    // it moved the share of blend weight landing on probes a surface cannot see from 27.1% to
    // 26.0%, while removing 13% of all bounce weight. The probes that leak are not the sealed
    // ones — they are ordinary, well-lit probes on the far side of a wall, which no property of
    // the probe alone can identify. The flag is kept because reading it back is how that was
    // settled (SponzaLoop's ProbeReachCensus, and the probe view's Reachable field); it is not
    // kept as a shading input, because it did not earn one.
    // .r mean distance, .g VARIANCE — already differenced by the injector, where the moments are
    // exact. Forming it here from a second moment meant differencing two bilinearly filtered
    // values, which cancels catastrophically and put visible bands on flat stone.
    vec2 moments = texture(depthAtlas, blix_probeUv(probe, dims, -dir)).rg;
    if (dist > moments.x) {
        float variance = max(moments.y, 1e-5);
        float d = dist - moments.x;
        float chebyshev = variance / (variance + d * d);
        // Cubed, as the paper has it: the raw ratio falls off far too gently to close a leak.
        weight *= max(chebyshev * chebyshev * chebyshev, 0.0);
    }

    // <b>And then the occupancy grid, which simply knows.</b> Chebyshev is a statistical stand-in
    // for line of sight, adopted when probes sat metres apart and marching between them was
    // unaffordable. At 8x density that premise is gone: a probe is about 0.8 m away and an
    // occupancy cell is 0.145 m, so the honest answer costs five or six taps of a 6 MB texture that
    // every neighbouring pixel is walking too.
    //
    // Measured on the orbit, --viz 21, sleeping off so every probe carries real moments:
    //
    //   occStrength   leak mean   median   p95     >25%     surviving weight   march cost
    //   0 (Chebyshev)     17.4%     1.1%   100%    20.8%              24.4%             -
    //   1 (marched)        0.0%     0.0%     0%     0.0%              21.0%       6.46 ms
    //
    // The zero is tautological — the census marches the same grid — so it is the SECOND number that
    // carries the result: surviving weight falls only 24.4% to 21.0%. The test removes a sixth of
    // the blend and it is the sixth that was arriving through walls, not an indiscriminate cull.
    // (A test that rejected everything would report the same perfect zero with confidence at 0.)
    //
    // <b>And it changes the picture by 0.36 mean sRGB at sleep 0, 0.04 at the shipped sleep, against
    // a bounce term worth 5.50.</b> Removing a sixth of the blend weight moves almost no light,
    // because the probes it removes carry radiance close to the ones that remain — the field is
    // smooth, and a normalised blend does not care which of two similar probes it asked.
    //
    // So this metric measures a MECHANISM and not an EFFECT, and the distinction was worth the cost
    // of learning. "Share of blend weight landing on probes the point cannot see" is exactly the
    // right way to ask whether the visibility test works, and no kind of evidence at all that the
    // visible colour bleed has this cause. The bleed reported from the chair is still unexplained;
    // what is now known is that it is not this, at this viewpoint.
    //
    // <b>And it is unaffordable at full resolution.</b> 6.46 ms, against 2.95 ms for the whole
    // tetrahedral saving it unblocks. Four corners barely helps (6.07 ms) because emptying the set
    // more often fires the retry-all-eight path. There is no cheap knob either: see the note below
    // on the low-share threshold. Six taps per probe is the honest price of the honest answer, and
    // the only remaining lever is how many pixels ask — which is the half-resolution incident-light
    // field, where the same test costs about 1.6 ms.
    //
    // occStrength 0 restores the old behaviour exactly, which is what makes this an --ab arm rather
    // than a rewrite. Watch the fallback rate alongside the leak: a test that rejects everything
    // reports no leak and no light, and only the confidence number tells those two apart.
    // <b>Skipping low-share corners was tried and gated nothing.</b> The obvious saving is to
    // march only probes carrying a real share of the blend — but at a 0.05 threshold the cost moved
    // 6.46 ms to 6.63 (noise) and the leak did not move at all, because everything Chebyshev and
    // the facing term let through is already above it. The cheap knob does not exist; the cost is
    // the march, and the lever is how many PIXELS ask for one.
    if (occStrength > 0.0 && weight > 1e-4) {
        float blocked = blix_probeOccluded(occupancy, occDims, boundsMin, boundsSpan, worldPos, probePos);
        weight *= mix(1.0, 1.0 - blocked, occStrength);
    }
    return weight;
}

/// The share of a point's blend weight that lands on probes it genuinely cannot see.
///
/// <b>A number for the thing that has been judged by eye all evening.</b> The Chebyshev test is a
/// statistical stand-in for visibility, and the figure this repo has carried — that it recovers 39%
/// of misdirected weight — came from a one-off census nobody can re-run against a change. This
/// computes it per pixel against a ground-truth march, so a fix to the occlusion can be watched
/// rather than argued: 0 is a point whose every contributing probe can see it, 1 is a point lit
/// entirely through walls.
float blix_probeLeakFraction(
    sampler2D depthAtlas, sampler3D occupancy, ivec3 dims, ivec3 occDims,
    vec3 boundsMin, vec3 boundsSpan, vec3 worldPos, vec3 n, bool tetrahedral, float occStrength)
{
    worldPos = blix_probeBiasedPos(worldPos, n, dims, boundsSpan);
    vec3 grid = clamp((worldPos - boundsMin) / boundsSpan, vec3(0.0), vec3(1.0)) * vec3(dims) - 0.5;
    ivec3 base = ivec3(floor(grid));
    vec3 frac = clamp(grid - vec3(base), vec3(0.0), vec3(1.0));

    ivec3 offsets[8];
    float shares[8];
    int corners = blix_probeCorners(frac, tetrahedral, offsets, shares);

    float total = 0.0;
    float leaked = 0.0;
    for (int i = 0; i < corners; ++i) {
        if (shares[i] <= 1e-4) continue;
        ivec3 probe = clamp(base + offsets[i], ivec3(0), dims - 1);

        vec3 probePos = blix_probePosition(probe, dims, boundsMin, boundsSpan);
        vec3 dir;
        float w = shares[i] * blix_probeVote(depthAtlas, occupancy, dims, occDims, boundsMin,
                                            boundsSpan, probe, probePos, worldPos, n,
                                            occStrength, dir);
        if (w <= 1e-4) continue;

        total += w;
        leaked += w * blix_probeOccluded(occupancy, occDims, boundsMin, boundsSpan, worldPos, probePos);
    }
    return total > 1e-5 ? leaked / total : 0.0;
}

vec3 blix_probeIrradianceEx(
    sampler2D irradianceAtlas, sampler2D depthAtlas, sampler3D occupancy,
    ivec3 dims, ivec3 occDims, vec3 boundsMin, vec3 boundsSpan,
    vec3 worldPos, vec3 n, bool tetrahedral, float occStrength, out float confidence)
{
    // <b>A surface must not reject its own probes, and without this bias it does.</b> The visibility
    // test asks a probe how far its geometry is in this direction — and for a point sitting ON a
    // wall, the nearest geometry that way IS that wall. The point is then further from the probe
    // than the probe's own depth says, every one of the eight fails, and the fallback returns black.
    // It shows up exactly where the test is most needed: surfaces tucked under arches and against
    // walls, which go dark instead of picking up bounced sun.
    //
    // A quarter of a cell is the usual figure: enough to clear a surface, far short of moving the
    // sample into the next room.
    worldPos = blix_probeBiasedPos(worldPos, n, dims, boundsSpan);

    vec3 grid = clamp((worldPos - boundsMin) / boundsSpan, vec3(0.0), vec3(1.0)) * vec3(dims) - 0.5;
    ivec3 base = ivec3(floor(grid));
    vec3 frac = clamp(grid - vec3(base), vec3(0.0), vec3(1.0));

    vec3 sum = vec3(0.0);
    float weightSum = 0.0;

    // <b>Four corners or eight, and on this machine that is a question about bytes.</b> A cube
    // splits into six tetrahedra by Kuhn's triangulation: sort the fractional coordinates and the
    // ordering names the tetrahedron, whose four barycentric weights are the successive differences
    // of the sorted values. The reconstruction stays C0 across tetrahedron faces, so there are no
    // seams — it is a different exact interpolation of the same lattice, not an approximation of
    // trilinear.
    //
    // Worth trying because the lit pass measured its arithmetic as free and its fetches as the
    // entire cost: the GGX lobe is 0.04 ms while this lookup's terms are 8.69. Halving the corners
    // halves both the irradiance and the depth fetches and pays a few compares for it, and it
    // measures exactly that — 1.302x of frame to 1.170x, a 2.95 ms saving.
    //
    // <b>It ships OFF, because it leaks, and the mechanism is worth stating exactly.</b> The first
    // guess was that four candidates empty the surviving set more often and drop through to the
    // unweighted nearest-probe fallback. That happens, and retrying the other four corners when the
    // set comes back empty costs nothing measurable — the 2.95 ms survived the fix intact — and it
    // did not stop the leak.
    //
    // The leak is in the NEARLY-empty case. With eight corners, six probes behind a wall and two
    // survivors average to something diluted; with four, a tetrahedron holding three rejected and
    // one survivor gives that one probe full weight. The Chebyshev test in this scene recovers only
    // 39% of the blend weight that lands on probes with no line of sight, so this does not create
    // leaks — it removes the dilution that was hiding the ones already there.
    //
    // Which makes this gated on the VISIBILITY TEST rather than on the interpolation. Against a
    // test that rejected correctly, four corners would be as good as eight and 2.95 ms cheaper. The
    // way to earn it is to fix the occlusion, not the reconstruction — and note that halving the
    // PIXELS asking instead (a half-resolution incident-light pass) has none of this problem, since
    // every query it does make is still the full eight-corner blend.
    ivec3 offsets[8];
    float shares[8];
    int corners = blix_probeCorners(frac, tetrahedral, offsets, shares);

    for (int i = 0; i < corners; ++i) {
        ivec3 probe = clamp(base + offsets[i], ivec3(0), dims - 1);
        vec3 probePos = blix_probePosition(probe, dims, boundsMin, boundsSpan);
        vec3 dir;
        float weight = shares[i] * blix_probeVote(depthAtlas, occupancy, dims, occDims, boundsMin,
                                                  boundsSpan, probe, probePos, worldPos, n,
                                                  occStrength, dir);
        if (weight <= 1e-4) continue;
        sum += texture(irradianceAtlas, blix_probeUv(probe, dims, n)).rgb * weight;
        weightSum += weight;
    }

    // <b>When four candidates all fail, try the other four before giving up.</b> The tetrahedral
    // set is half the cube's corners, so an empty result does not mean the point is unreachable —
    // it means the half we happened to pick was. Dropping straight to the unweighted nearest probe
    // from there is what put colour through interior walls: that fallback carries no occlusion at
    // all, and halving the candidates made it fire far more often than trilinear ever did.
    //
    // Retrying the full eight makes the leak strictly no worse than the eight-corner blend, because
    // the worst case IS the eight-corner blend. It costs eight fetches only where four found
    // nothing, which is the rare case by construction — the typical pixel still pays four.
    if (tetrahedral && weightSum <= 1e-5) {
        blix_probeCorners(frac, false, offsets, shares);
        for (int i = 0; i < 8; ++i) {
            ivec3 probe = clamp(base + offsets[i], ivec3(0), dims - 1);
            vec3 probePos = blix_probePosition(probe, dims, boundsMin, boundsSpan);
            vec3 dir;
            float weight = shares[i] * blix_probeVote(depthAtlas, occupancy, dims, occDims, boundsMin,
                                                      boundsSpan, probe, probePos, worldPos, n,
                                                      occStrength, dir);
            if (weight <= 1e-4) continue;
            sum += texture(irradianceAtlas, blix_probeUv(probe, dims, n)).rgb * weight;
            weightSum += weight;
        }
    }

    confidence = weightSum;
    if (weightSum > 1e-5) return sum / weightSum;
    ivec3 nearest = clamp(ivec3(floor(grid + 0.5)), ivec3(0), dims - 1);
    return texture(irradianceAtlas, blix_probeUv(nearest, dims, n)).rgb;
}

/// Eight-corner form, for callers that have not been given the choice.
vec3 blix_probeIrradianceEx(
    sampler2D irradianceAtlas, sampler2D depthAtlas, sampler3D occupancy,
    ivec3 dims, ivec3 occDims, vec3 boundsMin, vec3 boundsSpan,
    vec3 worldPos, vec3 n, float occStrength, out float confidence)
{
    return blix_probeIrradianceEx(irradianceAtlas, depthAtlas, occupancy, dims, occDims, boundsMin,
                                  boundsSpan, worldPos, n, false, occStrength, confidence);
}

/// The lookup without the diagnostic, for call sites that do not want to carry the out parameter.
vec3 blix_probeIrradiance(
    sampler2D irradianceAtlas, sampler2D depthAtlas, sampler3D occupancy,
    ivec3 dims, ivec3 occDims, vec3 boundsMin, vec3 boundsSpan,
    vec3 worldPos, vec3 n, float occStrength)
{
    float ignored;
    return blix_probeIrradianceEx(irradianceAtlas, depthAtlas, occupancy, dims, occDims, boundsMin,
                                  boundsSpan, worldPos, n, occStrength, ignored);
}

