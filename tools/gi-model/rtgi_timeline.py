# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The RTGI cache's incident light over time against a truth boot's path trace
(uv run tools/gi-model/rtgi_timeline.py TRUTH.log DUMP [DUMP ...]).

TRUTH a --gpu-reference run's log: its points (the gate's hardware wavefront reference: "surface at x,y ... ref R",
SponzaLoop.GpuReference.cs), each dump's field read at those pixels -- energy, rel |error| and median as the gate's.

Dumps are --rtgi-dump's (SponzaLoop.Rtgi.cs): incident.rgba16f the full-res field the lit pass read (rgb the indirect
irradiance, no direct sun), normal.rgba16f, meta.json. The same camera in every run. Points with no surface under them
(a zero normal) are left out.

Printed per dump: energy (field over truth, summed), rel |error| (mean |F - R| over mean R, as the gate's) and the median
F/R where R is over 2% of its mean. --mean: the dumps' fields averaged first; --flicker: how much each point's light moves
frame to frame; --settle: each dump's distance from the last; --pair: two seeds' runs, the noise left in one."""
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


def points_mode(log, dumps, mean_of=False, flicker=False, settle=False):
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
    if settle:
        # --settle: dumps over time, the last the settled state -- each one's distance from it at the points (mean and p90
        # of |F - F_final| / F_final): the long tail, whatever the bias; beside it the error against the truth.
        metas = [load(d) for d in dumps]
        meta, inc, nrm = metas[-1]
        w, h = meta['width'], meta['height']
        px, py = np.clip((xs * w).astype(int), 0, w - 1), np.clip((ys * h).astype(int), 0, h - 1)
        ok = np.abs(nrm[py, px, :3]).sum(axis=1) > 1e-6
        final = lum(inc[py, px, :3])[ok]
        r = R[ok]
        for d, (m, i, _) in zip(dumps, metas):
            f = lum(i[py, px, :3])[ok]
            dist = np.abs(f - final) / np.maximum(final, 0.02 * final.mean())
            print(f'{Path(d).name:>8} (frame {m["frame"]}): from settled mean {dist.mean():.4f}, p90 {np.percentile(dist, 90):.4f}; '
                  f'against the truth {np.abs(f - r).mean() / r.mean():.4f}')
        return
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
        bm, bp, bn = spots(metas[-1][1], metas[-1][2], k=33)
        print(f'flicker over {len(metas)} frames: mean {rel.mean():.4f}, p90 {np.percentile(rel, 90):.4f}; '
              f'their mean: energy {f.sum() / r.sum():.4f}, rel |error| {np.abs(f - r).mean() / r.mean():.4f}; '
              f'spotting (last frame, flat) 9x9: mean {sm:.4f}, p90 {sp:.4f}; 33x33 (blotches): mean {bm:.4f}, p90 {bp:.4f} ({bn:,} px)')
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


def pair_mode(log, dirs_a, dirs_b):
    """--pair: two runs differing only in --rtgi-seed, dumped at the same frames. Per frame the runs' relative difference
    over every surface pixel (mean and p90 of |A - B| / ((A + B) / 2), over sqrt 2: the noise left in one run, bias
    cancelled) and each run's error against the truth's points."""
    import re
    pts = []
    for line in Path(log).read_text().splitlines():
        m = re.match(r'\s+surface\s+at ([0-9.]+),([0-9.]+)\s+gpu\s+total \S+ ref ([0-9.eE+-]+)', line)
        if m and m.group(3) != 'NaN':
            pts.append((float(m.group(1)), float(m.group(2)), float(m.group(3))))
    xs, ys, R = (np.array(v) for v in zip(*pts))
    for da, db in zip(dirs_a, dirs_b):
        ma, ia, na = load(da)
        mb, ib, nb = load(db)
        la, lb = lum(ia[..., :3]), lum(ib[..., :3])
        surf = (np.abs(na[..., :3]).sum(axis=2) > 1e-6) & (np.abs(nb[..., :3]).sum(axis=2) > 1e-6)
        avg = 0.5 * (la + lb)
        floor = 0.02 * avg[surf].mean()
        rel = (np.abs(la - lb) / np.maximum(avg, floor))[surf] / np.sqrt(2.0)
        w, h = ma['width'], ma['height']
        px, py = np.clip((xs * w).astype(int), 0, w - 1), np.clip((ys * h).astype(int), 0, h - 1)
        ea = np.abs(la[py, px] - R).mean() / R.mean()
        eb = np.abs(lb[py, px] - R).mean() / R.mean()
        print(f'{Path(da).name:>8} (frame {ma["frame"]}): noise left mean {rel.mean():.4f}, p90 {np.percentile(rel, 90):.4f}; '
              f'error against the truth {ea:.4f} / {eb:.4f}')


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    if '--pair' in sys.argv:
        args = [a for a in sys.argv[2:] if a != '--pair']
        half = len(args) // 2
        pair_mode(sys.argv[1], args[:half], args[half:])
        return
    if not sys.argv[1].endswith('.log'):
        raise SystemExit(f'{sys.argv[1]}: the truth is a --gpu-reference run\'s log (.log)')
    args = [a for a in sys.argv[2:] if a not in ('--mean', '--flicker', '--settle')]
    points_mode(sys.argv[1], args, mean_of='--mean' in sys.argv, flicker='--flicker' in sys.argv, settle='--settle' in sys.argv)


if __name__ == '__main__':
    main()
