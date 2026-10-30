"""Fit GT7's parameters (and our shoulder tail) for HDR displays, one parameter set across several peaks.

    uv run python -m tmlab.fit_hdr [--starts N] [--peaks 400,1000,4000] [--weights chroma=2,...]

HDR has no reference operator to match (SDR had Blix's shipped curve), so the target comes from GT7's own premise
and from what the SDR work and the pilot plots measured. Per peak, in units of each term's tolerance:

    lin       grey below 0.35 x peak against baseline GT7, PQ-encoded.        tolerance 0.01 (PQ signal)
              GT7's HDR curves coincide below their shoulders: a scene value means the same nits whatever
              the display, and that is the property worth keeping.
    shoulder  the clamp corner and the reach. GT7's final min(., peak) cuts its shoulder off at a slope, a hard
              knee (the slides call it a "sharp clipping point"); the penalty is the slope where the output
              meets the peak, tolerance 0.05. And the grey at 20 x peak must be at least 97% of it (tolerance 2%),
              so a long tail does not leave the display's top unused.
    hue       output minus input hue, saturated colours, -5..+11 stops, ICtCp, weighted by output chroma. 3 deg
    chroma    shortfall against baseline GT7's chroma at the same peak (one-sided: it may keep MORE).  0.03
    highlight grey at scene = 1, 2, 4, 8 x peak may fall below baseline GT7 by at most `floor` of baseline
              (0.15 by default); the shortfall beyond that, in fractions of the peak, tolerance 0.02. Without
              it the smooth-shoulder fit dimmed highlights freely: a scene value at the peak came out at
              57% of it where baseline GT7 gives 83%.

Like the SDR fit it uses only synthetic sweeps; the captures are scored afterwards.
"""
from __future__ import annotations

import argparse
import json
import time
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from . import REFERENCE_LUMINANCE, aces2, color, metrics, pipeline
from .fit import (BOUNDS_BY, NAMES, NAMES_TAIL, _HUES709, fit, hue_chroma, to_params, to_vector, wrap)
from .gt7 import GT7Params, GT7ToneMapping

TOL = {"lin": 0.01, "shoulder_slope": 0.05, "shoulder_reach": 0.02, "hue": 3.0, "chroma": 0.03, "highlight": 0.02}
HIGHLIGHT_K = (1.0, 2.0, 4.0, 8.0)  # scene light as a multiple of the display peak


def pq(fb):
    return color.inverse_eotf_st2084(np.maximum(fb, 0.0))


@dataclass
class HdrObjective:
    peaks: tuple = (400.0, 1000.0, 1600.0, 4000.0)
    baseline: GT7Params = GT7Params()
    weights: dict = None
    names: tuple = NAMES_TAIL
    floor: float = 0.15  # how far below baseline GT7 a highlight may fall, as a fraction of baseline

    def __post_init__(self):
        self.weights = {"lin": 1.0, "shoulder": 1.0, "hue": 1.0, "chroma": 1.0, "highlight": 1.0, **(self.weights or {})}
        stops = np.linspace(-5, 11, 65)
        scene = (_HUES709[:, None, :] * (0.18 * 2.0 ** stops)[None, :, None]).reshape(-1, 3)
        self.scene = scene
        self.in_fb = pipeline.to_gt7_input(scene)
        self.in_hue, _ = hue_chroma(self.in_fb)
        self.per_peak = []
        for peak in self.peaks:
            base = GT7ToneMapping.hdr_display(peak, self.baseline)
            t = base.target
            x_lin = np.exp(np.linspace(np.log(1e-3), np.log(0.35 * t), 100))
            x_all = np.exp(np.linspace(np.log(1e-3), np.log(1e3 * t), 900))
            self.per_peak.append({
                "peak": peak, "t": t, "x_lin": x_lin, "x_all": x_all,
                "base_lin": pq(self._grey(base, x_lin)),
                "x_hi": np.array(HIGHLIGHT_K) * t,
                "base_hi": self._grey(base, np.array(HIGHLIGHT_K) * t),
                "base_chroma": hue_chroma(base(self.in_fb))[1],
            })

    @staticmethod
    def _grey(tm, x):
        return tm(np.stack([x, x, x], axis=-1))[:, 1]

    def components(self, vec) -> dict:
        p = to_params(vec, self.names)
        lin, slope, reach, hue, chroma, highlight = [], [], [], [], [], []
        penalty = 0.0
        for pp in self.per_peak:
            tm = GT7ToneMapping.hdr_display(pp["peak"], p)
            t = pp["t"]
            lin.append((((pq(self._grey(tm, pp["x_lin"])) - pp["base_lin"]) / TOL["lin"]) ** 2).mean())

            grey = self._grey(tm, pp["x_all"])
            x = pp["x_all"]
            hit = np.nonzero(grey >= 0.9999 * t)[0]
            if hit.size and hit[0] > 0:  # the clamp bites: how steep is the output where it meets the peak
                i = hit[0]
                slope.append(((grey[i] - grey[i - 1]) / (x[i] - x[i - 1]) / TOL["shoulder_slope"]) ** 2)
            else:
                slope.append(0.0)
            out_hi = self._grey(tm, pp["x_hi"])
            short = np.maximum(pp["base_hi"] * (1.0 - self.floor) - out_hi, 0.0) / t
            highlight.append(float(((short / TOL["highlight"]) ** 2).mean()))
            r20 = float(self._grey(tm, np.array([20.0 * t]))[0] / t)
            reach.append((max(0.0, 0.97 - r20) / TOL["shoulder_reach"]) ** 2)
            penalty += float(np.sum(np.maximum(-np.diff(grey), 0.0)) * 1e3)
            penalty += 1e2 * max(0.0, p.gray_point - p.linear_section * t)

            out = tm(self.in_fb)
            oh, oc = hue_chroma(out)
            w = np.clip((oc - 0.01) / 0.02, 0.0, 1.0)
            hue.append(float((w * (wrap(oh - self.in_hue) / TOL["hue"]) ** 2).sum() / max(w.sum(), 1e-9)))
            chroma.append(float(((np.maximum(pp["base_chroma"] - oc, 0.0) / TOL["chroma"]) ** 2).mean()))
        penalty += 1e2 * max(0.0, 0.08 - (p.fade_end - p.fade_start))
        return {"lin": float(np.mean(lin)), "shoulder": float(np.mean(slope) + np.mean(reach)),
                "hue": float(np.mean(hue)), "chroma": float(np.mean(chroma)),
                "highlight": float(np.mean(highlight)), "penalty": penalty}

    def __call__(self, vec) -> float:
        c = self.components(vec)
        w = self.weights
        return (w["lin"] * c["lin"] + w["shoulder"] * c["shoulder"] + w["hue"] * c["hue"]
                + w["chroma"] * c["chroma"] + w["highlight"] * c["highlight"] + c["penalty"])


