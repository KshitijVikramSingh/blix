"""Auto-exposure as adaptation dynamics, tuned against written-down scenario specs.

    uv run python -m tmlab.adaptation [--no-tune]

Why this and not a per-frame "best exposure": GT7's slides (176-221) reject the camera model in which every frame is
properly exposed ("life doesn't always want to be properly exposed": night stays dark, snow stays blinding) and get
those cases from the eye's own limits instead: a short-term stage (pupil and neural gain, 3-5 EV, a few seconds) held
inside a window set by a long-term stage (receptor sensitivity, ~12 EV, tens of minutes). The static proxy fit in
exposure.py failed leave-one-scene-out for the opposite reason: its target lifted a night frame by 3.9 stops.

The model, in log2 luminance (EV). All times in seconds.

    m(t)   metered scene level, from the frame (trimmed log-average) blended with an optional world reference
    S      short-term level   follows m, tau_s_light when m > S (brightening), tau_s_dark when m < S
    A      long-term level    follows m, tau_a_light / tau_a_dark
    S is clamped to [A - range_dark, A + range_light]       (the pupil saturates; it recovers the moment m is back inside)
    exposure  e = key - S           eps = m - S is the exposure error: negative = darker than proper, positive = brighter

Dusk is slow because A lags and the window holds S above m; the snow field blinds because S cannot rise past A +
range_light. No override, no context volume.

WHAT IS AND IS NOT FITTED. GT7 publishes only the ranges and orders of magnitude, not the constants, and there is no
measured data here, so nothing is "fitted to data". The parameters are TUNED to the SPECS below, which are this lab's
own statements of the behaviour GT7 describes (seconds for a tunnel, tens of minutes for dusk, light adaptation faster
than dark, a night frame not lifted, a retroreflective sign not mistaken for the scene). The table `main` prints says
which specs hold, before and after tuning, and the tuning pushes the parameters only as far as the specs require.
"""
from __future__ import annotations

import argparse
import json
from dataclasses import dataclass, replace
from pathlib import Path

import numpy as np
from scipy.optimize import differential_evolution

from . import capture
from .exposure import CENTERS, log_histogram

KEY = float(np.log2(0.18))  # where the metered level lands; ev below are relative to it


@dataclass(frozen=True)
class Adaptation:
    tau_s_light: float = 0.4     # s, short-term, scene brighter than S: light adaptation is the fast one
    tau_s_dark: float = 1.5      # s, short-term, scene darker than S
    range_dark: float = 4.0      # EV: how far BELOW the long-term level S may sit
    range_light: float = 4.0     # EV: how far ABOVE
    tau_a_light: float = 90.0    # s, long-term, brightening
    tau_a_dark: float = 900.0    # s, long-term, darkening ("tens of minutes")
    trim_hi: float = 0.03        # fraction of the brightest pixels the meter ignores (signs, speculars, the sun)
    w_world: float = 0.3         # weight of the position-independent reference against the frame, for camera cuts


# The result of tuning, rounded, as a constant to use. Each value is inside the feasible interval `main` prints (the
# trim and world weight in the middle of their joint region, not on its edge). Held by tests/test_adaptation.py.
TUNED = Adaptation(tau_s_light=0.4, tau_s_dark=1.6, range_dark=4.0, range_light=4.0, tau_a_light=90.0, tau_a_dark=900.0,
                   trim_hi=0.09, w_world=0.64)


NAMES = ("tau_s_light", "tau_s_dark", "range_dark", "range_light", "tau_a_light", "tau_a_dark", "trim_hi", "w_world")
BOUNDS = ((0.1, 2.0), (0.3, 6.0), (3.0, 5.0), (3.0, 5.0), (20.0, 600.0), (100.0, 3000.0), (0.0, 0.15), (0.0, 1.0))
LOG_SCALE = {"tau_s_light", "tau_s_dark", "tau_a_light", "tau_a_dark"}  # searched in log space


# --- metering -------------------------------------------------------------------------------
def median_level(hist) -> float:
    return float(np.interp(0.5, np.cumsum(hist), CENTERS))


def with_extras(hist, extras=()):
    """`extras`: [(area fraction, log2 luminance)]: a sign, a sky, a flare, mixed into the histogram as point masses."""
    h = np.array(hist, dtype=np.float64) * (1.0 - sum(f for f, _ in extras))
    for f, level in extras:
        h[int(np.clip(np.searchsorted(ex_edges(), level) - 1, 0, len(h) - 1))] += f
    return h


def ex_edges():
    from .exposure import EDGES
    return EDGES


