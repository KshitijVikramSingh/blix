"""Colour correction: fit a small grade so a tone-mapped SDR image matches a target look.

    uv run python -m tmlab.grade [--data data/metering] [--tonemap baseline|tuned]

Where the grade sits is where Blix's own notes put it: HDR linear, BEFORE the tone map ("applying colour grading after
tonemap just shifts already-clipped LDR values around"). Five parameters, in the order they apply:

    ev          log2 exposure                                   (a grade's exposure is its own, not the auto-exposure's)
    wb_r, wb_b  log2 gains on red and blue, green fixed         (white balance / tint)
    contrast    luminance ** contrast around 0.18, hue-neutral  (the existing blix_contrast pivots at 0.5 on raw values,
                                                                 which is not meaningful for HDR; this replaces it)
    saturation  mix(luma, colour, s), Rec.709 luma              (blix_saturate exactly)

The target is the ACES 2.x SDR output (OCIO) of the same frame, taken as it comes (no mid-grey matching: the grade's ev
absorbs level). The question it answers is "how close can GT7, with a cheap grade, get to the ACES look", scored by Oklab
distance on pixels stratified by luminance (so the bright tail counts as much as the stage) and checked on held-out
scenes. A target from artists works the same way: `fit_grade` takes any function from scene-linear to display-linear.
"""
from __future__ import annotations

import argparse
import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from scipy.optimize import minimize

from . import aces2, capture, metrics, pipeline
from .gt7 import GT7Params, GT7ToneMapping

LUMA709 = np.array([0.2126, 0.7152, 0.0722])
NAMES = ("ev", "wb_r", "wb_b", "contrast", "saturation")
BOUNDS = ((-3.0, 3.0), (-1.0, 1.0), (-1.0, 1.0), (0.5, 1.5), (0.3, 1.7))


@dataclass(frozen=True)
class Grade:
    ev: float = 0.0
    wb_r: float = 0.0
    wb_b: float = 0.0
    contrast: float = 1.0
    saturation: float = 1.0

    def apply(self, rgb709):
        c = np.asarray(rgb709, dtype=np.float64) * 2.0 ** self.ev * np.array([2.0 ** self.wb_r, 1.0, 2.0 ** self.wb_b])
        y = c @ LUMA709
        c = c * ((np.maximum(y, 1e-6) / 0.18) ** (self.contrast - 1.0))[..., None]
        y = (c @ LUMA709)[..., None]
        return np.maximum(y + self.saturation * (c - y), 0.0)


def to_grade(vec) -> Grade:
    return Grade(**dict(zip(NAMES, (float(v) for v in vec))))


def to_vector(g: Grade) -> np.ndarray:
    return np.array([getattr(g, n) for n in NAMES])


def target_aces2(rgb709):
    return np.clip(aces2.sdr(rgb709), 0.0, 1.0)


STRATA = (0.0, 0.25, 0.50, 0.75, 0.90, 0.97, 0.995, 1.0)  # luminance quantiles; fine at the top so the tail is sampled


def stratified_sample(rgb, per_bin: int = 150, seed: int = 0):
    """Pixels drawn equally from luminance strata (STRATA), so the few bright pixels count as much as the broad stage.

    A stratum smaller than `per_bin` contributes all of its pixels: the brightest 0.5% of a frame is taken whole.
    """
    flat = rgb.reshape(-1, 3)
    order = np.argsort(flat @ LUMA709)
    cuts = [int(round(q * len(order))) for q in STRATA]
    rng = np.random.default_rng(seed)
    picks = [rng.choice(order[a:b], size=min(per_bin, b - a), replace=False) for a, b in zip(cuts[:-1], cuts[1:]) if b > a]
    return flat[np.concatenate(picks)]


def huber(d, delta=0.03):
    return np.where(d <= delta, 0.5 * d * d, delta * (d - 0.5 * delta))


def fit_grade(pixels, tonemap, target, starts: int = 6, seed: int = 0):
    """Least Huber Oklab distance between tonemap(grade(pixels)) and target(pixels). Returns (Grade, loss)."""
    tgt = target(pixels)
    lo, hi = np.array(BOUNDS).T
    rng = np.random.default_rng(seed)

    def loss(v):
        out = tonemap(to_grade(v).apply(pixels))
        return float(np.mean(huber(metrics.delta_oklab(out, tgt))))

    x0s = [to_vector(Grade())] + [rng.uniform(lo, hi) for _ in range(starts - 1)]
    best = None
    for x0 in x0s:
        r = minimize(loss, x0, method="L-BFGS-B", bounds=BOUNDS, options={"maxiter": 80, "eps": 1e-4})
        if best is None or r.fun < best.fun:
            best = r
    r = minimize(loss, best.x, method="Nelder-Mead", bounds=BOUNDS, options={"maxiter": 300, "adaptive": True})
    x = np.clip(r.x if r.fun < best.fun else best.x, lo, hi)
    return to_grade(x), float(min(r.fun, best.fun))


