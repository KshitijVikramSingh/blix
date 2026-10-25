# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The RTGI cache's incident light over time against a truth boot's path trace
(uv run tools/gi-model/rtgi_timeline.py TRUTH DUMP [DUMP ...]).

TRUTH a log (.log): a --gpu-reference run's points (the gate's hardware wavefront reference: "surface at x,y ... ref R",
SponzaLoop.GpuReference.cs), each dump's field read at those pixels -- energy, rel |error| and median as the gate's.
TRUTH a dump directory: the per-pixel reference view below (--gi-reference: the software BVH, ~290 ms a frame at
1440x810, 2026-10-26 -- the points are the one to use).

Dumps are --rtgi-dump's (SponzaLoop.Rtgi.cs): incident.rgba16f the full-res field the lit pass read (rgb the indirect
irradiance, no direct sun), normal.rgba16f, meta.json; the truth's also reference.f32x4 (--gi-reference: per half-res
pixel the sum of PI x radiance and the paths) and reference_moments.f32. The same camera in every run. A reference pixel
h stands for a full-res pixel of its 2x2 (rotating), so the field is the 2x2's mean there. Pixels with no surface (a
zero normal in any of the four) are left out.

Printed per dump: energy (field over trace, summed), rel |error| (mean |F - R| over mean R, as the gate's), the median
F/R where R is over 2% of its mean, and the trace's own noise on the same scale (its standard errors' mean over mean R):
the floor no field can be judged below."""
import json
import sys
from pathlib import Path

import numpy as np


def load(d):
    d = Path(d)
    meta = json.loads((d / 'meta.json').read_text())
    w, h = meta['width'], meta['height']
    inc = np.frombuffer((d / 'incident.rgba16f').read_bytes(), np.float16).astype(np.float32).reshape(h, w, 4)
    nrm = np.frombuffer((d / 'normal.rgba16f').read_bytes(), np.float16).astype(np.float32).reshape(h, w, 4)
    return meta, inc, nrm


def lum(c):
    return c[..., 0] * 0.2126 + c[..., 1] * 0.7152 + c[..., 2] * 0.0722


def spots(inc, nrm, k=9):
    """Spotting: how far each pixel's luminance strays from its own neighbourhood's (a k x k box), over the neighbourhood,
    where the whole box is one surface (normals within ~8 degrees of the pixel's) -- noise and blotches on a flat wall,
    not light's real structure at creases and edges, which the mask leaves out. Mean and p90 over those pixels."""
    L = lum(inc[..., :3]).astype(np.float64)
    n = nrm[..., :3].astype(np.float64)
    surf = np.abs(n).sum(axis=2) > 1e-6

    def box(a):
        c = np.cumsum(np.cumsum(np.pad(a, ((1, 0), (1, 0))), axis=0), axis=1)
        h, w = a.shape
        r = k // 2
        y0, y1 = np.clip(np.arange(h) - r, 0, h), np.clip(np.arange(h) + r + 1, 0, h)
        x0, x1 = np.clip(np.arange(w) - r, 0, w), np.clip(np.arange(w) + r + 1, 0, w)
        return c[y1][:, x1] - c[y0][:, x1] - c[y1][:, x0] + c[y0][:, x0]

    cnt = box(surf.astype(np.float64))
    mean = box(np.where(surf, L, 0.0)) / np.maximum(cnt, 1)
    nmean = np.stack([box(np.where(surf, n[..., i], 0.0)) for i in range(3)], axis=2) / np.maximum(cnt, 1)[..., None]
    flat = surf & (cnt >= k * k) & (np.linalg.norm(nmean, axis=2) > 0.99) & ((n * nmean).sum(axis=2) > 0.99)
    rel = np.abs(L - mean) / np.maximum(mean, 1e-4 + 0.02 * mean[flat].mean() if flat.any() else 1e-4)
    return rel[flat].mean(), np.percentile(rel[flat], 90), flat.sum()


def points_mode(log, dumps, mean_of=False, flicker=False):
    import re
    pts = []
    for line in Path(log).read_text().splitlines():
        m = re.match(r'\s+surface\s+at ([0-9.]+),([0-9.]+)\s+gpu\s+total \S+ ref ([0-9.eE+-]+)', line)
        if m and m.group(3) != 'NaN':
            pts.append((float(m.group(1)), float(m.group(2)), float(m.group(3))))
    if not pts:
        raise SystemExit(f'{log}: no surface reference lines')
    xs, ys, R = (np.array(v) for v in zip(*pts))
    mean = R.mean()
    big = R > 0.02 * mean
    print(f'truth: {Path(log).name}, {len(pts)} points, mean {mean:.4f}')
    if flicker:
        # --flicker: consecutive frames' fields at the points -- how much each point's light moves frame to frame (its
        # standard deviation over its mean, the points' mean), the error of their mean beside it.
        metas = [load(d) for d in dumps]
        meta, _, nrm = metas[0]
        w, h = meta['width'], meta['height']
        px, py = np.clip((xs * w).astype(int), 0, w - 1), np.clip((ys * h).astype(int), 0, h - 1)
        ok = np.abs(nrm[py, px, :3]).sum(axis=1) > 1e-6
        F = np.array([lum(m[1][py, px, :3]) for m in metas])[:, ok]
        f, r = F.mean(axis=0), R[ok]
        rel = F.std(axis=0) / np.maximum(f, 0.02 * f.mean())
        sm, sp, sn = spots(metas[-1][1], metas[-1][2])
        print(f'flicker over {len(metas)} frames: mean {rel.mean():.4f}, p90 {np.percentile(rel, 90):.4f}; '
              f'their mean: energy {f.sum() / r.sum():.4f}, rel |error| {np.abs(f - r).mean() / r.mean():.4f}; '
              f'spotting (last frame, 9x9, flat): mean {sm:.4f}, p90 {sp:.4f} ({sn:,} px)')
        return
    if mean_of:
        # --mean: the dumps' fields averaged first (consecutive frames): what a temporal filter would converge to.
        metas = [load(d) for d in dumps]
        meta, _, nrm = metas[0]
        inc = np.mean([m[1] for m in metas], axis=0)
        dumps = [(f'mean of {len(metas)}', meta, inc, nrm)]
    for d in dumps:
        if isinstance(d, tuple):
            name, meta, inc, nrm = d
        else:
            name = Path(d).name
            meta, inc, nrm = load(d)
        w, h = meta['width'], meta['height']
        px, py = np.clip((xs * w).astype(int), 0, w - 1), np.clip((ys * h).astype(int), 0, h - 1)
        F = lum(inc[py, px, :3])
        ok = np.abs(nrm[py, px, :3]).sum(axis=1) > 1e-6
        f, r = F[ok], R[ok]
        b = big[ok]
        print(f'{name:>8} (frame {meta["frame"]}): energy {f.sum() / r.sum():.4f}, rel |error| {np.abs(f - r).mean() / r.mean():.4f}, '
              f'median {np.median(f[b] / r[b]):.4f} ({ok.sum()} points)')


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    if sys.argv[1].endswith('.log'):
        args = [a for a in sys.argv[2:] if a not in ('--mean', '--flicker')]
        points_mode(sys.argv[1], args, mean_of='--mean' in sys.argv, flicker='--flicker' in sys.argv)
        return
    tmeta, _, tnrm = load(sys.argv[1])
    rw, rh = tmeta['referenceWidth'], tmeta['referenceHeight']
    acc = np.frombuffer((Path(sys.argv[1]) / 'reference.f32x4').read_bytes(), np.float32).reshape(rh, rw, 4)
    mom = np.frombuffer((Path(sys.argv[1]) / 'reference_moments.f32').read_bytes(), np.float32).reshape(rh, rw)
    n = np.maximum(acc[..., 3], 1.0)
    ref = lum(acc[..., :3]) / n
    var = np.maximum(mom / n - ref * ref, 0.0)
    se = np.sqrt(var / n)
    print(f'truth: {Path(sys.argv[1]).name}, {acc[..., 3].mean():.0f} paths a pixel, {rw}x{rh}')
    for d in sys.argv[2:]:
        meta, inc, nrm = load(d)
        h2, w2 = rh * 2, rw * 2
        f = lum(inc[:h2, :w2, :3]).reshape(rh, 2, rw, 2).mean(axis=(1, 3))
        surface = (np.abs(nrm[:h2, :w2, :3]).sum(axis=2) > 1e-6).reshape(rh, 2, rw, 2).all(axis=(1, 3))
        ok = surface & (acc[..., 3] > 0)
        F, R, S = f[ok], ref[ok], se[ok]
        mean = R.mean()
        big = R > 0.02 * mean
        # 8x8 blocks of reference pixels, each the mean of its surface pixels: the trace's noise mostly averaged out.
        bh, bw = rh // 8, rw // 8
        def blocks(x):
            x = np.where(ok, x, 0.0)[:bh * 8, :bw * 8].reshape(bh, 8, bw, 8).sum(axis=(1, 3))
            return x
        cnt = blocks(np.ones_like(f))
        bf, br = blocks(f) / np.maximum(cnt, 1), blocks(ref) / np.maximum(cnt, 1)
        bs = np.sqrt(blocks(se * se)) / np.maximum(cnt, 1)
        bok = cnt >= 32
        bmean = br[bok].mean()
        print(f'{Path(d).name:>10} (frame {meta["frame"]}): energy {F.sum() / R.sum():.4f}, pixel rel |error| {np.abs(F - R).mean() / mean:.4f} '
              f'(trace noise {S.mean() / mean:.4f}), median {np.median(F[big] / R[big]):.4f}; 8x8 blocks rel |error| '
              f'{np.abs(bf - br)[bok].mean() / bmean:.4f} (trace noise {bs[bok].mean() / bmean:.4f})')


if __name__ == '__main__':
    main()
