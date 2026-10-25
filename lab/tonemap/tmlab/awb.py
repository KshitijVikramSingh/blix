"""Auto white balance as adaptation dynamics, the same mechanism as the auto-exposure in adaptation.py.

    uv run python -m tmlab.awb

GT7's slide 222 says auto white balance is the exposure mechanism applied to colour temperature and tint, toward D65, with a
faint virtual light for stability, limits taken from real camera AWB, and the blend done luminance-weighted in Yxy. The slides
give no formulas or constants, so this is the lab's reading of it, and every constant below is CHOSEN to meet the specs in
tests/test_awb.py, not fitted to data.

The model. The illuminant estimate is (mired, tint): a point on the Planckian locus (mired = 1e6 / kelvin, so equal steps
are about equally visible) and a green(-)/magenta(+) tint, in the units of `gains`. D65 is (153.8 mired, 0 tint).

    measure    trimmed, saturation-discounted mean of the frame's linear RGB (the brightest `trim` of pixels ignored, as in
               the exposure meter; saturated pixels count for less, since a green meadow is not a green light) ->
               the (mired, tint) whose gains turn that mean neutral
    target     D65 + w * (measured - D65),   w = (1 - virtual) * confidence(meter EV)
               `virtual` is a faint light that is always there (a floor against any one frame's cast); `confidence` falls
               to 0 in the dark, where a frame's colour says nothing about the light, so the estimate relaxes to D65 and
               a night scene is not "corrected"
    limits     the estimate stays inside [kelvin_lo, kelvin_hi] and +-tint_limit (what a camera's AWB will believe)
    state      follows the target exponentially, time constant `tau`
    applied    the gains for D65 + strength * (state - D65): PARTIAL adaptation. 1.0 cancels the cast completely (a sunset
               turns white), 0 is off. A stylised scene opts out with strength 0.

Gains are normalised to leave a neutral's luminance alone, so white balance does not move exposure.
"""
from __future__ import annotations

from dataclasses import dataclass, replace

import numpy as np

D65_MIRED = 1e6 / 6504.0
LUMA = np.array([0.2126, 0.7152, 0.0722])


def planckian_xy(kelvin):
    """The Planckian locus in CIE xy, Kim et al. (2002), valid 1667-25000 K (same cubic splines as WhiteBalance.cs)."""
    t = np.clip(np.asarray(kelvin, dtype=np.float64), 1667.0, 25000.0)
    x = np.where(t <= 4000.0,
                 -0.2661239e9 / t ** 3 - 0.2343589e6 / t ** 2 + 0.8776956e3 / t + 0.179910,
                 -3.0258469e9 / t ** 3 + 2.1070379e6 / t ** 2 + 0.2226347e3 / t + 0.240390)
    y = np.where(t <= 2222.0, -1.1063814 * x ** 3 - 1.34811020 * x ** 2 + 2.18555832 * x - 0.20219683,
                 np.where(t <= 4000.0, -0.9549476 * x ** 3 - 1.37418593 * x ** 2 + 2.09137015 * x - 0.16748867,
                          3.0817580 * x ** 3 - 5.87338670 * x ** 2 + 3.75112997 * x - 0.37001483))
    return x, y


def _light_rgb(mired):
    """The light's linear sRGB at Y = 1, for a point on the locus, divided by D65's so that D65 is (1, 1, 1)."""
    def raw(m):
        x, y = planckian_xy(1e6 / np.asarray(m, dtype=np.float64))
        xx, zz = x / y, (1.0 - x - y) / y
        return np.stack([3.2404542 * xx - 1.5371385 - 0.4985314 * zz,
                         -0.9692660 * xx + 1.8760108 + 0.0415560 * zz,
                         0.0556434 * xx - 0.2040259 + 1.0572252 * zz], axis=-1)
    return raw(mired) / raw(D65_MIRED)


def gains(mired: float, tint: float, normalise: bool = True) -> np.ndarray:
    """Linear RGB gains that make a neutral under (mired, tint) grey; D65 with no tint is identity."""
    l = _light_rgb(mired)
    g = np.array([l[1] / l[0], 1.0 / (1.0 + tint), l[1] / l[2]])
    return g / float(g @ LUMA) if normalise else g


@dataclass(frozen=True)
class Awb:
    tau: float = 4.0            # s: how fast the estimate follows the target
    strength: float = 0.7       # partial adaptation: how much of the estimated cast is removed
    virtual: float = 0.15       # weight of the always-present D65 virtual light
    conf_lo: float = -9.0       # meter EV at and below which a frame's colour is not believed at all
    conf_hi: float = -5.0       # and at and above which it is believed fully
    kelvin_lo: float = 2200.0   # the estimate's limits, from what camera AWB accepts
    kelvin_hi: float = 12000.0
    tint_limit: float = 0.06   # tight: natural light barely varies in tint, and a frame's green is mostly foliage
    trim: float = 0.09          # brightest fraction of pixels ignored (as in the exposure meter)
    sat0: float = 0.3           # saturation at which a pixel's weight has halved
    sky_weight: float = 0.15    # what a pixel the renderer marked as sky counts for: sky is a light's colour seen through
                                # the air, not a surface lit by it, and it fills the frame (the ten HDRIs read 6100-11800 K
                                # unmasked, a blue-sky bias, not a measurement of the light on the ground)


