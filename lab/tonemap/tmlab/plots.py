"""Pilot plots: GT7 against Blix's shipped curves.  uv run python -m tmlab.plots [out_dir]

Input convention for the comparison: the same scene-linear numbers go into every operator, 0.18 being
mid grey. GT7 reads them as frame-buffer values (1.0 = 100 nits) and its SDR output is scaled to sRGB
(paper white 250 nits -> 1.0 after the 1/2.5 correction), so GT7-SDR and the Blix curves are on one axis.
"""
from __future__ import annotations

import sys
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

from . import blix_curves, color, pipeline
from .gt7 import GT7ToneMapping

OUT = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("out")


def _grey(x):
    return np.stack([x, x, x], axis=-1)


def ramp_comparison():
    x = np.exp(np.linspace(np.log(1e-3), np.log(1e2), 600))
    sdr = GT7ToneMapping.sdr_display()
    fig, (a, b) = plt.subplots(1, 2, figsize=(12, 4.5))
    for name, fn in (("ACES", blix_curves.aces), ("AgX", blix_curves.agx),
                     ("Reinhard", blix_curves.reinhard), ("neutral", blix_curves.neutral)):
        a.plot(x, fn(x), label=f"Blix {name}", lw=1.4)
        b.plot(x, fn(x), lw=1.4)
    g = pipeline.gt7_sdr(_grey(x), sdr)[:, 1]
    a.plot(x, g, "k", lw=2.2, label="GT7 SDR (anchor 250 nits)")
    b.plot(x, g, "k", lw=2.2)
    a.set_xscale("log"); a.set_xlabel("scene linear (0.18 = mid grey)"); a.set_ylabel("display linear, 0..1")
    a.set_title("Grey ramp, log x"); a.legend(fontsize=8); a.grid(alpha=0.3)
    b.set_xlim(0, 4); b.set_xlabel("scene linear"); b.set_title("Grey ramp, linear x (toe and shoulder)")
    b.grid(alpha=0.3)
    fig.tight_layout(); fig.savefig(OUT / "1_ramp_sdr.png", dpi=130); plt.close(fig)


def hdr_peaks():
    x = np.exp(np.linspace(np.log(1e-3), np.log(5e2), 600))
    fig, (a, b) = plt.subplots(1, 2, figsize=(12, 4.5))
    for peak in (250, 600, 1000, 4000, 10000):
        tm = GT7ToneMapping.hdr_display(peak)
        y = tm(_grey(x))[:, 1] * 100.0  # nits
        a.plot(x, y, label=f"{peak} nits")
        b.plot(x, y, label=f"{peak} nits")
    a.set_xscale("log"); a.set_yscale("log"); a.set_ylim(1e-2, 1.2e4)
    a.set_xlabel("scene linear (1.0 = 100 nits)"); a.set_ylabel("display luminance, nits")
    a.set_title("GT7 HDR: one operator, variable peak"); a.legend(); a.grid(alpha=0.3, which="both")
    b.set_xlim(0, 120); b.set_ylim(0, 1.1e3 )
    b.set_xlabel("scene linear"); b.set_title("Zoom: the same curves, linear axes"); b.grid(alpha=0.3)
    fig.tight_layout(); fig.savefig(OUT / "2_hdr_peaks.png", dpi=130); plt.close(fig)


