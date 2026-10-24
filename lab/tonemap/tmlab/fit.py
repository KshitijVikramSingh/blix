"""Fit GT7's seven parameters for an SDR display.

    uv run python -m tmlab.fit [--starts N] [--seed S]

The target, stated so it can be argued with: keep ACES's highlight roll-off, keep GT7's low hue shift, keep its
colour, and leave the response below scene 1.0 where GT7 puts it. Each term is a mean squared error in units of its
own tolerance, so "1.0" reads as "off by one tolerance on average" and the terms can be weighed against each other:

    rolloff   grey ramp, scene 1..100, against Blix's ACES, in sRGB-encoded values.   tolerance 0.03
    hue       output minus input hue on saturated colours across -5..+9 stops, ICtCp.  tolerance 3 degrees
    chroma    output chroma shortfall against ACES's on the same colours.              tolerance 0.03 (ICtCp)
    toe       grey ramp below scene 1.0 against baseline GT7, sRGB-encoded.            tolerance 0.02

plus two validity penalties (the curve is monotone; its shoulder joins the linear part, which needs
linear_section * peak >= gray_point). Without the chroma term the hue term has a trivial minimum: whiten
everything and no colour is left to be wrong.

The fit uses only synthetic sweeps. The captures in data/ are held out and scored afterwards (`evaluate`), so a
parameter set that only suits the sweeps shows up as a gap there.
"""
from __future__ import annotations

import argparse
import json
import time
from dataclasses import dataclass, replace
from pathlib import Path

import numpy as np
from scipy.optimize import minimize

from . import REFERENCE_LUMINANCE, SDR_PAPER_WHITE, aces2, blix_curves, color, pipeline
from .gt7 import GT7Params, GT7ToneMapping

NAMES = ("alpha", "gray_point", "linear_section", "toe_strength", "blend_ratio", "fade_start", "fade_end")
NAMES_TAIL = NAMES + ("shoulder_tail",)  # GT7's seven plus our shoulder-shape parameter
BOUNDS_BY = {"alpha": (0.001, 0.95), "gray_point": (0.10, 1.50), "linear_section": (0.10, 0.95),
             "toe_strength": (1.00, 2.00), "blend_ratio": (0.0, 1.0), "fade_start": (0.50, 1.50),
             "fade_end": (0.70, 2.50), "shoulder_tail": (0.0, 3.0)}
BOUNDS = tuple(BOUNDS_BY[n] for n in NAMES)
TOL = {"rolloff": 0.03, "hue": 3.0, "chroma": 0.03, "toe": 0.02}
SDR_TARGET_FB = SDR_PAPER_WHITE / REFERENCE_LUMINANCE  # 2.5: the display's white in frame-buffer units

_HUES709 = np.array([(1.0, 0.08, 0.04), (1.0, 0.35, 0.03), (0.1, 1.0, 0.1),
                     (0.05, 0.6, 1.0), (0.04, 0.08, 1.0), (1.0, 0.1, 0.8)])


def to_params(vec, names=NAMES) -> GT7Params:
    return GT7Params(**dict(zip(names, (float(v) for v in vec))))


def to_vector(p: GT7Params, names=NAMES) -> np.ndarray:
    return np.array([getattr(p, n) for n in names])


def srgb(x):
    x = np.clip(x, 0.0, 1.0)
    return np.where(x <= 0.0031308, 12.92 * x, 1.055 * np.power(x, 1 / 2.4) - 0.055)


def ictcp(rgb2020_fb):
    return color.rgb2020_to_ictcp(np.maximum(rgb2020_fb, 1e-9))


def hue_chroma(rgb2020_fb):
    i = ictcp(rgb2020_fb)
    return np.degrees(np.arctan2(i[..., 2], i[..., 1])), np.hypot(i[..., 1], i[..., 2])


def display_to_fb2020(display709):
    """Display-linear Rec.709 (1.0 = the SDR white) -> Rec.2020 frame-buffer units, at the display's absolute level."""
    return color.rec709_to_rec2020(np.asarray(display709)) * SDR_TARGET_FB


def wrap(deg):
    return (deg + 180.0) % 360.0 - 180.0


