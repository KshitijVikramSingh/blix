# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""Where a small sun step changes the texels' light, and what pushing it from its sources costs
(uv run tools/gi-model/sun_flip_push.py VIEW_A VIEW_B [PARTS_A PARTS_B]).

A texel's sun part is what its rays bring from their hits: the sun there (its unshadowed term times its visibility,
--ray-capture-sun's word 6: full and the hit texels' bit blend) and the bounce. Between two suns a ray changes by
  flip    full_B x (bits_B - bits_A)    the hit crossed a shadow edge: thin strips, sharp
  angle   bits_A x (full_B - full_A)    the hit stayed lit (or dark) and its cosine moved: smooth, everywhere
  bounce  bounce_B - bounce_A           the light the hit's own surroundings send: smooth
VIEW_A, VIEW_B are view dumps of Crytek Sponza booted under two suns with cosine rays (--sun-nodes-no-guide), one seed
and --ray-capture B --ray-capture-sun: the sweep's rays match one for one (texel, pass, ray), so a texel's change over
them is the change itself, no noise between. PARTS_A/PARTS_B: the runs' BLIX_DUMP_PARTS prefixes (.instant.0: the
texels' own sun bits, the flipped texels -- the push's sources).

1. The parts' shares of the change, and how far a texel's light is left from the new sun's by
     keep        the old light
     + angle     the angle part added: exact if a texel keeps the sun-weighted sum of its hits' normals (the sun term at a
                 lit hit is linear in the sun's direction while its cosine stays positive -- checked here: full_B against
                 full_A x cos_B / cos_A at the hit texel's normal)
     + bounce    and the bounce part (what the live patch light's change would carry)
     + flip      and the flip part (what a push from the flipped texels would carry): the whole change
2. The push: a flipped texel h shoots M_h cosine rays and adds its change at what they hit. By reciprocity a receiver's
   push estimate has the variance sum_h P_t(h) dL_h^2 A_h / (A_t M_h) (P_t(h): the chance a gather ray of t hits h), so
   with rays in proportion to area the push matches an N-ray gather's noise at N x (flipped area / receiver area) rays:
   its cost is the flipped area at the receivers' resolution.

Crytek Sponza from the authored sun (180, -72), 2026-10-25, 2.43 M matched sweep rays over 18,989 texels:
  3.09 deg   rays crossing a shadow edge 0.21%; texels seeing one 9.9%; the change by part: flip 80%, angle 21%,
             bounce 6%; the angle part linear in the sun to 0.00% (median, 26 K lit hits); keep 0.077 mean error (6.7%
             over 10% off), + angle + bounce 0.067, + angle + flip 0.010 (1.8%), all three 0.000; 77,563 texels flipped
             (1.37%): ~10 M push rays at 128 a flipped texel, against the ~0.46 G a sun change gathers
  10.07 deg  0.76%; 17.1%; flip 83%, angle 22%, bounce 7%; linear to 0.01%; keep 0.219 (23.0%), + angle + flip
             0.047 (12.9%), all three 0.000; 177,442 flipped (3.12%): ~23 M push rays
Open: the bounce part's source (the live patch light's change), and the push's noise simulated, not by reciprocity."""
import json
import sys
from pathlib import Path

import numpy as np

LUM = np.array([0.2126, 0.7152, 0.0722])


def load(view):
    d = sorted(Path(view).glob('view-*'))[-1]
    m = json.loads((d / 'meta.json').read_text())
    n = m['texelCount']
    raw = (d / 'rays_captured.u32x8').read_bytes()
    rec = np.frombuffer(raw[16:], np.uint32).reshape(-1, 8)
    tag = rec[:, 1] & 0x7FFF
    if not (rec[:, 1] & 0x8000).any():
        raise SystemExit(f'{view}: no --ray-capture-sun')
    rec = rec[tag == 1]   # the sweep's (round 0): the refine's and the fine round's texels are picked by the light
    f = dict(tid=rec[:, 0].astype(np.int64), pas=(rec[:, 1] >> 16).astype(np.int64), r=((rec[:, 3] >> 8) & 31).astype(np.int64),
             dist=rec[:, 2].copy().view(np.float32), kind=rec[:, 3] & 7,
             whole=rec[:, 4].copy().view(np.float32).astype(np.float64), bounce=rec[:, 5].copy().view(np.float32).astype(np.float64),
             ntex=np.where(rec[:, 7] < n, rec[:, 7].astype(np.int64), -1))
    hv = rec[:, 6].copy().view(np.float16).reshape(-1, 2).astype(np.float64)
    f['full'], f['bits'] = hv[:, 0], hv[:, 1]
    f['hit'] = (f['kind'] >= 2) & (f['dist'] < 1e20)
    f['key'] = (f['tid'] << 24) | (f['pas'] << 5) | f['r']
    t = np.frombuffer((d / 'texels.f32x8').read_bytes(), np.float32).reshape(-1, 8)[:n]
    return m, f, t


def matched(a, b):
    ia, ib = np.argsort(a['key']), np.argsort(b['key'])
    _, pa, pb = np.intersect1d(a['key'][ia], b['key'][ib], return_indices=True)
    A = {k: v[ia][pa] for k, v in a.items()}
    B = {k: v[ib][pb] for k, v in b.items()}
    same = (A['ntex'] == B['ntex']) & (np.abs(A['dist'] - B['dist']) <= 1e-4 * np.maximum(A['dist'], 1))
    return A, B, same


