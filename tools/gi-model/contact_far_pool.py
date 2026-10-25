# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The contacts' far light pooled over their neighbours (uv run tools/gi-model/contact_far_pool.py VIEW REF_PARTS REF2_PARTS).

A contact texel's noise is its far hits' sun (the ray capture on Crytek Sponza: hits past 1 m ~87% of the variance,
the sun at a hit 62-66% of it from 4-8% of the rays at 5-7x the mean): each texel samples the same far, sunlit field
with a few lucky rays. Its estimate is its near rays' part plus its far rays' share (its openness: how much of it
sees past the crease) times their mean light. The model keeps a texel's own near part and own far share and pools only
the far rays' mean light over its neighbours on its plane (normals within ~18 deg, 2 cm of its plane, within r), each
weighed by its far rays. VIEW: a view dump with --ray-capture's fine round (BLIX_CAPTURE_FINE_ONLY, --ray-capture 3 and
room for it); REF_PARTS, REF2_PARTS: two 1,024-ray boots' BLIX_DUMP_PARTS final dumps (the fine round raised too: their
difference the floor). Errors as the eye's: the whole light, a tenth of the median as the floor."""
import json
import sys
from pathlib import Path

import numpy as np

LUM = np.array([0.2126, 0.7152, 0.0722])
IRR = 9.42


def u9(w):
    w = w.astype(np.int64)
    sc = np.exp2((w >> 27) - 24.0)
    return np.stack([(w & 511) * sc, ((w >> 9) & 511) * sc, ((w >> 18) & 511) * sc], -1)


def light(path):
    raw = Path(path).read_bytes()
    n = int(np.frombuffer(raw[:4], np.int32)[0])
    w = np.frombuffer(raw[4:4 + 16 * n], np.uint32)
    return u9(w[:2 * n].reshape(n, 2)[:, 0]) @ LUM + IRR * (u9(w[2 * n:4 * n].reshape(n, 2)[:, 0]) @ LUM)


def main():
    if len(sys.argv) < 4:
        raise SystemExit(__doc__)
    view = Path(sys.argv[1])
    view = sorted(view.glob('view-*'))[-1] if not (view / 'meta.json').exists() else view
    meta = json.loads((view / 'meta.json').read_text())
    n = meta['texelCount']
    raw = (view / 'rays_captured.u32x8').read_bytes()
    rec = np.frombuffer(raw[16:], np.uint32).reshape(-1, 8)
    tag = rec[:, 1] & 0xFFFF
    keep = ((tag & 15) == 3) & ((tag >> 8) == 0)   # the fine round and its refine rounds, not the near rounds
    rec = rec[keep]
    tid = rec[:, 0].astype(np.int64)
    dist = np.ascontiguousarray(rec[:, 2]).view(np.float32).astype(np.float64)
    how = rec[:, 3] & 7
    whole = np.ascontiguousarray(rec[:, 4]).view(np.float32).astype(np.float64)
    hit = how >= 2
    t = np.frombuffer((view / 'texels.f32x8').read_bytes(), np.float32).reshape(-1, 8)[:n]
    ref, ref2 = light(sys.argv[2]), light(sys.argv[3])
    u, inv, nr = np.unique(tid, return_inverse=True, return_counts=True)
    own = np.bincount(inv, whole) / nr
    pos = t[u, :3].astype(np.float64)
    nrm = t[u, 4:7].astype(np.float64)
    nrm /= np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-9)
    truth = ref[u]
    floor = 0.1 * np.median(ref[ref > 0])
    err = lambda x: np.abs(x - truth) / np.maximum(truth, floor)
    e_floor = err(ref2[u])
    print(f'{len(u):,} texels of the fine round sampled ({len(tid):,} rays, {np.median(nr):.0f} a texel); their light against the '
          f'1,024-ray boot: energy {own.sum() / truth.sum():.3f}; own estimate {err(own).mean():.4f} mean error, '
          f'{(err(own) > 0.1).mean():.1%} over 10%;  a 2nd 1,024-ray boot there {e_floor.mean():.4f}, {(e_floor > 0.1).mean():.1%}')
    for far_d in (0.5, 1.0, 2.0):
        far = hit & (dist >= far_d)
        fN = np.bincount(inv, far.astype(float))
        fS = np.bincount(inv, np.where(far, whole, 0.0))
        rest = (np.bincount(inv, whole) - fS) / nr
        share = fN / nr
        for r in (0.1, 0.2, 0.4, 0.8):
            cell = np.floor(pos / r).astype(np.int64)
            key = (cell[:, 0] * 73856093) ^ (cell[:, 1] * 19349663) ^ (cell[:, 2] * 83492791)
            order = np.argsort(key)
            ks = key[order]
            pooledS = np.zeros(len(u))
            pooledN = np.zeros(len(u))
            # Each texel's 27 cells about it: its neighbours on its plane within r.
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    for dz in (-1, 0, 1):
                        c2 = cell + np.array([dx, dy, dz])
                        k2 = (c2[:, 0] * 73856093) ^ (c2[:, 1] * 19349663) ^ (c2[:, 2] * 83492791)
                        lo = np.searchsorted(ks, k2, 'left')
                        hi = np.searchsorted(ks, k2, 'right')
                        cnt = hi - lo
                        if cnt.max() == 0:
                            continue
                        idx = np.repeat(np.arange(len(u)), cnt)
                        offs = np.concatenate([np.arange(a, b) for a, b in zip(lo, hi) if b > a]) if cnt.sum() else np.array([], int)
                        j = order[offs]
                        dp = pos[j] - pos[idx]
                        ok = (np.einsum('ij,ij->i', nrm[j], nrm[idx]) >= 0.95) & (np.abs(np.einsum('ij,ij->i', dp, nrm[idx])) <= 0.02) & \
                             (np.einsum('ij,ij->i', dp, dp) <= r * r)
                        np.add.at(pooledS, idx[ok], fS[j[ok]])
                        np.add.at(pooledN, idx[ok], fN[j[ok]])
            mean_far = np.where(pooledN > 0, pooledS / np.maximum(pooledN, 1e-9), np.where(fN > 0, fS / np.maximum(fN, 1), 0.0))
            est = rest + share * mean_far
            e = err(est)
            print(f'  far past {far_d:.1f} m, pooled within {r:.1f} m: {e.mean():.4f} mean error, {(e > 0.1).mean():.1%} over 10%, energy '
                  f'{est.sum() / truth.sum():.3f}; neighbours a texel median {np.median(pooledN / np.maximum(fN, 1)):.1f}x its own far rays')


if __name__ == '__main__':
    main()
