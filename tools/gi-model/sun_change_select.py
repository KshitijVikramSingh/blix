# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""Which texels a sun change must gather again (uv run tools/gi-model/sun_change_select.py PREFIX0 VIEW0 [PREFIX1 VIEW1]).

A sun change today sweeps every coarse texel and every fine texel the copy flags -- about as many rays as the boot's
gather (Crytek Sponza at 128 rays: ~0.7 G) -- though most texels see nothing whose sun changed. The candidate: gather
again only the texels a cheap rule picks, the rest keep the instant layer's light (texel_patch_light.comp: the old
gather relit by its patches' change, what shows before any ray). The rule is judged against the full gather at the new
sun: a texel left out is wrong by |instant - full| / max(full, a tenth of the median light) (SponzaLoop.Recall.cs's
error), and the full gather is itself noisy -- with a second seed's run, the error two full gathers have against each
other on the same texels is the floor a rule has to stay near.

PREFIXn is a run's BLIX_DUMP_PARTS prefix (SponzaLoop.Recall.cs's DumpParts): .instant.0 (the boot's instant layer,
the old sun's bits), .final.0 (the boot's light), .instant.1 (the new sun's instant layer and bits), .final.1 (the
sun change's light), with .provenance beside each. VIEWn its --view-dump directory (texels.f32x8: the records'
positions; meta.json: the sun's irradiance). The same scene, camera and sun change in both runs, BLIX_GATHER_SEED apart.

Rules (each a share of the texels to gather again, the rest kept on the instant layer):
  own      the texel's own sun bit flipped
  near r   a texel within r (a grid of r-cells, the 3 x 3 x 3 about it) flipped -- what its rays' near hits saw change
  instant t  the instant layer moved the texel's light by over t of it (the patches' estimate of the change)
  and their unions
Per rule: the share gathered, and over the texels left out: their mean error, the share over 50% off (Recall's bad),
the energy; against the second seed's (a full gather's own error) on the same texels.

