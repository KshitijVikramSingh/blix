# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The settle rule's bias where light is rare, and stops that do not read their own luck (uv run
tools/gi-model/settle_rule.py [--texels N]).

slice_resolve.comp / SunNodesRefine.cs CookSettled: a texel's passes (32 rays each) start at FIRST, a round adds STEP
until MOST; it stops once the passes' standard error is under SHARE (5%) of the larger of its light and the dark bar
(0.1 of the scene's median texel light). A stop that reads the estimate it stops: a texel that has not met the rare
light yet reads dark AND steady, and stops there. The crevices' corridor is lit through a doorway: with the light lists
off (2026-10-22) its surfaces read ~1% dark, two seeds; on, -0.35% -- the lists find the doorway more often.

A texel's ray here: a steady part (exponential, (1 - F) of its light) and the rare part, met with odds P a ray and
carrying F / P (F of its light). Truth is 1; the scene's median texel light MEDIAN (the bar is 0.1 of it), so a dark
texel (truth well under the median) is judged against the bar. Neighbours (the pooled stop) share the distribution.

The stops:
  own        today's
  pooled     the variance pooled over the 3x3 (tools/gi-model/skewed_filter.py): each texel's stop on its neighbourhood's noise
  split      stopped on its odd passes' statistics, estimated from its even passes alone: the stop knows nothing of the
             estimate (unbiased, at half the rays' worth)
  split all  stopped on its odd passes, estimated from all: half the coupling
  floor V    the variance at least V x the light^2 a pass (a prior on how rough a texel's rays can be: the rare light's
             variance is F^2 (1/P - 1) a ray)
  patch-pool its patch's texels' relative noise (variance / light^2) pooled, the larger of that x the patch's mean light^2 and its
             own variance: never settles sooner than own; what the cook can pool (a refine round's list is unordered)
  patch      not settled while its light is under half its patch's (the solve's: smooth, noise-free, here the truth x
             a patch error, sd 20%) -- a texel that reads far darker than its surroundings predict refines on
Reported: the mean over truth (the bias), the spread (noise), and the mean rays a texel took.
"""
import sys
import numpy as np

rng = np.random.default_rng(11)
N = int(sys.argv[sys.argv.index("--texels") + 1]) if "--texels" in sys.argv else 4000
SHARE = 0.05


def passes_for(n, p, f, k):
    """k passes of 32 rays for n texels: each pass's mean."""
    steady = rng.exponential(1 - f, size=(n, k, 32))
    rare = rng.random((n, k, 32)) < p
    return (steady + rare * (f / p)).mean(2)


def run(rule, p, f, first, step, most, median, prior=0.0, ratio=1.0, cut=0.5):
    bar = 0.1 * median
    fp, sp, mp = first // 32, step // 32, most // 32
    groups = 9 if rule in ("pooled", "patch-pool") else 1
    P = passes_for(N * groups, p, f, mp).reshape(N, groups, mp)
    # The patch's light at the texel: ratio x the texel's truth (a crease texel is darker than its patch's centre), a 20% error.
    patch = ratio * np.clip(1 + 0.2 * rng.standard_normal(N), 0.3, None)
    est = np.empty(N); used = np.empty(N)
    for i in range(N):
        k = fp
        while True:
            if rule in ("split", "split all"):
                dec = P[i, 0, 1:k:2]
                m = dec.mean(); var = dec.var(ddof=1); n = dec.size
                se = np.sqrt(var / n) / np.sqrt(2)          # as if all k passes, from the odd half's spread
            elif rule == "pooled":
                g = P[i, :, :k]
                m = g[0].mean(); var = g.var(1, ddof=1).mean(); se = np.sqrt(var / k)
            elif rule == "patch-pool":
                # Its patch's texels' relative noise (variance over light^2, each its own), the larger of that x its own
                # light^2 and its own variance: it settles no sooner than today.
                g = P[i, :, :k]
                m = g[0].mean()
                rel = (g.var(1, ddof=1) / np.maximum(g.mean(1), 1e-9) ** 2).mean()
                # x the patch's mean light, not its own: an unlucky dark texel's own light shrinks the pool's noise with it.
                mp_ = g.mean()
                var = max(g[0].var(ddof=1), rel * mp_ * mp_); se = np.sqrt(var / k)
            else:
                own = P[i, 0, :k]
                m = own.mean(); var = own.var(ddof=1)
                if rule == "floor": var = max(var, prior * m * m)
                se = np.sqrt(var / k)
            done = se <= SHARE * max(m, bar)
            if rule == "patch" and m < cut * patch[i]: done = False
            if done or k >= mp: break
            k = min(k + sp, mp)
        est[i] = P[i, 0, 0:k:2].mean() if rule == "split" else P[i, 0, :k].mean()
        used[i] = k * 32
    return est.mean() - 1, est.std(), used.mean()


def main():
    rules = [("own", 0.0), ("pooled", 0.0), ("patch-pool", 0.0), ("patch", 0.0)]
    cases = [
        # (name, P, F, median, ratio): the corridor's texels dark against the scene (median 10-30x their light), the
        # doorway most of it; a smooth texel (no rare light: the early stop's whole point); a crease texel at half its
        # patch's light (the patch rule's cost).
        ("corridor, lists on (P 0.05)", 0.05, 0.7, 10.0, 1.0),
        ("corridor, lists off (P 0.02)", 0.02, 0.7, 10.0, 1.0),
        ("corridor, lists off (P 0.01)", 0.01, 0.7, 10.0, 1.0),
        ("darker corridor (median 30x), P 0.02", 0.02, 0.7, 30.0, 1.0),
        ("darker corridor (median 30x), P 0.01", 0.01, 0.7, 30.0, 1.0),
        ("a lit texel, rare sun (P 0.02)", 0.02, 0.5, 1.0, 1.0),
        ("a smooth texel (no rare light)", 0.5, 0.0, 1.0, 1.0),
        ("a smooth dark texel (median 10x)", 0.5, 0.0, 10.0, 1.0),
        ("a crease texel at 0.5 of its patch, P 0.02", 0.02, 0.5, 3.0, 2.0),
    ]
    for stage, first, step, most in (("coarse: 512 + 128 a round to 2,048", 512, 128, 2048), ("fine: 128 + 128 a round to 1,024", 128, 128, 1024)):
        print(f"\n== {stage}")
        for name, p, f, median, ratio in cases:
            print(f"   {name}")
            for rule, prior in rules:
                cut = prior if rule == "patch" and prior > 0 else 0.5
                bias, noise, rays = run(rule, p, f, first, step, most, median, 0.0, ratio, cut)
                label = rule + (f" {cut:g}" if rule == "patch" else "")
                if rule == "patch-pool": label = "patch-pool"
                print(f"      {label:10s} bias {100 * bias:+6.2f}%   noise {100 * noise:5.2f}%   rays {rays:6.0f}")


if __name__ == "__main__":
    main()
