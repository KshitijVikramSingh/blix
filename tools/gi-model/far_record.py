# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The far record from the sweep's own rays, for the fine round's contacts
(uv run tools/gi-model/far_record.py VIEW TEXEL_PATCH REF_PARTS REF2_PARTS).

The contacts' noise is their far sunlit hits (the ray capture: past 1 m ~87% of the variance), and pooling the far light
a ray over neighbours fails -- they see different far fields through different gaps; what they share is radiance by
direction. The far record from the patches' lists (--live-far-lists) made every texel worse (its bins by patch centres,
36 of them). Here the record comes from the rays a light change already traces: the coarse sweep's and its refine
rounds' far rays (sky misses, hits past R), each into its texel's patch and its direction's bin (octahedral, world
space, N x N); a fine-round ray that goes far takes its patch's bin mean where the bin holds enough rays, its own light
where not. VIEW: a view dump with --ray-capture of every round and BLIX_CAPTURE_DIR=1 (word 6 the direction);
TEXEL_PATCH: the texels' patches (BLIX_REFINE_AUDIT's .patch); REF_PARTS, REF2_PARTS: two 1,024-ray boots' final parts
(the fine round raised too: their difference the floor)."""
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


def oct_bin(word, n_bins):
    """The octahedral encoding (snorm 2 x 16) straight into an N x N grid over [-1, 1]^2: the whole sphere."""
    xy = np.stack([(word & 0xFFFF).astype(np.uint16).view(np.int16), (word >> 16).astype(np.uint16).view(np.int16)], -1) / 32767.0
    ij = np.clip(((xy + 1.0) * 0.5 * n_bins).astype(np.int64), 0, n_bins - 1)
    return ij[:, 0] * n_bins + ij[:, 1]


def main():
    if len(sys.argv) < 5:
        raise SystemExit(__doc__)
    view = Path(sys.argv[1])
    view = sorted(view.glob('view-*'))[-1] if not (view / 'meta.json').exists() else view
    n = json.loads((view / 'meta.json').read_text())['texelCount']
    raw = (view / 'rays_captured.u32x8').read_bytes()
    rec = np.frombuffer(raw[16:], np.uint32).reshape(-1, 8)
    tag = rec[:, 1] & 0x3FFF
    rnd, near = tag & 15, (tag >> 8) & 0x3F
    tid = rec[:, 0].astype(np.int64)
    dist = np.ascontiguousarray(rec[:, 2]).view(np.float32).astype(np.float64)
    how = rec[:, 3] & 7
    whole = np.ascontiguousarray(rec[:, 4]).view(np.float32).astype(np.float64)
    dirw = rec[:, 6]
    patch = np.frombuffer(Path(sys.argv[2]).read_bytes(), np.int32)[:n]
    ref, ref2 = light(sys.argv[3]), light(sys.argv[4])
    sweep = np.isin(rnd, (1, 2)) & (near == 0)
    fine = (rnd == 3) & (near == 0)
    print(f'{len(rec):,} rays: the sweep and its refine {sweep.sum():,}, the fine round {fine.sum():,}')
    fu, finv, fnr = np.unique(tid[fine], return_inverse=True, return_counts=True)
    fw = whole[fine]
    own = np.bincount(finv, fw) / fnr
    truth = ref[fu]
    floor = 0.1 * np.median(ref[ref > 0])
    err = lambda x: np.abs(x - truth) / np.maximum(truth, floor)
    ef = err(ref2[fu])
    print(f'{len(fu):,} fine texels sampled ({np.median(fnr):.0f} rays each): energy {own.sum() / truth.sum():.3f}; their own estimate '
          f'{err(own).mean():.4f} mean error ({(err(own) > 0.1).mean():.1%} over 10%); a 2nd 1,024-ray boot {ef.mean():.4f} ({(ef > 0.1).mean():.1%})')
    fp = patch[tid[fine]]
    for reach in (0.5, 1.0):
        far_s = sweep & ((how == 0) | ((how >= 2) & (dist >= reach)))
        far_f = (how[fine] == 0) | ((how[fine] >= 2) & (dist[fine] >= reach))
        for bins in (4, 8, 12, 16):
            nb = bins * bins
            sp = patch[tid[far_s]]
            ok = sp >= 0
            key_s = sp[ok].astype(np.int64) * nb + oct_bin(dirw[far_s][ok], bins)
            uk, kinv = np.unique(key_s, return_inverse=True)
            ksum = np.bincount(kinv, whole[far_s][ok])
            kn = np.bincount(kinv)
            key_f = fp.astype(np.int64) * nb + oct_bin(dirw[fine], bins)
            pos = np.searchsorted(uk, key_f)
            hit = (pos < len(uk)) & (uk[np.minimum(pos, len(uk) - 1)] == key_f) & (fp >= 0) & far_f
            for least in (4, 16):
                use = hit & (kn[np.minimum(pos, len(uk) - 1)] >= least)
                rw = np.where(use, ksum[np.minimum(pos, len(uk) - 1)] / np.maximum(kn[np.minimum(pos, len(uk) - 1)], 1), fw)
                est = np.bincount(finv, rw) / fnr
                e = err(est)
                print(f'  reach {reach:.1f} m, {bins:2d}x{bins:<2d} bins, a bin known at {least:2d}+ rays: the record answers {use.sum() / far_f.sum():.0%} of the '
                      f'far rays; {e.mean():.4f} mean error ({(e > 0.1).mean():.1%} over 10%), energy {est.sum() / truth.sum():.3f}')


if __name__ == '__main__':
    main()
