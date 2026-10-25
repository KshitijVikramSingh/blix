# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""A light change's error over time, end to end in the engine (uv run tools/gi-model/change_timeline.py TRUTH0 TRUTH1
RUN [RUN ...]): each RUN a sun change with BLIX_DUMP_PARTS and BLIX_DUMP_CHANGE_TIMES (its parts.t<s>.0 and
parts.landed.0), TRUTHn boots at the new sun (two seeds: the truth their mean; directional_ratio.py's B runs, their
view the same camera); --filtered: the composed, filtered light (each dump's .resolved) the image reads. The error as patch_layers.py's: relative, the screen's pixel-weighted over the live fine texels
(the view's texel per pixel), and the scene's over every live fine texel."""
import re
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
import directional_ratio as dr  # noqa: E402


def main():
    t0, t1 = dr.run(sys.argv[1]), dr.run(sys.argv[2])
    irr = dr.irradiance(t0['meta'])
    n = len(t0['sky'])
    view = t0['view']
    first = int(t0['meta']['lightmapFirstRecord'])
    prov = np.frombuffer((Path(sys.argv[1]) / 'parts.final.0.provenance').read_bytes(), np.uint8)[:n] & 15
    fine = (np.arange(n) >= first) & ((prov == 3) | (prov == 5))
    lt = np.frombuffer((view / 'lightmap_texel.r32u').read_bytes(), np.uint32)
    lt = lt[lt != 0]
    aw, ah = t0['meta']['lightmapAtlas']
    index = np.frombuffer((view / 'lightmap_index.r32u').read_bytes(), np.uint32)
    pix = index[np.minimum((lt >> 16) >> 3, ah - 1).astype(np.int64) * aw + np.minimum((lt & 0xFFFF) >> 3, aw - 1)]
    pixels = np.bincount(pix[pix < n].astype(np.int64), minlength=n).astype(np.float64)
    T = 0.5 * ((t0['sky'] + irr * t0['sun']) + (t1['sky'] + irr * t1['sun']))
    floor = 0.1 * np.median(T[fine & (T > 0)])
    filtered = '--filtered' in sys.argv
    def resolved(path):
        path = Path(path)
        if path.name == 'parts.shot':   # DumpShotState: the count, then the resolved's length and words
            w = np.frombuffer(path.read_bytes()[8:8 + 8 * n], np.uint32).reshape(-1, 2)[:, 0]
        else:
            w = np.frombuffer(Path(str(path) + '.resolved').read_bytes(), np.uint32).reshape(-1, 2)[:n, 0]
        return dr.u9(w) @ dr.LUM
    if filtered:
        # The light the image reads: composed and filtered (texel_compose.comp's resolved), against the truths' own at
        # their shots (the landing's dumps were written before its last compose).
        T = 0.5 * (resolved(Path(sys.argv[1]) / 'parts.shot') + resolved(Path(sys.argv[2]) / 'parts.shot'))
        floor = 0.1 * np.median(T[fine & (T > 0)])
    def err(path):
        if filtered: e = np.abs(resolved(path) - T) / np.maximum(T, floor)
        else:
            sky, sun = dr.parts(path)
            e = np.abs(sky + irr * sun - T) / np.maximum(T, floor)
        return (e * pixels)[fine].sum() / pixels[fine].sum(), e[fine].mean()
    print(f'truth: {Path(sys.argv[1]).name} + {Path(sys.argv[2]).name}; {int(pixels.sum()):,} pixels; a truth seed against their mean: '
          f'screen {err(Path(sys.argv[1]) / ("parts.shot" if filtered else "parts.final.0"))[0]:.4f}')
    for run in [r for r in sys.argv[3:] if not r.startswith('--')]:
        d = Path(run)
        stages = sorted((p for p in d.glob('parts.t*.0') if re.fullmatch(r'parts\.t[\d.]+\.0', p.name)), key=lambda p: float(p.name[7:-2]))
        line = []
        for p in stages:
            s, sc = err(p)
            line.append(f'{p.name[7:-2]}s {s:.3f}')
        s, sc = err(d / 'parts.shot') if filtered else err(d / 'parts.landed.1') if (d / 'parts.landed.1').exists() else err(d / 'parts.landed.0')
        took = [l for l in (d / 'run.log').read_text().splitlines() if 'light change took' in l]
        print(f'  {d.name:12s} screen ' + ' '.join(line) + f' | landed {s:.3f} (scene {sc:.3f}){" -- " + took[-1].split("took ")[1] if took else ""}')


if __name__ == '__main__':
    main()
