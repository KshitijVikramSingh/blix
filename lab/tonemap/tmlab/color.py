"""Colour transforms: PQ, ICtCp, Jzazbz (linear Rec.2020 in), Oklab, Rec.709 <-> Rec.2020.

The ICtCp and Jzazbz constants are those of the GT7 reference sample (ITU-T T.302; Safdar et al. 2017
adjusted for linear Rec.2020), so the lab reproduces it. Negative input is clipped to zero: the
reference's pow() would produce NaN there, and a tonemapper's input is non-negative by contract.
"""
from __future__ import annotations

import numpy as np

from . import REFERENCE_LUMINANCE

# --- ST 2084 (PQ), in frame-buffer units (1.0 = 100 cd/m^2) -----------------------------------
_M1 = 0.1593017578125
_M2 = 78.84375
_C1 = 0.8359375
_C2 = 18.8515625
_C3 = 18.6875
_PQ_PEAK = 10000.0


def eotf_st2084(n, exponent_scale: float = 1.0):
    """Normalised PQ signal (0..1) -> frame-buffer linear."""
    n = np.clip(n, 0.0, 1.0)
    m2 = _M2 * exponent_scale
    np_ = np.power(n, 1.0 / m2)
    l = np.maximum(np_ - _C1, 0.0) / (_C2 - _C3 * np_)
    return np.power(l, 1.0 / _M1) * _PQ_PEAK / REFERENCE_LUMINANCE


def inverse_eotf_st2084(v, exponent_scale: float = 1.0):
    """Frame-buffer linear -> normalised PQ signal."""
    m2 = _M2 * exponent_scale
    y = np.maximum(v, 0.0) * REFERENCE_LUMINANCE / _PQ_PEAK
    ym = np.power(y, _M1)
    return np.exp2(m2 * (np.log2(_C1 + _C2 * ym) - np.log2(1.0 + _C3 * ym)))


# --- ICtCp -------------------------------------------------------------------------------
_RGB_TO_LMS_ICTCP = np.array([[1688.0, 2146.0, 262.0],
                              [683.0, 2951.0, 462.0],
                              [99.0, 309.0, 3688.0]]) / 4096.0
_LMS_PQ_TO_ICTCP = np.array([[2048.0, 2048.0, 0.0],
                             [6610.0, -13613.0, 7003.0],
                             [17933.0, -17390.0, -543.0]]) / 4096.0
_ICTCP_TO_LMS_PQ = np.array([[1.0, 0.00860904, 0.11103],
                             [1.0, -0.00860904, -0.11103],
                             [1.0, 0.560031, -0.320627]])
_LMS_TO_RGB_ICTCP = np.array([[3.43661, -2.50645, 0.0698454],
                              [-0.79133, 1.9836, -0.192271],
                              [-0.0259499, -0.0989137, 1.12486]])


def rgb2020_to_ictcp(rgb):
    lms = np.asarray(rgb) @ _RGB_TO_LMS_ICTCP.T
    return inverse_eotf_st2084(lms) @ _LMS_PQ_TO_ICTCP.T


def ictcp_to_rgb2020(ictcp):
    lms = eotf_st2084(np.asarray(ictcp) @ _ICTCP_TO_LMS_PQ.T)
    return np.maximum(lms @ _LMS_TO_RGB_ICTCP.T, 0.0)


# --- Jzazbz (linear Rec.2020) ------------------------------------------------------------
JZAZBZ_EXPONENT_SCALE = 1.7
_JZ_OFFSET = 1.6295499532821566e-11
_RGB_TO_LMS_JZ = np.array([[0.530004, 0.355704, 0.086090],
                           [0.289388, 0.525395, 0.157481],
                           [0.091098, 0.147588, 0.734234]])
_LMS_PQ_TO_AB_JZ = np.array([[3.524000, -4.066708, 0.542708],
                             [0.199076, 1.096799, -1.295875]])
_AB_TO_LMS_JZ = np.array([[1.386050432715393e-1, 5.804731615611869e-2],
                          [-1.386050432715393e-1, -5.804731615611869e-2],
                          [-9.601924202631895e-2, -8.118918960560390e-1]])
_LMS_TO_RGB_JZ = np.array([[2.990669, -2.049742, 0.088977],
                           [-1.634525, 3.145627, -0.483037],
                           [-0.042505, -0.377983, 1.448019]])


def rgb2020_to_jzazbz(rgb):
    lms = np.asarray(rgb) @ _RGB_TO_LMS_JZ.T
    pq = inverse_eotf_st2084(lms, JZAZBZ_EXPONENT_SCALE)
    iz = 0.5 * pq[..., 0] + 0.5 * pq[..., 1]
    jz = (0.44 * iz) / (1.0 - 0.56 * iz) - _JZ_OFFSET
    ab = pq @ _LMS_PQ_TO_AB_JZ.T
    return np.concatenate([jz[..., None], ab], axis=-1)