def meter_ev(hist, trim_hi: float, gain_ev: float = 0.0, extras=()) -> float:
    """Trimmed log-average luminance: the brightest `trim_hi` of the pixels are not counted. Exposure-equivariant."""
    h = with_extras(hist, extras)
    c = np.cumsum(h)
    kept = np.clip((1.0 - trim_hi) - (c - h), 0.0, h)  # mass of each bin below the cut (a fractional bin is split)
    return float((kept * CENTERS).sum() / max(kept.sum(), 1e-12)) + gain_ev


# --- dynamics ---------------------------------------------------------------------------------
def simulate(m, dt: float, p: Adaptation, start: float | None = None):
    """Run the two stages over a metered series m (EV, one value per step of dt seconds). Returns (S, A)."""
    m = np.asarray(m, dtype=np.float64)
    S = np.empty_like(m)
    A = np.empty_like(m)
    s = a = float(m[0] if start is None else start)
    ks = (1.0 - np.exp(-dt / p.tau_s_light), 1.0 - np.exp(-dt / p.tau_s_dark))
    ka = (1.0 - np.exp(-dt / p.tau_a_light), 1.0 - np.exp(-dt / p.tau_a_dark))
    for i, v in enumerate(m):
        s += (v - s) * (ks[0] if v > s else ks[1])
        a += (v - a) * (ka[0] if v > a else ka[1])
        s = min(max(s, a - p.range_dark), a + p.range_light)
        S[i], A[i] = s, a
    return S, A


def first_time(t, cond, after: float, default: float = 1e9) -> float:
    idx = np.nonzero(cond & (t >= after))[0]
    return float(t[idx[0]] - after) if idx.size else default


# --- scenarios and specs ------------------------------------------------------------------------
@dataclass
class Spec:
    scenario: str
    name: str
    describes: str
    lo: float
    hi: float
    scale: float  # tolerance for the penalty: a miss of `scale` costs 1


def load_bases(data_dir: Path):
    """Histograms the scenarios use: a night frame with lamps and speculars, and two framings of one lit scene."""
    def h(name):
        return log_histogram(capture.load(Path(data_dir) / f"{name}.pfm").rgb)
    night = h("night_lamps__night__z1.7")
    cands = {z: h(f"materials_grid__bright__z{z}") for z in ("1", "1.7", "3")}
    return {"night": night, "cuts": cands}


