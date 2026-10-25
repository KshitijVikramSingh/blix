# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy", "scipy"]
# ///
"""A sun change's gather scheduled by patch layers, against today's order, over time
(uv run tools/gi-model/patch_layers.py A B_SEED0 B_SEED1 [--rays-per-s R] [--refine X]).

Today a sun change gathers texels in stages -- the visible and changed, the coarse sweep (on screen first), the fine
picks, a near round -- and refines texel by texel (worst first). The candidate (the user's, 2026-10-25): rank the
patches -- by what of them is on screen (size, distance), and by how wrong their light is likely to be -- and relight a
few ranks ("layers") of patches a round, all of a patch's texels together; refine the same way. Judged by the error on
screen (pixel-weighted, the view's own texels) and over the scene (every live fine texel), over the change's time.

The units a gather owns: the coarse texels (ids under lightmapFirstRecord) and the fine picks (provenance 3 at B); the
fine copies (5) follow the nearest coarse texel on their patch. A unit's error (relative, the select's: / max(truth, a
tenth of the median)) against truth T = the mean of the two B seeds:
  before its gather   the scalar ratio relight's |R x G_A - T| (what shows after the re-aim)
  after n rounds      its gather's noise, |B0 - B1| / sqrt(2) / sqrt(n) (a copy: its own B seeds', its source's rounds)
Time: rays (128 a unit a round) over the rays a second (--rays-per-s; default 6.3 M: today's in-frame change, 0.45 G
rays in 71 s on Crytek Sponza with its setup). Orders, each gathering every unit once, then refine rounds (128 more rays
to the worst by the order's own measure) until --refine x the first pass's rays:
  today      on-screen units whose sun flipped first; the selected coarse (instant moved > 0.1), on screen first; the
             fine picks; refine by noise (not by screen)
  patches    patches by (pixels + a texel's share) x the patch's expected error (its instant change, the runtime's
             signal) per ray, every unit; refine patches by pixels x noise; patches/s: the selected units alone (as today)
  distance   patches by pixels per ray alone (no error)
  oracle     units by their true error drop x (pixels + share) per ray (a bound)

Crytek Sponza, sun A (40, -50), the view's camera (-6, 1.7, 0), 2026-10-25. Screen error at 0 / 1 / 2 / 10 / 40 / 70 s:
  20 deg  today      0.242 0.168 0.094 0.093 0.066 0.054 (ends 73 s)
          patches/s  0.242 0.118 0.106 0.087 0.042 0.026 (the same units and rays: ends 73 s)
          oracle     0.242 0.061 0.059 0.059 0.077 0.029
  5 deg   today      0.081 0.067 0.067 0.068 0.048 0.041 ;  patches/s 0.081 0.071 0.072 0.071 0.034 0.023
  60 deg  today      0.169 0.107 0.081 0.081 0.055 0.044 ;  patches/s 0.169 0.095 0.086 0.075 0.038 0.024
The first pass's order matters for a second or two (today's visible-first catches up by 2 s); the oracle's 0.06 at 1 s
says a better error signal could take the screen there at once. The refine is the lever: by patch, weighed by its
pixels, the same rays end the screen at 0.023-0.026 against today's 0.041-0.054 (the scene's 0.028-0.031 against
0.019-0.022: off-screen pays). The plateaus are a first pass that replaces a relit texel by one gather's noise where
the relit light was better (the oracle's rise at 40 s): a gather should blend with the relit light, not replace it."""
import argparse
import sys
from pathlib import Path

import numpy as np
from scipy.spatial import cKDTree

sys.path.insert(0, str(Path(__file__).parent))
import directional_ratio as dr  # noqa: E402

