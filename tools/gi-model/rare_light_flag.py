# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""Which fine texels earn the fine refine: a flag for light carried by a few rare rays (uv run
tools/gi-model/rare_light_flag.py [HS_DIR]).

The fine round gives every fine texel 128 rays and refines further (to 1,024) only its contacts and cloth
(SponzaLoop.Transfer.cs): refining every noisy one was 2 G of the fine round's 3.3 G rays on Sponza. At the crevices
corridor's turn the end wall's foot near z 0.8 takes the courtyard's sky through the doorway along lines that barely
clear the corner block -- a few rays carry it, and its records swing 10-67% seed to seed at 128 rays (2-25% at 512),
while the turn as a whole is quiet (1.7%) (2026-10-23).

On the lean fine round's capture at 512 rays (dumps cd0, cd1: BLIX_CAPTURE_FINE_ONLY=1 --lean-shade --fine-rays 512
--ray-capture 2), per texel: the 128-ray estimate is its first four 32-ray passes' mean, the refined one all its
passes'. A flag is read from the first 128 rays alone, as the fine round would: the share of the light its top three
rays carry (top3), its relative standard error (rse), or a random pick of the same size (random). Each seed flags from
its own rays. Reported per rule and threshold: the texels flagged, the rays spent over 128 a texel, and the seed-to-seed
noise (|a - b| over the mean, / sqrt 2) of every fine texel, the turn's and the end wall's foot near z 0.8's.

