# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""What the texels' edge-aware filter and the settle rule do to a gather whose noise is skewed (uv run
tools/gi-model/skewed_filter.py).

In the dark crevices the light reaches a texel through rare paths: a ray finds it with probability p, carrying 1/p. A
texel's estimate over N rays is then Binomial(N, p) / (N p) -- mean 1, but most texels low and a few high. The filter
(texel_filter_neighbour.glsl, texelFilterWeight) weighs a 3x3 neighbour exp(-d^2/2) exp(-e^2), the edge
e = (other - centre) / (0.5 centre) -- against the centre, unless --texel-filter-symmetric (against the brighter of the
two). A low centre drops its brighter neighbours as edges and stays low; a high one keeps its darker ones and is pulled
down: the mean falls. The settle rule (slice_resolve.comp) stops a texel once its passes' standard error is under 5% of
max(its light, the dark bar) -- a stop that reads the estimate it stops. Truth is 1 everywhere."""
import numpy as np

rng = np.random.default_rng(3)
SIZE = 256


def estimates(p, rays):
    return rng.binomial(rays, p, size=(SIZE, SIZE)) / (rays * p)


def filtered(E, mode):
    acc = np.zeros_like(E); tot = np.zeros_like(E)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            o = np.roll(np.roll(E, dy, 0), dx, 1)
            w = np.exp(-0.5 * (dx * dx + dy * dy))
            if mode == "centre":
                e = (o - E) / (0.5 * E + 1e-5)
                w = w * np.exp(-e * e)
            elif mode == "symmetric":
                e = (o - E) / (0.5 * np.maximum(o, E) + 1e-5)
                w = w * np.exp(-e * e)
            acc += w * o; tot += w
    return acc / tot


def settled_pooled(p, first, step, cap, bar, share=0.05, neighbours=9):
    """The same stop with each texel's noise judged from its neighbourhood: the passes' variance pooled over its
    `neighbours` (which share its paths), so when it stops does not hang on its own luck."""
    out = np.empty(SIZE * 8)
    for i in range(out.size):
        group = [list(rng.binomial(32, p, size=first // 32) / (32 * p)) for _ in range(neighbours)]
        while True:
            own = group[0]
            m = np.mean([np.mean(g) for g in group])
            var = np.mean([np.var(g, ddof=1) for g in group])
            se = np.sqrt(var / len(own))
            if se < share * max(m, bar) or len(own) * 32 >= cap: break
            for g in group: g += list(rng.binomial(32, p, size=step // 32) / (32 * p))
        out[i] = np.mean(group[0])
    return out.mean()


def settled(p, first, step, cap, bar, share=0.05):
    """Passes of `step` rays from `first` to `cap`, stopping once the standard error over passes < share x max(mean, bar)."""
    out = np.empty(SIZE * 8)
    for i in range(out.size):
        passes = list(rng.binomial(32, p, size=first // 32) / (32 * p))
        while True:
            m = np.mean(passes)
            se = np.std(passes, ddof=1) / np.sqrt(len(passes)) if len(passes) > 1 else np.inf
            if se < share * max(m, bar) or len(passes) * 32 >= cap: break
            passes += list(rng.binomial(32, p, size=step // 32) / (32 * p))
        out[i] = np.mean(passes)
    return out.mean()


if __name__ == "__main__":
    print(f"{'p (hit odds)':>13}{'rays':>6}{'raw':>8}{'centre (now)':>14}{'symmetric':>11}{'no edge':>9}   mean over truth, after the filter")
    for p in (0.5, 0.2, 0.05, 0.02, 0.01):
        for rays in (128, 512):
            E = estimates(p, rays)
            print(f"{p:13.2f}{rays:6d}{E.mean():8.3f}{filtered(E, 'centre').mean():14.3f}{filtered(E, 'symmetric').mean():11.3f}{filtered(E, 'none').mean():9.3f}")
    print("\nthe settle rule (128 rays, +128 a round to 1024, 5%, the dark bar at 0.1 of the light):")
    for p in (0.2, 0.05, 0.02):
        print(f"   p {p:.2f}: settled mean {settled(p, 128, 128, 1024, 0.1):.3f}   pooled over the 3x3 {settled_pooled(p, 128, 128, 1024, 0.1):.3f}")
