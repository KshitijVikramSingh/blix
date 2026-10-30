"""Glare as a sum of Gaussians fitted to the diffraction point-spread function.

    uv run python -m tmlab.glare [--terms 6]

A lens turns a point of light into its point-spread function (PSF); for a circular aperture that is the Airy pattern,

    I(x) = (2 J1(x) / x)^2,    x = pi r / (lambda N / p) = 3.8317 r / a        a = 1.22 lambda N / p

with r in pixels of pitch p, N the F-number, lambda the wavelength: the pattern's first dark ring is at r = a (the Airy
radius, in pixels). Everything about the shape is therefore in r / a, so ONE fit serves every F-number and only the scale
`a` changes. (GT7's slides describe 24 hand-calibrated parameters for each of ten F-numbers; the scaling law is why this
needs none of that for the diffraction part. It says nothing about scatter in a real lens, which is not in this model.)

The radial profile averaged over the rings falls as r^-3, over many decades: that heavy tail is what makes a bright source
glow far from itself, and it only shows when the source is HDR-bright (the tail is 1e-4 of the core a few tens of Airy
radii out). A Gaussian has no tail, so the PSF is approximated by a sum of Gaussians of increasing width,

    PSF(r) = sum_i  w_i / (2 pi s_i^2) * exp(-r^2 / (2 s_i^2)),    sum w_i = 1 (energy-preserving)

fitted in log space (so the tail counts as much as the core) over r / a in [0.05, 400]. On a GPU each term is a separable
blur at a resolution matched to its width, which is how the demo applies it.

SCATTER (second half of this module). Diffraction is only the aperture's part. The eye and a real lens also scatter light at
large angles, and the standard description of it is the Stiles-Holladay equivalent veiling luminance (CIE 146:2002 and its
predecessor CIE 135/1999, J.J. Vos):

    L_veil = E * 10 / theta^2        theta in degrees, valid about 1 to 30 degrees; E the illuminance the source makes at the eye
    age-adjusted:  x (1 + (age / 70)^4)

as a point-spread function that is 10 / theta^2 per steradian. Its energy between 1 and 30 degrees is
20 pi (pi/180)^2 ln(30) = 6.5% of the light (x1.016 at age 25), the right order for a healthy eye's straylight. Sources: CIE 146:2002
(summary in the TNO paper "On the cause of disability glare...", repository.tno.nl docId 8955). The formula is only given for
1-30 degrees; below 1 degree the diffraction part above takes over (a flat knee at 1 degree here), and above 30 degrees it
is rolled off, both choices of this lab, not of the standard. Scale: degrees on the screen, so it follows the field of view.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

import numpy as np
from scipy.optimize import least_squares
from scipy.special import j0, j1

RESULTS = Path(__file__).resolve().parents[1] / "results"
FIRST_ZERO = 3.8317059702075125   # first zero of J1: x at r = a
R_LO, R_HI = 0.05, 400.0          # fit range, in Airy radii


def airy(x):
    x = np.maximum(np.asarray(x, dtype=np.float64), 1e-9)
    return (2.0 * j1(x) / x) ** 2


def airy_ring_average(r_over_a, window: float = np.pi):
    """The Airy pattern's radial profile averaged over a window of one ring period in x: the envelope a Gaussian sum can follow.

    The average is the encircled-energy difference over the window divided by its area, so it conserves energy exactly
    (an arithmetic average of the intensity would inflate the profile wherever it is convex). The window is
    +-window/2 in x, narrowed to +-x/2 near the core so the core itself is not smeared. Unit total energy, per unit area in
    units of a^-2.
    """
    x = FIRST_ZERO * np.atleast_1d(np.asarray(r_over_a, dtype=np.float64))
    half = np.minimum(window / 2.0, 0.5 * x)
    lo, hi = (x - half) / FIRST_ZERO, (x + half) / FIRST_ZERO
    out = (encircled_energy(hi) - encircled_energy(lo)) / (np.pi * (hi ** 2 - lo ** 2))
    return out if np.ndim(r_over_a) else float(out[0])


def encircled_energy(r_over_a):
    """Fraction of an Airy pattern's energy within r (exact): 1 - J0(x)^2 - J1(x)^2."""
    x = FIRST_ZERO * np.asarray(r_over_a, dtype=np.float64)
    return 1.0 - j0(x) ** 2 - j1(x) ** 2