def hue_shift():
    """Output hue angle (ICtCp Ct/Cp plane) against exposure for saturated colours.

    A flat line is hue preserved; Blix's per-channel curves drift as channels clip at different rates.
    Blix curves act on Rec.709 data; both are measured in Rec.2020 so the angles are comparable.
    """
    base709 = {"red": (1.0, 0.08, 0.04), "orange": (1.0, 0.35, 0.03), "green": (0.1, 1.0, 0.1),
               "cyan": (0.05, 0.6, 1.0), "blue": (0.04, 0.08, 1.0), "magenta": (1.0, 0.1, 0.8)}
    stops = np.linspace(-6, 6, 97)
    scale = 0.18 * 2.0 ** stops
    sdr = GT7ToneMapping.sdr_display()
    hdr = GT7ToneMapping.hdr_display(1000.0)

    def hue(rgb2020):
        # Hue angle is meaningless at near-zero chroma (GT7 fades chroma to white at the peak on purpose),
        # so those points are left blank rather than plotted as spikes.
        i = color.rgb2020_to_ictcp(np.maximum(rgb2020, 1e-9))
        angle = np.degrees(np.arctan2(i[..., 2], i[..., 1]))
        return np.where(np.hypot(i[..., 1], i[..., 2]) < 0.02, np.nan, angle)

    fig, axes = plt.subplots(2, 3, figsize=(13, 7), sharex=True)
    for ax, (name, c) in zip(axes.ravel(), base709.items()):
        c709 = np.array(c) * scale[:, None]
        c2020 = color.rec709_to_rec2020(c709)
        ref = hue(c2020)
        ax.axhline(0, color="0.7", lw=0.8)
        for lab, fn in (("Blix ACES", blix_curves.aces), ("Blix AgX", blix_curves.agx)):
            out = color.rec709_to_rec2020(fn(c709))
            ax.plot(stops, (hue(out) - ref + 180) % 360 - 180, label=lab, lw=1.3)
        for lab, tm, st in (("GT7 SDR", sdr, "k-"), ("GT7 HDR 1000", hdr, "k--")):
            ax.plot(stops, (hue(tm(pipeline.to_gt7_input(c709))) - ref + 180) % 360 - 180, st, lw=1.8, label=lab)
        ax.set_title(name); ax.grid(alpha=0.3)
    for ax in axes[1]:
        ax.set_xlabel("exposure (stops from mid grey)")
    for ax in axes[:, 0]:
        ax.set_ylabel("hue shift, degrees")
    axes[0, 0].legend(fontsize=8)
    fig.suptitle("Hue shift of saturated colours vs exposure (output minus input, ICtCp; blank = chroma < 0.02)")
    fig.tight_layout(); fig.savefig(OUT / "3_hue_shift.png", dpi=130); plt.close(fig)


def anchor_sensitivity():
    """What the exposure anchor does: GT7 SDR's grey ramp for several anchors, against Blix's ACES and neutral."""
    from .pipeline import SceneConvention
    x = np.exp(np.linspace(np.log(1e-3), np.log(1e2), 600))
    fig, (a, b) = plt.subplots(1, 2, figsize=(12, 4.5))
    for ax in (a, b):
        ax.plot(x, blix_curves.aces(x), "0.55", lw=1.4, label="Blix ACES")
        ax.plot(x, blix_curves.neutral(x), "0.75", lw=1.2, ls=":", label="Blix neutral (linear clamp)")
        for anchor in (100, 203, 250, 400):
            ax.plot(x, pipeline.gt7_sdr(_grey(x), conv=SceneConvention(anchor))[:, 1], label=f"GT7 anchor {anchor} nits")
    aces_anchor = pipeline.matching_anchor(blix_curves.aces)
    b.plot(x, pipeline.gt7_sdr(_grey(x), conv=SceneConvention(aces_anchor))[:, 1], "k--", lw=1.8,
           label=f"GT7 anchor {aces_anchor:.0f} (matches ACES at 0.18)")
    a.set_xscale("log"); b.set_xlim(0, 3)
    a.set_title("Exposure anchor: what scene 1.0 is, in nits"); b.set_title("Linear x, with the ACES-matched anchor")
    for ax in (a, b):
        ax.set_xlabel("scene linear (0.18 = mid grey)"); ax.set_ylabel("display linear, 0..1"); ax.grid(alpha=0.3)
    a.legend(fontsize=8); b.legend(fontsize=8)
    fig.tight_layout(); fig.savefig(OUT / "4_anchor_sensitivity.png", dpi=130); plt.close(fig)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    ramp_comparison(); hdr_peaks(); hue_shift(); anchor_sensitivity()
    print("wrote", *sorted(p.name for p in OUT.glob("*.png")), "to", OUT.resolve())


if __name__ == "__main__":
    main()
