"""Side-by-side comparison sheets: baseline GT7 | tuned | ACES 2.x | where they differ.

    uv run python -m tmlab.compare [--peak 1000] [--fit out/fit_hdr.json] [--label "8 params, floor 0.15"]

One row per scene. The last column is the point of the sheet: Delta E ITP between baseline and tuned as a heat map
(about 1 is just visible, 10 is obvious), contours at 2 and 10, the pixels each variant pins at the display peak
outlined (magenta baseline, lime tuned), and an arrow at the largest difference with the nits on each side.

Two files: `comparison_hdr.png/.heic` (the three tone-mapped panels are real PQ/Rec.2020, the rest of the sheet is SDR
placed at a 203-nit white), and `comparison_preview.png`, the same layout with every panel shown as nits / peak under a
2.4 gamma. The preview is for reading on any screen; it is NOT a tone map, so judge the highlights in the HDR file.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import numpy as np
from matplotlib.backends.backend_agg import FigureCanvasAgg
from matplotlib.figure import Figure

from . import REFERENCE_LUMINANCE, aces2, capture, color, hdr_export, metrics, pipeline
from .gt7 import GT7Params, GT7ToneMapping
from .pipeline import SceneConvention

PW, PH = 640, 360                 # a panel, pixels
M, GAP, HEAD, ROWH, FOOT, CBAR = 24, 22, 96, 24 + PH + 74, 230, 90
BG, FG = "#121212", "#e8e8e8"
LUMA2020 = np.array([0.2627, 0.6780, 0.0593])


def preview(fb2020, peak):
    """Panel for the SDR preview: nits / peak, 2.4 gamma. A relative-luminance view, not a tone map."""
    lin = color.rec2020_to_rec709(np.maximum(fb2020, 0.0) * REFERENCE_LUMINANCE / peak)
    return np.clip(lin, 0.0, 1.0) ** (1 / 2.4)


def srgb_to_linear(x):
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def analyse(rgb, exposure, peak, params, conv):
    t = peak / REFERENCE_LUMINANCE
    fb_in = pipeline.to_gt7_input(rgb, exposure, conv)
    base = GT7ToneMapping.hdr_display(peak, GT7Params())(fb_in)
    tuned = GT7ToneMapping.hdr_display(peak, params)(fb_in)
    aces_peak = min(aces2.HDR_STYLES, key=lambda k: abs(k - peak))
    mid = float(GT7ToneMapping.hdr_display(peak)(pipeline.to_gt7_input(np.full(3, 0.18), 1.0, conv))[1])
    e = aces2.matching_exposure(lambda x: aces2.hdr(x, aces_peak), mid)
    aces = aces2.hdr(rgb * exposure * e, aces_peak)
    dE = metrics.delta_e_itp(base * REFERENCE_LUMINANCE, tuned * REFERENCE_LUMINANCE)
    # Split each difference into its brightness part and its colour part (ICtCp: I against Ct, Cp), so the sheet can say
    # which one it is: a pixel at the same nits can still differ a lot in colour.
    ib, it = color.rgb2020_to_ictcp(np.maximum(base, 1e-9)), color.rgb2020_to_ictcp(np.maximum(tuned, 1e-9))
    d_lum = 720.0 * np.abs(ib[..., 0] - it[..., 0])
    d_col = 720.0 * np.hypot(0.5 * (ib[..., 1] - it[..., 1]), ib[..., 2] - it[..., 2])
    yb, yt = (base @ LUMA2020) * REFERENCE_LUMINANCE, (tuned @ LUMA2020) * REFERENCE_LUMINANCE
    return {"base": base, "tuned": tuned, "aces": aces, "aces_peak": aces_peak, "dE": dE, "d_lum": d_lum, "d_col": d_col, "yb": yb, "yt": yt,
            "clip_b": yb >= 0.99 * peak, "clip_t": yt >= 0.99 * peak}


def build(scenes, peak, params, anchor, data_dir, out_dir, label):
    conv = SceneConvention(scene_white_nits=anchor)
    rows = []
    for scene in scenes:
        cap = capture.load(Path(data_dir) / f"{scene}.pfm").downsampled(4)
        a = analyse(cap.rgb.astype(np.float64), cap.exposure, peak, params, conv)
        assert a["dE"].shape == (PH, PW), f"{scene}: expected a {PW}x{PH} panel after downsampling, got {a['dE'].shape}"
        rows.append((scene, a))

    W = M + 4 * PW + 3 * GAP + CBAR + M
    H = HEAD + len(rows) * ROWH + FOOT
    fig = Figure(figsize=(W / 100, H / 100), dpi=100, facecolor=BG)
    canvas = FigureCanvasAgg(fig)

    def axes_at(x, y):  # pixel position, y from the top
        ax = fig.add_axes([x / W, 1 - (y + PH) / H, PW / W, PH / H])
        ax.set_xticks([]); ax.set_yticks([])
        for sp in ax.spines.values():
            sp.set_color("#333333")
        return ax

    def text(x, y, s, size=11, color=FG, weight="normal"):
        fig.text(x / W, 1 - y / H, s, color=color, fontsize=size, fontweight=weight, va="top", family="DejaVu Sans")

    text(M, 14, f"HDR tone mapping at a {peak:g}-nit display: GT7 baseline against tuned ({label}), with ACES 2.x for reference",
         size=15, weight="bold")
    text(M, 42, f"exposure anchor {anchor:g} nits (scene white).  Open comparison_hdr.heic in Preview/Photos on an HDR display; "
                "comparison_preview.png is a relative-luminance preview, not a tone map.", size=10, color="#aaaaaa")
    titles = ("GT7 baseline", "tuned", f"ACES 2.x ({rows[0][1]['aces_peak']} nit, matched at mid grey)", "where baseline and tuned differ")
    placements = []
    for c, ttl in enumerate(titles):
        text(M + c * (PW + GAP), HEAD - 26, ttl, size=12, weight="bold")

    for r, (scene, a) in enumerate(rows):
        y0 = HEAD + r * ROWH + 24
        text(M, y0 - 20, scene.replace("__", "  /  "), size=11, color="#9ecbff")
        for c, key in enumerate(("base", "tuned", "aces")):
            x = M + c * (PW + GAP)
            ax = axes_at(x, y0)
            ax.imshow(preview(a[key], peak), interpolation="nearest", aspect="auto")
            placements.append((x, y0, a[key]))
        x = M + 3 * (PW + GAP)
        ax = axes_at(x, y0)
        im = ax.imshow(a["dE"], cmap="magma", vmin=0, vmax=30, interpolation="nearest", aspect="auto")
        ax.contour(a["dE"], levels=[2.0], colors=["#ffd24d"], linewidths=0.8)
        ax.contour(a["dE"], levels=[10.0], colors=["#ffffff"], linewidths=1.0)
        if a["clip_b"].any():
            ax.contour(a["clip_b"].astype(float), levels=[0.5], colors=["#ff3df0"], linewidths=1.4)
        if a["clip_t"].any():
            ax.contour(a["clip_t"].astype(float), levels=[0.5], colors=["#7dff3d"], linewidths=1.4, linestyles="dashed")
        iy, ix = np.unravel_index(int(np.argmax(a["dE"])), a["dE"].shape)
        ax.annotate(f"largest: dE {a['dE'][iy, ix]:.0f}  (brightness {a['d_lum'][iy, ix]:.0f}, colour {a['d_col'][iy, ix]:.0f})\n"
                    f"{a['yb'][iy, ix]:.0f} -> {a['yt'][iy, ix]:.0f} nits",
                    xy=(ix, iy), xytext=(max(10, min(ix - 120, PW - 300)), iy + (40 if iy < PH // 2 else -60)),
                    color="white", fontsize=9, arrowprops=dict(arrowstyle="->", color="white", lw=1.2),
                    bbox=dict(boxstyle="round,pad=0.25", fc="#000000", ec="#888888", alpha=0.8))
        ax.set_xlim(0, PW - 1); ax.set_ylim(PH - 1, 0)
        d = a["dE"]
        vis = d > 2
        colour_share = 100.0 * np.mean(a["d_col"][vis] > a["d_lum"][vis]) if vis.any() else 0.0
        text(x, y0 + PH + 6,
             f"{100 * vis.mean():.1f}% of pixels differ visibly (dE>2), {100 * np.mean(d > 10):.1f}% obviously (dE>10);  "
             f"of the visible ones {colour_share:.0f}% differ mainly in colour\n"
             f"pinned at the {peak:g}-nit peak:  baseline {100 * a['clip_b'].mean():.1f}%   tuned {100 * a['clip_t'].mean():.1f}%",
             size=9.5, color="#cccccc")
        if r == 0:
            cax = fig.add_axes([(M + 4 * PW + 3 * GAP + 26) / W, 1 - (y0 + PH) / H, 16 / W, PH / H])
            cb = fig.colorbar(im, cax=cax)
            cb.ax.tick_params(colors=FG, labelsize=8)
            cb.set_label("Delta E ITP", color=FG, fontsize=9)

    fy = HEAD + len(rows) * ROWH + 8
    notes = [
        "THINGS TO LOOK FOR",
        "- Right column: bright = the two differ. Yellow contour = dE 2 (barely visible), white = dE 10 (obvious). Arrow = the single largest difference.",
        "- Magenta outline = pixels the BASELINE pins flat at the display peak; dashed lime = the same for TUNED. Magenta without lime means the baseline clips there and tuned keeps gradation.",
        "- Tuned deliberately sits lower in the brightest highlights (that is the floor: how far below baseline they may fall) in exchange for no clamp corner and steadier hue.",
        "- Neutral greys below the knee should match (dE near 0 on the grey stage). SATURATED mid-tones can still differ, in colour and not in brightness:",
        "  tuned blends 96% hue-preserving against baseline's 60%, so coloured spheres shift in saturation/hue (the sheet says 'colour' vs 'brightness' at the largest difference).",
        "- Compare column 2 with column 3: ACES 2.x is a softer, lower-contrast rendering; tuned should sit between baseline and ACES in the highlights.",
    ]
    for i, n in enumerate(notes):
        text(M, fy + i * 24, n, size=11 if i else 12, color=FG if i else "#ffd24d", weight="normal" if i else "bold")

    canvas.draw()
    sheet = np.asarray(canvas.buffer_rgba())[..., :3].copy()
    assert sheet.shape[:2] == (H, W)
    out = Path(out_dir); out.mkdir(parents=True, exist_ok=True)

    hdr_export.write_png8_srgb(out / "comparison_preview.png", srgb_to_linear(sheet / 255.0))
    # HDR: the SDR furniture at a 203-nit white, the three tone-mapped panels as the real thing.
    fb = color.rec709_to_rec2020(srgb_to_linear(sheet / 255.0) * 2.03)
    pq = hdr_export.pq_encode(fb)
    for x, y, panel in placements:
        pq[y:y + PH, x:x + PW] = hdr_export.pq_encode(panel)
    hdr_export.write_png16_cicp(out / "comparison_hdr.png", pq)
    made = ["comparison_preview.png", "comparison_hdr.png"]
    if (dest := hdr_export.convert_with_sips(out / "comparison_hdr.png", "heic")) is not None:
        made.append(dest.name)
    return made, rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--peak", type=float, default=1000.0)
    ap.add_argument("--anchor", type=float, default=250.0)
    ap.add_argument("--fit", default="out/fit_hdr.json")
    ap.add_argument("--label", default="8 params, floor 0.15")
    ap.add_argument("--scenes", default="emissive_ramp__dim,night_lamps__night,materials_grid__grazing")
    ap.add_argument("--data", default="data")
    ap.add_argument("--out", default="out/compare")
    a = ap.parse_args()
    params = GT7Params(**json.loads(Path(a.fit).read_text())[a.label]["params"])
    made, _ = build(a.scenes.split(","), a.peak, params, a.anchor, a.data, a.out, a.label)
    print("wrote", ", ".join(made), "to", Path(a.out).resolve())


if __name__ == "__main__":
    main()