def gaussian_sum(r, weights, sigmas):
    r = np.asarray(r, dtype=np.float64)[..., None]
    w, s = np.asarray(weights), np.asarray(sigmas)
    with np.errstate(over="ignore", invalid="ignore"):   # the multi-start tries absurd widths; they lose
        return np.sum(w / (2.0 * np.pi * s ** 2) * np.exp(-r ** 2 / (2.0 * s ** 2)), axis=-1)


def gaussian_encircled(r, weights, sigmas):
    r = np.asarray(r, dtype=np.float64)[..., None]
    with np.errstate(over="ignore", invalid="ignore"):
        return np.sum(np.asarray(weights) * (1.0 - np.exp(-r ** 2 / (2.0 * np.asarray(sigmas) ** 2))), axis=-1)


GRID = np.geomspace(R_LO, R_HI, 400)


def _unpack(v):
    n = len(v) // 2
    w = np.exp(v[:n]); w = w / w.sum()
    return w, np.exp(v[n:])


def fit(terms: int = 6, starts: int = 24, seed: int = 0):
    """Weights (sum 1) and sigmas (in Airy radii) minimising the log-space error of the radial profile, multi-start."""
    target = np.log10(airy_ring_average(GRID))
    rng = np.random.default_rng(seed)
    best = None
    for _ in range(starts):
        sig0 = np.sort(np.exp(rng.uniform(np.log(0.15), np.log(250.0), terms)))
        v0 = np.concatenate([rng.normal(-np.log(terms), 0.8, terms) - 0.5 * np.log(sig0) * 0.0, np.log(sig0)])
        def residual(v):
            w, s = _unpack(v)
            # log error over the profile, plus the encircled energy at a few radii (so the core's energy is right)
            prof = np.log10(np.maximum(gaussian_sum(GRID, w, s), 1e-30)) - target
            rs = np.array([0.5, 1.0, 2.0, 5.0, 20.0, 100.0])
            ee = 3.0 * (gaussian_encircled(rs, w, s) - encircled_energy(rs))
            return np.concatenate([prof, ee])
        sol = least_squares(residual, v0, method="lm", max_nfev=4000)
        if best is None or sol.cost < best.cost:
            best = sol
    w, s = _unpack(best.x)
    order = np.argsort(s)
    return w[order], s[order]


def report(w, s) -> dict:
    target = airy_ring_average(GRID)
    err = np.log10(gaussian_sum(GRID, w, s)) - np.log10(target)
    ee = np.abs(gaussian_encircled(np.array([1.0, 2.0, 5.0, 20.0, 100.0]), w, s) - encircled_energy(np.array([1.0, 2.0, 5.0, 20.0, 100.0])))
    return {"terms": int(len(w)), "weights": w.tolist(), "sigmas_in_airy_radii": s.tolist(),
            "max_abs_log10_error": float(np.abs(err).max()), "rms_log10_error": float(np.sqrt((err ** 2).mean())),
            "max_encircled_energy_error": float(ee.max()), "fit_range_airy_radii": [R_LO, R_HI]}


# ---- scatter -------------------------------------------------------------------------------------------------------------

THETA_LO, THETA_HI = 1.0, 30.0           # degrees: the Stiles-Holladay range
SCATTER_GRID = np.geomspace(0.2, 60.0, 300)


def stiles_holladay_fraction(age: float = 25.0) -> float:
    """Fraction of a source's light the Stiles-Holladay veil carries between 1 and 30 degrees, age adjusted."""
    return 20.0 * np.pi * (np.pi / 180.0) ** 2 * np.log(THETA_HI / THETA_LO) * (1.0 + (age / 70.0) ** 4)


