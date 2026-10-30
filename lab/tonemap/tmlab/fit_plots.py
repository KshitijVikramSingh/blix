"""Figures for the SDR fit: grey ramps, and hue shift against exposure.  uv run python -m tmlab.fit_plots"""
from __future__ import annotations

from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

from . import blix_curves, pipeline
from .fit import NAMES, SdrObjective, _HUES709, display_to_fb2020, fit, hue_chroma, wrap
from .gt7 import GT7Params, GT7ToneMapping

OUT = Path("out")
HUE_NAMES = ("red", "orange", "green", "cyan", "blue", "magenta")


def _grey(x):
    return np.stack([x, x, x], axis=-1)


def main():
    OUT.mkdir(exist_ok=True)
    variants = {"GT7 baseline": GT7Params()}
    for wc in (1.0, 4.0):
        params, _, _ = fit(8, 0, SdrObjective(weights={"chroma": wc}))
        variants[f"fit, chroma weight {wc:g}"] = params
    styles = {"GT7 baseline": ("k", "-", 2.2), "fit, chroma weight 1": ("tab:red", "-", 1.8),
              "fit, chroma weight 4": ("tab:blue", "-", 1.8)}

    # --- grey ramps ---------------------------------------------------------------------------
    x = np.exp(np.linspace(np.log(1e-3), np.log(1e2), 600))
    fig, (a, b) = plt.subplots(1, 2, figsize=(12, 4.5))
    for ax in (a, b):
        ax.plot(x, blix_curves.aces(x), color="0.55", lw=1.6, ls="--", label="Blix ACES (the roll-off target)")
        for name, p in variants.items():
            c, ls, lw = styles[name]
            ax.plot(x, pipeline.gt7_sdr(_grey(x), GT7ToneMapping.sdr_display(p))[:, 1], color=c, ls=ls, lw=lw, label=name)
        ax.grid(alpha=0.3); ax.set_xlabel("scene linear (0.18 = mid grey)"); ax.set_ylabel("display linear, 0..1")
    a.set_xscale("log"); a.set_title("Grey ramp, log x"); a.legend(fontsize=8)
    b.set_xlim(0, 6); b.set_ylim(0.5, 1.02); b.set_title("Shoulder, linear x (zoomed)")
    fig.tight_layout(); fig.savefig(OUT / "6a_fit_grey.png", dpi=130); plt.close(fig)

    # --- hue shift ----------------------------------------------------------------------------
    stops = np.linspace(-5, 9, 113)
    fig, axes = plt.subplots(2, 3, figsize=(13, 7), sharex=True)
    for ax, hue709, name in zip(axes.ravel(), _HUES709, HUE_NAMES):
        scene = hue709[None, :] * (0.18 * 2.0 ** stops)[:, None]
        in_hue, _ = hue_chroma(pipeline.to_gt7_input(scene))
        ax.axhline(0, color="0.8", lw=0.8)
        aces_out = display_to_fb2020(pipeline.blix_curve(scene, blix_curves.aces))
        h, c = hue_chroma(aces_out)
        ax.plot(stops, np.where(c < 0.02, np.nan, wrap(h - in_hue)), color="0.55", ls="--", lw=1.5, label="Blix ACES")
        for vname, p in variants.items():
            col, ls, lw = styles[vname]
            out = display_to_fb2020(pipeline.gt7_sdr(scene, GT7ToneMapping.sdr_display(p)))
            h, c = hue_chroma(out)
            ax.plot(stops, np.where(c < 0.02, np.nan, wrap(h - in_hue)), color=col, ls=ls, lw=lw, label=vname)
        ax.set_title(name); ax.grid(alpha=0.3)
    for ax in axes[1]:
        ax.set_xlabel("exposure (stops from mid grey)")
    for ax in axes[:, 0]:
        ax.set_ylabel("hue shift, degrees")
    axes[0, 0].legend(fontsize=7)
    fig.suptitle("Hue shift vs exposure, SDR (blank = output chroma < 0.02)")
    fig.tight_layout(); fig.savefig(OUT / "6b_fit_hue.png", dpi=130); plt.close(fig)

    print("wrote 6a_fit_grey.png 6b_fit_hue.png to", OUT.resolve())
    for vname, p in variants.items():
        print(f"{vname:24s}", "  ".join(f"{n}={getattr(p, n):.3f}" for n in NAMES))


if __name__ == "__main__":
    main()
