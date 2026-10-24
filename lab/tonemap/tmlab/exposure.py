"""Auto-exposure: a metering model fitted to a stated proxy for "good exposure".

SUPERSEDED by adaptation.py, kept as the record of a negative result. Its target (the best exposure of EACH frame) is the
camera-model GT7's slides reject, and it failed leave-one-scene-out. The histogram and log-luminance helpers here are
still used by adaptation.py.

    uv run python -m tmlab.exposure [--data data/metering] [--starts N]

There are no human labels for "the right exposure", so the target is a proxy, written down here so it can be argued with
and replaced. For each frame the target log2 exposure E* is the one that maximises, under baseline GT7 SDR (anchor 250),

    score(e) = entropy of the output's sRGB-encoded values (32 bins, 0..1)   - how much of the display's range is used
               - 2.0 x fraction of pixels above 0.97                          - highlights blown to white
               - 1.0 x fraction of pixels below 0.03                          - shadows crushed to black

A frame that is mostly dark with a few lamps gets an E* that lifts the dark and blows the lamps as little as it can;
no mood or partial adaptation is in it (a game would keep a night scene dark on purpose). `labels` below lets a
person override E* for named frames; the fit then follows the labels.

The model meters a frame from its log2-luminance histogram:

    mean_l = weighted mean of l, weights = a window that ignores very dark and very bright pixels (soft edges)
    E_mid  = key - mean_l                         the metered level lands at `key`
    E_prot = protect - l_q                        the q-th percentile may not land above `protect` (q is fitted)
    log2 E = min(E_mid, E_prot)

Seven parameters. Any such model is exposure-equivariant by construction (scale a frame by 2^u and the predicted exposure
moves by -u), so what is fitted is WHICH statistic, not how to follow a global gain. Not fitted: temporal adaptation
(rates up and down), because there is no data for it here.
"""
from __future__ import annotations

import argparse
import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from scipy.optimize import minimize

from . import capture, pipeline

LUMA709 = np.array([0.2126, 0.7152, 0.0722])
EDGES = np.linspace(-14.0, 10.0, 97)              # log2 luminance, 0.25-stop bins
CENTERS = 0.5 * (EDGES[:-1] + EDGES[1:])
EXPOSURE_GRID = np.arange(-10.0, 10.01, 0.25)     # candidate log2 exposures for the proxy
W_BLOWN, W_CRUSHED = 2.0, 1.0


def srgb(x):
    x = np.clip(x, 0.0, 1.0)
    return np.where(x <= 0.0031308, 12.92 * x, 1.055 * np.power(x, 1 / 2.4) - 0.055)


def log2_luminance(rgb) -> np.ndarray:
    y = np.asarray(rgb, dtype=np.float64).reshape(-1, 3) @ LUMA709
    return np.log2(np.clip(y, 2.0 ** -14, 2.0 ** 10))


def log_histogram(rgb) -> np.ndarray:
    """Normalised log2-luminance histogram on EDGES (pixels outside the range land in the end bins)."""
    h, _ = np.histogram(log2_luminance(rgb), bins=EDGES)
    return h / max(h.sum(), 1)


# --- the proxy target -------------------------------------------------------------------------
def score(rgb_small, log2_e: float) -> float:
    out = pipeline.gt7_sdr(np.asarray(rgb_small, dtype=np.float64).reshape(-1, 3) * 2.0 ** log2_e)
    v = srgb(out @ LUMA709)
    counts, _ = np.histogram(v, bins=32, range=(0.0, 1.0))
    p = counts[counts > 0] / counts.sum()
    entropy = float(-(p * np.log2(p)).sum() / np.log2(32))
    return entropy - W_BLOWN * float(np.mean(v > 0.97)) - W_CRUSHED * float(np.mean(v < 0.03))


def best_exposure(rgb_small, grid=EXPOSURE_GRID) -> float:
    """log2 exposure maximising `score`, refined by a parabola through the best grid point and its neighbours."""
    s = np.array([score(rgb_small, e) for e in grid])
    i = int(np.argmax(s))
    if 0 < i < len(grid) - 1:
        a, b, c = s[i - 1], s[i], s[i + 1]
        denom = a - 2 * b + c
        if denom < 0:
            return float(grid[i] + 0.5 * (grid[1] - grid[0]) * (a - c) / denom)
    return float(grid[i])