def scatter_profile(theta):
    """The scatter envelope, unnormalised: 10/theta^2 inside 1..30 degrees, flat below 1 and rolled off above 30 (this lab's knees)."""
    t = np.asarray(theta, dtype=np.float64)
    return 1.0 / (t ** 2 + THETA_LO ** 2) / (1.0 + (t / THETA_HI) ** 4)


def _scatter_norm() -> float:
    t = np.geomspace(1e-3, 2000.0, 200000)
    return float(np.trapezoid(2 * np.pi * t * scatter_profile(t), t))


def fit_scatter(terms: int = 5, starts: int = 24, seed: int = 0):
    """Gaussian-sum (weights sum 1, sigmas in degrees) approximating the unit-energy scatter profile, log-space, multi-start."""
    target = np.log10(scatter_profile(SCATTER_GRID) / _scatter_norm())
    rng = np.random.default_rng(seed)
    best = None
    for _ in range(starts):
        sig0 = np.sort(np.exp(rng.uniform(np.log(0.3), np.log(40.0), terms)))
        v0 = np.concatenate([rng.normal(-np.log(terms), 0.8, terms), np.log(sig0)])
        def residual(v):
            w, sg = _unpack(v)
            return np.log10(np.maximum(gaussian_sum(SCATTER_GRID, w, sg), 1e-30)) - target
        sol = least_squares(residual, v0, method="lm", max_nfev=4000)
        if best is None or sol.cost < best.cost:
            best = sol
    w, sg = _unpack(best.x)
    order = np.argsort(sg)
    return w[order], sg[order]


def scatter_report(w, sg, age: float = 25.0) -> dict:
    err = np.log10(gaussian_sum(SCATTER_GRID, w, sg)) - np.log10(scatter_profile(SCATTER_GRID) / _scatter_norm())
    return {"terms": int(len(w)), "weights": w.tolist(), "sigmas_deg": sg.tolist(), "max_abs_log10_error": float(np.abs(err).max()),
            "rms_log10_error": float(np.sqrt((err ** 2).mean())), "fit_range_deg": [float(SCATTER_GRID[0]), float(SCATTER_GRID[-1])],
            "fraction_age25": float(stiles_holladay_fraction(age)),
            "source": "Stiles-Holladay veiling luminance 10/theta^2 (deg), CIE 146:2002; knees at 1 and 30 degrees are this lab's"}


def main_scatter():
    RESULTS.mkdir(exist_ok=True)
    for n in (4, 5, 6):
        w, sg = fit_scatter(n)
        r = scatter_report(w, sg)
        print(f"scatter {n} terms: max |log10 error| {r['max_abs_log10_error']:.3f} (x{10 ** r['max_abs_log10_error']:.2f}); sigmas deg {np.round(sg, 2)} weights {np.round(w, 4)}")
        if n == 4:   # four terms: x1.31 everywhere, four more chains in the demo instead of five
            (RESULTS / "scatter_fit.json").write_text(json.dumps(r, indent=1))
    print(f"fraction of light scattered into 1-30 degrees, age 25: {stiles_holladay_fraction(25.0):.4f}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--scatter", action="store_true", help="fit the Stiles-Holladay scatter instead (writes results/scatter_fit.json)")
    ap.add_argument("--terms", type=int, nargs="*", default=[3, 4, 5, 6, 8])
    ap.add_argument("--save", type=int, default=6, help="which term count to write to results/glare_fit.json")
    args = ap.parse_args()
    RESULTS.mkdir(exist_ok=True)
    for n in args.terms:
        w, s = fit(n)
        r = report(w, s)
        print(f"{n} terms: max |log10 error| {r['max_abs_log10_error']:.3f} (x{10 ** r['max_abs_log10_error']:.2f}), rms {r['rms_log10_error']:.3f}, "
              f"encircled energy error {r['max_encircled_energy_error']:.4f}; sigmas {np.round(s, 2)} weights {np.round(w, 4)}")
        if n == args.save:
            (RESULTS / "glare_fit.json").write_text(json.dumps(r, indent=1))
            print(f"  wrote {RESULTS / 'glare_fit.json'}")


if __name__ == "__main__":
    import sys
    main_scatter() if "--scatter" in sys.argv else main()
