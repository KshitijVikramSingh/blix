#ifndef BLIX_COVERAGE_GLSL
#define BLIX_COVERAGE_GLSL
#include "noise.glsl"

// Choosing WHICH multisample samples an alpha-cutout fragment owns, rather than letting the
// hardware choose the same ones for every layer.
//
// <b>Alpha-to-coverage turns alpha into a sample mask deterministically.</b> Two fragments with the
// same alpha therefore claim the same samples, and coverage stops accumulating: stack ten leaf cards
// at 6.3% each and you do not get 1 - 0.937^10 = 48%, you get something close to 6.3%, because they
// keep fighting over the same one or two buckets. Depth then hands those buckets to the nearest card
// and every other sample keeps whatever lies beyond the canopy. The resolve turns a ten-layer stack
// into a homogeneous foliage-and-background average with no layer contrast at all — a tree that
// glows flat instead of self-shadowing.
//
// This is the documented failure of A2C for overlapping transparents, and the documented remedy is
// to decorrelate the per-fragment mask. Give each layer its own permutation and depth complexity
// becomes opacity again: near leaves hide far leaves, gaps stay gaps.
//
// <b>The hash is WORLD space, so it does not crawl.</b> Screen space would give two cards at the
// same pixel the same permutation, which is the pathology rather than the cure; and it would swim
// under camera motion. Quantised world position gives each card a stable, distinct stream.

/// A sample mask with exactly `round(coverage * samples)` bits set, chosen by `hash`.
///
/// Reservoir-style selection: bit i is taken with probability remaining/(samples - i), which yields
/// a uniformly random subset of exactly the right size. The count is what preserves expected
/// coverage; the choice is what decorrelates it.
int blix_coverageMask(float coverage, int samples, float hash) {
    samples = clamp(samples, 1, 32);
    int n = int(floor(coverage * float(samples) + 0.5));
    if (n <= 0) return 0;
    if (n >= samples) return (1 << samples) - 1;

    int mask = 0;
    int remaining = n;
    float h = hash;
    for (int i = 0; i < samples; ++i) {
        // Cheap decorrelated stream: the golden-ratio increment walks the unit interval without
        // ever repeating a pattern short enough to see.
        h = fract(h * 1.6180339887 + 0.7548776662);
        if (h * float(samples - i) < float(remaining)) {
            mask |= 1 << i;
            remaining--;
        }
    }
    return mask;
}

/// The per-layer hash. Quantised finer than a leaf card is thick, so two cards overlapping in one
/// pixel land in different cells and therefore on different samples.
float blix_layerHash(vec3 worldPos) {
    return blix_hash31(floor(worldPos * 64.0));
}

#endif // BLIX_COVERAGE_GLSL