def evaluate(params: GT7Params, data_dir: Path, peak: float = 1000.0, factor: int = 8) -> dict:
    """Held-out captures at one display peak: how far the fit moves the picture, and where it clamps."""
    from . import capture

    base_tm = GT7ToneMapping.hdr_display(peak, GT7Params())
    fit_tm = GT7ToneMapping.hdr_display(peak, params)
    t = peak / REFERENCE_LUMINANCE
    rows = {}
    # ACES 2.x at the same peak, as an outside comparison (no OCIO transform exists for 1600 nits): exposure
    # matched to baseline GT7 on mid grey, so it is a comparison of shape.
    aces_peak = min(aces2.HDR_STYLES, key=lambda k: abs(k - peak))
    mid = float(base_tm(pipeline.to_gt7_input(np.full(3, 0.18)))[1])
    aces_e = aces2.matching_exposure(lambda rgb: aces2.hdr(rgb, aces_peak), mid)
    for path in sorted(data_dir.glob("*.pfm")):
        cap = capture.load(path).downsampled(factor)
        rgb = cap.rgb.reshape(-1, 3).astype(np.float64)
        fb = pipeline.to_gt7_input(rgb, cap.exposure)
        in_hue, in_chroma = hue_chroma(fb)
        sat = (in_chroma > 0.03) & (rgb @ np.array([0.2126, 0.7152, 0.0722]) > 0.3)
        ob, of = base_tm(fb), fit_tm(fb)
        row = {}
        for name, out in (("gt7_base", ob), ("gt7_fit", of)):
            oh, oc = hue_chroma(out)
            w = np.clip((oc - 0.01) / 0.02, 0.0, 1.0)[sat]
            lum = out @ np.array([0.2627, 0.6780, 0.0593])  # Rec.2020 luminance
            row[name] = {
                "hue_shift_deg": float((w * np.abs(wrap(oh - in_hue))[sat]).sum() / max(w.sum(), 1e-9)) if sat.any() else float("nan"),
                "chroma_kept_pct": float(100.0 * np.mean(oc[sat] / in_chroma[sat])) if sat.any() else float("nan"),
                "at_peak_pct": float(100.0 * np.mean(lum > 0.99 * t)),
            }
        oa = aces2.hdr(rgb * cap.exposure * aces_e, aces_peak)
        row["delta_e_itp_vs_aces2"] = {
            "base": float(metrics.delta_e_itp(ob * REFERENCE_LUMINANCE, oa * REFERENCE_LUMINANCE).mean()),
            "fit": float(metrics.delta_e_itp(of * REFERENCE_LUMINANCE, oa * REFERENCE_LUMINANCE).mean())}
        d = metrics.delta_e_itp(ob * REFERENCE_LUMINANCE, of * REFERENCE_LUMINANCE)
        row["delta_e_itp_base_vs_fit"] = {"mean": float(d.mean()), "p95": float(np.percentile(d, 95)), "max": float(d.max())}
        rows[cap.name] = row
    return rows


