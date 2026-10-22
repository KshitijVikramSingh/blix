#!/usr/bin/env python3
"""The determinism check's verdict (tools/testbeds/determinism.sh): each pair of view dumps held against each other.

usage: determinism.py OUT_DIR NAME...   (reads OUT_DIR/NAME-a/view-*/ and OUT_DIR/NAME-b/view-*/)

The GI's state at rest must match byte for byte: the texel records, each texel's resolved light and its sun's and sky's
parts, how many rays it took and where its light came from, the patches' live light -- and the lit image made of them.
guide_patch is left out: a later stage writes it after the light rests, and the dump's frame is when the cook landed plus
a margin, which the background cook's finish moves. A pair that differs prints how many texels and by how much.
"""
import json
import sys
from pathlib import Path

import numpy as np

COMPARED = ["texels.f32x8", "resolved.u32x2", "sun.u32x2", "sky.u32x2", "rays.u16", "provenance.u8", "patch_light.u32x2",
            "hdr.r11g11b10f"]


def light(d, n):
    w = np.frombuffer((d / "resolved.u32x2").read_bytes(), np.uint32).reshape(n, 2)[:, 0].astype(np.uint64)
    sc = np.exp2((w >> 27).astype(np.int64) - 24)
    return (w & 511) * sc * 0.2126 + ((w >> 9) & 511) * sc * 0.7152 + ((w >> 18) & 511) * sc * 0.0722


def main():
    out, names = Path(sys.argv[1]), sys.argv[2:]
    failed = False
    for name in names:
        a = sorted((out / f"{name}-a").glob("view-*")); b = sorted((out / f"{name}-b").glob("view-*"))
        if not a or not b:
            print(f"FAIL {name}: a dump is missing"); failed = True; continue
        a, b = a[-1], b[-1]
        differ = [f for f in COMPARED if (a / f).exists() and (a / f).read_bytes() != (b / f).read_bytes()]
        if not differ:
            print(f"ok   {name}: identical ({', '.join(f for f in COMPARED if (a / f).exists())})")
            continue
        failed = True
        n = json.loads((a / "meta.json").read_text())["texelCount"]
        ga, gb = light(a, n), light(b, n)
        d = ga != gb
        r = np.abs(ga[d] - gb[d]) / np.maximum((ga[d] + gb[d]) / 2, 1e-9) if d.any() else np.zeros(1)
        print(f"FAIL {name}: {', '.join(differ)} differ; {d.sum():,} texels' light, |diff| median {np.median(r):.4f}, max {r.max():.3f}")
    print("determinism: every pair matches." if not failed else "determinism: FAILED.")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
