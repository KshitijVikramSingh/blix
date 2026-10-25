# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The directional ratio relight: a sun change's light from the hierarchy, each texel's last gather kept as a ratio per
world octant (uv run tools/gi-model/directional_ratio.py A B_SEED0 B_SEED1 [B_SEED0 B_SEED1 ...]).

Today's ratio relight (texel_ratio.comp) keeps one number a texel: its gathered sun part over what the instant layer
(texel_patch_light.comp: the live patch light by world octant, each octant by the texel's openness there over its
patch's) gave it. A sun change is then the new instant layer times that ratio -- the patches carry the change, the
ratio the texel's own geometry (contacts, near occlusion). One number cannot tell where the light came from: a texel
under an arch whose gather saw the bounce come from below, and whose patch's light moves to come from the side, keeps
the old proportion. The candidate keeps the ratio per octant: est_B = sum_o I_B[o] x G_A[o] / I_A[o], where I is the
instant layer's sun by octant (BLIX_DUMP_INSTANT_OCT) and G_A[o] the texel's gathered sun at A split by its own rays'
sun by octant (BLIX_CAPTURE_DIR: word 5 the ray's sun column, word 6 its direction). An octant the instant layer or
the rays hardly saw takes the texel's scalar ratio; variants shrink the per-octant ratio toward it by its rays.

A: a boot at sun A with --ray-capture (1 in 2^B texels: the texels judged) and BLIX_DUMP_PARTS (.instant.0 and its
.octants, .final.0) and --view-dump. B_SEEDn: boots at sun B (two seeds -- the judge is the second, so neither the
candidate's gather at A nor the first B shares its rays; the first B's error against the second is the floor, what a
fresh gather at B would leave). Several (B_SEED0, B_SEED1) pairs: several steps. Errors as the select's: the whole
light (the sky's part A's, kept), |est - truth| / max(truth, a tenth of the median light).

Crytek Sponza, sun A (40, -50), steps 5 / 20 / 60 deg (yaw 47.8 / 71.3 / 142.1), 33 K captured texels at 256 rays
(2026-10-25). Mean error -- a fresh gather (the floor) / A kept / the instant layer / scalar ratio / by octant (best):
  5 deg   0.108 / 0.130 / 0.632 / 0.137 / 0.140
  20 deg  0.099 / 0.347 / 0.593 / 0.280 / 0.276
  60 deg  0.104 / 0.485 / 0.659 / 0.232 / 0.250
No gain from direction: the octants' ratios carry what the scalar does, or less (60 deg). The hierarchy's change itself
is the limit: log(instant B / A) against the true log change, per texel: 20 deg corr 0.88, |residual| median 0.27
(a second gather 0.10); 60 deg 0.85, 0.32; at 5 deg 36% of the texels whose light changed over 2x the instant layer
moved under 10% -- sun on geometry under a patch's size (25 cm, blended), not where the light comes from."""
import json
import sys
from pathlib import Path

import numpy as np

LUM = np.array([0.2126, 0.7152, 0.0722])


def u9(w):
    w = w.astype(np.int64)
    sc = np.exp2((w >> 27) - 24.0)
    return np.stack([(w & 511) * sc, ((w >> 9) & 511) * sc, ((w >> 18) & 511) * sc], -1)


def parts(path):
    raw = Path(path).read_bytes()
    n = int(np.frombuffer(raw[:4], np.int32)[0])
    w = np.frombuffer(raw[4:4 + 16 * n], np.uint32)
    return u9(w[:2 * n].reshape(n, 2)[:, 0]) @ LUM, u9(w[2 * n:4 * n].reshape(n, 2)[:, 0]) @ LUM


def run(d):
    d = Path(d)
    view = d / 'view'
    view = sorted(view.glob('view-*'))[-1] if not (view / 'meta.json').exists() else view
    meta = json.loads((view / 'meta.json').read_text())
    sky, sun = parts(d / 'parts.final.0')
    _, isun = parts(d / 'parts.instant.0')
    octs = np.frombuffer((d / 'parts.instant.0.octants').read_bytes(), np.float32).reshape(-1, 8)
    return dict(view=view, meta=meta, sky=sky, sun=sun, isun=isun, oct=octs.astype(np.float64))


def irradiance(meta):
    for k in ('sunIrradiance',):
        if k in meta:
            v = meta[k]
            return float(np.dot(v, LUM)) if isinstance(v, list) else float(v)
    return 9.42


def main():
    if len(sys.argv) < 4 or (len(sys.argv) - 2) % 2:
        raise SystemExit(__doc__)
    a = run(sys.argv[1])
    irr = irradiance(a['meta'])
    n = len(a['sky'])
    raw = (a['view'] / 'rays_captured.u32x8').read_bytes()
    rec = np.frombuffer(raw[16:], np.uint32).reshape(-1, 8)
    rec = rec[rec[:, 0] < n]
    tid = rec[:, 0].astype(np.int64)
    sunY = np.ascontiguousarray(rec[:, 5]).view(np.float32).astype(np.float64)
    xy = np.stack([(rec[:, 6] & 0xFFFF).astype(np.uint16).view(np.int16), (rec[:, 6] >> 16).astype(np.uint16).view(np.int16)], -1) / 32767.0
    # The octahedral decode's sign per axis is all the octant needs: z < 0 folds, x and y keep their signs.
    z = 1.0 - np.abs(xy[:, 0]) - np.abs(xy[:, 1])
    o = (xy[:, 0] > 0).astype(np.int64) | ((xy[:, 1] > 0).astype(np.int64) << 1) | ((z > 0).astype(np.int64) << 2)
    ok = np.isfinite(sunY) & (sunY >= 0)
    u, inv = np.unique(tid[ok], return_inverse=True)
    raysO = np.bincount(inv * 8 + o[ok], minlength=len(u) * 8).reshape(-1, 8)
    sunO = np.bincount(inv * 8 + o[ok], sunY[ok], minlength=len(u) * 8).reshape(-1, 8)
    rays = raysO.sum(1)
    tot = sunO.sum(1)
    print(f'{ok.sum():,} captured rays over {len(u):,} texels ({np.median(rays):.0f} a texel); the instant layer by octant against its '
          f'sun part: {a["oct"][u].sum() / max(a["isun"][u].sum(), 1e-30):.4f} (1: the dump sums as the part)')
    keep = (rays >= 32) & (a['sun'][u] >= 0)
    u, raysO, sunO, rays, tot = u[keep], raysO[keep], sunO[keep], rays[keep], tot[keep]
    GA = a['sun'][u]
    share = np.where(tot[:, None] > 0, sunO / np.maximum(tot[:, None], 1e-30), 1.0 / 8)
    GAo = GA[:, None] * share
    IA = a['oct'][u]
    skyA = a['sky'][u]
    for pair in range(2, len(sys.argv), 2):
        b0, b1 = run(sys.argv[pair]), run(sys.argv[pair + 1])
        IB = b0['oct'][u]   # the instant layer is the patches' light: the same either seed but for the patch solve's own
        truth = b1['sky'][u] + irr * b1['sun'][u]
        floor = 0.1 * np.median(truth[truth > 0])
        err = lambda est: np.abs(est - truth) / np.maximum(truth, floor)
        def line(name, sunEst, sky=skyA):
            e = err(sky + irr * sunEst)
            moved = np.abs(IB.sum(1) - IA.sum(1)) > 0.1 * np.maximum(GA, 1e-3)
            return (f'  {name:34s} {e.mean():.4f} mean, {(e > 0.1).mean():5.1%} over 10%, {(e > 0.5).mean():5.2%} over 50%, energy '
                    f'{(sky + irr * sunEst).sum() / truth.sum():.3f}; where the instant moved >10%: {e[moved].mean():.4f} ({moved.mean():.0%} of texels)')
        ang = np.degrees(np.arccos(np.clip(np.dot(np.array(b0['meta'].get('sunToward', [0, 1, 0])), np.array(a['meta'].get('sunToward', [0, 1, 0]))), -1, 1)))
        print(f'== {Path(sys.argv[pair]).name} / {Path(sys.argv[pair + 1]).name} (step {ang:.0f} deg)')
        print(line('a fresh gather (the floor)', b0['sun'][u], b0['sky'][u]))
        print(line('no relight (A\'s light kept)', GA))
        print(line('the instant layer alone', IB.sum(1)))
        R = np.clip(GA / np.maximum(IA.sum(1), 1e-30), 0, 16)
        scalar = np.where(IA.sum(1) > 1e-6, R * IB.sum(1), GA)
        print(line('scalar ratio (today)', scalar))
        for least, k in ((1, 0.0), (4, 0.0), (8, 0.0), (1, 4.0), (1, 16.0), (1, 64.0)):
            # Per octant: the gathered over the instant, shrunk toward the texel's scalar ratio by k rays' worth.
            w = k / np.maximum(raysO, 1)
            ro = (GAo + w * R[:, None] * IA) / np.maximum(IA * (1 + w), 1e-30)
            use = (IA > 1e-6) & (raysO >= least)
            ro = np.where(use, np.clip(ro, 0, 16), R[:, None])
            est = np.where(IA.sum(1) > 1e-6, (ro * IB).sum(1), GA)
            print(line(f'by octant (rays >= {least}, shrink {k:g})', est))


if __name__ == '__main__':
    main()
