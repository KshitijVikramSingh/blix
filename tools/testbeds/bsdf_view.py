# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy", "pillow"]
# ///
"""The material samples' verdict: the frame's light against the BSDF reference's at the reference's points, by material
(uv run --with numpy --with pillow tools/testbeds/bsdf_view.py OUT_DIR scene...).

Reads OUT_DIR/<scene>.log -- a --gpu-reference --ref-bsdf run (tools/testbeds/bsdf.sh) -- whose per-point lines are the gate's
("surface  at X,Y  gpu  total F ref R  material NAME": F the incident light the frame shaded with, R the path trace's irradiance
at the same point, NAME the material of the triangle the point sits on). Prints, per scene, a row for every material with points
and one for the scene: the points, energy (mean F / mean R: light created or lost), the median F / R (the typical point) and
rel |error| (mean |F - R| / mean R: how far each point is). Where OUT_DIR/<scene>.png (the shot) is there, writes
OUT_DIR/<scene>-points.png: the shot with each point marked by F / R (white 1, red brighter, blue darker, full at +-50%).

A point the frame never saw on the reference's face (F < 0) is left out, as gate.py does.
"""
import re
import sys
from collections import defaultdict
from pathlib import Path

LINE = re.compile(r'(surface|seam)\s+at (\S+),(\S+)\s+gpu\s+total (-?[\d.]+) ref (-?[\d.na]+)(?:\s+material (\S+))?')


def points(log):
    out = []
    for line in log.read_text(errors='replace').splitlines():
        m = LINE.search(line)
        if not m:
            continue
        f, r = float(m.group(4)), float(m.group(5))
        if f < 0 or r != r:   # off the reference's face, or no paths
            continue
        out.append((float(m.group(2)), float(m.group(3)), f, r, m.group(6) or '-'))
    return out


def row(name, pts):
    fs = [p[2] for p in pts]
    rs = [p[3] for p in pts]
    mean_r = sum(rs) / len(rs)
    ratios = sorted(f / r for f, r in zip(fs, rs) if r > 1e-6)
    energy = (sum(fs) / len(fs)) / mean_r if mean_r > 0 else float('nan')
    median = ratios[len(ratios) // 2] if ratios else float('nan')
    err = sum(abs(f - r) for f, r in zip(fs, rs)) / len(fs) / mean_r if mean_r > 0 else float('nan')
    return f'    {name[:40]:40s} {len(pts):5d}  energy {energy:7.3f}  median {median:7.3f}  rel |error| {err:7.3f}  (ref mean {mean_r:.4f})'


def mark(shot, pts, out):
    from PIL import Image, ImageDraw
    im = Image.open(shot).convert('RGB')
    d = ImageDraw.Draw(im)
    w, h = im.size
    for x, y, f, r, _ in pts:
        t = max(-1.0, min(1.0, (f / r - 1.0) / 0.5)) if r > 1e-6 else 0.0
        c = (255, int(255 * (1 - t)), int(255 * (1 - t))) if t > 0 else (int(255 * (1 + t)), int(255 * (1 + t)), 255)
        px, py = x * w, y * h
        d.ellipse([px - 4, py - 4, px + 4, py + 4], fill=c, outline=(0, 0, 0))
    im.save(out)


def main():
    out = Path(sys.argv[1])
    scenes = sys.argv[2:] or sorted(p.stem for p in out.glob('*.log'))
    print('bsdf: the frame (RTGI) against the glTF BSDF reference at its points, by the material each sits on')
    for s in scenes:
        log = out / f'{s}.log'
        if not log.exists():
            print(f'  {s}: no log')
            continue
        pts = points(log)
        if not pts:
            print(f'  {s}: no reference samples -- did the run reach its shot? ({log})')
            continue
        print(f'  {s}:')
        by = defaultdict(list)
        for p in pts:
            by[p[4]].append(p)
        for name, group in sorted(by.items(), key=lambda kv: -len(kv[1])):
            print(row(name, group))
        print(row('(all)', pts))
        shot = out / f'{s}.png'
        if shot.exists():
            mark(shot, pts, out / f'{s}-points.png')


if __name__ == '__main__':
    main()