2026-10-23 (cd0/cd1, 6,525 fine texels): refining all to 512 halves the turn's noise (0.080 -> 0.043, +300% rays); no
flag beats a random pick by much (rse > 0.10: 31% flagged, +92% rays, turn 0.055; top3 > 0.20: 14%, +43%, 0.067). The
end wall's foot does not settle with rays: its sky -- 39% of its light, through the doorway -- 0.131 at 128 rays, still
0.088 at 512 (its cell hits 0.096 -> 0.025; 7 of its 62 texels captured at both seeds). Its fix samples the doorway,
not more rays.
"""
import json
import sys
from pathlib import Path

import numpy as np

HS = Path(sys.argv[1] if len(sys.argv) > 1 else '/private/tmp/blix-dbg/hs')
LUM = np.array([.2126, .7152, .0722])
FIRST = 4   # the fine round's 128 rays: four passes of 32


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


def per_texel(tag, N, n):
    raw = (dump(tag) / 'rays_captured.u32x8').read_bytes()
    cnt = np.frombuffer(raw[:4], np.uint32)[0]
    R = np.frombuffer(raw[16:], np.uint32).reshape(-1, 8)[:cnt]
    ids = R[:, 0].astype(np.int64)
    passid = R[:, 6] & 0xFF
    dirs = oct_decode(R[:, 1]).astype(float)
    L = np.stack([R[:, 2], R[:, 3], R[:, 4]], 1).copy().view(np.float32).astype(float) @ LUM
    c = L * np.maximum(np.einsum('ij,ij->i', dirs, N[ids]), 0)
    first = passid < FIRST
    cnt_all = np.bincount(ids, minlength=n)
    cnt_f = np.bincount(ids[first], minlength=n)
    full = np.bincount(ids, c, minlength=n) / np.maximum(cnt_all, 1)
    m128 = np.bincount(ids[first], c[first], minlength=n) / np.maximum(cnt_f, 1)
    sq = np.bincount(ids[first], c[first] ** 2, minlength=n) / np.maximum(cnt_f, 1)
    rse = np.sqrt(np.maximum(sq - m128 ** 2, 0) / np.maximum(cnt_f, 1)) / np.maximum(m128, 1e-12)
    # The share of the first 128 rays' light its top three rays carry.
    order = np.lexsort((-c[first], ids[first]))
    fid, fc = ids[first][order], c[first][order]
    start = np.r_[0, np.flatnonzero(np.diff(fid)) + 1]
    rank = np.arange(len(fid)) - np.repeat(start, np.diff(np.r_[start, len(fid)]))
    top3 = np.bincount(fid[rank < 3], fc[rank < 3], minlength=n) / np.maximum(np.bincount(ids[first], c[first], minlength=n), 1e-12)
    return dict(full=full, m128=m128, rse=rse, top3=top3, n=cnt_all, nf=cnt_f)


def main():
    d0 = dump('cd0')
    n = json.loads((d0 / 'meta.json').read_text())['texelCount']
    t = np.frombuffer((d0 / 'texels.f32x8').read_bytes(), np.float32).reshape(-1, 8)[:n]
    P, N = t[:, :3].astype(float), t[:, 4:7].astype(float)
    N /= np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-9)
    a, b = per_texel('cd0', N, n), per_texel('cd1', N, n)
    fine = np.flatnonzero((a['nf'] >= 96) & (b['nf'] >= 96) & (a['n'] >= 384) & (b['n'] >= 384))
    f = P + N * 0.02
    turn = (((f[:, 0] > 8.2) & (f[:, 0] < 9.2) & (f[:, 2] > -0.25) & (f[:, 2] < 1.0)) |
            ((f[:, 0] > 7.2) & (f[:, 0] < 9.2) & (f[:, 2] > 1.0) & (f[:, 2] < 2.0))) & (f[:, 1] > 0) & (f[:, 1] < 2.5)
    foot = (np.abs(P[:, 0] - 9.2) < 0.01) & (N[:, 0] < -0.9) & (P[:, 1] < 0.25) & (np.abs(P[:, 2] - 0.8) < 0.15)
    groups = (('every fine texel', np.ones(n, bool)), ('the turn', turn), ("the end wall's foot near z 0.8", foot))
    print(f'fine texels captured at both seeds with ~512 rays: {len(fine)}; rays a texel median {np.median(a["n"][fine]):.0f}')
    for lab, g in groups:
        print(f'  {lab}: {np.count_nonzero(g[fine])} of them')
    mean = (a['full'] + b['full']) / 2

    def noise(x, y, sel):
        return np.abs(x[sel] - y[sel]) / np.maximum(mean[sel], 1e-12) / np.sqrt(2)

    def report(name, fa, fb):
        # each seed's texel: refined where its own flag is up, its 128 rays where not
        ea = np.where(fa, a['full'], a['m128'])
        eb = np.where(fb, b['full'], b['m128'])
        spent = (fa[fine].mean() + fb[fine].mean()) / 2 * (np.median(a['n'][fine]) - 128) / 128
        cells = []
        for lab, g in groups:
            sel = fine[g[fine]]
            if len(sel) == 0:
                cells.append('-')
                continue
            z = noise(ea, eb, sel)
            cells.append(f'{np.median(z):.3f} / {np.percentile(z, 90):.3f}')
        flagged = (fa[fine].mean() + fb[fine].mean()) / 2
        foot_flagged = (fa[fine][foot[fine]].mean() + fb[fine][foot[fine]].mean()) / 2 if foot[fine].any() else 0
        print(f'  {name:22s} flagged {flagged:6.1%} (foot {foot_flagged:4.0%}), rays +{spent:5.1%}   ' + '   '.join(f'{c:>15s}' for c in cells))

    print(f"\nnoise median / p90, seed to seed      {'':26s}" + '   '.join(f'{lab[:15]:>15s}' for lab, _ in groups))
    zero = np.zeros(n, bool)
    report('none (today)', zero, zero)
    report('all refined', ~zero, ~zero)
    rng = np.random.default_rng(3)
    for frac in (0.05, 0.1, 0.2):
        report(f'random {frac:.0%}', rng.random(n) < frac, rng.random(n) < frac)
    for th in (0.5, 0.35, 0.25, 0.2):
        report(f'top3 > {th:.2f}', a['top3'] > th, b['top3'] > th)
    for th in (0.15, 0.1, 0.07, 0.05):
        report(f'rse > {th:.2f}', a['rse'] > th, b['rse'] > th)


if __name__ == '__main__':
    main()
