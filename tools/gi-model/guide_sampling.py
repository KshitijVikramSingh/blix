# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""Sun-aware sampling for the contacts: their rays drawn from a learned guide, judged by importance reweighting
(uv run tools/gi-model/guide_sampling.py VIEW TEXEL_PATCH).

A contact's noise is its far sunlit hits (4-8% of its rays at 5-7x the mean). Sharing the far light itself fails
(tools/gi-model/far_record.py: a patch's bin is another view of the far field), but a GUIDE only steers where rays go:
each ray is still answered by its own trace and weighed by its true pdf, so an approximate guide costs variance, never
bias. From the captured rays (BLIX_CAPTURE_DIR=1: word 6 the direction, word 7 the weight cos / p) the estimator's
variance under any other pdf q follows from the same samples: E_q[(f/q)^2] = E_p[Y^2 p / q], Y = f / p the ray's light.
  q = alpha x cos / pi + (1 - alpha) x g, g a histogram over octahedral bins of the light found in each:
    own    the texel's own first half of its rays (judged on its second half)
    patch  its patch's coarse sweep and refine rays (the light change traces them anyway)
Per contact band: the standard deviation under q over today's (the ratio of rays at the same noise: its square)."""
import json
import sys
from pathlib import Path

import numpy as np

BANDS = [(0.999, 1.01, 'open'), (0.85, 0.999, '0.85-0.999'), (0.0, 0.85, '<0.85')]


def oct_decode(word):
    xy = np.stack([(word & 0xFFFF).astype(np.uint16).view(np.int16), (word >> 16).astype(np.uint16).view(np.int16)], -1) / 32767.0
    z = 1.0 - np.abs(xy[:, 0]) - np.abs(xy[:, 1])
    x, y = xy[:, 0].copy(), xy[:, 1].copy()
    neg = z < 0
    x[neg], y[neg] = (1 - np.abs(xy[neg, 1])) * np.sign(xy[neg, 0]), (1 - np.abs(xy[neg, 0])) * np.sign(xy[neg, 1])
    d = np.stack([x, y, z], -1)
    return xy, d / np.maximum(np.linalg.norm(d, axis=1, keepdims=True), 1e-12)


def oct_bin(xy, n):
    ij = np.clip(((xy + 1.0) * 0.5 * n).astype(np.int64), 0, n - 1)
    return ij[:, 0] * n + ij[:, 1]


def main():
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    view = Path(sys.argv[1])
    view = sorted(view.glob('view-*'))[-1] if not (view / 'meta.json').exists() else view
    n_tex = json.loads((view / 'meta.json').read_text())['texelCount']
    rec = np.frombuffer((view / 'rays_captured.u32x8').read_bytes()[16:], np.uint32).reshape(-1, 8)
    tag = rec[:, 1] & 0x3FFF
    rnd, near = tag & 15, (tag >> 8) & 0x3F
    tid = rec[:, 0].astype(np.int64)
    Y = np.ascontiguousarray(rec[:, 4]).view(np.float32).astype(np.float64)
    w = np.ascontiguousarray(rec[:, 7]).view(np.float32).astype(np.float64)
    xy, d = oct_decode(rec[:, 6])
    t = np.frombuffer((view / 'texels.f32x8').read_bytes(), np.float32).reshape(-1, 8)[:n_tex]
    nrm = t[:, 4:7].astype(np.float64)
    nrm /= np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-9)
    cos = np.einsum('ij,ij->i', d, nrm[tid])
    p = np.where(w > 0, np.maximum(cos, 0) / np.maximum(w, 1e-12), 0.0)
    patch = np.frombuffer(Path(sys.argv[2]).read_bytes(), np.int32)[:n_tex]
    contact = np.frombuffer((view / 'contact.u8').read_bytes(), np.uint8)[:n_tex]
    cval = np.where(contact > 0, (contact.astype(float) - 1) / 254, np.nan)
    fine = (rnd == 3) & (near == 0) & (p > 0)
    sweep = np.isin(rnd, (1, 2)) & (near == 0) & (p > 0)
    print(f'{fine.sum():,} fine-round rays over {len(np.unique(tid[fine])):,} texels; {sweep.sum():,} sweep rays for the patch guides')
    ft, fY, fp, fxy = tid[fine], Y[fine], p[fine], xy[fine]
    fcos = np.maximum(cos[fine], 0)
    u, inv, cnt = np.unique(ft, return_inverse=True, return_counts=True)
    # The rays' order within a texel: the first half learns, the second is judged (for the own guide).
    order = np.argsort(inv, kind='stable')
    rank = np.empty(len(inv), np.int64)
    rank[order] = np.arange(len(inv)) - np.repeat(np.cumsum(cnt) - cnt, cnt)
    second = rank >= cnt[inv] // 2
    for nb in (8, 16):
        omega = 4.0 * np.pi / (nb * nb)
        fb = oct_bin(fxy, nb)
        # Own: each texel's first half's light by bin.
        own_key = inv * nb * nb + fb
        first = ~second
        own_sum = np.bincount(own_key[first], fY[first], minlength=len(u) * nb * nb)
        own_tot = np.bincount(inv[first], fY[first], minlength=len(u))
        g_own = own_sum[own_key] / np.maximum(own_tot[inv], 1e-30) / omega
        # Patch: the sweep's rays of the texel's patch, by bin.
        sp = patch[tid[sweep]]
        okp = sp >= 0
        pk = sp[okp].astype(np.int64) * nb * nb + oct_bin(xy[sweep][okp], nb)
        uk, kinv = np.unique(pk, return_inverse=True)
        ksum = np.bincount(kinv, Y[sweep][okp])
        ptot = np.bincount(sp[okp], Y[sweep][okp], minlength=patch.max() + 1)
        fpat = patch[ft]
        key = fpat.astype(np.int64) * nb * nb + fb
        pos = np.minimum(np.searchsorted(uk, key), len(uk) - 1)
        has = (uk[pos] == key) & (fpat >= 0)
        g_pat = np.where(has, ksum[pos], 0.0) / np.maximum(np.where(fpat >= 0, ptot[np.maximum(fpat, 0)], 1.0), 1e-30) / omega
        # The oracle (an upper bound: learned from every ray of the texel, the judged ones too).
        all_sum = np.bincount(own_key, fY, minlength=len(u) * nb * nb)
        all_tot = np.bincount(inv, fY, minlength=len(u))
        g_orc = all_sum[own_key] / np.maximum(all_tot[inv], 1e-30) / omega
        for name, g, judged in (('own', g_own, second), ('patch', g_pat, np.ones(len(fY), bool)), ('oracle', g_orc, np.ones(len(fY), bool))):
            for alpha in (0.25, 0.5, 0.75):
                q = alpha * fcos / np.pi + (1 - alpha) * g
                k = judged & (q > 0)
                ji = inv[k]
                cj = np.bincount(ji, minlength=len(u)).astype(float)
                mu = np.bincount(ji, fY[k], minlength=len(u)) / np.maximum(cj, 1)
                var_p = np.bincount(ji, fY[k] ** 2, minlength=len(u)) / np.maximum(cj, 1) - mu ** 2
                var_q = np.bincount(ji, fY[k] ** 2 * fp[k] / q[k], minlength=len(u)) / np.maximum(cj, 1) - mu ** 2
                okt = (cj >= 16) & (var_p > 0) & (mu > 0)
                ratio = np.sqrt(np.maximum(var_q[okt], 0) / var_p[okt])
                line = f'  {nb:2d}x{nb:<2d} bins, {name:6s} guide, alpha {alpha:.2f}: sd over today\'s median {np.median(ratio):.3f} (rays at the same noise x{np.median(ratio) ** 2:.2f})'
                for lo, hi, bname in BANDS:
                    kb = okt & (cval[u] >= lo) & (cval[u] < hi)
                    if kb.sum() < 50: continue
                    rb = np.sqrt(np.maximum(var_q[kb], 0) / var_p[kb])
                    line += f'; {bname} {np.median(rb):.3f}'
                print(line)


if __name__ == '__main__':
    main()