RAYS = 128


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('a'); ap.add_argument('b0'); ap.add_argument('b1')
    ap.add_argument('--rays-per-s', type=float, default=6.3e6)
    ap.add_argument('--refine', type=float, default=2.0)
    ap.add_argument('--share', type=float, default=0.05, help='a texel off screen weighs this many pixels')
    args = ap.parse_args()
    a, b0, b1 = dr.run(args.a), dr.run(args.b0), dr.run(args.b1)
    irr = dr.irradiance(a['meta'])
    n = len(a['sky'])
    first = int(b0['meta']['lightmapFirstRecord'])
    view = b0['view']
    prov = np.frombuffer((Path(args.b0) / 'parts.final.0.provenance').read_bytes(), np.uint8)[:n] & 15
    tex = np.frombuffer((view / 'texels.f32x8').read_bytes(), np.float32).reshape(-1, 8)[:n]
    pos = tex[:, :3].astype(np.float64)
    patch = np.frombuffer((view / 'patch.i32').read_bytes(), np.int32)[:n].astype(np.int64)
    patch = np.where(patch < 0, -1, patch)
    # The prepass's texel a pixel: its atlas position in eighths (x | y << 16), the atlas index its record.
    lt = np.frombuffer((view / 'lightmap_texel.r32u').read_bytes(), np.uint32)
    lt = lt[lt != 0]
    aw, ah = b0['meta']['lightmapAtlas']
    ax, ay = np.minimum((lt & 0xFFFF) >> 3, aw - 1), np.minimum((lt >> 16) >> 3, ah - 1)
    index = np.frombuffer((view / 'lightmap_index.r32u').read_bytes(), np.uint32)
    pix = index[ay.astype(np.int64) * aw + ax]
    pix = pix[pix < n]
    pixels = np.bincount(pix.astype(np.int64), minlength=n).astype(np.float64)
    ids = np.arange(n)
    coarse = ids < first
    pick = (~coarse) & (prov == 3)
    copy = (~coarse) & (prov == 5)
    unit = coarse | pick
    # The copies' sources: the nearest coarse texel, on their patch where one of the 4 nearest is.
    tree = cKDTree(pos[:first])
    _, k4 = tree.query(pos[copy], k=4, workers=-1)
    same = patch[k4] == patch[copy][:, None]
    choice = np.where(same.any(1), k4[np.arange(len(k4)), np.argmax(same, 1)], k4[:, 0])
    source = ids.copy()
    source[copy] = choice
    live_fine = pick | copy
    T = 0.5 * ((b0['sky'] + irr * b0['sun']) + (b1['sky'] + irr * b1['sun']))
    floor = 0.1 * np.median(T[live_fine & (T > 0)])
    rel = lambda x: np.abs(x - T) / np.maximum(T, floor)
    IA, IB = a['oct'].sum(1), b0['oct'].sum(1)
    R = np.where(IA > 1e-6, np.clip(IB / np.maximum(IA, 1e-30), 0, 16), 1.0)
    e0 = rel(a['sky'] + irr * R * a['sun'])
    sigma = np.abs((b0['sky'] + irr * b0['sun']) - (b1['sky'] + irr * b1['sun'])) / np.sqrt(2) / np.maximum(T, floor)
    # The runtime's signal: how far the instant layer moved the texel's light (the select's), per patch.
    moved = np.abs(irr * (IB - IA) * np.where(IA > 1e-6, a['sun'] / np.maximum(IA, 1e-30), 1.0)) / np.maximum(a['sky'] + irr * a['sun'], floor)
    toA, toB = np.array(a['meta']['sunToward']), np.array(b0['meta']['sunToward'])
    print(f'step {np.degrees(np.arccos(np.clip(toA @ toB, -1, 1))):.0f} deg; units {unit.sum():,} (coarse {coarse.sum():,}, fine picks {pick.sum():,}), '
          f'copies {copy.sum():,}; on screen {int(pixels.sum()):,} pixels over {(pixels > 0).sum():,} texels; {args.rays_per_s / 1e6:.1f} M rays/s')
    for nm, m in (('coarse', coarse), ('fine picks', pick), ('copies', copy)):
        print(f'    {nm}: relit error {e0[m].mean():.3f}, a gather\'s noise {sigma[m].mean():.3f}; on screen {(pixels[m] > 0).sum():,} texels')
    # A unit's weight: its pixels and its copies' (on screen), and its texels' share (off screen).
    wpix = np.bincount(source[live_fine], pixels[live_fine], minlength=n)
    wtex = np.bincount(source[live_fine], minlength=n).astype(np.float64)
    wpix[pick] = pixels[pick]; wtex[pick] = 1.0
    units = np.flatnonzero(unit)

    def errors(rounds):
        """Screen and scene error with each unit's rounds (0: relit)."""
        r = rounds[source]
        e = np.where(r > 0, sigma / np.sqrt(np.maximum(r, 1)), e0)
        scr = (e * pixels)[live_fine].sum() / pixels[live_fine].sum()
        return scr, e[live_fine].mean()

    def simulate(name, order, refine_score):
        rounds = np.zeros(n)
        budget_first = len(order) * RAYS
        marks = [0, 1, 2, 5, 10, 20, 40, 70, 100]
        out, mi, rays = [], 0, 0.0
        step = max(len(order) // 400, 1)
        timeline = []
        def mark():
            timeline.append((rays / args.rays_per_s, *errors(rounds)))
        mark()
        for i in range(0, len(order), step):
            rounds[order[i:i + step]] += 1
            rays += len(order[i:i + step]) * RAYS
            mark()
        # Refine: rounds of the worst tenth of the units by the order's score, until the budget.
        while rays < budget_first * (1 + args.refine):
            s = refine_score(rounds)
            top = units[np.argsort(-s[units])[: max(len(units) // 10, 1)]]
            for i in range(0, len(top), step):
                rounds[top[i:i + step]] += 1
                rays += len(top[i:i + step]) * RAYS
                if rays >= budget_first * (1 + args.refine): break
            mark()
        tl = np.array(timeline)
        cells = []
        for t in marks:
            j = np.searchsorted(tl[:, 0], t, 'right') - 1
            cells.append(f'{tl[j, 1]:.3f}')
        final = tl[-1, 1]
        t90 = tl[np.argmax(tl[:, 1] <= 1.25 * final), 0]
        print(f'  {name:9s} screen ' + ' '.join(f'{c:>6s}' for c in cells) + f'  | to within 25% of its end {t90:5.1f} s; '
              f'scene at the end {tl[-1, 2]:.3f}, end {tl[-1, 0]:.0f} s')

    print('  (screen error at t =  ' + ' '.join(f'{t:>5d}s' for t in (0, 1, 2, 5, 10, 20, 40, 70, 100)) + ')')
    # today
    bitflip = moved > 0.1
    on = wpix > 0
    sel = bitflip | pick
    stage = np.where(on & bitflip & unit, 0, np.where(coarse & sel & on, 1, np.where(coarse & sel, 2, np.where(pick, 3, 4))))
    today = units[np.lexsort((units, stage[units]))]
    today = today[stage[today] < 4]
    simulate('today', today, lambda r: sigma / np.sqrt(np.maximum(r, 1)) * (r > 0))
    # patches by (pixels + share) x expected error per ray
    w = wpix + args.share * wtex
    pk = np.maximum(patch, -1) + 1
    pw = np.bincount(pk[units], w[units]); pe = np.bincount(pk[units], (moved * w)[units]) / np.maximum(pw, 1e-12)
    pc = np.bincount(pk[units]).astype(np.float64)
    pscore = pw * (pe + 0.02) / np.maximum(pc, 1)
    order = units[np.lexsort((units, -pscore[pk[units]]))]
    simulate('patches', order, lambda r: np.bincount(pk[units], (w * sigma / np.sqrt(np.maximum(r, 1)))[units], minlength=len(pc))[pk] / np.maximum(pc[pk], 1))
    seln = order[sel[order]]
    simulate('patches/s', seln, lambda r: np.bincount(pk[units], (w * sigma / np.sqrt(np.maximum(r, 1)))[units], minlength=len(pc))[pk] / np.maximum(pc[pk], 1))
    dscore = pw / np.maximum(pc, 1)
    order = units[np.lexsort((units, -dscore[pk[units]]))]
    simulate('distance', order, lambda r: np.bincount(pk[units], (w * sigma / np.sqrt(np.maximum(r, 1)))[units], minlength=len(pc))[pk] / np.maximum(pc[pk], 1))
    gain = np.maximum(e0 - sigma, 0)
    ogain = np.bincount(source[live_fine], (gain * (pixels + args.share))[live_fine], minlength=n)
    ogain[pick] = (gain * (pixels + args.share))[pick]
    order = units[np.argsort(-ogain[units])]
    simulate('oracle', order, lambda r: w * sigma / np.sqrt(np.maximum(r, 1)) * (1 / np.sqrt(np.maximum(r, 1)) - 1 / np.sqrt(r + 1)))


if __name__ == '__main__':
    main()
