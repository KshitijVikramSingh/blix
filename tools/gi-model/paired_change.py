# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""A sun change estimated as a change: the old landed light plus the same rays' difference between the new sun and the
old (uv run tools/gi-model/paired_change.py OLD TRUTH0 TRUTH1 P_OLD P_NEW [P_OLD P_NEW ...]).

Each round today estimates a texel's whole light again; its noise falls as 1 / sqrt(rays). The light did not change
whole -- the change did. Traced once and shaded for both suns, a ray's two answers move together, so their difference
is far quieter than either: est = L_old + mean(Y_new - Y_old) over k rays (a control variate with the old sun's gather).
P_OLD, P_NEW: gathers of k rays a texel at the old sun and the new, cosine rays alone (no guides, no light strategy), no
refine, no near round, one seed -- the same rays at both suns (texel_nodes_rays.comp seeds by texel, pass, ray). OLD:
the old sun's landed light (today's full gather, another seed). TRUTH0, TRUTH1: boots at the new sun, two seeds; the
judge is TRUTH1 (independent of every other run), TRUTH0's error against it the floor (a fresh full gather).
Texels judged: those both P runs wrote the same way (the coarse gathered, the fine picks, the fine copies: provenance
equal), and the screen's (the view of TRUTH0's dump: its texel per pixel).

Crytek Sponza, sun A (40, -50), seeds 0 (old) / 7000 (P) / 0 and 1000 (truth), 2026-10-25 (16 rays ran as 32: a pass is
32 rays). Screen error at 32 / 64 rays -- floor (fresh full gather), old light kept, the k-ray gather at B, paired (sun
part alone):
  5 deg   0.105 / 0.112 / 0.345 0.229 / 0.138 0.126   -- paired worse than keeping the old light
  20 deg  0.122 / 0.229 / 0.390 0.262 / 0.225 0.181
  60 deg  0.116 / 0.443 / 0.351 0.237 / 0.291 0.217
The pairing helps over the same rays' fresh estimate but stays far from the floor: a ray's two answers barely move
together -- its change is a hit's sun turning on or off (shadows move under the hit), the same rays' variance again.
At small steps the old light alone is at the floor."""
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
import directional_ratio as dr  # noqa: E402


def light(d):
    sky, sun = dr.parts(Path(d) / 'parts.final.0')
    return sky, sun


def prov(d, n):
    return np.frombuffer((Path(d) / 'parts.final.0.provenance').read_bytes(), np.uint8)[:n] & 15


def main():
    if len(sys.argv) < 6 or (len(sys.argv) - 4) % 2:
        raise SystemExit(__doc__)
    t0 = dr.run(sys.argv[2])
    irr = dr.irradiance(t0['meta'])
    n = len(t0['sky'])
    view = t0['view']
    lt = np.frombuffer((view / 'lightmap_texel.r32u').read_bytes(), np.uint32)
    lt = lt[lt != 0]
    aw, ah = t0['meta']['lightmapAtlas']
    index = np.frombuffer((view / 'lightmap_index.r32u').read_bytes(), np.uint32)
    pix = index[np.minimum((lt >> 16) >> 3, ah - 1).astype(np.int64) * aw + np.minimum((lt & 0xFFFF) >> 3, aw - 1)]
    pixels = np.bincount(pix[pix < n].astype(np.int64), minlength=n).astype(np.float64)
    oSky, oSun = light(sys.argv[1])
    t1Sky, t1Sun = light(sys.argv[3])
    t0Sky, t0Sun = light(sys.argv[2])
    truth = t1Sky + irr * t1Sun
    p_t0 = prov(sys.argv[2], n)
    floor = 0.1 * np.median(truth[(p_t0 > 0) & (truth > 0)])
    old = oSky + irr * oSun
    def errs(est, mask):
        e = np.abs(est - truth) / np.maximum(truth, floor)
        scr = (e * pixels)[mask].sum() / max(pixels[mask].sum(), 1e-9)
        return f'scene {e[mask].mean():.4f}, screen {scr:.4f}'
    toA = None
    for pair in range(4, len(sys.argv), 2):
        pa, pb = sys.argv[pair], sys.argv[pair + 1]
        aSky, aSun = light(pa)
        bSky, bSun = light(pb)
        same = (prov(pa, n) == prov(pb, n)) & (prov(pa, n) > 0) & (p_t0 > 0)
        ya, yb = aSky + irr * aSun, bSky + irr * bSun
        paired = old + (yb - ya)
        # The sky's part does not change with the sun: the paired estimate's sky the old's (its own ray noise cancels).
        paired_sun = oSky + irr * (oSun + (bSun - aSun))
        print(f'== {Path(pa).name} -> {Path(pb).name}: {same.sum():,} texels written alike ({(pixels[same] > 0).sum():,} on screen)')
        print(f'  a fresh full gather (floor)    {errs(t0Sky + irr * t0Sun, same)}')
        print(f'  the old light kept             {errs(old, same)}')
        print(f'  the k-ray gather at the new    {errs(yb, same)}')
        print(f'  paired: old + (new - old)      {errs(paired, same)}')
        print(f'  paired, the sun part alone     {errs(paired_sun, same)}')


if __name__ == '__main__':
    main()
