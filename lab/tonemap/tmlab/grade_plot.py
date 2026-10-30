"""Visual check of a fitted grade: ungraded GT7 | graded | ACES 2.x target | Oklab distance.  uv run python -m tmlab.grade_plot"""
from __future__ import annotations

import json
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

from . import capture, metrics, pipeline
from .grade import Grade, NAMES, target_aces2
from .exposure import srgb

FRAMES = ("emissive_ramp__dim__z1.7", "night_lamps__night__z1.7", "materials_grid__grazing__z1.7")


def main(which: str = "baseline"):
    g = Grade(**json.loads(Path(f"out/grade_{which}.json").read_text())["grade"])
    fig, axes = plt.subplots(len(FRAMES), 4, figsize=(15, 2.9 * len(FRAMES)))
    for row, name in zip(axes, FRAMES):
        cap = capture.load(Path("data/metering") / f"{name}.pfm")
        rgb = cap.rgb.astype(np.float64) * cap.exposure
        before = pipeline.gt7_sdr(rgb)
        after = pipeline.gt7_sdr(g.apply(rgb))
        tgt = target_aces2(rgb)
        d = metrics.delta_oklab(after, tgt)
        d0 = metrics.delta_oklab(before, tgt)
        for ax, img, title in ((row[0], before, f"GT7 SDR, ungraded (dist {d0.mean():.3f})"), (row[1], after, f"graded (dist {d.mean():.3f})"),
                               (row[2], tgt, "ACES 2.x SDR target")):
            ax.imshow(srgb(img)); ax.set_title(f"{name}\n{title}" if img is before else title, fontsize=8); ax.axis("off")
        im = row[3].imshow(d, cmap="magma", vmin=0, vmax=0.15); row[3].set_title("remaining Oklab distance (graded vs target)", fontsize=8); row[3].axis("off")
    fig.colorbar(im, ax=axes[:, 3], fraction=0.03)
    fig.suptitle(f"grade ({which} GT7 SDR): " + "  ".join(f"{n} {getattr(g, n):.2f}" for n in NAMES), fontsize=10)
    fig.savefig(f"out/10_grade_{which}.png", dpi=110, bbox_inches="tight")
    print(f"wrote out/10_grade_{which}.png")


if __name__ == "__main__":
    main()
