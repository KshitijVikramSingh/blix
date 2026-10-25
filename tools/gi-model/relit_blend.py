# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""A sun change's gather blended with the relit light instead of replacing it (uv run tools/gi-model/relit_blend.py A
B_SEED0 B_SEED1 [...pairs]; directional_ratio.py's inputs).

patch_layers.py: a texel's one gather is noisier than its relit light where the relit light was close, so a first pass
that replaces plateaus. Blend by inverse variance: est = (r / vr + g / vg) / (1 / vr + 1 / vg), r the relit light
(scalar ratio), g the gather (B seed 0), judged on B seed 1. The runtime knows g's noise from its own rays (here its
patch's mean |B0 - B1| / sqrt(2), relative) and has a sign of r's error: how far the relit instant moved the texel's
light (texel_ratio.comp's move m). Its error modelled sd_r = (c x m + d) x the light; c, d searched.

Crytek Sponza, 5 / 20 / 60 deg (2026-10-25), the selected (moved > 0.1) after one gather: the gather 0.211 / 0.183 /
0.180, blended at each step's best c, d 0.196 / 0.177 / 0.164 -- the best c, d differ per step, and one c, d (1, 0.2)
leaves 20 deg worse (0.185). Under 10% at one gather, less as the refine cuts the gather's noise: not built."""
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
import directional_ratio as dr  # noqa: E402


def main():
    a = dr.run(sys.argv[1])
    irr = dr.irradiance(a['meta'])
    n = len(a['sky'])
    view = a['view']
    patch = np.frombuffer((view / 'patch.i32').read_bytes(), np.int32)[:n].astype(np.int64)
    for pair in range(2, len(sys.argv), 2):
        b0, b1 = dr.run(sys.argv[pair]), dr.run(sys.argv[pair + 1])
        prov = np.frombuffer((Path(sys.argv[pair]) / 'parts.final.0.provenance').read_bytes(), np.uint8)[:n] & 15
        first = int(b0['meta']['lightmapFirstRecord'])
        unit = (np.arange(n) < first) | (prov == 3)
        IA, IB = a['oct'].sum(1), b0['oct'].sum(1)
        R = np.where(IA > 1e-6, np.clip(IB / np.maximum(IA, 1e-30), 0, 16), 1.0)
        landed = a['sky'] + irr * a['sun']
        r = a['sky'] + irr * R * a['sun']
        g = b0['sky'] + irr * b0['sun']
        t = b1['sky'] + irr * b1['sun']
        floor = 0.1 * np.median(t[unit & (t > 0)])
        m = np.abs(r - landed) / np.maximum(landed, floor)
        rel_noise = np.abs(g - t) / np.sqrt(2) / np.maximum(t, floor)
        ok = unit & (patch >= 0)
        pk = patch[ok]
        pn = np.bincount(pk, rel_noise[ok]) / np.maximum(np.bincount(pk), 1)
        sg = np.full(n, np.median(rel_noise[ok])); sg[ok] = pn[pk]
        sg = np.maximum(sg, 1e-3) * np.maximum(g, floor)
        err = lambda x, mask: (np.abs(x - t) / np.maximum(t, floor))[mask].mean()
        sel = unit & (m > 0.1)
        print(f'== {Path(sys.argv[pair]).name}: units {unit.sum():,}, the select\'s (moved > 0.1) {sel.sum():,}')
        for name, mask in (('every unit', unit), ('the selected', sel)):
            print(f'  {name}: relit {err(r, mask):.4f}, a gather {err(g, mask):.4f}')
        best = None
        for c in (0.5, 1.0, 2.0, 4.0):
            for d in (0.05, 0.1, 0.2, 0.4):
                sr = (c * m + d) * np.maximum(landed, floor)
                w = 1 / sr ** 2 / (1 / sr ** 2 + 1 / sg ** 2)
                est = w * r + (1 - w) * g
                e_all, e_sel = err(est, unit), err(est, sel)
                if best is None or e_all < best[0]: best = (e_all, e_sel, c, d)
                print(f'    blend c {c:4.2f} d {d:4.2f}: every unit {e_all:.4f}, the selected {e_sel:.4f}')
        print(f'  best: c {best[2]} d {best[3]}: every unit {best[0]:.4f}, the selected {best[1]:.4f}')


if __name__ == '__main__':
    main()
