# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The cache's light, the pixel's own rays for what is near it (uv run tools/gi-model/near_far_hybrid.py CAPTURE PATCH_FROM
[--seed S]).

CAPTURE an --rtgi-capture directory, three words a sample (SponzaLoop.RtgiCapture.cs): the sample (PI x L_in x the ray's
weight, flags), its hit (xyz, the ray's weight), the hit's normal and distance (1e30 a miss). Pixels past PATCH_FROM: the
dense patch, every second full-res pixel of a block (a half-res pixel each).

The estimator at a pixel, per ray: a ray that met a surface within r of it brings its own sample (a crease's other face, a
contact, an overhang: the near field, at the pixel's scale); a ray past r brings its cell's mean far sample instead -- the
cache's, learnt from every ray its cell's points sent past r over time (here: every far sample of the patch's pixels in
that cell, over the reference half, as a settled cache holds it). E = mean over the pixel's rays of (near ? e : e_far(cell)):
the far light's noise -- the rare sunlit surface through an opening, 80% of a pixel's light from 5% of its rays -- replaced
by its mean, the pixel's near geometry and how much of the far field it sees (the share escaping) kept as its own rays say.
A cell is 5 cm + 0.5 cm a metre (--rtgi-cell, --rtgi-cell-grow).

Scored as restir_gi.py: run on the first half's samples, against the second half's converged gather, its floor the first
half's own; by D (1 in D pixels trace a frame, turning), a spatial mean over plane-tested neighbours within R and a temporal
mean over N frames; flicker frame to frame; the error 8 and 32 frames after a reset. And the far mean's own bias: the
estimator with every frame's rays (converged) against the reference -- what replacing far samples by their cell's mean
costs where a pixel's far field differs from its cell's (a crease seeing part of the sky its cell sees whole)."""
import json, sys
from pathlib import Path
import numpy as np

LUM = np.array([0.2126, 0.7152, 0.0722])


def main():
    cap, patch0 = Path(sys.argv[1]), int(sys.argv[2])
    seed = int(sys.argv[sys.argv.index("--seed") + 1]) if "--seed" in sys.argv else 1
    rng = np.random.default_rng(seed)
    meta = json.loads((cap / "capture.json").read_text())
    K, F = meta["points"], meta["frames"]
    a = np.frombuffer((cap / "capture.f32").read_bytes(), np.float32).reshape(-1, 4)
    s = a[: 3 * F * K].reshape(F, K, 3, 4)[:, patch0:]
    geo = a[3 * F * K: 3 * F * K + 2 * K].reshape(K, 2, 4)[patch0:]
    P = K - patch0
    flags = s[:, :, 0, 3].astype(int)
    use = ((flags & 4) != 0) & ((flags & 1) != 0)
    Le = s[:, :, 0, :3].astype(np.float64) @ LUM
    hitD = s[:, :, 2, 3].astype(np.float64)
    pos, nrm = geo[:, 0, :3].astype(np.float64), geo[:, 1, :3].astype(np.float64)
    camera = np.array(meta["camera"])
    dist = np.linalg.norm(pos - camera, axis=1)

    half = F // 2
    A, B = slice(0, half), slice(half, F)
    def conv(sl):
        u = use[sl]
        return np.where(u.sum(0) > 0, (Le[sl] * u).sum(0) / np.maximum(u.sum(0), 1), np.nan)
    refA, ref = conv(A), conv(B)
    good = np.isfinite(ref) & np.isfinite(refA) & (ref > 1e-4) & (np.abs(nrm).sum(1) > 0)
    dark = good & (ref <= np.quantile(ref[good], 0.25))
    def score(est, sel=None):
        g = good if sel is None else sel
        return np.nanmean(np.abs(est[g] - ref[g])) / ref[g].mean()
    print(f"patch: {P} half-res pixels, {half} frames a half; the reference's own floor: {score(refA):.3f}, darkest quartile {score(refA, dark):.3f}")
    dumps = sorted((cap / "dump").glob("f*"), key=lambda p: int(p.name[1:]))
    pix = np.array(meta["pixels"][patch0:])
    if dumps:
        m = json.loads((dumps[-1] / "meta.json").read_text())
        inc = np.frombuffer((dumps[-1] / "incident.rgba16f").read_bytes(), np.float16).astype(np.float32).reshape(m["height"], m["width"], 4)
        cache = inc[pix[:, 1], pix[:, 0], :3] @ LUM
        print(f"the cache as displayed ({dumps[-1].name}): rel |error| {score(cache):.3f}, darkest quartile {score(cache, dark):.3f}")

    # Cells: 5 cm + 0.5 cm a metre, on the pixel's side (its normal's major axis and sign).
    size = 0.05 + 0.005 * dist
    level = np.round(np.log2(size / 0.05)).astype(int)
    csz = 0.05 * 2.0 ** level
    side = np.argmax(np.abs(nrm), 1) * 2 + (nrm[np.arange(P), np.argmax(np.abs(nrm), 1)] > 0)
    cell_key = np.stack([np.floor(pos[:, 0] / csz), np.floor(pos[:, 1] / csz), np.floor(pos[:, 2] / csz), level, side], 1)
    _, cell = np.unique(cell_key, axis=0, return_inverse=True)
    cell = cell.ravel()
    nc = cell.max() + 1
    print(f"cells over the patch: {nc} ({P / nc:.1f} pixels each)")

    pix_g = pix
    gx, gy = (pix_g[:, 0] - pix_g[:, 0].min()) // 2, (pix_g[:, 1] - pix_g[:, 1].min()) // 2
    W, H = gx.max() + 1, gy.max() + 1
    grid = np.full((H, W), -1); grid[gy, gx] = np.arange(P)

    def spatial_table(R):
        offs = [(dx, dy) for dy in range(-R, R + 1) for dx in range(-R, R + 1) if dx * dx + dy * dy <= R * R]
        nbs, wts = [], []
        for dx, dy in offs:
            jx, jy = gx + dx, gy + dy
            ok = (jx >= 0) & (jx < W) & (jy >= 0) & (jy < H)
            j = np.where(ok, grid[np.clip(jy, 0, H - 1), np.clip(jx, 0, W - 1)], -1); jj = np.maximum(j, 0)
            plane = np.abs(((pos[jj] - pos) * nrm).sum(-1)) / np.maximum(dist, 1e-3)
            same = (j >= 0) & (plane < 0.01) & ((nrm[jj] * nrm).sum(-1) > 0.9)
            nbs.append(jj); wts.append(same.astype(float))
        return np.stack(nbs), np.stack(wts)

    def run(r, D, R, N, frames=320):
        near = hitD < r
        # The cell's far mean: every far sample of its pixels over the reference-free half (A), as a settled cache holds it.
        farA = use[A] & ~near[A]
        num = np.bincount(cell, (Le[A] * farA).sum(0), nc); den = np.bincount(cell, farA.sum(0), nc)
        efar_cell = np.where(den > 0, num / np.maximum(den, 1), 0.0)[cell]
        x = np.where(near, Le, efar_cell[None, :])   # each ray's contribution
        # Converged (every frame of half A): the far mean's own bias.
        conv_h = np.where(use[A].sum(0) > 0, (x[A] * use[A]).sum(0) / np.maximum(use[A].sum(0), 1), np.nan)
        nb, wt = spatial_table(R) if R > 0 else (np.arange(P)[None], np.ones((1, P)))
        phase = rng.integers(0, D, P); a0 = rng.integers(0, half - frames)
        acc = np.full(P, np.nan); cnt = np.zeros(P); prev = None; flick = []; errs = {}
        for t in range(frames):
            fr = a0 + t
            on = ((phase + t) % D == 0) & use[fr]
            v = np.where(on, x[fr], 0.0)
            numv = (wt * v[nb] * on[nb]).sum(0); denv = (wt * on[nb]).sum(0)
            est = np.where(denv > 0, numv / np.maximum(denv, 1e-9), np.nan)
            has = np.isfinite(est)
            alpha = np.maximum(1.0 / N, 1.0 / (cnt + 1))
            acc = np.where(has, np.where(np.isfinite(acc), acc + alpha * (est - acc), est), acc)
            cnt += has
            if t in (7, 31): errs[t + 1] = score(acc)
            if t >= frames - 48 and prev is not None:
                flick.append(np.nanmean(np.abs(acc[good] - prev[good])) / ref[good].mean())
            prev = acc.copy()
        return score(conv_h), score(conv_h, dark), score(acc), score(acc, dark), np.mean(flick), errs[8], errs[32], near[use].mean()

    print("\nr the near radius; D 1 in D half-res pixels trace a frame (turning; 960 x 540 / D rays at 1080p); R a spatial mean "
          "over plane-tested neighbours (patch pixels); N the temporal mean's frames.")
    print("converged error (darkest quartile) | at the budget: settled error (darkest quartile) | flicker | from a reset 8, 32 frames")
    for r in (0.25, 0.5, 1.0):
        for D, R, N in ((4, 0, 8), (4, 2, 8), (16, 2, 8), (16, 4, 16), (64, 4, 16)):
            ce, cd, se, sd, fl, e8, e32, nshare = run(r, D, R, N)
            print(f"  r {r:4.2f} ({100 * nshare:4.1f}% of rays near) D {D:2d} R {R} N {N:2d}: {ce:.3f} ({cd:.3f}) | {se:.3f} ({sd:.3f}) | "
                  f"{100 * fl:5.2f}% | {e8:.3f}, {e32:.3f}")


if __name__ == "__main__":
    main()
