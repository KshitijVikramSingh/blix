# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy", "scipy"]
# ///

2026-10-23 (6,525 corridor fine texels, all within 40 cm of a crease; --shape: the direction alone): 128 rays 6.5 / 13.3%,
pooled 10 cm 5.6 / 11.1%, 20 cm 5.5 / 10.7%, similarity-weighted 20 cm 5.1 / 9.9%, against the truth's own floor 3.7 /
6.9% -- the error above the floor halved, no smear at creases (0-5 cm: 5.5 -> 4.5 at 20 deg). No direction: 28.7 / 55.7%.
The capture holds 1 texel in 4: a real 20 cm pool has ~4x the neighbours.
"""How hard the texel record's direction can be pooled across neighbours (uv run tools/gi-model/direction_pooling.py
[HS_DIR]).

The light-agnostic record keeps the texel's own irradiance exact and adds a per-channel H4 direction shape
(tools/gi-model/directional_bases.py, 2026-10-23: per channel 1.3-1.5% at 20 deg against 5.5-5.9% for one shared shape).
From 128 rays that shape is the noisy part (8.6-9.8% worst channel at 20 deg); pooling it over neighbours trades its
noise for smear where the direction field turns quickly: creases, bounce shadow edges.

On the lean fine round's capture of the crevices corridor (dumps cd0, cd1: 1 in 4 texels, 512 rays each, two seeds):
  estimate   a texel's first 128 rays (cd0, passes 0-3): its irradiance at its normal, and its H4 shape -- per channel,
             fitted in world space (1, nx, ny, nz) over normals within 45 deg, divided by its irradiance at its normal
  truth      its other ~900 rays (cd0's later passes, all of cd1's): the irradiance at each tilted normal from the rays
  pooled     the shape averaged over captured neighbours facing within 25 deg, within R (5 / 10 / 20 cm), each by its
             first 128 rays; 'similar' weights each by exp(-|ln(E_i / E_j)| / 0.3), its irradiance's likeness
The prediction at a tilted normal n: the texel's own irradiance x the shape's value at n over its value at the normal.
Reported: the worst channel's |error| over its irradiance, max over normals tilted <=20 / <=40 deg, median over texels,
binned by the distance to the nearest surface turned over 45 deg (a crease), for each R.
"""
import json
import sys
from pathlib import Path

import numpy as np
from scipy.spatial import cKDTree

HS = Path(sys.argv[1] if len(sys.argv) > 1 else '/private/tmp/blix-dbg/hs')
LUM = np.array([.2126, .7152, .0722])
FIRST = 4
rng = np.random.default_rng(11)


def dump(tag):
    return sorted((HS / f'dump-{tag}').glob('view-*'))[-1]


def oct_decode(w):
    o = np.stack([(w & 0xFFFF).astype(np.int16), (w >> 16).astype(np.uint16).astype(np.int16)], 1) / 32767.0
    z = 1 - np.abs(o).sum(1)
    x, y = o[:, 0].copy(), o[:, 1].copy()
    neg = z < 0
    x[neg], y[neg] = (1 - np.abs(o[neg, 1])) * np.sign(o[neg, 0]), (1 - np.abs(o[neg, 0])) * np.sign(o[neg, 1])
    v = np.stack([x, y, z], 1)
    return v / np.linalg.norm(v, axis=1, keepdims=True)


def cone(n0, m, maxdeg, seed):
    g = np.random.default_rng(seed)
    th = np.arccos(1 - g.random(m) * (1 - np.cos(np.radians(maxdeg))))
    ph = 2 * np.pi * g.random(m)
    a = np.array([0.0, 1.0, 0.0]) if abs(n0[1]) < 0.999 else np.array([1.0, 0, 0])
    t = np.cross(a, n0); t /= np.linalg.norm(t); b = np.cross(n0, t)
    return (np.sin(th) * np.cos(ph))[:, None] * t + (np.sin(th) * np.sin(ph))[:, None] * b + np.cos(th)[:, None] * n0


def irr(dirs, l, normals):
    """Each normal's irradiance from rays (L / pdf rgb): the mean of L/pdf x max(n.w, 0)."""
    return np.clip(normals @ dirs.T, 0, None) @ l / len(dirs)


def load(tag):
    raw = (dump(tag) / 'rays_captured.u32x8').read_bytes()
    cnt = np.frombuffer(raw[:4], np.uint32)[0]
    R = np.frombuffer(raw[16:], np.uint32).reshape(-1, 8)[:cnt]
    ids = R[:, 0].astype(np.int64)
    dirs = oct_decode(R[:, 1]).astype(float)
    l = np.stack([R[:, 2], R[:, 3], R[:, 4]], 1).copy().view(np.float32).astype(float)   # L / pdf about the texel's normal
    return ids, dirs, l, R[:, 6] & 0xFF


