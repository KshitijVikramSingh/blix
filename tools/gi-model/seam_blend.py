# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""A pixel's light across lightmap chart seams: its own chart's texels alone, or blended with the next chart's
(uv run tools/gi-model/seam_blend.py).

drapes' curtain is 2 cm strips, each its own chart one texel across (xatlas's planar charts), its folds a sine 6 cm deep
and 30 cm long, smooth normals. A pixel reads the four atlas texels about its lightmap coordinate (texel.glsl,
texelsAroundLightmap), all in its own chart: across a strip there is one, so the light is that texel's from edge to
edge and steps at every strip's edge -- the user's vertical stripes (2026-10-22: close up, the GI's step against the
path trace 0.85% rms at a change of record, 0.6% within one). The cook lists, per atlas texel, the texels of OTHER
charts touching it in the world (SponzaLoop.Seams.cs: within 4.5 cm, on its plane within 1 cm, facing within cos 0.95).

The truth: irradiance about the pixel's smooth normal, under a uniform dome plus a bright patch from one side
(facet_normal.py's light). Texels: at each strip's centre, gathered about the smooth normal there (--gather-smooth-normal),
plus gather noise (independent a texel, sd NOISE). Reconstructions of a pixel at x:
  own        its strip's texel alone (today)
  tent       its strip's and the nearer neighbour strip's, linear in the distance between their centres -- what a
             bilinear would do if the strips were one chart
  tent+own   the tent where the neighbour's facing is within cos 0.95 of its own (the seam list's rule), else own

Reported: the step at strip edges (the jump between the two sides, against the truth's, rms), the error against the
truth (rms), and the noise's share."""
import numpy as np

period, depth, strip = 0.30, 0.06, 0.02
k = 2 * np.pi / period
slope = lambda x: 0.5 * depth * k * np.cos(k * x)


def normal(s):
    return np.stack([-s, np.zeros_like(s), np.ones_like(s)], -1) / np.hypot(s, 1.0)[..., None]


def irradiance(n, d, share):
    return (1 - share) + share * np.maximum((n * d).sum(-1), 0.0) / max(d[2], 1e-6)


def run(share, az, noise, seeds=200):
    a, b = np.radians(60.0), np.radians(az)
    d = np.array([np.sin(a) * np.cos(b), -np.sin(a) * np.sin(b), np.cos(a)])
    x = np.arange(0.0, 3 * period, 0.0005)
    truth = irradiance(normal(slope(x)), d, share)
    centres = (np.floor(x / strip) + 0.5) * strip
    ks = np.floor(x / strip).astype(int)
    nk = ks.max() + 2
    cpos = (np.arange(nk) + 0.5) * strip
    texel_truth = irradiance(normal(slope(cpos)), d, share)
    cn = normal(slope(cpos))
    rng = np.random.default_rng(7)
    res = {}
    for name in ("own", "tent", "tent+own"):
        errs, steps = [], []
        for s in range(seeds):
            tex = texel_truth * (1 + noise * rng.standard_normal(nk))
            own = tex[ks]
            if name == "own":
                rec = own
            else:
                side = np.where(x >= centres, 1, -1)
                other = np.clip(ks + side, 0, nk - 1)
                t = np.abs(x - centres) / strip                    # 0 at its centre, 0.5 at its edge
                blend = (1 - t) * own + t * tex[other]
                if name == "tent+own":
                    ok = (cn[ks] * cn[other]).sum(-1) > 0.95
                    blend = np.where(ok, blend, own)
                rec = blend
            errs.append(rec / truth - 1)
            # The step at each strip's edge: the reconstruction's jump between its two sides, against the truth's.
            edge = np.where(np.diff(ks) != 0)[0]
            steps.append((rec[edge + 1] - rec[edge]) / rec[edge] - (truth[edge + 1] - truth[edge]) / truth[edge])
        e = np.concatenate(errs); st = np.concatenate(steps)
        res[name] = (100 * np.sqrt(np.mean(e ** 2)), 100 * np.sqrt(np.mean(st ** 2)), 100 * np.abs(st).max())
    return res


def main():
    for noise in (0.0, 0.01):
        print(f"\n== texel noise {100 * noise:.0f}%")
        for share in (0.3, 0.6):
            for az in (0.0, 90.0):
                r = run(share, az, noise, seeds=1 if noise == 0 else 200)
                where = "across" if az == 0 else "along"
                print(f"   bright patch {100 * share:.0f}%, {where} the folds: " + "; ".join(
                    f"{n} error rms {v[0]:.2f}% step rms {v[1]:.2f}% (max {v[2]:.1f}%)" for n, v in r.items()))


if __name__ == "__main__":
    main()