def run(p: Adaptation, bases: dict) -> dict:
    """Every scenario, with the metrics the specs are written against. Returns {metric name: value} and traces."""
    out, traces = {}, {}

    def step_scenario(name, dt, levels, t_end):
        t = np.arange(0.0, t_end, dt)
        m = np.zeros_like(t)
        for t0, lvl in levels:
            m[t >= t0] = lvl
        S, A = simulate(m, dt, p)
        traces[name] = (t, m, S, A)
        return t, m, S, A

    # tunnel: 8 EV dark for 8 s, entered at 10 s, left at 18 s.
    t, m, S, A = step_scenario("tunnel", 0.05, [(10.0, -8.0), (18.0, 0.0)], 40.0)
    eps = m - S
    at = lambda x: float(eps[int(round(x / 0.05))])
    out["tunnel.recovers_3s"] = at(13.0)
    out["tunnel.still_dark_at_exit"] = at(17.95)
    out["tunnel.exit_flash"] = at(18.05)
    out["tunnel.exit_recovery_s"] = first_time(t, np.abs(eps) < 1.0, 18.05)

    # cloud: the sun goes behind a cloud for 6 s (5 EV).
    t, m, S, A = step_scenario("cloud", 0.05, [(10.0, -5.0), (16.0, 0.0)], 40.0)
    eps = m - S
    out["cloud.recovers_4s"] = float(eps[int(round(14.0 / 0.05))])
    out["cloud.exit_recovery_s"] = first_time(t, np.abs(eps) < 0.5, 16.05)

    # dusk and dawn: 14 EV over 30 minutes, then 30 minutes more at the end level.
    def ramp(name, a, b):
        t = np.arange(0.0, 3600.0, 2.0)
        m = np.where(t < 1800.0, a + (b - a) * t / 1800.0, b)
        S, A = simulate(m, 2.0, p)
        traces[name] = (t, m, S, A)
        eps = m - S
        return t, eps

    t, eps = ramp("dusk", 0.0, -14.0)
    out["dusk.lag_at_dark"] = float(eps[900])
    out["dusk.settled_1h"] = float(abs(eps[-1]))
    lag_dusk = abs(float(eps[900]))
    t, eps = ramp("dawn", -14.0, 0.0)
    # The slides claim an asymmetry, not a size: dawn is not dusk run backwards, because light adaptation is faster.
    out["dawn.asymmetry"] = abs(float(eps[900])) / max(lag_dusk, 1e-6)

    # snow: a field 6 EV brighter than what the eye is adapted to, for five minutes.
    t = np.arange(0.0, 320.0, 0.5)
    m = np.where(t < 10.0, 0.0, 6.0)
    S, A = simulate(m, 0.5, p)
    traces["snow"] = (t, m, S, A)
    eps = m - S
    out["snow.blinding_5s"] = float(eps[int(round(15.0 / 0.5))])
    out["snow.adapted_5min"] = float(abs(eps[-1]))

    # retroreflective signs: a 0.5% patch, 12 EV above the frame's median, flashing in the headlights.
    night = bases["night"]
    med = median_level(night)
    sign = [(0.005, med + 12.0)]
    base_m = meter_ev(night, p.trim_hi)
    flash_m = meter_ev(night, p.trim_hi, extras=sign)
    t = np.arange(0.0, 40.0, 0.05)
    on = np.zeros_like(t, dtype=bool)
    for t0 in (5.0, 12.0, 19.0):
        on |= (t >= t0) & (t < t0 + 1.5)
    m_flash = np.where(on, flash_m, base_m)
    S_f, _ = simulate(m_flash, 0.05, p, start=base_m)
    S_0, _ = simulate(np.full_like(t, base_m), 0.05, p, start=base_m)
    traces["sign"] = (t, m_flash, S_f, S_0)
    out["sign.meter_shift"] = abs(flash_m - base_m)
    out["sign.exposure_shift"] = float(np.max(np.abs(S_f - S_0)))
    # speculars: glints on glossy spheres and chrome, 1.5% of the frame at +9 EV, added to the same frame. (Not "remove
    # everything above median + 6 EV from the frame": that region is 12% of this night frame and includes its sky band,
    # window and lamps, so the test confounded rejecting speculars with trimming real bright areas.)
    glints = [(0.015, med + 9.0)]
    out["specular.meter_shift"] = abs(meter_ev(night, p.trim_hi, extras=glints) - base_m)
    # but a real bright area is not ignored: a sky taking 20% of the frame, 6 EV above the median.
    sky = [(0.20, med + 6.0)]
    out["sky.meter_response"] = meter_ev(night, p.trim_hi, extras=sky) - base_m

    # camera cuts every 4.5 s between two framings of one lit scene; the world reference does not change.
    ms = {z: meter_ev(h, p.trim_hi) for z, h in bases["cuts"].items()}
    za, zb = max(((a, b) for a in ms for b in ms if a < b), key=lambda ab: abs(ms[ab[0]] - ms[ab[1]]))
    world = 0.5 * (ms[za] + ms[zb])
    t = np.arange(0.0, 60.0, 0.05)
    frame_m = np.where(((t // 4.5) % 2) == 0, ms[za], ms[zb])
    m = (1.0 - p.w_world) * frame_m + p.w_world * world
    S, _ = simulate(m, 0.05, p, start=float(m[0]))
    traces["cuts"] = (t, m, S, S)
    out["cuts.exposure_p2p"] = float(np.ptp(S[t >= 20.0]))
    out["cuts.framing_gap"] = abs(ms[za] - ms[zb])
    return out, traces


SPECS = (
    Spec("tunnel", "tunnel.recovers_3s", "3 s after entering an 8 EV tunnel the error has come back from -8 to above -5", -5.0, np.inf, 0.3),
    Spec("tunnel", "tunnel.still_dark_at_exit", "but it has not fully adapted by the end of the 8 s tunnel: still at least 2.5 EV dark", -np.inf, -2.5, 0.3),
    Spec("tunnel", "tunnel.exit_flash", "leaving it, the view blows out by at least 2 EV", 2.0, np.inf, 0.3),
    Spec("tunnel", "tunnel.exit_recovery_s", "and recovers to within 1 EV in 1.5 s (light adaptation is the fast one)", 0.0, 1.5, 0.3),
    Spec("cloud", "cloud.recovers_4s", "behind a 5 EV cloud, within 2 EV of proper after 4 s", -2.0, 0.0, 0.3),
    Spec("cloud", "cloud.exit_recovery_s", "and back to within 0.5 EV within 2 s of it passing", 0.0, 2.0, 0.3),
    Spec("dusk", "dusk.lag_at_dark", "dusk: when the light bottoms out the view is still at least 1.5 EV darker than proper", -np.inf, -1.5, 0.3),
    Spec("dusk", "dusk.settled_1h", "an hour later it is within 2 EV (the 14 EV range exceeds what adaptation covers)", 0.0, 2.0, 0.3),
    Spec("dawn", "dawn.asymmetry", "dawn is not dusk reversed: at the end of an equal 30-minute ramp its lag is at most half of dusk's", 0.0, 0.5, 0.1),
    Spec("snow", "snow.blinding_5s", "a field 6 EV brighter than the adapted level still blinds by at least 1 EV after 5 s", 1.0, np.inf, 0.3),
    Spec("snow", "snow.adapted_5min", "and is within 0.5 EV after five minutes", 0.0, 0.5, 0.25),
    Spec("sign", "sign.meter_shift", "a retroreflective sign (0.5% of the frame, +12 EV) moves the metered level by under 0.15 EV", 0.0, 0.15, 0.1),
    Spec("sign", "sign.exposure_shift", "and the exposure by under 0.15 EV", 0.0, 0.15, 0.1),
    Spec("sign", "specular.meter_shift", "speculars (glints, 1.5% of the frame at +9 EV) move the meter by under 0.10 EV", 0.0, 0.10, 0.1),
    Spec("sign", "sky.meter_response", "but a sky filling 20% of the frame is not ignored: the meter moves at least 0.4 EV", 0.4, np.inf, 0.2),
    Spec("cuts", "cuts.exposure_p2p", "across camera cuts every 4.5 s the exposure swings by at most 1.0 EV (the world reference steadies it)", 0.2, 1.0, 0.2),
)


def violations(values: dict) -> list:
    rows = []
    for s in SPECS:
        v = values[s.name]
        miss = max(s.lo - v, v - s.hi, 0.0)
        rows.append((s, v, miss))
    return rows


def penalty(p: Adaptation, bases: dict) -> float:
    values, _ = run(p, bases)
    return float(sum((miss / s.scale) ** 2 for s, _, miss in violations(values)))


def to_params(vec) -> Adaptation:
    d = {n: (float(np.exp(v)) if n in LOG_SCALE else float(v)) for n, v in zip(NAMES, vec)}
    return Adaptation(**d)


def to_vector(p: Adaptation) -> np.ndarray:
    return np.array([np.log(getattr(p, n)) if n in LOG_SCALE else getattr(p, n) for n in NAMES])


def tune(bases: dict, seed: int = 0, maxiter: int = 60):
    bounds = [(np.log(lo), np.log(hi)) if n in LOG_SCALE else (lo, hi) for n, (lo, hi) in zip(NAMES, BOUNDS)]
    # Start from the written-down defaults, so the tuning moves the parameters only as far as the specs force.
    x0 = np.clip(to_vector(Adaptation()), [b[0] for b in bounds], [b[1] for b in bounds])
    r = differential_evolution(lambda v: penalty(to_params(v), bases) + 1e-3 * float(np.sum(((v - x0) / (np.array(bounds)[:, 1] - np.array(bounds)[:, 0])) ** 2)),
                               bounds, seed=seed, maxiter=maxiter, popsize=14, tol=1e-8, x0=x0, polish=False)
    return to_params(r.x)


def feasible_grid(p: Adaptation, bases: dict, a: str = "trim_hi", b: str = "w_world", n: int = 16):
    """Joint feasibility of two parameters that interact (the meter's trim and the world weight both bear on camera cuts).

    Returns (a-values, b-values, ok[a, b]). One parameter at a time understates freedom when two specs pull on the
    same pair: raising the trim helps speculars and hurts cuts, and the world weight can pay for it.
    """
    ba, bb = dict(zip(NAMES, BOUNDS))[a], dict(zip(NAMES, BOUNDS))[b]
    va, vb = np.linspace(*ba, n), np.linspace(*bb, n)
    ok = np.array([[penalty(replace(p, **{a: float(x), b: float(y)}), bases) <= 1e-9 for y in vb] for x in va])
    return va, vb, ok


def recentre_pair(p: Adaptation, bases: dict, a: str = "trim_hi", b: str = "w_world") -> Adaptation:
    """Move two interacting parameters to the centroid of their joint feasible region, away from the spec edges.

    Tuning with a pull toward the defaults leaves a parameter wherever the spec first lets go of it, which is the edge:
    no margin. The specs' tolerances are this lab's own, so a point with room on every side is the better choice.
    """
    va, vb, ok = feasible_grid(p, bases, a, b, n=31)
    if not ok.any():
        return p
    ia, ib = np.nonzero(ok)
    return replace(p, **{a: float(va[ia].mean()), b: float(vb[ib].mean())})


def smoothing_only(p: Adaptation) -> Adaptation:
    """The textbook auto-exposure: one symmetric exponential smoother, no window, no long-term stage. Same meter as `p`."""
    return replace(p, tau_s_light=1.0, tau_s_dark=1.0, range_dark=60.0, range_light=60.0)


def feasible_ranges(p: Adaptation, bases: dict, points: int = 41) -> dict:
    """For each parameter, the interval around its value over which every spec still holds, the others held fixed.

    Narrow = the specs pin it. Wide (out to its bound) = the specs leave it free: a taste choice, not a result.
    """
    out = {}
    for n, (lo, hi) in zip(NAMES, BOUNDS):
        grid = np.geomspace(lo, hi, points) if n in LOG_SCALE else np.linspace(lo, hi, points)
        ok = np.array([penalty(replace(p, **{n: float(v)}), bases) <= 1e-9 for v in grid])
        here = int(np.argmin(np.abs(grid - getattr(p, n))))
        a = b = here
        while a > 0 and ok[a - 1]:
            a -= 1
        while b < points - 1 and ok[b + 1]:
            b += 1
        out[n] = (float(grid[a]), float(grid[b]), (lo, hi), bool(ok[here]))
    return out


def table(values_by_label: dict) -> str:
    labels = list(values_by_label)
    head = f"{'spec':28s} " + " ".join(f"{l:>14s}" for l in labels) + "   target"
    lines = [head]
    for s in SPECS:
        cells = []
        for l in labels:
            v = values_by_label[l][s.name]
            ok = s.lo - 1e-9 <= v <= s.hi + 1e-9
            cells.append(f"{v:11.2f} {'ok' if ok else '--'}")
        lo = "" if np.isinf(s.lo) else f"{s.lo:g}"
        hi = "" if np.isinf(s.hi) else f"{s.hi:g}"
        lines.append(f"{s.name:28s} " + " ".join(f"{c:>14s}" for c in cells) + f"   [{lo} .. {hi}]")
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="data/metering")
    ap.add_argument("--no-tune", action="store_true")
    ap.add_argument("--out", default="out")
    a = ap.parse_args()
    bases = load_bases(Path(a.data))
    default = Adaptation()
    tuned = default if a.no_tune else recentre_pair(tune(bases), bases)
    vd, td = run(default, bases)
    vt, tt = run(tuned, bases)
    vn, tn = run(smoothing_only(tuned), bases)
    print(table({"smoothing only": vn, "defaults": vd, "tuned": vt}))
    print()
    for label, p in (("defaults", default), ("tuned", tuned)):
        print(f"{label:9s} " + "  ".join(f"{n}={getattr(p, n):.3g}" for n in NAMES))
    nd = sum(1 for _, _, m in violations(vd) if m > 1e-9)
    nt = sum(1 for _, _, m in violations(vt) if m > 1e-9)
    nn = sum(1 for _, _, m in violations(vn) if m > 1e-9)
    print(f"\nspecs missed: smoothing only {nn} of {len(SPECS)}, defaults {nd}, tuned {nt}")
    print("\nwhere each tuned parameter is free (the interval over which every spec still holds, others fixed):")
    ranges = feasible_ranges(tuned, bases)
    for n, (lo, hi, (blo, bhi), ok) in ranges.items():
        pinned = "PINNED" if (hi / max(lo, 1e-9) < 1.3 if n in LOG_SCALE else (hi - lo) < 0.15 * (bhi - blo)) else "free-ish"
        edge = "  (runs to a bound)" if (lo <= blo * 1.001 or hi >= bhi * 0.999) else ""
        print(f"   {n:13s} {getattr(tuned, n):8.3g}   holds over [{lo:.3g}, {hi:.3g}]   bound [{blo:g}, {bhi:g}]   {pinned}{edge}")
    out = Path(a.out); out.mkdir(exist_ok=True)
    va, vb, ok = feasible_grid(tuned, bases)
    print("\ntrim_hi (rows) against w_world (columns): # = every spec holds. One parameter at a time looks pinned;"
          " the pair is not.")
    print("   w_world " + " ".join(f"{y:4.2f}" for y in vb))
    for x, row in zip(va, ok):
        print(f"   trim {x:5.3f}  " + "    ".join("#" if v else "." for v in row))
    (out / "adaptation.json").write_text(json.dumps({
        "defaults": {n: getattr(default, n) for n in NAMES}, "tuned": {n: getattr(tuned, n) for n in NAMES},
        "values_defaults": vd, "values_tuned": vt, "values_smoothing_only": vn,
        "feasible": {n: {"lo": lo, "hi": hi} for n, (lo, hi, _, _) in ranges.items()},
        "specs": [s.describes for s in SPECS]}, indent=1))
    print("wrote", (out / "adaptation.json").resolve())


if __name__ == "__main__":
    main()
