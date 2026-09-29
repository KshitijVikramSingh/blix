// probe_volume.glsl's sampling functions, written once for both sampling forms.
//
// Included twice by probe_volume.glsl, after sampling_form.glsl has spelled one form and then the other:
// never include it directly, and never give it #pragma once, which would drop the second form.
// BLIX_TEX2D/BLIX_TEX3D are the texture parameter types, BLIX_SAMPLER_PARAM and BLIX_SAMPLER_ARG
// add the one sampler the separate form takes, and BLIX_S2D/BLIX_S3D spell a tap.

/// <summary>Is there geometry between two points, by marching the occupancy grid?</summary>
///
/// <b>The ground truth the Chebyshev test approximates, and at this probe density it is cheap.</b>
/// A probe is about 0.8 m away and an occupancy cell is 0.145 m, so a line-of-sight check between a
/// shading point and its probe is five or six steps of a DDA through a texture already resident.
/// The statistical test exists because that used to be unaffordable at coarse probe spacing; it is
/// worth knowing what it costs and what it gets wrong now that it is not.
///
/// <b>Soft, and the first version was not, which was visible as blotching.</b> It returned 1.0 on
/// the first cell over 0.5, so a probe was either kept whole or deleted whole. As a shading point
/// slides across a vault, each of the eight probes crosses that threshold at some point, and every
/// crossing steps the normalised blend discontinuously — irregular patches on a smooth ceiling,
/// reported from the chair the first time this was switched on over an arcade. The Chebyshev test
/// it replaces never blotches for exactly this reason: it is a continuous falloff.
///
/// The grid stores DENSITY, not a boolean (see sky_inject.comp), so integrating it is also the more
/// honest reading of what the baker wrote. Beer-Lambert over the segment, with the step LENGTH in
/// the exponent so the result does not depend on how many steps the distance happened to earn.
float blix_probeOccluded(BLIX_TEX3D occupancy BLIX_SAMPLER_PARAM, ivec3 occDims, vec3 boundsMin, vec3 boundsSpan,
                         vec3 from, vec3 to)
{
    vec3 d = to - from;
    float len = length(d);
    if (len < 1e-4) return 0.0;
    vec3 cell = boundsSpan / vec3(occDims);
    float minCell = min(min(cell.x, cell.y), cell.z);
    int steps = int(clamp(len / minCell, 1.0, 24.0));
    float ds = len / float(steps);

    // Endpoints excluded: the surface the point sits on, and the probe's own cell, are both allowed
    // to be solid without meaning the path between them is blocked.
    float density = 0.0;
    for (int i = 1; i < steps; ++i) {
        vec3 p = from + d * (float(i) / float(steps));
        vec3 uvw = clamp((p - boundsMin) / boundsSpan, vec3(0.0), vec3(1.0));
        density += texture(BLIX_S3D(occupancy), uvw).r;
    }

    // Scaled so that one full-density cell attenuates by exp(-4) — opaque for any purpose here,
    // while a cell the baker only partly filled attenuates in proportion.
    return 1.0 - exp(-(4.0 / minCell) * ds * density);
}

/// What one probe's vote is worth: the facing term and the Chebyshev visibility test.
///
/// <b>Extracted so the leak metric cannot drift from the thing it measures.</b> A diagnostic that
/// re-implements the weighting it is auditing eventually audits a different weighting — this repo
/// has been bitten by exactly that shape twice tonight, once in a path tracer and once in a census.
float blix_probeVote(BLIX_TEX2D depthAtlas, BLIX_TEX3D occupancy BLIX_SAMPLER_PARAM, ivec3 dims, ivec3 occDims,
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
    vec2 moments = texture(BLIX_S2D(depthAtlas), blix_probeUv(probe, dims, -dir)).rg;
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
        float blocked = blix_probeOccluded(occupancy BLIX_SAMPLER_ARG, occDims, boundsMin, boundsSpan, worldPos, probePos);
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
    BLIX_TEX2D depthAtlas, BLIX_TEX3D occupancy BLIX_SAMPLER_PARAM, ivec3 dims, ivec3 occDims,
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
        float w = shares[i] * blix_probeVote(depthAtlas, occupancy BLIX_SAMPLER_ARG, dims, occDims, boundsMin,
                                            boundsSpan, probe, probePos, worldPos, n,
                                            occStrength, dir);
        if (w <= 1e-4) continue;

        total += w;
        leaked += w * blix_probeOccluded(occupancy BLIX_SAMPLER_ARG, occDims, boundsMin, boundsSpan, worldPos, probePos);
    }
    return total > 1e-5 ? leaked / total : 0.0;
}

vec3 blix_probeIrradianceEx(
    BLIX_TEX2D irradianceAtlas, BLIX_TEX2D depthAtlas, BLIX_TEX3D occupancy BLIX_SAMPLER_PARAM,
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
        float weight = shares[i] * blix_probeVote(depthAtlas, occupancy BLIX_SAMPLER_ARG, dims, occDims, boundsMin,
                                                  boundsSpan, probe, probePos, worldPos, n,
                                                  occStrength, dir);
        if (weight <= 1e-4) continue;
        sum += texture(BLIX_S2D(irradianceAtlas), blix_probeUv(probe, dims, n)).rgb * weight;
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
            float weight = shares[i] * blix_probeVote(depthAtlas, occupancy BLIX_SAMPLER_ARG, dims, occDims, boundsMin,
                                                      boundsSpan, probe, probePos, worldPos, n,
                                                      occStrength, dir);
            if (weight <= 1e-4) continue;
            sum += texture(BLIX_S2D(irradianceAtlas), blix_probeUv(probe, dims, n)).rgb * weight;
            weightSum += weight;
        }
    }

    confidence = weightSum;
    if (weightSum > 1e-5) return sum / weightSum;
    ivec3 nearest = clamp(ivec3(floor(grid + 0.5)), ivec3(0), dims - 1);
    return texture(BLIX_S2D(irradianceAtlas), blix_probeUv(nearest, dims, n)).rgb;
}

/// Eight-corner form, for callers that have not been given the choice.
vec3 blix_probeIrradianceEx(
    BLIX_TEX2D irradianceAtlas, BLIX_TEX2D depthAtlas, BLIX_TEX3D occupancy BLIX_SAMPLER_PARAM,
    ivec3 dims, ivec3 occDims, vec3 boundsMin, vec3 boundsSpan,
    vec3 worldPos, vec3 n, float occStrength, out float confidence)
{
    return blix_probeIrradianceEx(irradianceAtlas, depthAtlas, occupancy BLIX_SAMPLER_ARG, dims, occDims, boundsMin,
                                  boundsSpan, worldPos, n, false, occStrength, confidence);
}

/// The lookup without the diagnostic, for call sites that do not want to carry the out parameter.
vec3 blix_probeIrradiance(
    BLIX_TEX2D irradianceAtlas, BLIX_TEX2D depthAtlas, BLIX_TEX3D occupancy BLIX_SAMPLER_PARAM,
    ivec3 dims, ivec3 occDims, vec3 boundsMin, vec3 boundsSpan,
    vec3 worldPos, vec3 n, float occStrength)
{
    float ignored;
    return blix_probeIrradianceEx(irradianceAtlas, depthAtlas, occupancy BLIX_SAMPLER_ARG, dims, occDims, boundsMin,
                                  boundsSpan, worldPos, n, occStrength, ignored);
}