def make_reference(name: str, baseline: GT7Params = GT7Params()):
    """(function from exposed scene-linear Rec.709 to display-linear Rec.709, the exposure it was matched with).

    blix_aces: Blix's shipped Narkowicz curve, as is. aces2: the ACES output transform through OCIO, its input
    scaled so it agrees with baseline GT7 on mid grey (ACES alone puts 0.18 at 10 nits; the GT7 anchor near 45),
    which makes the comparison one of shape, not of level.
    """
    if name == "blix_aces":
        return blix_curves.aces, 1.0
    if name == "aces2":
        mid = float(pipeline.gt7_sdr(np.full(3, 0.18), GT7ToneMapping.sdr_display(baseline))[1])
        e = aces2.matching_exposure(aces2.sdr, mid)
        return (lambda rgb: aces2.sdr(rgb, e)), e
    raise ValueError(f"unknown reference {name!r}")


@dataclass
class SdrObjective:
    """The targets, computed once; call it with a parameter vector."""

    baseline: GT7Params = GT7Params()
    # How the terms are weighed: the artistic decision. 1 each is "equal tolerance units".
    weights: dict = None
    names: tuple = NAMES  # the free parameters; the rest stay at GT7's published values
    reference: str = "blix_aces"  # what the roll-off and chroma terms are measured against: see make_reference

    def __post_init__(self):
        self.weights = {"rolloff": 1.0, "hue": 1.0, "chroma": 1.0, "toe": 1.0, **(self.weights or {})}
        self.x_hi = np.exp(np.linspace(0.0, np.log(100.0), 120))
        self.x_lo = np.exp(np.linspace(np.log(1e-3), 0.0, 120))
        self.x_all = np.exp(np.linspace(np.log(1e-4), np.log(1e3), 800))
        self.ref_fn, self.ref_exposure = make_reference(self.reference, self.baseline)
        self.aces_hi = srgb(self.ref_fn(np.stack([self.x_hi] * 3, axis=-1))[:, 1])
        base = GT7ToneMapping.sdr_display(self.baseline)
        self.base_lo = srgb(self._grey(base, self.x_lo))

        stops = np.linspace(-5, 9, 57)
        scene = (_HUES709[:, None, :] * (0.18 * 2.0 ** stops)[None, :, None]).reshape(-1, 3)
        self.scene = scene
        self.in_fb = pipeline.to_gt7_input(scene)
        self.in_hue, _ = hue_chroma(self.in_fb)
        _, self.aces_chroma = hue_chroma(display_to_fb2020(self.ref_fn(scene)))

    @staticmethod
    def _grey(tm, x):
        return pipeline.gt7_sdr(np.stack([x, x, x], axis=-1), tm)[:, 1]

    def components(self, vec) -> dict:
        p = to_params(vec, self.names)
        tm = GT7ToneMapping.sdr_display(p)

        roll = ((srgb(self._grey(tm, self.x_hi)) - self.aces_hi) / TOL["rolloff"]) ** 2
        toe = ((srgb(self._grey(tm, self.x_lo)) - self.base_lo) / TOL["toe"]) ** 2

        out = pipeline.gt7_sdr(self.scene, tm)
        out_hue, out_chroma = hue_chroma(display_to_fb2020(out))
        # Weight each colour by how much chroma it has left, so a near-white output is not scored on a noisy angle.
        w = np.clip((out_chroma - 0.01) / 0.02, 0.0, 1.0)
        hue = (wrap(out_hue - self.in_hue) / TOL["hue"]) ** 2
        short = np.maximum(self.aces_chroma - out_chroma, 0.0) / TOL["chroma"]

        grey = self._grey(tm, self.x_all)
        mono = float(np.sum(np.maximum(-np.diff(grey), 0.0)) * 1e3)
        join = max(0.0, p.gray_point - p.linear_section * tm.target)
        order = max(0.0, 0.08 - (p.fade_end - p.fade_start))
        return {
            "rolloff": float(roll.mean()),
            "hue": float((w * hue).sum() / max(w.sum(), 1e-9)),
            "chroma": float((short ** 2).mean()),
            "toe": float(toe.mean()),
            "penalty": mono + 1e2 * join + 1e2 * order,
        }

    def __call__(self, vec) -> float:
        c = self.components(vec)
        w = self.weights
        return (w["rolloff"] * c["rolloff"] + w["hue"] * c["hue"] + w["chroma"] * c["chroma"]
                + w["toe"] * c["toe"] + c["penalty"])