def bits_of(prefix, n):
    raw = Path(f'{prefix}.instant.0').read_bytes()
    count = int(np.frombuffer(raw[:4], np.int32)[0])
    rest = raw[4 + 16 * count:]
    stride, column = np.frombuffer(rest[:8], np.int32)
    w = np.frombuffer(rest[8:8 + 4 * count * stride], np.uint32).reshape(count, stride)
    return ((w[:, column // 32] >> (column % 32)) & 1)[:n]


def to_sun(m):
    s = np.array(m['sunToward'], float) if 'sunToward' in m else None
    return s / np.linalg.norm(s) if s is not None else None


def main():
    args = sys.argv[1:]
    if len(args) not in (2, 4):
        raise SystemExit(__doc__)
    ma, fa, ta = load(args[0])
    mb, fb, _ = load(args[1])
    A, B, same = matched(fa, fb)
    print(f'{len(A["key"]):,} sweep rays matched across the suns, {same.mean():.1%} with the same hit')
    A = {k: v[same] for k, v in A.items()}
    B = {k: v[same] for k, v in B.items()}
    sa, sb = to_sun(ma), to_sun(mb)
    if sa is not None and sb is not None:
        print(f'the suns {np.degrees(np.arccos(np.clip(sa @ sb, -1, 1))):.2f} deg apart')

    sun_a = np.where(A['hit'], A['whole'] - A['bounce'], 0.0)
    sun_b = np.where(B['hit'], B['whole'] - B['bounce'], 0.0)
    # A ray's class from the sun it brought (the shade's: the hit's albedo, its own shadow ray), not word 6's bit blend:
    # full x bits read 2.5x the captured sun (bits are the hit texels' cached visibility, not the ray's).
    lit_a, lit_b = sun_a > 1e-9, sun_b > 1e-9
    flipped_ray = lit_a != lit_b
    print(f'rays whose hit crossed a shadow edge: {flipped_ray.mean():.2%}; lit under both {(lit_a & lit_b).mean():.2%}')
    flip = np.where(flipped_ray, sun_b - sun_a, 0.0)
    angle = np.where(lit_a & lit_b, sun_b - sun_a, 0.0)
    bounce = B['bounce'] - A['bounce']
    lit = lit_a & lit_b
    whole_a, whole_b = A['whole'], B['whole']

    # Linearity of the sun term at a lit hit (angle): full_B against full_A x cos_B / cos_A at the hit texel's normal.
    if sa is not None and sb is not None:
        on = lit & (A['ntex'] >= 0)
        nrm = ta[A['ntex'][on], 4:7].astype(float)
        nrm /= np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-9)
        ca, cb = nrm @ sa, nrm @ sb
        good = ca > 0.05
        pred = sun_a[on][good] * np.maximum(cb[good], 0) / ca[good]
        truth = sun_b[on][good]
        print(f'the angle part as linear in the sun (cos_B / cos_A at the hit\'s normal), {good.sum():,} lit hits on texels: '
              f'bias {100 * (pred.sum() / truth.sum() - 1):+.2f}%, |error| median {100 * np.median(np.abs(pred / np.maximum(truth, 1e-9) - 1)):.2f}%')

    # Per texel (its matched rays' means).
    u, inv = np.unique(A['tid'], return_inverse=True)
    cnt = np.bincount(inv).astype(float)
    mean = lambda x: np.bincount(inv, weights=x) / cnt
    t_a, t_b = mean(whole_a), mean(whole_b)
    d_flip, d_angle, d_bounce = mean(flip), mean(angle), mean(bounce)
    d_all = t_b - t_a
    floor = 0.1 * np.median(t_b[t_b > 0])
    err = lambda p: np.abs(p - t_b) / np.maximum(t_b, floor)
    print(f'\n{len(u):,} texels ({np.median(cnt):.0f} rays each): the change |dL| summed over them, by part: '
          f'flip {np.abs(d_flip).sum() / np.abs(d_all).sum():.0%}, angle {np.abs(d_angle).sum() / np.abs(d_all).sum():.0%}, '
          f'bounce {np.abs(d_bounce).sum() / np.abs(d_all).sum():.0%} (they need not add up: they cancel); '
          f'the residual (sky, misses) {np.abs(d_all - d_flip - d_angle - d_bounce).sum() / np.abs(d_all).sum():.1%}')
    print(f'texels with any flipped hit: {(np.bincount(inv, weights=(np.abs(flip) > 0).astype(float)) > 0).mean():.1%}')
    for name, p in (('keep', t_a), ('+ angle', t_a + d_angle), ('+ angle + bounce', t_a + d_angle + d_bounce),
                    ('+ angle + bounce + flip', t_a + d_angle + d_bounce + d_flip), ('+ angle + flip', t_a + d_angle + d_flip)):
        e = err(p)
        print(f'  {name:26s} mean error {e.mean():.4f}, over 10% off {(e > 0.1).mean():.2%}, over 50% {(e > 0.5).mean():.2%}, '
              f'energy {p.sum() / t_b.sum():.4f}')

    if len(args) == 4:
        n = ma['texelCount']
        ba, bb = bits_of(args[2], n), bits_of(args[3], n)
        live = np.abs(ta[:, 3]) >= 2
        flipped = live & (ba != bb)
        print(f'\nthe push\'s sources: {flipped.sum():,} of {live.sum():,} live texels flipped ({flipped.sum() / live.sum():.2%})')
        print('  (the push matches a 128-ray gather\'s noise at 128 x the flipped area over the receiver\'s area: '
              'at the finest texels, 128 rays a flipped finest texel)')

if __name__ == '__main__':
    main()