def main():
    d0 = dump('cd0')
    n = json.loads((d0 / 'meta.json').read_text())['texelCount']
    t = np.frombuffer((d0 / 'texels.f32x8').read_bytes(), np.float32).reshape(-1, 8)[:n]
    P, N = t[:, :3].astype(float), t[:, 4:7].astype(float)
    N /= np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-9)
    live = np.abs(t[:, 3]) >= 2
    a_ids, a_dirs, a_l, a_pass = load('cd0')
    b_ids, b_dirs, b_l, _ = load('cd1')
    oa, ob = np.argsort(a_ids, kind='stable'), np.argsort(b_ids, kind='stable')
    a_ids, a_dirs, a_l, a_pass = a_ids[oa], a_dirs[oa], a_l[oa], a_pass[oa]
    b_ids, b_dirs, b_l = b_ids[ob], b_dirs[ob], b_l[ob]
    sa = {k: (s, e) for k, s, e in zip(*np.unique(a_ids, return_index=True), np.r_[np.unique(a_ids, return_index=True)[1][1:], len(a_ids)])}
    ub, ib = np.unique(b_ids, return_index=True)
    sb = {k: (s, e) for k, s, e in zip(ub, ib, np.r_[ib[1:], len(b_ids)])}
    texels = np.array([k for k in sa if k in sb and live[k] and sa[k][1] - sa[k][0] >= 384 and sb[k][1] - sb[k][0] >= 384])
    print(f'corridor texels captured at both seeds: {len(texels)}')
    # the distance to the nearest surface turned over 45 deg (a crease), from every live texel
    allt = np.flatnonzero(live)
    tree = cKDTree(P[allt])
    crease = np.full(n, np.inf)
    for tx in texels:
        idx = tree.query_ball_point(P[tx], 0.6)
        if idx:
            j = allt[idx]
            turned = (N[j] @ N[tx]) < np.cos(np.radians(45))
            if turned.any():
                crease[tx] = np.linalg.norm(P[j[turned]] - P[tx], axis=1).min()
    FIT = 300
    est, own_e0, truth20, truth40, T20, T40, truth0, halves = {}, {}, {}, {}, {}, {}, {}, {}
    SHAPE = '--shape' in sys.argv
    for tx in texels:
        n0 = N[tx]
        s, e = sa[tx]
        first = a_pass[s:e] < FIRST
        d1, l1 = a_dirs[s:e][first], a_l[s:e][first]
        # the 128 rays' L/pdf is about the texel's normal; the irradiance at another normal reuses them (their cosine there)
        fit_n = cone(n0, FIT, 45, 1)
        e_fit = irr(d1, l1, fit_n)
        A = np.c_[np.ones(FIT), fit_n]
        coef = np.linalg.lstsq(A, e_fit, rcond=None)[0]              # (4, 3): world-space H4 per channel
        e0 = np.r_[1.0, n0] @ coef
        est[tx] = coef / np.where(np.abs(e0) > 1e-12, e0, 1.0)[None]  # the shape: 1 at the texel's normal
        own_e0[tx] = irr(d1, l1, n0[None])[0]
        # the truth: its other rays
        rest_d = np.vstack([a_dirs[s:e][~first], b_dirs[sb[tx][0]:sb[tx][1]]])
        rest_l = np.vstack([a_l[s:e][~first], b_l[sb[tx][0]:sb[tx][1]]])
        T20[tx], T40[tx] = cone(n0, 120, 20, 2), cone(n0, 120, 40, 3)
        truth20[tx], truth40[tx] = irr(rest_d, rest_l, T20[tx]), irr(rest_d, rest_l, T40[tx])
        truth0[tx] = irr(rest_d, rest_l, n0[None])[0]
        h = len(rest_d) // 2   # the truth's own floor: its halves held against each other
        halves[tx] = [(irr(rest_d[:h], rest_l[:h], T), irr(rest_d[h:], rest_l[h:], T), irr(rest_d[:h], rest_l[:h], n0[None])[0],
                       irr(rest_d[h:], rest_l[h:], n0[None])[0]) for T in (T20[tx], T40[tx])]
    ptree = cKDTree(P[texels])
    lum0 = {tx: max(own_e0[tx] @ LUM, 1e-12) for tx in texels}

    def score(tx, shape):
        out = []
        for T, tr in ((T20[tx], truth20[tx]), (T40[tx], truth40[tx])):
            e_at0 = truth0[tx] if SHAPE else own_e0[tx]   # --shape: the direction alone, the irradiance at the normal the truth's
            pred = e_at0[None] * (np.c_[np.ones(len(T)), T] @ shape)
            e0t = np.maximum(np.abs(tr).mean(0), 1e-12)
            out.append((np.abs(pred - tr) / e0t[None]).max())     # the worst channel, the worst normal
        return out

    variants = [('own (128 rays)', 0.0, False), ('pool 5 cm', 0.05, False), ('pool 10 cm', 0.10, False), ('pool 20 cm', 0.20, False),
                ('similar 10 cm', 0.10, True), ('similar 20 cm', 0.20, True), ('no direction', None, False)]
    bins = [(0, 0.05), (0.05, 0.15), (0.15, 0.4), (0.4, np.inf)]
    res = {v[0]: [] for v in variants}
    for tx in texels:
        for name, R, sim in variants:
            if R is None:
                shape = np.zeros((4, 3)); shape[0] = 1.0; shape[1:] = 0.0
                # flat: the irradiance at the texel's normal everywhere (the basis is 1 at n0 only if no direction)
                out = []
                for T, tr in ((T20[tx], truth20[tx]), (T40[tx], truth40[tx])):
                    e0t = np.maximum(np.abs(tr).mean(0), 1e-12)
                    out.append((np.abs((truth0[tx] if SHAPE else own_e0[tx])[None] - tr) / e0t[None]).max())
                res[name].append((crease[tx], *out))
                continue
            if R == 0.0:
                shape = est[tx]
            else:
                nb = [texels[j] for j in ptree.query_ball_point(P[tx], R)]
                nb = [j for j in nb if N[j] @ N[tx] > np.cos(np.radians(25))]
                w = np.array([np.exp(-abs(np.log(lum0[j] / lum0[tx])) / 0.3) if sim else 1.0 for j in nb])
                shape = sum(wi * est[j] for wi, j in zip(w, nb)) / w.sum()
                # renormalise to 1 at the texel's own normal (the record keeps its irradiance exact)
                at0 = np.r_[1.0, N[tx]] @ shape
                shape = shape / np.where(np.abs(at0) > 1e-12, at0, 1.0)[None]
            res[name].append((crease[tx], *score(tx, shape)))
    print('worst channel |error| over its irradiance, max over normals tilted <=20 / <=40 deg; median over texels '
          '(the truth: each texel\'s other ~900 rays)')
    head = ''.join(f'{f"crease {lo * 100:.0f}-{hi * 100:.0f} cm" if hi < np.inf else f"crease > {lo * 100:.0f} cm":>20s}' for lo, hi in bins)
    print(f'  {"":16s}{"all":>16s}{head}')
    counts = None
    for name, _, _ in variants:
        r = np.array(res[name])
        cells = [f'{np.median(r[:, 1]) * 100:5.1f} / {np.median(r[:, 2]) * 100:4.1f}%']
        cnt = []
        for lo, hi in bins:
            q = (r[:, 0] >= lo) & (r[:, 0] < hi)
            cnt.append(q.sum())
            cells.append(f'{np.median(r[q, 1]) * 100:5.1f} / {np.median(r[q, 2]) * 100:4.1f}%' if q.sum() > 5 else '-')
        counts = cnt
        print(f'  {name:16s}' + ''.join(f'{c:>16s}' if i == 0 else f'{c:>20s}' for i, c in enumerate(cells)))
    # the floor: one half of a texel's truth against the other (the shape scaled at the normal when --shape), / sqrt 2 as each
    # variant is held against the whole truth (twice the rays of a half)
    fl = []
    for tx in texels:
        row = []
        for (h1, h2, z1, z2), in [(x,) for x in halves[tx]]:
            p = h1 * (z2 / np.where(np.abs(z1) > 1e-12, z1, 1.0))[None] if SHAPE else h1
            e0t = np.maximum(np.abs(h2).mean(0), 1e-12)
            row.append((np.abs(p - h2) / e0t[None]).max() / np.sqrt(2))
        fl.append((crease[tx], *row))
    fl = np.array(fl)
    cells = [f'{np.median(fl[:, 1]) * 100:5.1f} / {np.median(fl[:, 2]) * 100:4.1f}%']
    for lo, hi in bins:
        q = (fl[:, 0] >= lo) & (fl[:, 0] < hi)
        cells.append(f'{np.median(fl[q, 1]) * 100:5.1f} / {np.median(fl[q, 2]) * 100:4.1f}%' if q.sum() > 5 else '-')
    print(f'  {"truth floor":16s}' + ''.join(f'{c:>16s}' if i == 0 else f'{c:>20s}' for i, c in enumerate(cells)))
    print(f'  texels per bin: {counts}')


if __name__ == '__main__':
    main()
