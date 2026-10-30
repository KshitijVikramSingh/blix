"""Capture report: stats, and the engine's ACES next to GT7 SDR on the same radiance.

    uv run python -m tmlab.report [data_dir] [out_dir]
"""
from __future__ import annotations

import sys
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

from . import blix_curves, capture, metrics, pipeline

DATA = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("data")
OUT = Path(sys.argv[2]) if len(sys.argv) > 2 else Path("out")


def srgb_oetf(x):
    x = np.clip(x, 0.0, 1.0)
    return np.where(x <= 0.0031308, 12.92 * x, 1.055 * np.power(x, 1 / 2.4) - 0.055)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    caps = [capture.load(p).downsampled(4) for p in sorted(DATA.glob("*.pfm"))]
    if not caps:
        sys.exit(f"no .pfm captures in {DATA}")

    fig, axes = plt.subplots(len(caps), 2, figsize=(11, 3.2 * len(caps)), squeeze=False)
    for row, cap in zip(axes, caps):
        s = capture.summary(cap)
        print(f"{s['name']}: {s['size'][0]}x{s['size'][1]} (downsampled), exposure {s['exposure']}, "
              f"luminance median {s['lum_median']:.3f} p99 {s['lum_p99']:.2f} p99.9 {s['lum_p99_9']:.2f} max {s['lum_max']:.2f}, "
              f"{s['range_stops_p1_p99_9']:.1f} stops p1..p99.9, {s['clipped_at_1_pct']:.1f}% of pixels above 1.0, "
              f"non-finite {s['nan_or_inf']}")
        aces = pipeline.blix_curve(cap.rgb, blix_curves.aces, cap.exposure)
        gt7 = pipeline.gt7_sdr(cap.rgb, exposure=cap.exposure)
        d = metrics.delta_oklab(aces, gt7)
        print(f"   Oklab distance engine-ACES vs GT7-SDR: mean {d.mean():.4f}  p95 {np.percentile(d, 95):.4f}  max {d.max():.4f}")
        for ax, img, title in ((row[0], aces, "engine: ACES"), (row[1], gt7, "GT7 SDR (anchor 250 nits)")):
            ax.imshow(srgb_oetf(img)); ax.set_title(f"{cap.name}: {title}", fontsize=9); ax.axis("off")
    fig.tight_layout(); fig.savefig(OUT / "5_capture_compare.png", dpi=110)
    print("wrote", (OUT / "5_capture_compare.png").resolve())


if __name__ == "__main__":
    main()
