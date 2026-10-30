"""Perceptual distances between two images.

delta_e_itp: ITU-R BT.2124 Delta E ITP, on display-referred linear Rec.2020 in nits. The HDR metric:
about 1 is a just-noticeable difference.
delta_oklab: Euclidean distance in Oklab on display-referred linear Rec.709 relative to white. An SDR
metric only: Oklab assumes values near the 0..1 range.
"""
from __future__ import annotations

import numpy as np

from . import REFERENCE_LUMINANCE, color


def delta_e_itp(rgb2020_nits_a, rgb2020_nits_b):
    """Per-pixel Delta E ITP. Inputs in cd/m^2, shape (..., 3)."""
    a = color.rgb2020_to_ictcp(np.asarray(rgb2020_nits_a) / REFERENCE_LUMINANCE)
    b = color.rgb2020_to_ictcp(np.asarray(rgb2020_nits_b) / REFERENCE_LUMINANCE)
    d = a - b
    return 720.0 * np.sqrt(d[..., 0] ** 2 + 0.25 * d[..., 1] ** 2 + d[..., 2] ** 2)


def delta_oklab(rgb709_a, rgb709_b):
    """Per-pixel Oklab distance. Inputs linear Rec.709, 1.0 = white."""
    return np.linalg.norm(color.rec709_to_oklab(rgb709_a) - color.rec709_to_oklab(rgb709_b), axis=-1)
