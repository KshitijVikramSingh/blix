"""What the adaptation looks like: one frame through GT7 SDR at the exposure each model gives over time.

    uv run python -m tmlab.adaptation_filmstrip

The scene level is the frame's own metered level plus the scenario's change in EV (the radiance is scaled by that gain),
and each model's exposure is 2^(KEY - S): at rest that puts the metered level at mid grey, so any difference between the
rows is the model, not the picture. Top row: two-stage adaptation. Bottom row: a plain smoother, which is "every frame
properly exposed, one second late".
"""
from __future__ import annotations

from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

from . import adaptation as ad
from . import capture, exposure, pipeline

FRAME = "materials_grid__bright__z1.7"


def frames_at(rgb, m0, gain, S, idx):
    out = []
    for i in idx:
        out.append(pipeline.gt7_sdr(rgb * 2.0 ** gain[i], exposure=2.0 ** (ad.KEY - S[i])))
    return out


def strip(ax_rows, rgb, m0, trace_two, trace_one, times, t_unit, labels):
    t, m, S2, _ = trace_two
    _, _, S1, _ = trace_one
    gain = m - m[0]
    shift = m0 - m[0]                                # the traces start at level 0; move them to this frame's own level
    S2a, S1a, ma = S2 + shift, S1 + shift, m + shift
    idx = [int(np.argmin(np.abs(t - x))) for x in times]
    for row, S, title in ((ax_rows[0], S2a, "two-stage adaptation"), (ax_rows[1], S1a, "plain smoothing")):
        imgs = frames_at(rgb, m0, gain, S, idx)
        for k, (ax, img) in enumerate(zip(row, imgs)):
            ax.imshow(np.clip(exposure.srgb(img), 0, 1)); ax.axis("off")
            ax.set_title(f"{labels[k]}\nerror {ma[idx[k]] - S[idx[k]]:+.1f} EV", fontsize=8)
        row[0].text(-0.02, 0.5, title, transform=row[0].transAxes, rotation=90, va="center", ha="right", fontsize=9)


def main():
    cap = capture.load(Path("data/metering") / f"{FRAME}.pfm")
    rgb = cap.rgb[::2, ::2].astype(np.float64)
    bases = ad.load_bases(Path("data/metering"))
    m0 = ad.meter_ev(exposure.log_histogram(cap.rgb), ad.TUNED.trim_hi)
    _, two = ad.run(ad.TUNED, bases)
    _, one = ad.run(ad.smoothing_only(ad.TUNED), bases)

    fig, axes = plt.subplots(2, 8, figsize=(18, 4.4))
    times = [9.9, 10.1, 11.0, 13.0, 17.9, 18.1, 19.0, 22.0]
    labels = ["outdoors", "tunnel entry", "+1 s", "+3 s", "end of tunnel", "exit", "+1 s", "+4 s"]
    strip(axes, rgb, m0, two["tunnel"], one["tunnel"], times, "s", labels)
    fig.suptitle("Tunnel: 8 EV dark for 8 s. The two-stage view stays dark inside and blows out briefly on exit; the plain smoother is always 'properly' exposed, a beat late", fontsize=10)
    fig.tight_layout(); fig.savefig("out/12_tunnel_filmstrip.png", dpi=110)

    fig, axes = plt.subplots(2, 7, figsize=(16, 4.4))
    times = [0.0, 10 * 60, 20 * 60, 30 * 60, 33 * 60, 36 * 60, 50 * 60]
    labels = ["day", "10 min", "20 min", "30 min (darkest)", "33 min", "36 min", "50 min"]
    strip(axes, rgb, m0, two["dusk"], one["dusk"], times, "min", labels)
    fig.suptitle("Dusk: 14 EV over 30 min. The two-stage view is darker than proper while adaptation lags, then catches up; the plain smoother never looks dark", fontsize=10)
    fig.tight_layout(); fig.savefig("out/13_dusk_filmstrip.png", dpi=110)
    print("wrote out/12_tunnel_filmstrip.png out/13_dusk_filmstrip.png")


if __name__ == "__main__":
    main()
