"""GT7 tone mapping (Polyphony Digital, SIGGRAPH 2025), reproduced from the published algorithm.

Port of the reference's structure, not a copy of its code: tests/golden holds values generated from the
reference itself, and tests/test_gt7.py holds this port to them. Everything is vectorised over (..., 3)
and the parameters are a dataclass, because fitting is the point of the lab.

Input: linear Rec.2020, frame-buffer units (1.0 = 100 cd/m^2).
Output: SDR -> [0, 1] ready for the sRGB OETF; HDR -> [0, peak/100] ready for PQ.
"""
from __future__ import annotations

from dataclasses import dataclass, replace

import numpy as np

from . import REFERENCE_LUMINANCE, SDR_PAPER_WHITE
from . import color


def smoothstep(x, e0, e1):
    # The reference's argument order: value first, then the edges.
    t = np.clip((x - e0) / (e1 - e0), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


@dataclass(frozen=True)
class GT7Params:
    # Curve ("GT Tone Mapping V2"): the published GT7 values.
    alpha: float = 0.25
    gray_point: float = 0.538
    linear_section: float = 0.444
    toe_strength: float = 1.280
    # Shoulder tail, ours (not in GT7): 0 is the published exponential shoulder exactly; larger is a heavier tail
    # (1 behaves like Reinhard's x/(1+x), the sort of tail Blix's ACES has). The knee value and slope are unchanged.
    shoulder_tail: float = 0.0
    # Hue handling.
    blend_ratio: float = 0.6
    fade_start: float = 0.98
    fade_end: float = 1.16
    ucs: str = "ictcp"  # "ictcp" | "jzazbz"


class GT7ToneMapping:
    def __init__(self, peak_nits: float, params: GT7Params = GT7Params(), sdr: bool = False):
        self.params = params
        self.sdr = sdr
        self.peak_nits = SDR_PAPER_WHITE if sdr else float(peak_nits)
        self.target = self.peak_nits / REFERENCE_LUMINANCE
        self.sdr_correction = 1.0 / (SDR_PAPER_WHITE / REFERENCE_LUMINANCE) if sdr else 1.0

        p = params
        k = (p.linear_section - 1.0) / (p.alpha - 1.0)
        self._ka = self.target * p.linear_section + self.target * k
        self._kb = -self.target * k * np.exp(p.linear_section / k)
        self._kc = -1.0 / (k * self.target)
        # The same shoulder as knee + span * f(t): the knee (x0), and how far the shoulder climbs above it (span).
        self._x0 = p.linear_section * self.target
        self._span = self.target * k

        if p.ucs == "ictcp":
            self._to_ucs, self._from_ucs = color.rgb2020_to_ictcp, color.ictcp_to_rgb2020
        elif p.ucs == "jzazbz":
            self._to_ucs, self._from_ucs = color.rgb2020_to_jzazbz, color.jzazbz_to_rgb2020
        else:
            raise ValueError(f"unknown ucs {p.ucs!r}")
        self._target_ucs_l = float(self._to_ucs(np.full(3, self.target))[0])

    @classmethod
    def sdr_display(cls, params: GT7Params = GT7Params()) -> "GT7ToneMapping":
        return cls(SDR_PAPER_WHITE, params, sdr=True)

    @classmethod
    def hdr_display(cls, peak_nits: float, params: GT7Params = GT7Params()) -> "GT7ToneMapping":
        if not 250.0 <= peak_nits <= 10000.0:
            # The reference's stated range: its curve constants assume a 250-nit paper white.
            raise ValueError("GT7 HDR peak must be within 250..10000 nits")
        return cls(peak_nits, params, sdr=False)

    def with_params(self, **changes) -> "GT7ToneMapping":
        return GT7ToneMapping(self.peak_nits, replace(self.params, **changes), self.sdr)

    def curve(self, x):
        """The per-channel curve, evaluated on any array."""
        p = self.params
        x = np.maximum(np.asarray(x, dtype=np.float64), 0.0)
        w_lin = smoothstep(x, 0.0, p.gray_point)
        toe = p.gray_point * np.power(x / p.gray_point, p.toe_strength)
        lower = (1.0 - w_lin) * toe + w_lin * x
        if p.shoulder_tail == 0.0:
            shoulder = self._ka + self._kb * np.exp(x * self._kc)  # the published form
        else:
            t = np.maximum(x - self._x0, 0.0) / self._span
            shoulder = self._x0 + self._span * (1.0 - np.power(1.0 + p.shoulder_tail * t, -1.0 / p.shoulder_tail))
        return np.where(x < p.linear_section * self.target, lower, shoulder)

    def __call__(self, rgb):
        """Tone-map linear Rec.2020 frame-buffer values."""
        p = self.params
        rgb = np.maximum(np.asarray(rgb, dtype=np.float64), 0.0)
        ucs = self._to_ucs(rgb)
        skewed = self.curve(rgb)
        skewed_ucs = self._to_ucs(skewed)

        chroma_scale = 1.0 - smoothstep(ucs[..., 0] / self._target_ucs_l, p.fade_start, p.fade_end)
        scaled_ucs = np.stack(
            [skewed_ucs[..., 0], ucs[..., 1] * chroma_scale, ucs[..., 2] * chroma_scale], axis=-1)
        scaled = self._from_ucs(scaled_ucs)

        blended = (1.0 - p.blend_ratio) * skewed + p.blend_ratio * scaled
        return self.sdr_correction * np.minimum(blended, self.target)