def measure_mean(rgb: np.ndarray, p: Awb, sky: np.ndarray | None = None) -> np.ndarray:
    """The trimmed, saturation-discounted mean linear RGB of a frame (any shape ending in 3).

    `sky` is an optional mask the size of the frame, 1 where the renderer drew sky (the demo's meter pass writes it to alpha):
    those pixels count for `sky_weight`. Whatever else a renderer knows (a depth buffer, emissive flags) could go the same way.
    """
    flat = rgb.reshape(-1, 3).astype(np.float64)
    y = flat @ LUMA
    order = np.sort(y)
    threshold = order[int(np.ceil((1.0 - p.trim) * len(y))) - 1]
    keep = y <= threshold
    flat = flat[keep]
    top = flat.max(axis=1)
    sat = np.where(top > 1e-9, (top - flat.min(axis=1)) / np.maximum(top, 1e-9), 0.0)
    w = 1.0 / (1.0 + (sat / p.sat0) ** 2)
    if sky is not None:
        w = w * np.where(sky.reshape(-1)[keep] > 0.5, p.sky_weight, 1.0)
    return (flat * w[:, None]).sum(axis=0) / max(w.sum(), 1e-12)


def cast_of(mean: np.ndarray, p: Awb) -> tuple[float, float]:
    """The (mired, tint) whose gains turn `mean` neutral, within the limits: red/blue fixes the mired, green the tint."""
    r, g, b = np.maximum(mean, 1e-9)
    lo, hi = 1e6 / p.kelvin_hi, 1e6 / p.kelvin_lo
    for _ in range(60):   # the light's r/b rises with mired (warmer is redder): bisect on it
        mid = 0.5 * (lo + hi)
        l = _light_rgb(mid)
        if l[0] / l[2] > r / b: hi = mid
        else: lo = mid
    mired = 0.5 * (lo + hi)
    l = _light_rgb(mired)
    tint = float(np.clip(g * l[0] / (r * l[1]) - 1.0, -p.tint_limit, p.tint_limit))
    return float(mired), tint


def confidence(meter_ev: float, p: Awb) -> float:
    return float(np.clip((meter_ev - p.conf_lo) / (p.conf_hi - p.conf_lo), 0.0, 1.0))


class State:
    """The estimate, stepped like adaptation.AdaptationState."""

    def __init__(self, p: Awb):
        self.p = p
        self.mired, self.tint = D65_MIRED, 0.0

    def reset(self, mired: float, tint: float):
        self.mired, self.tint = mired, tint

    def target(self, measured: tuple[float, float], meter_ev: float) -> tuple[float, float]:
        p = self.p
        w = (1.0 - p.virtual) * confidence(meter_ev, p)
        m = np.clip(D65_MIRED + w * (measured[0] - D65_MIRED), 1e6 / p.kelvin_hi, 1e6 / p.kelvin_lo)
        return float(m), float(np.clip(w * measured[1], -p.tint_limit, p.tint_limit))

    def step(self, dt: float, measured: tuple[float, float], meter_ev: float):
        k = 1.0 - np.exp(-dt / self.p.tau)
        tm, tt = self.target(measured, meter_ev)
        self.mired += (tm - self.mired) * k
        self.tint += (tt - self.tint) * k

    def gains(self) -> np.ndarray:
        s = self.p.strength
        return gains(D65_MIRED + s * (self.mired - D65_MIRED), s * self.tint)


def simulate(frames, dt: float, p: Awb, start: tuple[float, float] | None = None):
    """Run `frames` = [(mean_rgb, meter_ev), ...] and return the (mired, tint) trace."""
    s = State(p)
    if start is not None: s.reset(*start)
    out = []
    for mean, ev in frames:
        s.step(dt, cast_of(np.asarray(mean), p), ev)
        out.append((s.mired, s.tint))
    return np.array(out)


def main():
    p = Awb()
    print("gains (luminance-normalised) for a neutral under a few lights, then after partial adaptation at strength", p.strength)
    for kelvin in (2700, 3200, 4500, 6504, 9000):
        m = 1e6 / kelvin
        full = gains(m, 0.0)
        part = gains(D65_MIRED + p.strength * (m - D65_MIRED), 0.0)
        residual = (_light_rgb(m) * part)
        print(f"  {kelvin:5d} K  full {np.round(full, 3)}  partial {np.round(part, 3)}  neutral comes out {np.round(residual / residual[1], 3)}")


if __name__ == "__main__":
    main()
