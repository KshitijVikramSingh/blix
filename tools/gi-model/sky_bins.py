# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""A texel's sky as a light-free list: its visibility in direction bins, lit by a sky it was not cooked under (uv run
tools/gi-model/sky_bins.py).

The sky is half to three quarters of a texel's light (measured: Sponza's coarse texels 76%, fine 49%). A list
keeps per texel the cosine-weighted share of its hemisphere that sees the sky, by bin (octahedral, N x N over the
sphere); a sky lights it as sum_b V_b L_b, L_b the sky's mean radiance over bin b. Exact: the integral of V L cos. The
error is V and L varying together inside a bin, and it shows when the sky changes (a time of day): cooked for one, lit
by another.

Visibility: a texel on the floor of a courtyard (walls of random heights and gaps about it) or under a canopy edge;
skies: an overcast gradient (zenith 3x the horizon) and a clear sky with a bright circumsolar region (a sun 25 or 60
degrees up, its disc left out: the sun is the shadow rays'). Reported: the error of the binned sky against exact, per
bin count, over random texels."""
import numpy as np

rng = np.random.default_rng(3)


def octa_bin(d, n):
    p = d / np.abs(d).sum(-1, keepdims=True)
    x, y = p[..., 0], p[..., 2]
    neg = p[..., 1] < 0
    ox = np.where(neg, (1 - np.abs(y)) * np.sign(x), x)
    oy = np.where(neg, (1 - np.abs(x)) * np.sign(y), y)
    i = np.clip(((ox * 0.5 + 0.5) * n).astype(int), 0, n - 1)
    j = np.clip(((oy * 0.5 + 0.5) * n).astype(int), 0, n - 1)
    return j * n + i


def sky(d, kind, sun_el):
    up = np.clip(d[..., 1], 0, 1)
    if kind == "overcast":
        return (1 + 2 * up) / 3
    s = np.array([np.cos(np.radians(sun_el)), np.sin(np.radians(sun_el)), 0.0])
    c = np.clip(d @ s, -1, 1)
    return 0.3 + 0.5 * (1 - up) ** 2 + 2.5 * np.exp(-np.arccos(c) / 0.35)


def visibility(d, scene):
    az = np.arctan2(d[..., 2], d[..., 0])
    el = np.arcsin(np.clip(d[..., 1], -1, 1))
    k = len(scene)
    sector = ((az + np.pi) / (2 * np.pi) * k).astype(int) % k
    return el > scene[sector]


def main():
    m = 200_000
    d = rng.normal(size=(m, 3)); d /= np.linalg.norm(d, axis=1, keepdims=True)
    d[:, 1] = np.abs(d[:, 1])                        # the upper hemisphere (a floor texel)
    cosw = d[:, 1]
    skies = [("overcast", 0), ("clear, sun 60 up", 60), ("clear, sun 25 up", 25)]
    for bins in (4, 8, 16):
        b = octa_bin(d, bins)
        errs = {s2: [] for s2, _ in skies}   # the bins are the geometry's: the same whichever sky they were cooked under
        for _ in range(60):
            # A courtyard: 24 sectors of horizon, walls 10-70 degrees, some gaps (doorways, arcades).
            scene = np.radians(rng.uniform(10, 70, 24)) * (rng.random(24) > 0.2)
            v = visibility(d, scene) * cosw
            Vb = np.bincount(b, v, minlength=bins * bins) / m
            for s2, e2 in skies:
                L2 = sky(d, "overcast" if s2 == "overcast" else "clear", e2)
                exact = (v * L2).mean()
                Lb = np.bincount(b, L2 * cosw, minlength=bins * bins) / np.maximum(np.bincount(b, cosw, minlength=bins * bins), 1e-12)
                binned = (Vb * Lb).sum()
                errs[s2].append(binned / exact - 1)
        worst = max(np.percentile(np.abs(np.array(e)), 90) for e in errs.values())
        print(f"{bins:2d} x {bins:2d} bins ({bins * bins} a texel, the upper half {bins * bins // 2}): p90 |error| over the skies {100 * worst:.2f}%; "
              + "; ".join(f"{k}: median {100 * np.median(np.abs(np.array(e))):.2f}%" for k, e in errs.items()))


if __name__ == "__main__":
    main()