# --- the metering model ----------------------------------------------------------------------
@dataclass(frozen=True)
class MeteringParams:
    dark_cut: float = -6.0      # log2 luminance below which pixels stop counting
    dark_soft: float = 1.0
    bright_cut: float = 4.0     # above which they stop counting
    bright_soft: float = 1.0
    key: float = float(np.log2(0.18))
    protect: float = 3.0        # log2 level the q-th percentile may reach after exposure
    q: float = 0.995            # which percentile guards the highlights (0.9 reacts to a frame with large bright areas)


NAMES = ("dark_cut", "dark_soft", "bright_cut", "bright_soft", "key", "protect", "q")
BOUNDS = ((-12.0, 0.0), (0.25, 3.0), (-2.0, 10.0), (0.25, 3.0), (-6.0, 1.0), (-2.0, 8.0), (0.5, 0.9995))


def to_params(vec) -> MeteringParams:
    return MeteringParams(**dict(zip(NAMES, (float(v) for v in vec))))


def to_vector(p: MeteringParams) -> np.ndarray:
    return np.array([getattr(p, n) for n in NAMES])


def _sigmoid(x):
    return 1.0 / (1.0 + np.exp(-x))


def meter(hist, p: MeteringParams) -> float:
    """Predicted log2 exposure for a frame, from its log2-luminance histogram."""
    w = _sigmoid((CENTERS - p.dark_cut) / p.dark_soft) * (1.0 - _sigmoid((CENTERS - p.bright_cut) / p.bright_soft))
    ww = w * hist
    if ww.sum() < 1e-9:
        ww = hist  # a frame entirely outside the window is metered as a whole rather than not at all
    mean_l = float((ww * CENTERS).sum() / max(ww.sum(), 1e-12))
    l_q = float(np.interp(p.q, np.cumsum(hist), CENTERS))  # smooth in q, so the fit has a gradient to follow
    return min(p.key - mean_l, p.protect - l_q)


# --- textbook metering, as baselines, each with one fitted constant ------------------------------
def _stat(hist, kind: str) -> float:
    if kind == "geometric_mean":
        return float((hist * CENTERS).sum())
    if kind == "arithmetic_mean":
        return float(np.log2(max((hist * 2.0 ** CENTERS).sum(), 1e-12)))
    if kind == "median":
        return float(CENTERS[min(int(np.searchsorted(np.cumsum(hist), 0.5)), len(CENTERS) - 1)])
    if kind == "clipped_10_90":  # a log-average of the middle 80%, the usual histogram-metering heuristic
        c = np.cumsum(hist)
        keep = (c >= 0.10) & (c - hist <= 0.90)
        return float((hist[keep] * CENTERS[keep]).sum() / max(hist[keep].sum(), 1e-12))
    raise ValueError(kind)


BASELINES = ("geometric_mean", "arithmetic_mean", "median", "clipped_10_90")


def baseline_predictions(hists, kind: str, key: float | None):
    """key None: the textbook constant (metered level -> 0.18). Otherwise the given constant."""
    k = float(np.log2(0.18)) if key is None else key
    return np.array([k - _stat(h, kind) for h in hists])


def fit_baseline_key(hists, e_star, kind: str) -> float:
    return float(np.mean(e_star + np.array([_stat(h, kind) for h in hists])))  # least squares for the one constant


# --- fitting and validation --------------------------------------------------------------------
def fit_metering(hists, e_star, starts: int = 12, seed: int = 0) -> MeteringParams:
    rng = np.random.default_rng(seed)
    lo, hi = np.array(BOUNDS).T

    def loss(v):
        p = to_params(v)
        return float(np.mean([(meter(h, p) - t) ** 2 for h, t in zip(hists, e_star)]))

    x0s = [to_vector(MeteringParams())] + [rng.uniform(lo, hi) for _ in range(starts - 1)]
    best = None
    for x0 in x0s:
        r = minimize(loss, x0, method="L-BFGS-B", bounds=BOUNDS, options={"maxiter": 150})
        r = minimize(loss, np.clip(r.x, lo, hi), method="Nelder-Mead", bounds=BOUNDS, options={"maxiter": 600, "adaptive": True})
        if best is None or r.fun < best.fun:
            best = r
    return to_params(np.clip(best.x, lo, hi))


def rms(a, b) -> float:
    return float(np.sqrt(np.mean((np.asarray(a) - np.asarray(b)) ** 2)))