def fit(starts: int = 6, seed: int = 0, objective: SdrObjective | None = None):
    obj = objective or SdrObjective()
    rng = np.random.default_rng(seed)
    bounds = tuple(BOUNDS_BY[n] for n in obj.names)
    lo, hi = np.array(bounds).T
    x0s = [to_vector(obj.baseline, obj.names)] + [rng.uniform(lo, hi) for _ in range(starts - 1)]
    best = None
    for x0 in x0s:
        r = minimize(obj, x0, method="L-BFGS-B", bounds=bounds, options={"maxiter": 200, "eps": 1e-5})
        r = minimize(obj, np.clip(r.x, lo, hi), method="Nelder-Mead", bounds=bounds,
                     options={"maxiter": 800, "xatol": 1e-5, "fatol": 1e-8, "adaptive": True})
        if best is None or r.fun < best.fun:
            best = r
    return to_params(np.clip(best.x, lo, hi), obj.names), obj, best


def evaluate(params: GT7Params, data_dir: Path, factor: int = 8, reference: str = "blix_aces") -> dict:
    """Score on the held-out captures: baseline GT7, the fit, and the engine's ACES, per capture."""
    from . import capture, metrics

    rows = {}
    ref_fn, _ = make_reference(reference)
    base_tm = GT7ToneMapping.sdr_display(GT7Params())
    fit_tm = GT7ToneMapping.sdr_display(params)
    for path in sorted(data_dir.glob("*.pfm")):
        cap = capture.load(path).downsampled(factor)
        rgb = cap.rgb.reshape(-1, 3).astype(np.float64)
        lum = rgb @ np.array([0.2126, 0.7152, 0.0722])
        in_hue, in_chroma = hue_chroma(pipeline.to_gt7_input(rgb, cap.exposure))
        sat = (in_chroma > 0.03) & (lum > 0.3)
        hi = lum > 1.0
        aces = ref_fn(rgb * cap.exposure)
        row = {}
        for name, out in (("aces", aces), ("gt7_base", pipeline.gt7_sdr(rgb, base_tm, cap.exposure)),
                          ("gt7_fit", pipeline.gt7_sdr(rgb, fit_tm, cap.exposure))):
            out_lum = out @ np.array([0.2126, 0.7152, 0.0722])
            oh, oc = hue_chroma(display_to_fb2020(out))
            # The objective's weighting: a colour counts for as much chroma as it has left, so a near-white output is
            # not scored on a noisy angle.
            w = np.clip((oc - 0.01) / 0.02, 0.0, 1.0)[sat]
            row[name] = {
                "hue_shift_deg": float((w * np.abs(wrap(oh - in_hue))[sat]).sum() / max(w.sum(), 1e-9)) if sat.any() else float("nan"),
                "chroma_kept_pct": float(100.0 * np.mean(oc[sat] / in_chroma[sat])) if sat.any() else float("nan"),
                "highlight_gap_vs_aces": float(np.mean(np.abs(srgb(out_lum[hi]) - srgb(aces[hi] @ np.array([0.2126, 0.7152, 0.0722]))))) if hi.any() else float("nan"),
                "blown_pct": float(100.0 * np.mean(out_lum[hi] > 0.98)) if hi.any() else float("nan"),
                "oklab_vs_aces": float(metrics.delta_oklab(out, aces).mean()),
            }
        rows[cap.name] = row
    return rows