Crytek Sponza, sun to (-0.413, 0.766, 0.492), seeds 0 and 1000 (2026-10-25): of the 2.06 M texels the change gathered,
the instant layer kept everywhere is 0.201 mean error, 8.9% bad (a second full gather: 0.112, 4.4%). Flips are no rule
-- own 10.5% gathered, the left-out unchanged (0.202, 8.9%); near 1 m 72% gathered, 0.113 / 6.0% (nearly every texel
is a metre or two from some flip). The instant layer's change is: over 10% gathers 48%, the left-out 0.035 / 1.4%
(the second seed there 0.042 / 1.4%); over 20% 42%, 0.053 / 2.3% (0.056 / 2.2%); over 40% 31%, 0.078 / 3.6%
(0.074 / 3.0%); energy 0.997-0.998. A texel the instant layer barely moved is as near the full gather as a second full
gather is. Not modelled: the fine copy reads the coarse, so a coarse texel left out moves the fine copied from it."""
import json
import sys
from pathlib import Path

import numpy as np

LUM = np.array([0.2126, 0.7152, 0.0722])
BAD = 0.5


def u9(w):
    w = w.astype(np.int64)
    sc = np.exp2((w >> 27) - 24.0)
    return np.stack([(w & 511) * sc, ((w >> 9) & 511) * sc, ((w >> 18) & 511) * sc], -1)


def parts(path, irr):
    raw = Path(path).read_bytes()
    n = int(np.frombuffer(raw[:4], np.int32)[0])
    words = np.frombuffer(raw[4:4 + 16 * n], np.uint32)
    sky, sun = words[:2 * n].reshape(n, 2)[:, 0], words[2 * n:4 * n].reshape(n, 2)[:, 0]
    sky_l, sun_l = u9(sky) @ LUM, (u9(sun) * irr[None]) @ LUM
    bits = None
    rest = raw[4 + 16 * n:]
    if len(rest) >= 8:
        stride, column = np.frombuffer(rest[:8], np.int32)
        w = np.frombuffer(rest[8:8 + 4 * n * stride], np.uint32).reshape(n, stride)
        bits = (w[:, column // 32] >> (column % 32)) & 1
    return n, sky_l, sun_l, bits


def provenance(path, n):
    p = Path(str(path) + '.provenance')
    return np.frombuffer(p.read_bytes(), np.uint8)[:n] if p.exists() else None


def run(prefix, view):
    d = sorted(Path(view).glob('view-*'))[-1]
    m = json.loads((d / 'meta.json').read_text())
    irr = np.array(m['sunIrradiance'], float)
    n, _, _, bits_a = parts(f'{prefix}.instant.0', irr)
    _, sky_a, sun_a, _ = parts(f'{prefix}.final.0', irr)
    _, sky_i, sun_i, bits_b = parts(f'{prefix}.instant.1', irr)
    _, sky_b, sun_b, _ = parts(f'{prefix}.final.1', irr)
    t = np.frombuffer((d / 'texels.f32x8').read_bytes(), np.float32).reshape(-1, 8)[:n]
    return dict(n=n, pos=t[:, :3].astype(float), live=np.abs(t[:, 3]) >= 2, a=sky_a + sun_a, inst=sky_i + sun_i, full=sky_b + sun_b,
                flipped=(bits_a != bits_b) if bits_a is not None and bits_b is not None else None,
                prov=provenance(f'{prefix}.final.1', n))


def near_flips(pos, flipped, live, r):
    """Whether a flipped texel lies in the 3 x 3 x 3 r-cells about each texel."""
    cell = np.floor(pos / r).astype(np.int64)
    key = lambda c: (c[:, 0] * 73856093) ^ (c[:, 1] * 19349663) ^ (c[:, 2] * 83492791)
    hot = np.unique(key(cell[flipped & live]))
    out = np.zeros(len(pos), bool)
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            for dz in (-1, 0, 1):
                k = key(cell + np.array([dx, dy, dz]))
                i = np.searchsorted(hot, k)
                out |= (i < len(hot)) & (hot[np.minimum(i, len(hot) - 1)] == k)
    return out


def error(x, ref, floor):
    return np.abs(x - ref) / np.maximum(ref, floor)


def main():
    args = sys.argv[1:]
    if len(args) not in (2, 4):
        raise SystemExit(__doc__)
    r0 = run(args[0], args[1])
    r1 = run(args[2], args[3]) if len(args) == 4 else None
    live = r0['live']
    full = r0['full']
    floor = 0.1 * np.median(full[live & (full > 0)])
    flipped = r0['flipped']
    if flipped is None:
        raise SystemExit('no sun bits in the instant dumps (DumpParts("instant", sunBits: true))')
    # The texels the sun change gathered (sun provenance: 1 sweep, 2 refine, 3 fine, 4 the visible and changed).
    sun_code = r0['prov'] & 15 if r0['prov'] is not None else np.ones(r0['n'], np.uint8)
    gathered = live & np.isin(sun_code, (1, 2, 3, 4))
    print(f'{r0["n"]:,} texels, {live.sum():,} live; the sun change gathered {gathered.sum():,} '
          f'(sweep/refine {np.isin(sun_code, (1, 2)).sum():,}, fine {(sun_code == 3).sum():,}, visible and changed {(sun_code == 4).sum():,}); '
          f'own bit flipped {(flipped & live).sum():,}')
    e_inst = error(r0['inst'], full, floor)
    e_seed = error(r1['full'], full, floor) if r1 is not None else None
    e_old = error(r0['a'], full, floor)
    rel_change = np.abs(r0['inst'] - r0['a']) / np.maximum(r0['a'], floor)
    scope = gathered
    print(f'over the gathered: the old light kept, mean error {e_old[scope].mean():.3f}, bad {(e_old[scope] > BAD).mean():.1%}; '
          f'the instant layer kept {e_inst[scope].mean():.3f}, bad {(e_inst[scope] > BAD).mean():.1%}'
          + (f'; a second full gather {e_seed[scope].mean():.3f}, bad {(e_seed[scope] > BAD).mean():.1%}' if e_seed is not None else ''))
    rules = {'own': flipped & live}
    for r in (0.25, 0.5, 1.0, 2.0, 4.0):
        rules[f'near {r:g} m'] = near_flips(r0['pos'], flipped, live, r)
    for t in (0.05, 0.1, 0.2, 0.4):
        rules[f'instant {t:g}'] = rel_change > t
    for r in (0.5, 1.0):
        for t in (0.1, 0.2):
            rules[f'near {r:g} m | instant {t:g}'] = rules[f'near {r:g} m'] | rules[f'instant {t:g}']
    print(f'\n{"rule":28s} {"gathers":>8s}  {"left out":>9s} {"mean err":>8s} {"bad":>6s} {"energy":>6s}'
          + ('  | 2nd seed there: mean err    bad' if e_seed is not None else ''))
    for name, pick in rules.items():
        take = pick & scope
        out = scope & ~pick
        if out.sum() == 0:
            continue
        energy = r0['inst'][out].sum() / max(full[out].sum(), 1e-12)
        line = (f'{name:28s} {take.sum() / scope.sum():8.1%}  {out.sum():9,d} {e_inst[out].mean():8.3f} {(e_inst[out] > BAD).mean():6.1%} {energy:6.3f}')
        if e_seed is not None:
            line += f'  | {e_seed[out].mean():24.3f} {(e_seed[out] > BAD).mean():6.1%}'
        print(line)


if __name__ == '__main__':
    main()