def distance_stats(out, tgt) -> dict:
    d = metrics.delta_oklab(out, tgt)
    return {"mean": float(d.mean()), "p95": float(np.percentile(d, 95)), "max": float(d.max())}


def load_scenes(data_dir: Path):
    by_scene: dict[str, list] = {}
    for path in sorted(Path(data_dir).glob("*.pfm")):
        cap = capture.load(path)
        by_scene.setdefault(cap.meta.get("scene", path.stem.split("__")[0]), []).append(cap)
    return by_scene


def make_tonemap(kind: str, starts: int = 8):
    if kind == "baseline":
        tm = GT7ToneMapping.sdr_display(GT7Params())
    else:
        from .fit import NAMES_TAIL, SdrObjective, fit
        params, _, _ = fit(starts, 0, SdrObjective(weights={"chroma": 4.0}, names=NAMES_TAIL, reference="aces2"))
        tm = GT7ToneMapping.sdr_display(params)
    return (lambda scene709: pipeline.gt7_sdr(scene709, tm)), tm


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="data/metering")
    ap.add_argument("--tonemap", default="baseline", choices=("baseline", "tuned"))
    ap.add_argument("--out", default="out")
    a = ap.parse_args()
    out = Path(a.out); out.mkdir(exist_ok=True)
    tonemap, _ = make_tonemap(a.tonemap)
    scenes = load_scenes(Path(a.data))
    print(f"tone map: GT7 SDR, {a.tonemap}; target: ACES 2.x SDR; scenes: {', '.join(f'{k} ({len(v)} frames)' for k, v in scenes.items())}")

    def pixels_of(caps):
        return np.concatenate([stratified_sample(c.rgb * c.exposure, per_bin=50, seed=i) for i, c in enumerate(caps)])

    def held_stats(g, caps):
        res = []
        for c in caps:
            rgb = c.rgb[::2, ::2].reshape(-1, 3) * c.exposure
            res.append(distance_stats(tonemap(g.apply(rgb)), target_aces2(rgb)))
        return {k: float(np.mean([r[k] for r in res])) for k in ("mean", "p95", "max")}

    report = {}
    print("\nleave-one-scene-out (Oklab distance to the ACES 2.x look, mean over held-out frames; mean / p95):")
    for held in scenes:
        train = [c for s, cs in scenes.items() if s != held for c in cs]
        g, _ = fit_grade(pixels_of(train), tonemap, target_aces2, starts=3)
        before, after = held_stats(Grade(), scenes[held]), held_stats(g, scenes[held])
        print(f"   held out {held:16s} ungraded {before['mean']:.4f} / {before['p95']:.4f}   graded {after['mean']:.4f} / {after['p95']:.4f}"
              f"   ({100 * (1 - after['mean'] / before['mean']):.0f}% closer)   " + "  ".join(f"{n}={getattr(g, n):.2f}" for n in NAMES))
        report[held] = {"before": before, "after": after, "grade": {n: getattr(g, n) for n in NAMES}}

    every = [c for cs in scenes.values() for c in cs]
    g, _ = fit_grade(pixels_of(every), tonemap, target_aces2, starts=3)
    print("\nfitted on every frame:  " + "  ".join(f"{n}={getattr(g, n):.3f}" for n in NAMES))
    for s, cs in scenes.items():
        b, af = held_stats(Grade(), cs), held_stats(g, cs)
        print(f"   {s:16s} ungraded {b['mean']:.4f}   graded {af['mean']:.4f}")
    (out / f"grade_{a.tonemap}.json").write_text(json.dumps(
        {"tonemap": a.tonemap, "target": "aces2 sdr 100 nit", "grade": {n: getattr(g, n) for n in NAMES}, "leave_one_scene_out": report}, indent=1))
    print("\nwrote", (out / f"grade_{a.tonemap}.json").resolve())


if __name__ == "__main__":
    main()