def pareto(a, base_weights):
    """Chroma weight against everything else, so the trade is a table and not a guess."""
    rows = []
    for wc in (0.0, 0.25, 1.0, 4.0, 16.0, 64.0):
        names = NAMES_TAIL if a.tail else NAMES
        obj = SdrObjective(weights={**base_weights, "chroma": wc}, reference=a.reference, names=names)
        params, obj, _ = fit(a.starts, a.seed, obj)
        c = obj.components(to_vector(params, names))
        held = evaluate(params, Path(a.data), reference=a.reference)
        lit = [v["gt7_fit"] for k, v in held.items() if k.startswith("materials")]
        emi = [v["gt7_fit"] for k, v in held.items() if k.startswith("emissive")]
        rows.append((wc, c, params, np.mean([m["chroma_kept_pct"] for m in lit]), np.mean([m["chroma_kept_pct"] for m in emi]),
                     np.mean([m["hue_shift_deg"] for m in emi]), np.mean([m["blown_pct"] for m in emi])))
    base = evaluate(GT7Params(), Path(a.data), reference=a.reference)
    bo = SdrObjective(reference=a.reference)
    bc = bo.components(to_vector(bo.baseline))
    b_lit = np.mean([v["gt7_base"]["chroma_kept_pct"] for k, v in base.items() if k.startswith("materials")])
    b_emi = np.mean([v["gt7_base"]["chroma_kept_pct"] for k, v in base.items() if k.startswith("emissive")])
    print(f"{'w_chroma':>8s} | {'rolloff':>7s} {'hue':>6s} {'chroma':>6s} | {'blend':>5s} {'fadeS':>5s} {'fadeE':>5s} {'alpha':>5s} | "
          f"{'lit kept%':>9s} {'emis kept%':>10s} {'emis hue':>8s} {'emis blown%':>11s}")
    print(f"{'GT7':>8s} | {bc['rolloff']:7.3f} {bc['hue']:6.2f} {bc['chroma']:6.3f} | {0.6:5.2f} {0.98:5.2f} {1.16:5.2f} {0.25:5.2f} | "
          f"{b_lit:9.1f} {b_emi:10.1f}")
    for wc, c, p, lit, emi, eh, eb in rows:
        print(f"{wc:8.2f} | {c['rolloff']:7.3f} {c['hue']:6.2f} {c['chroma']:6.3f} | {p.blend_ratio:5.2f} {p.fade_start:5.2f} "
              f"{p.fade_end:5.2f} {p.alpha:5.2f} | {lit:9.1f} {emi:10.1f} {eh:8.2f} {eb:11.1f}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--starts", type=int, default=6)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--data", default="data")
    ap.add_argument("--out", default="out")
    ap.add_argument("--weights", default="", help="e.g. chroma=4,hue=1: the term weights (default 1 each)")
    ap.add_argument("--pareto", action="store_true", help="sweep the chroma weight and print the trade-off")
    ap.add_argument("--reference", default="blix_aces", choices=("blix_aces", "aces2"))
    ap.add_argument("--tail", action="store_true", help="also fit the shoulder_tail parameter")
    a = ap.parse_args()
    weights = {k: float(v) for k, v in (kv.split("=") for kv in a.weights.split(",") if kv)}

    if a.pareto:
        pareto(a, weights)
        return

    obj = SdrObjective(weights=weights, reference=a.reference, names=NAMES_TAIL if a.tail else NAMES)
    base_vec = to_vector(obj.baseline, obj.names)
    print(f"reference: {a.reference} (input exposure {obj.ref_exposure:.3f})")
    print("baseline GT7 (SDR):  ", {k: round(v, 3) for k, v in obj.components(base_vec).items()},
          f" total {obj(base_vec):.3f}")
    t = time.time()
    params, obj, res = fit(a.starts, a.seed, obj)
    print(f"fit in {time.time() - t:.0f}s ({res.nfev} evals in the best run)")
    print("fitted:              ", {k: round(v, 3) for k, v in obj.components(to_vector(params, obj.names)).items()},
          f" total {obj(to_vector(params, obj.names)):.3f}")
    print()
    print(f"{'parameter':16s} {'GT7':>8s} {'fitted':>8s}")
    for n in obj.names:
        print(f"{n:16s} {getattr(obj.baseline, n):8.3f} {getattr(params, n):8.3f}")

    out = Path(a.out); out.mkdir(parents=True, exist_ok=True)
    held = evaluate(params, Path(a.data), reference=a.reference)
    print("\ncaptures (hue shift deg, output-chroma weighted / chroma kept % / highlight gap vs ACES / % highlights blown / Oklab vs ACES):")
    for name, row in held.items():
        print(f"  {name}")
        for k, m in row.items():
            print(f"     {k:9s} {m['hue_shift_deg']:6.2f}  {m['chroma_kept_pct']:6.1f}  {m['highlight_gap_vs_aces']:.4f}  {m['blown_pct']:5.1f}  {m['oklab_vs_aces']:.4f}")
    (out / "fit_sdr.json").write_text(json.dumps(
        {"params": {n: getattr(params, n) for n in obj.names}, "baseline": {n: getattr(obj.baseline, n) for n in obj.names}, "reference": a.reference,
         "objective": obj.components(to_vector(params, obj.names)), "held_out": held}, indent=1))
    print("\nwrote", (out / "fit_sdr.json").resolve())


if __name__ == "__main__":
    main()
