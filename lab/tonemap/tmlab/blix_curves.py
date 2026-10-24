"""Blix's four shipped curves (src/Blix.Shaders/tonemap.glsl, src/Blix.Graphics.Images/Tonemap.cs).

The baseline the lab compares GT7 against. Display-referred: 1.0 in, 1.0 out is "white", with no
notion of nits, so they say nothing about HDR. The constants are checked against both shipped copies
by tests/test_blix_curves.py, which is the same kind of drift check Blix.Test.Graphics section BD makes.
"""
from __future__ import annotations

import numpy as np

ACES_A, ACES_B, ACES_C, ACES_D, ACES_E = 2.51, 0.03, 2.43, 0.59, 0.14
AGX_MIN_EV, AGX_MAX_EV = -12.47393, 4.026069


def aces(x):
    x = np.asarray(x, dtype=np.float64)
    return np.clip((x * (ACES_A * x + ACES_B)) / (x * (ACES_C * x + ACES_D) + ACES_E), 0.0, 1.0)


def reinhard(x):
    x = np.asarray(x, dtype=np.float64)
    return x / (1.0 + x)


def neutral(x):
    return np.clip(np.asarray(x, dtype=np.float64), 0.0, 1.0)


def agx(x):
    x = np.maximum(np.asarray(x, dtype=np.float64), 0.0)
    t = np.clip((np.log2(np.maximum(x, 1e-6)) - AGX_MIN_EV) / (AGX_MAX_EV - AGX_MIN_EV), 0.0, 1.0)
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0)


def apply(x, mode: float):
    """The shader's selector: <0.5 ACES, <1.5 AgX, <2.5 Reinhard, else neutral."""
    if mode < 0.5:
        return aces(x)
    if mode < 1.5:
        return agx(x)
    if mode < 2.5:
        return reinhard(x)
    return neutral(x)
