"""The convention that puts a Blix scene value and a GT7 input on one axis.

Blix's scene target is exposure-relative: a manual scalar multiplies it, and the curves treat 1.0 as white.
GT7 reads absolute light (frame-buffer 1.0 = 100 cd/m^2) and its constants assume a 250-nit SDR paper white.
So putting Blix's data through GT7 needs one number: what a scene value of 1.0, after exposure, IS in nits.
That number, `scene_white_nits`, is the anchor. Everything exposure-related in the lab hangs off it, and an
auto-exposure that places the scene's key at mid grey changes only the `exposure` argument, not the anchor.

    anchor = 250  (default)  a scene white is the SDR paper white GT7's constants were tuned around.
    anchor = 203             BT.2408's reference white for HDR graphics.
    anchor = 100             the sRGB convention, where scene 1.0 is 100 nits.

There is no right value; there is a chosen one, and `matching_anchor` finds the one at which GT7 and a reference
curve agree on mid grey, which is the fair way to compare their SHAPES.
"""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from . import REFERENCE_LUMINANCE, color
from .gt7 import GT7ToneMapping


@dataclass(frozen=True)
class SceneConvention:
    scene_white_nits: float = 250.0
    mid_grey: float = 0.18


def to_gt7_input(rgb709, exposure: float = 1.0, conv: SceneConvention = SceneConvention()):
    """Blix scene-linear Rec.709 (before exposure) -> GT7 input: linear Rec.2020, frame-buffer units."""
    scaled = np.asarray(rgb709, dtype=np.float64) * exposure * (conv.scene_white_nits / REFERENCE_LUMINANCE)
    return color.rec709_to_rec2020(scaled)


def gt7_sdr(rgb709, tm: GT7ToneMapping | None = None, exposure: float = 1.0,
            conv: SceneConvention = SceneConvention()):
    """GT7 SDR output as display-linear Rec.709 in 0..1, ready for the sRGB OETF (what a Blix curve returns)."""
    tm = tm or GT7ToneMapping.sdr_display()
    out2020 = tm(to_gt7_input(rgb709, exposure, conv))
    return np.clip(color.rec2020_to_rec709(out2020), 0.0, 1.0)


def gt7_hdr_nits(rgb709, tm: GT7ToneMapping, exposure: float = 1.0, conv: SceneConvention = SceneConvention()):
    """GT7 HDR output as linear Rec.2020 in cd/m^2, ready for PQ."""
    return tm(to_gt7_input(rgb709, exposure, conv)) * REFERENCE_LUMINANCE


def blix_curve(rgb709, curve, exposure: float = 1.0):
    """A Blix curve (blix_curves.aces and friends) on exposed scene-linear Rec.709: display-linear 0..1."""
    return curve(np.asarray(rgb709, dtype=np.float64) * exposure)


def matching_anchor(reference_curve, conv: SceneConvention = SceneConvention(), tm: GT7ToneMapping | None = None,
                    lo: float = 20.0, hi: float = 2000.0) -> float:
    """The anchor at which GT7 SDR and `reference_curve` map mid grey to the same display value.

    GT7's grey output rises monotonically with the anchor, so bisection is exact enough.
    """
    tm = tm or GT7ToneMapping.sdr_display()
    target = float(reference_curve(np.array(conv.mid_grey)))
    grey = np.full(3, conv.mid_grey)

    def out(anchor):
        c = SceneConvention(scene_white_nits=anchor, mid_grey=conv.mid_grey)
        return float(gt7_sdr(grey, tm, 1.0, c)[1])

    if not out(lo) <= target <= out(hi):
        raise ValueError(f"no anchor in [{lo}, {hi}] matches mid grey {target:.4f}")
    for _ in range(60):
        mid = 0.5 * (lo + hi)
        lo, hi = (mid, hi) if out(mid) < target else (lo, mid)
    return 0.5 * (lo + hi)
