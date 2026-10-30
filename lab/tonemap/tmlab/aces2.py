"""The ACES Output Transform (2.x) as a reference, through OpenColorIO's own built-ins.

Not reimplemented: OCIO 2.6 ships the Academy's output transforms, and its `cg-config-v5.0.0_aces-v2.1_ocio-v2.6`
names them "ACES 2.0 - SDR 100 nits (Rec.709)" and so on, built on the `..._2.0` builtin styles. So "ACES 2.1" in OCIO
is the 2.1 release of the config around the 2.0 output transform; there is no separate 2.1 transform to call. Say so
wherever a number from here is quoted.

Conventions: input is Blix scene-linear Rec.709, optionally scaled by an exposure. SDR output is display-linear
Rec.709 with 1.0 = the display's 100-nit white (the lab's SDR convention). HDR output is linear Rec.2020 in
frame-buffer units (1.0 = 100 cd/m^2), the GT7 convention, so both compare directly with `gt7.py`.

ACES places scene 0.18 at 10 nits on SDR (display 0.10); the lab's GT7 anchor puts it near 0.18. To compare SHAPES
rather than levels use `matching_exposure`, which scales the reference's input until it agrees on mid grey.
"""
from __future__ import annotations

from functools import lru_cache

import numpy as np

CONFIG = "cg-config-v5.0.0_aces-v2.1_ocio-v2.6"
_STYLE = "ACES-OUTPUT - ACES2065-1_to_CIE-XYZ-D65 - {}_2.0"
SDR_STYLE = "SDR-100nit-REC709"
HDR_STYLES = {500: "HDR-500nit-REC2020", 1000: "HDR-1000nit-REC2020", 2000: "HDR-2000nit-REC2020",
              4000: "HDR-4000nit-REC2020"}

_XYZ_TO_709 = np.array([[3.2409699419, -1.5373831776, -0.4986107603],
                        [-0.9692436363, 1.8759675015, 0.0415550574],
                        [0.0556300797, -0.2039769589, 1.0569715142]])
_XYZ_TO_2020 = np.array([[1.7166511880, -0.3556707838, -0.2533662814],
                         [-0.6666843518, 1.6164812366, 0.0157685458],
                         [0.0176398574, -0.0427706133, 0.9421031212]])


@lru_cache(maxsize=None)
def _config():
    import PyOpenColorIO as ocio
    return ocio.Config.CreateFromBuiltinConfig(CONFIG)


@lru_cache(maxsize=None)
def _to_aces():
    return _config().getProcessor("Linear Rec.709 (sRGB)", "ACES2065-1").getDefaultCPUProcessor()


@lru_cache(maxsize=None)
def _output(style: str):
    import PyOpenColorIO as ocio
    t = ocio.BuiltinTransform()
    t.setStyle(_STYLE.format(style))
    return _config().getProcessor(t).getDefaultCPUProcessor()


def _xyz(rgb709, style: str) -> np.ndarray:
    shape = np.shape(rgb709)
    px = np.ascontiguousarray(np.asarray(rgb709, dtype=np.float32).reshape(-1, 3))
    _to_aces().applyRGB(px)
    _output(style).applyRGB(px)
    return px.astype(np.float64).reshape(shape)


def sdr(rgb709, exposure: float = 1.0):
    """ACES SDR 100-nit Rec.709: display-linear Rec.709, 1.0 = display white. Not clipped."""
    return _xyz(np.asarray(rgb709) * exposure, SDR_STYLE) @ _XYZ_TO_709.T


def hdr(rgb709, peak: int = 1000, exposure: float = 1.0):
    """ACES HDR Rec.2020 for a 500/1000/2000/4000-nit display: linear Rec.2020, frame-buffer units."""
    if peak not in HDR_STYLES:
        raise ValueError(f"OCIO's ACES output transforms exist for {sorted(HDR_STYLES)} nits, not {peak}")
    return _xyz(np.asarray(rgb709) * exposure, HDR_STYLES[peak]) @ _XYZ_TO_2020.T


def matching_exposure(reference, target_mid_out: float, mid_grey: float = 0.18, lo: float = 0.05, hi: float = 64.0) -> float:
    """Exposure at which `reference(grey * exposure)` (green channel) equals `target_mid_out` for scene `mid_grey`."""
    def out(e):
        return float(np.asarray(reference(np.full(3, mid_grey * e)))[1])
    if not out(lo) <= target_mid_out <= out(hi):
        raise ValueError(f"no exposure in [{lo}, {hi}] puts mid grey at {target_mid_out:.4f}")
    for _ in range(60):
        mid = np.sqrt(lo * hi)
        lo, hi = (mid, hi) if out(mid) < target_mid_out else (lo, mid)
    return float(np.sqrt(lo * hi))