def grey_percent(params: GT7Params, peak: float, ks=(0.5, 1, 2, 4, 10)):
    tm = GT7ToneMapping.hdr_display(peak, params)
    t = peak / REFERENCE_LUMINANCE
    return [float(tm(np.full(3, k * t))[1] / t * 100) for k in ks]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--starts", type=int, default=10)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--peaks", default="400,1000,1600,4000")
    ap.add_argument("--floors", default="0.05,0.15,0.30", help="highlight tolerances to fit, as fractions of baseline")
    ap.add_argument("--weights", default="")
    ap.add_argument("--data", default="data")
    ap.add_argument("--out", default="out")
    a = ap.parse_args()
    peaks = tuple(float(v) for v in a.peaks.split(","))
    floors = [float(v) for v in a.floors.split(",")]
    weights = {k: float(v) for k, v in (kv.split("=") for kv in a.weights.split(",") if kv)}

    out = Path(a.out); out.mkdir(exist_ok=True)
    runs = [("7 params", NAMES, floors[len(floors) // 2])] + [(f"8 params, floor {f:g}", NAMES_TAIL, f) for f in floors]
    report, rows = {}, []
    base_obj = HdrObjective(peaks=peaks, weights=weights)
    base = base_obj.components(to_vector(base_obj.baseline, base_obj.names))
    for label, names, floor in runs:
        obj = HdrObjective(peaks=peaks, weights=weights, names=names, floor=floor)
        t0 = time.time()
        params, obj, _ = fit(a.starts, a.seed, obj)
        c = obj.components(to_vector(params, names))
        held = evaluate(params, Path(a.data), peak=1000.0)
        emi = [v for k, v in held.items() if k.startswith("emissive")]
        lit = [v for k, v in held.items() if k.startswith("materials")]
        rows.append((label, c, params, grey_percent(params, 1000.0),
                     np.mean([v["gt7_fit"]["hue_shift_deg"] for v in emi]), np.mean([v["gt7_fit"]["chroma_kept_pct"] for v in emi]),
                     np.mean([v["gt7_fit"]["at_peak_pct"] for v in emi]),
                     np.mean([v["delta_e_itp_base_vs_fit"]["p95"] for v in emi]), np.mean([v["delta_e_itp_vs_aces2"]["fit"] for v in emi]),
                     np.mean([v["delta_e_itp_vs_aces2"]["fit"] for v in lit])))
        report[label] = {"params": {n: getattr(params, n) for n in names}, "floor": floor, "objective": c, "held_out": held}
        print(f"  {label}: {time.time() - t0:.0f}s", flush=True)

    emi_b = [v for k, v in held.items() if k.startswith("emissive")]
    lit_b = [v for k, v in held.items() if k.startswith("materials")]
    print("\npeaks fitted jointly:", ", ".join(f"{p:g}" for p in peaks), "nits")
    print("baseline GT7 objective:", {k: round(v, 3) for k, v in base.items()})
    ks = (0.5, 1, 2, 4, 10)
    print(f"\n{'variant':20s} | {'shoulder':>8s} {'hue':>5s} {'chroma':>6s} {'highlt':>6s} | grey % of peak at k=" + " ".join(f"{k:>5g}" for k in ks)
          + " | emissive @1000: hue  kept%  atpeak%  dE p95 vs GT7   dE vs ACES2(fit) | lit dE vs ACES2")
    bg = grey_percent(GT7Params(), 1000.0)
    print(f"{'GT7 baseline':20s} | {base['shoulder']:8.3f} {base['hue']:5.2f} {base['chroma']:6.3f} {base['highlight']:6.3f} | {'':19s}" + " ".join(f"{v:5.1f}" for v in bg)
          + f" | {np.mean([v['gt7_base']['hue_shift_deg'] for v in emi_b]):20.2f} {np.mean([v['gt7_base']['chroma_kept_pct'] for v in emi_b]):6.1f} {np.mean([v['gt7_base']['at_peak_pct'] for v in emi_b]):8.1f} {'':14s} {np.mean([v['delta_e_itp_vs_aces2']['base'] for v in emi_b]):14.2f} | {np.mean([v['delta_e_itp_vs_aces2']['base'] for v in lit_b]):6.2f}")
    for label, c, p, g, eh, ek, ea, ed, eaces, laces in rows:
        print(f"{label:20s} | {c['shoulder']:8.3f} {c['hue']:5.2f} {c['chroma']:6.3f} {c['highlight']:6.3f} | {'':19s}" + " ".join(f"{v:5.1f}" for v in g)
              + f" | {eh:20.2f} {ek:6.1f} {ea:8.1f} {ed:14.1f} {eaces:14.2f} | {laces:6.2f}")
    print()
    for label, c, p, *_ in rows:
        print(f"{label:20s}", "  ".join(f"{n}={getattr(p, n):.3f}" for n in (NAMES_TAIL if 'tail' in label or '8' in label else NAMES)))
    (out / "fit_hdr.json").write_text(json.dumps(report, indent=1))
    print("\nwrote", (out / "fit_hdr.json").resolve())


if __name__ == "__main__":
    main()
