"""Aperture-blade diffraction spikes: ground truth from the exact polygon PSF, and the streak kernel the demo implements.

    uv run python -m tmlab.spikes

The far-field PSF of an aperture is the squared magnitude of the aperture's Fourier transform. For a circle that is the Airy
pattern (glare.py). For a polygon of N straight blades each straight edge diffracts light into a thin streak perpendicular to
itself, so the PSF grows spikes:

    N even:  N spikes (opposite edges are parallel and share a spike line)      N odd:  2N spikes (each edge's streak has
    no parallel partner, and the PSF is point-symmetric, so every normal also appears reversed)

The spike along an edge normal falls off roughly as r^-2 (the circle's smoothed profile falls as r^-3: the spikes carry the
light that stays far out), and is only about an Airy radius wide. This module computes the PSF by FFT, measures spike count,
direction and falloff against exactly those statements, and fits the amplitude of the streak kernel
    line density(r) = A / (r^2 + r0^2)      r in Airy radii of the equal-area circle, per spike
that spikes.glsl-style passes in the demo apply: one blur along each spike line, summed (see TonemapLab/Spikes in Program.cs).
Rounded blades, aperture dirt and dispersion (colour fringes) are not modelled.
"""
from __future__ import annotations

import json
from pathlib import Path

import numpy as np

RESULTS = Path(__file__).resolve().parents[1] / "results"
FIRST_ZERO = 3.8317059702075125


def polygon_aperture(blades: int, grid: int = 4096, radius: float = 40.0, rotation: float = 0.0) -> np.ndarray:
    """A regular polygon of `blades` sides on a grid x grid array, area-equal to a circle of `radius` pixels.

    `rotation` turns the first vertex from the +x axis (radians). Returns a 0/1 float mask.
    """
    # equal-area circle radius -> circumradius: area of a regular n-gon = n/2 R^2 sin(2 pi / n) = pi radius^2
    circum = radius * np.sqrt(2.0 * np.pi / (blades * np.sin(2.0 * np.pi / blades)))
    y, x = np.mgrid[:grid, :grid].astype(np.float64) - grid / 2
    inside = np.ones((grid, grid), dtype=bool)
    apothem = circum * np.cos(np.pi / blades)
    for k in range(blades):
        nrm = rotation + 2.0 * np.pi * k / blades + np.pi / blades    # edge normal angle
        inside &= (x * np.cos(nrm) + y * np.sin(nrm)) <= apothem
    return inside.astype(np.float64)


def psf_of(aperture: np.ndarray) -> np.ndarray:
    """Normalised (sum 1) far-field intensity, zero frequency at the centre."""
    field = np.fft.fftshift(np.fft.fft2(np.fft.ifftshift(aperture)))
    psf = np.abs(field) ** 2
    return psf / psf.sum()


def airy_radius_px(grid: int, radius: float) -> float:
    """Airy radius (first dark ring) of the equal-area circle, in PSF pixels: the PSF pixel is 1/grid cycles per aperture pixel."""
    return FIRST_ZERO / (2.0 * np.pi) * grid / radius


def ring_profile(psf: np.ndarray, r_px: float, half_width: float = 0.5, samples: int = 1440) -> np.ndarray:
    """Intensity around a ring of radius r_px (averaged over a thin radial band), indexed by angle in samples steps of 360/samples."""
    n = psf.shape[0]
    c = n // 2
    ang = np.arange(samples) * 2 * np.pi / samples
    out = np.zeros(samples)
    for dr in np.linspace(-half_width, half_width, 5) * r_px:
        xs, ys = c + (r_px + dr) * np.cos(ang), c + (r_px + dr) * np.sin(ang)
        x0, y0 = np.floor(xs).astype(int), np.floor(ys).astype(int)
        fx, fy = xs - x0, ys - y0
        out += (psf[y0, x0] * (1 - fx) * (1 - fy) + psf[y0, x0 + 1] * fx * (1 - fy)
                + psf[y0 + 1, x0] * (1 - fx) * fy + psf[y0 + 1, x0 + 1] * fx * fy)
    return out / 5


