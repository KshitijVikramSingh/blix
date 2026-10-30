"""Figure for the exposure result: predicted against proxy-target exposure, per frame.  uv run python -m tmlab.exposure_plot"""
from __future__ import annotations

import json
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

from . import exposure as ex

COLORS = {"emissive_ramp": "tab:orange", "materials_grid": "tab:blue", "night_lamps": "tab:purple"}


def main():
    frames = ex.load_frames(Path("data/metering"), None, Path("out/exposure_targets.json"))
    e = np.array([f[3] for f in frames])
    scenes = np.array([f[1] for f in frames])
    gm = ex.baseline_predictions([f[2] for f in frames], "geometric_mean", None)
    fitted = json.loads(Path("out/exposure_fit.json").read_text())
    ours = np.array([fitted["frames"][f[0]]["ours"] for f in frames])

    fig, axes = plt.subplots(1, 2, figsize=(11, 4.8), sharex=True, sharey=True)
    for ax, pred, title in ((axes[0], gm, f"textbook geometric mean, key 0.18 (RMS {ex.rms(gm, e):.2f} stops)"),
                            (axes[1], ours, f"fitted model, IN-SAMPLE (RMS {ex.rms(ours, e):.2f}; held-out 2.9, see text)")):
        for s, c in COLORS.items():
            m = scenes == s
            ax.scatter(e[m], pred[m], s=22, color=c, label=s, alpha=0.85)
        lim = (-7, 5)
        ax.plot(lim, lim, color="0.5", lw=1)
        ax.fill_between(lim, [l - 1 for l in lim], [l + 1 for l in lim], color="0.5", alpha=0.12, label="within 1 stop")
        ax.set_xlim(lim); ax.set_ylim(lim); ax.set_title(title, fontsize=10); ax.grid(alpha=0.3)
        ax.set_xlabel("proxy-target log2 exposure E*")
    axes[0].set_ylabel("predicted log2 exposure"); axes[0].legend(fontsize=8, loc="upper left")
    fig.tight_layout(); fig.savefig("out/9_exposure.png", dpi=130)
    print("wrote out/9_exposure.png")


if __name__ == "__main__":
    main()