def load_frames(data_dir: Path, labels: dict | None = None, cache: Path | None = None):
    """[(name, scene, histogram, E*)] for every metering frame; E* from the proxy unless `labels` names the frame."""
    cached = json.loads(cache.read_text()) if cache and cache.exists() else {}
    frames = []
    for path in sorted(Path(data_dir).glob("*.pfm")):
        cap = capture.load(path)
        scene = cap.meta.get("scene", path.stem.split("__")[0])
        if labels and path.stem in labels:
            e = float(labels[path.stem])
        elif path.stem in cached:
            e = cached[path.stem]
        else:
            e = best_exposure(cap.rgb[::4, ::4])
            cached[path.stem] = e
        frames.append((path.stem, scene, log_histogram(cap.rgb), e))
    if cache:
        cache.parent.mkdir(parents=True, exist_ok=True)
        cache.write_text(json.dumps(cached, indent=1))
    return frames


def cross_validate(frames, starts: int = 8):
    """Leave one scene out: fit on the others, predict the held-out one. Returns {method: pooled held-out RMS (stops)}."""
    scenes = sorted({f[1] for f in frames})
    preds = {m: np.zeros(len(frames)) for m in ("ours", *BASELINES, *(b + "_key0.18" for b in BASELINES))}
    for held in scenes:
        tr = [i for i, f in enumerate(frames) if f[1] != held]
        te = [i for i, f in enumerate(frames) if f[1] == held]
        htr, etr = [frames[i][2] for i in tr], np.array([frames[i][3] for i in tr])
        hte = [frames[i][2] for i in te]
        p = fit_metering(htr, etr, starts)
        for j, i in enumerate(te):
            preds["ours"][i] = meter(hte[j], p)
        for kind in BASELINES:
            key = fit_baseline_key(htr, etr, kind)
            for i, v in zip(te, baseline_predictions(hte, kind, key)):
                preds[kind][i] = v
            for i, v in zip(te, baseline_predictions(hte, kind, None)):
                preds[kind + "_key0.18"][i] = v
    e = np.array([f[3] for f in frames])
    return {m: rms(v, e) for m, v in preds.items()}, preds


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="data/metering")
    ap.add_argument("--starts", type=int, default=12)
    ap.add_argument("--labels", default=None, help="JSON {frame name: log2 exposure} overriding the proxy")
    ap.add_argument("--out", default="out")
    a = ap.parse_args()
    out = Path(a.out); out.mkdir(exist_ok=True)
    labels = json.loads(Path(a.labels).read_text()) if a.labels else None
    frames = load_frames(Path(a.data), labels, out / "exposure_targets.json")
    names = [f[0] for f in frames]
    e = np.array([f[3] for f in frames])
    print(f"{len(frames)} frames; proxy target E*: min {e.min():.2f}  median {np.median(e):.2f}  max {e.max():.2f} stops "
          f"(spread {e.max() - e.min():.1f})")
    cv, preds = cross_validate(frames, max(4, a.starts // 2))
    print("\nleave-one-scene-out RMS error of predicted exposure against the proxy target, in stops (lower is better):")
    for m, v in sorted(cv.items(), key=lambda kv: kv[1]):
        print(f"   {m:28s} {v:5.2f}")
    p = fit_metering([f[2] for f in frames], e, a.starts)
    fitted = np.array([meter(f[2], p) for f in frames])
    print(f"\nfitted on every frame (in-sample RMS {rms(fitted, e):.2f} stops):")
    print("   " + "  ".join(f"{n}={getattr(p, n):.2f}" for n in NAMES))
    gm = baseline_predictions([f[2] for f in frames], "geometric_mean", None)
    print(f"   textbook geometric mean (key 0.18), in-sample: {rms(gm, e):.2f} stops")
    print("\nper frame (log2 E*, ours, textbook geometric mean):")
    for n, ev, pv, gv in zip(names, e, fitted, gm):
        print(f"   {n:42s} {ev:6.2f} {pv:6.2f} {gv:6.2f}")
    (out / "exposure_fit.json").write_text(json.dumps(
        {"params": {n: getattr(p, n) for n in NAMES}, "cv_rms_stops": cv, "in_sample_rms": rms(fitted, e),
         "frames": {n: {"e_star": float(ev), "ours": float(pv)} for n, ev, pv in zip(names, e, fitted)}}, indent=1))
    print("\nwrote", (out / "exposure_fit.json").resolve())


if __name__ == "__main__":
    main()