def jzazbz_to_rgb2020(jab):
    jab = np.asarray(jab)
    jz = jab[..., 0] + _JZ_OFFSET
    iz = jz / (0.44 + 0.56 * jz)
    lms_pq = iz[..., None] + jab[..., 1:] @ _AB_TO_LMS_JZ.T
    lms = eotf_st2084(lms_pq, JZAZBZ_EXPONENT_SCALE)
    return lms @ _LMS_TO_RGB_JZ.T


# --- Rec.709 <-> Rec.2020 (BT.2087), linear -----------------------------------------------
REC709_TO_REC2020 = np.array([[0.6274, 0.3293, 0.0433],
                              [0.0691, 0.9195, 0.0114],
                              [0.0164, 0.0880, 0.8956]])
REC2020_TO_REC709 = np.linalg.inv(REC709_TO_REC2020)


def rec709_to_rec2020(rgb):
    return np.asarray(rgb) @ REC709_TO_REC2020.T


def rec2020_to_rec709(rgb):
    return np.asarray(rgb) @ REC2020_TO_REC709.T


LUMA709 = np.array([0.2126, 0.7152, 0.0722])


# Display P3 (D65) primaries, relative to Rec.709. The XDR panel this lab is developed on is P3; an extended-linear sRGB surface
# expresses P3 colours as Rec.709 values with a negative channel.
REC709_TO_P3 = np.array([[0.8224621, 0.1775380, 0.0000000],
                         [0.0331941, 0.9668058, 0.0000001],
                         [0.0170827, 0.0723974, 0.9105199]])
P3_TO_REC709 = np.linalg.inv(REC709_TO_P3)
LUMA_P3 = LUMA709 @ P3_TO_REC709   # the same absolute luminance Y as LUMA709, for P3 components (0.2290, 0.6917, 0.0793)


def gamut_map(rgb709, target: str = "709"):
    """gamut_map_709, to the Rec.709 gamut ("709") or the Display P3 gamut ("p3"); returns linear Rec.709 (negative where P3 needs it).

    The desaturation runs in the target gamut's own RGB: toward the grey of the same luminance, by the least amount that makes the
    target's smallest channel zero. For "p3" a colour inside P3 but outside Rec.709 is returned unchanged (negative channels and all).
    """
    if target == "709":
        return gamut_map_709(rgb709)
    c = np.asarray(rgb709, dtype=np.float64) @ REC709_TO_P3.T
    y = c @ LUMA_P3
    mn = c.min(axis=-1)
    bad = mn < 0.0
    t = np.where(bad & (y > 0.0), y / np.where(bad, y - mn, 1.0), 1.0)
    out = y[..., None] + t[..., None] * (c - y[..., None])
    out = np.where((bad & (y <= 0.0))[..., None], 0.0, np.maximum(out, 0.0))
    return out @ P3_TO_REC709.T


def gamut_map_709(rgb):
    """Bring linear Rec.709 values that went negative (a Rec.2020 colour outside the display's gamut) back to the gamut's edge.

    Per-channel clipping to zero changes the colour's hue as well as its chroma. This instead moves a colour toward the grey of the
    same luminance (Rec.709 luma Y) by the least amount that makes its smallest channel zero:
        t = Y / (Y - min(rgb)),   rgb' = Y + t (rgb - Y)
    so luminance is exactly kept, the hue direction in the plane through the grey axis is kept, and only chroma is given up. Colours
    already in gamut (min >= 0) are untouched; a colour with Y <= 0 goes to black. Values above 1 (HDR) are not touched: only the
    negative side is the gamut boundary of an extended-linear display. present.frag's `gamutMap709` is the same arithmetic.
    """
    c = np.asarray(rgb, dtype=np.float64)
    y = c @ LUMA709
    mn = c.min(axis=-1)
    bad = (mn < 0.0)
    t = np.where(bad & (y > 0.0), y / np.where(bad, y - mn, 1.0), 1.0)
    out = y[..., None] + t[..., None] * (c - y[..., None])
    out = np.where((bad & (y <= 0.0))[..., None], 0.0, out)
    return np.maximum(out, 0.0)


# --- Oklab (Ottosson), linear sRGB / Rec.709 in -------------------------------------------
_OK_M1 = np.array([[0.4122214708, 0.5363325363, 0.0514459929],
                   [0.2119034982, 0.6806995451, 0.1073969566],
                   [0.0883024619, 0.2817188376, 0.6299787005]])
_OK_M2 = np.array([[0.2104542553, 0.7936177850, -0.0040720468],
                   [1.9779984951, -2.4285922050, 0.4505937099],
                   [0.0259040371, 0.7827717662, -0.8086757660]])


def rec709_to_oklab(rgb):
    lms = np.asarray(rgb) @ _OK_M1.T
    return np.cbrt(lms) @ _OK_M2.T
