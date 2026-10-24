"""Traces of the adaptation model against a plain smoother, one panel per scenario.  uv run python -m tmlab.adaptation_plot"""
from __future__ import annotations

from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

from . import adaptation as ad


def main():
    bases = ad.load_bases(Path("data/metering"))
    _, tr = ad.run(ad.TUNED, bases)
    _, tn = ad.run(ad.smoothing_only(ad.TUNED), bases)
    panels = [("tunnel", (8, 30), "tunnel: 8 EV dark, 8 s"), ("cloud", (8, 30), "cloud: 5 EV for 6 s"),
              ("snow", (0, 120), "snow: +6 EV field"), ("dusk", None, "dusk: 14 EV over 30 min"),
              ("dawn", None, "dawn: the same, reversed"), ("sign", (4, 24), "retroreflective sign flashes"),
              ("cuts", (15, 45), "camera cuts every 4.5 s")]
    fig, axes = plt.subplots(2, 4, figsize=(17, 7))
    for ax, (name, xlim, title) in zip(axes.ravel(), panels):
        t, m, S, A = tr[name]
        tt, mm, SS, AA = tn[name]
        scale = 60.0 if name in ("dusk", "dawn") else 1.0
        unit = "minutes" if scale > 1 else "seconds"
        if name == "sign":
            ax.plot(t, m, color="0.7", lw=1.0, label="metered level (with signs)")
            ax.plot(t, S, color="tab:blue", lw=1.8, label="exposure level, signs present")
            ax.plot(t, A, color="k", lw=1.0, ls=":", label="same, no signs")
            ax.set_ylim(np.min(A) - 0.3, np.max(m) + 0.3)
        else:
            ax.plot(t / scale, m, color="0.7", lw=1.6, label="scene level m")
            ax.plot(tt / scale, SS, color="tab:red", lw=1.2, ls="--", label="smoothing only")
            ax.plot(t / scale, S, color="tab:blue", lw=1.8, label="short-term S")
            if name not in ("cuts",):
                ax.plot(t / scale, A, color="tab:orange", lw=1.4, label="long-term A")
        ax.set_title(title, fontsize=10); ax.set_xlabel(unit if name != "sign" else "seconds"); ax.grid(alpha=0.3)
        if xlim:
            ax.set_xlim(*xlim)
        ax.set_ylabel("log2 luminance (EV)")
    ax = axes.ravel()[-1]; ax.axis("off")
    h, l = axes.ravel()[0].get_legend_handles_labels()
    ax.legend(h, l, loc="center", fontsize=10, title="legend (red dashed: a plain smoother)")
    fig.suptitle("Two-stage adaptation (tuned) against exponential smoothing alone: the exposure level is -S, so a level that lags the scene is an exposure that lags it", fontsize=11)
    fig.tight_layout(); fig.savefig("out/11_adaptation.png", dpi=125)
    print("wrote out/11_adaptation.png")


if __name__ == "__main__":
    main()