def spike_angles(psf: np.ndarray, a_px: float, at: float = 25.0, samples: int = 1440, merge_deg: float = 12.0) -> np.ndarray:
    """Directions (radians, 0..2 pi) of the spikes: clusters of the azimuthal profile at `at` Airy radii standing well above its median.

    Across a spike the PSF oscillates (a sinc across the streak), so one spike shows several peaks within a few degrees; peaks
    closer than `merge_deg` are one spike, placed at the strongest sample of the cluster.
    """
    prof = ring_profile(psf, at * a_px, samples=samples)
    hot = prof > 3.0 * np.median(prof)
    step = 360.0 / samples
    idx = np.flatnonzero(hot)
    if len(idx) == 0:
        return np.array([])
    clusters, cur = [], [idx[0]]
    for i in idx[1:]:
        if (i - cur[-1]) * step < merge_deg:
            cur.append(i)
        else:
            clusters.append(cur); cur = [i]
    clusters.append(cur)
    if len(clusters) > 1 and (clusters[0][0] + samples - clusters[-1][-1]) * step < merge_deg:   # wraps around 360
        clusters[0] = clusters.pop() + clusters[0]
    return np.sort(np.array([c[int(np.argmax(prof[c]))] for c in clusters]) * 2 * np.pi / samples)


def spike_axis_profile(psf: np.ndarray, a_px: float, angle: float, radii_in_a, band: float = 1.5) -> np.ndarray:
    """Energy per unit radial length along a spike at `angle`, summed across the streak (+-`band` Airy radii), at the given radii.

    Units: PSF fraction per Airy radius. This is the line density the streak kernel has to reproduce.
    """
    n = psf.shape[0]
    c = n // 2
    out = []
    t = np.linspace(-band, band, int(band * 2 * 8) + 1)            # across the streak, in Airy radii
    for r in np.atleast_1d(radii_in_a):
        xs = c + (r * np.cos(angle) - t * np.sin(angle)) * a_px
        ys = c + (r * np.sin(angle) + t * np.cos(angle)) * a_px
        x0, y0 = np.floor(xs).astype(int), np.floor(ys).astype(int)
        fx, fy = xs - x0, ys - y0
        v = (psf[y0, x0] * (1 - fx) * (1 - fy) + psf[y0, x0 + 1] * fx * (1 - fy)
             + psf[y0 + 1, x0] * (1 - fx) * fy + psf[y0 + 1, x0 + 1] * fx * fy)
        out.append(np.trapezoid(v, t * a_px) * a_px)            # per Airy radius of length: (energy per px^2) * px across * px along
    return np.array(out)


GRID, RADIUS = 3072, 64.0
KNEE = 1.5   # Airy radii: inside this the spike is part of the core, and the kernel starts here


def measure(blades: int, rotation: float = 0.0) -> dict:
    """Spike count, directions and the binned axis profile's fit A / r^2 (r in Airy radii, 3..45), for one blade count."""
    from scipy.optimize import least_squares
    psf = psf_of(polygon_aperture(blades, GRID, RADIUS, rotation))
    a = airy_radius_px(GRID, RADIUS)
    ang = spike_angles(psf, a)
    rs = np.geomspace(3.0, 45.0, 400)
    prof = np.mean([spike_axis_profile(psf, a, t, rs) for t in ang], axis=0)
    edges = np.geomspace(3.0, 45.0, 13)
    b = np.digitize(rs, edges)
    rc = np.array([np.exp(np.mean(np.log(rs[b == i]))) for i in range(1, 13)])
    pc = np.array([prof[b == i].mean() for i in range(1, 13)])
    slope = float(np.polyfit(np.log(rc), np.log(pc), 1)[0])
    amp = float(np.exp(np.mean(np.log(pc * rc ** 2))))   # A in A / r^2, fitted with the exponent fixed at 2
    return {"blades": blades, "spikes": int(len(ang)), "angles_deg": np.degrees(ang).tolist(), "slope": slope,
            "amplitude_per_spike": amp, "amplitude_total": amp * len(ang)}


def main():
    RESULTS.mkdir(exist_ok=True)
    rows = [measure(n) for n in (5, 6, 7, 8)]
    for r in rows:
        print(f"{r['blades']} blades: {r['spikes']:2d} spikes, slope {r['slope']:+.2f}, A per spike {r['amplitude_per_spike']:.4f}, total {r['amplitude_total']:.3f}")
    total = float(np.mean([r["amplitude_total"] for r in rows]))
    out = {"rows": rows, "amplitude_total_mean": total, "knee_airy_radii": KNEE,
           "note": "line density per spike = amplitude_total / spikes / r^2, r in Airy radii of the equal-area circle, from r = knee outward; "
                   "measured on the FFT of a regular polygon aperture, 5 to 8 blades"}
    (RESULTS / "spike_fit.json").write_text(json.dumps(out, indent=1))
    print(f"mean total amplitude {total:.3f}; wrote {RESULTS / 'spike_fit.json'}")


if __name__ == "__main__":
    main()
