#!/usr/bin/env python3
"""The determinism check's verdict (tools/testbeds/determinism.sh): each pair of shots held against each other.

usage: determinism.py OUT_DIR NAME...   (reads OUT_DIR/NAME-a.png and OUT_DIR/NAME-b.png)

The image at rest must match byte for byte. A pair that differs prints how many pixels and by how much. A shot is the
scene as --shot writes it (linear x exposure, sRGB encoded). Until 2026-10-30 the shot's path held the ambient-occlusion
buffer instead, the scene beside it as NAME.scene.png, so this compared a buffer that was white whenever that pass was
off -- every pair "identical" whatever the light did.
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image


def main():
    out, names = Path(sys.argv[1]), sys.argv[2:]
    failed = False
    for name in names:
        a, b = out / f"{name}-a.png", out / f"{name}-b.png"
        if not a.exists() or not b.exists():
            print(f"FAIL {name}: a shot is missing"); failed = True; continue
        if a.read_bytes() == b.read_bytes():
            print(f"ok   {name}: identical")
            continue
        failed = True
        pa = np.asarray(Image.open(a).convert("RGB"), np.float64)
        pb = np.asarray(Image.open(b).convert("RGB"), np.float64)
        if pa.shape != pb.shape:
            print(f"FAIL {name}: the shots differ in size ({pa.shape[1]}x{pa.shape[0]}, {pb.shape[1]}x{pb.shape[0]})"); continue
        d = np.abs(pa - pb).max(axis=2)
        print(f"FAIL {name}: {int((d > 0).sum()):,} of {d.size:,} pixels differ; max {d.max():.0f}, mean over those {d[d > 0].mean():.2f} (of 255)")
    print("determinism: every pair matches." if not failed else "determinism: FAILED.")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
