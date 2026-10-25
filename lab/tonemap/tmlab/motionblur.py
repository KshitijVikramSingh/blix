"""Motion blur as accumulation of scene-linear light over the shutter, before the tone map.

    The frame is the mean of K renderings of the world taken at view angles spread evenly over the shutter,
        L_blur(x) = (1/K) sum_k L(x + (k + 1/2)/K - 1/2) * sweep)
    where `sweep` is how far the view turns while the shutter is open (frame-to-frame turn x shutter fraction; 0.5 is a 180 degree
    shutter). It is done on linear radiance, not on display values, for two reasons that the spec tests below pin down:
      * energy: a smeared lamp keeps its total light (the mean of radiance is conserved along the sweep);
      * contrast: averaging tone-mapped values (display-referred) lets a 1000x-brighter lamp drop to the level of its
        surroundings in the smear, which is not what a camera records; averaging radiance keeps the streak bright
        until the tone map decides what to do with it.
The demo does it in scene.frag (uBlurYaw, uBlurPitch, uBlurSamples), per pixel; this module is the arithmetic and the spec.
"""
from __future__ import annotations

import numpy as np


def sweep_samples(k: int) -> np.ndarray:
    """Sample positions over the shutter, in units of the sweep: (i + 1/2)/k - 1/2."""
    return (np.arange(k) + 0.5) / k - 0.5


def blur_1d(radiance: np.ndarray, sweep_px: float, k: int = 64) -> np.ndarray:
    """Blur a 1-D radiance scan by averaging shifted copies over a sweep of `sweep_px` pixels (linear interpolation)."""
    x = np.arange(len(radiance), dtype=np.float64)
    shifts = sweep_samples(k) * sweep_px
    return np.mean([np.interp(x + s, x, radiance, left=radiance[0], right=radiance[-1]) for s in shifts], axis=0)


# ---- screen-space approximations against shutter integration ---------------------------------------------------------------
#
# The demo's motion blur averages the world at several view angles across the shutter: ground truth, and affordable only for a
# cheap world. A production renderer blurs a finished frame along per-pixel velocity instead. For a camera that only turns, the
# finished frame at the shutter's centre contains every ray the blur needs (until it leaves the frame), so two cheaper candidates
# can be scored against the integration on real HDR environments:
#   reproject  every tap is the centre frame sampled where that tap's ray would appear (exact but for the frame's borders)
#   linear     every tap is the centre frame sampled at p + s * g(p), g the first-order screen velocity (the usual velocity buffer)
# `evaluate` renders an equirect environment through a pinhole camera, blurs it all three ways and returns the errors.

import math

from scipy.ndimage import map_coordinates


def _basis(yaw: float, pitch: float):
    f = np.array([math.cos(pitch) * math.sin(yaw), math.sin(pitch), math.cos(pitch) * math.cos(yaw)])
    r = np.cross([0.0, 1.0, 0.0], f); r /= np.linalg.norm(r)
    return f, r, np.cross(f, r)


def _rays(yaw, pitch, fov_deg, w, h):
    """Unit view rays, (h, w, 3), the same construction as scene.frag's viewRay (vertical fov, y down in the image)."""
    f, r, u = _basis(yaw, pitch)
    t = math.tan(math.radians(fov_deg) / 2)
    ndc_x = (np.arange(w) + 0.5) / w * 2 - 1
    ndc_y = (np.arange(h) + 0.5) / h * 2 - 1
    x, y = np.meshgrid(ndc_x, ndc_y)
    d = f + r * (x * (w / h) * t)[..., None] + u * (-y * t)[..., None]
    return d / np.linalg.norm(d, axis=-1, keepdims=True)


def _sample_equirect(env: np.ndarray, d: np.ndarray) -> np.ndarray:
    eh, ew, _ = env.shape
    lon = np.arctan2(d[..., 0], d[..., 2])
    lat = np.arcsin(np.clip(d[..., 1], -1, 1))
    u = (lon / (2 * np.pi) + 0.5) * ew - 0.5
    v = (0.5 - lat / np.pi) * eh - 0.5
    return np.stack([map_coordinates(env[..., c], [v, u], order=1, mode="grid-wrap") for c in range(3)], axis=-1)


def _project(d: np.ndarray, yaw, pitch, fov_deg, w, h):
    """Pixel coordinates (x, y) at which direction d appears in the camera at (yaw, pitch)."""
    f, r, u = _basis(yaw, pitch)
    t = math.tan(math.radians(fov_deg) / 2)
    z = d @ f
    nx = (d @ r) / z / ((w / h) * t)
    ny = -(d @ u) / z / t
    return (nx + 1) * w / 2 - 0.5, (ny + 1) * h / 2 - 0.5


def _warp(img: np.ndarray, x: np.ndarray, y: np.ndarray):
    """Sample img at pixel coordinates; also return a mask of taps inside the frame."""
    h, w, _ = img.shape
    inside = (x >= 0) & (x <= w - 1) & (y >= 0) & (y <= h - 1)
    out = np.stack([map_coordinates(img[..., c], [y, x], order=1, mode="nearest") for c in range(3)], axis=-1)
    return out, inside


def evaluate(env: np.ndarray, sweep_deg: float, fov_deg: float, w: int = 480, h: int = 270, taps: int = 24,
             yaw: float = 0.0, pitch: float = 0.0) -> dict:
    """Error of the two screen-space approximations against shutter integration of the environment, in linear light.

    The error is the relative RMS of (approx - reference) over the pixels whose every tap stays inside the frame, and over
    the pixels where some tap leaves it (reported apart: that is where a screen-space method has no data).
    """
    sweep = math.radians(sweep_deg)
    ts = sweep_samples(taps) * sweep
    ref = np.mean([_sample_equirect(env, _rays(yaw + t, pitch, fov_deg, w, h)) for t in ts], axis=0)
    centre = _sample_equirect(env, _rays(yaw, pitch, fov_deg, w, h))
    d0 = _rays(yaw, pitch, fov_deg, w, h)
    px, py = np.meshgrid(np.arange(w), np.arange(h))
    # first-order screen velocity of the ray seen at each pixel: where the same world direction appears after a small turn
    eps = 1e-3
    x1, y1 = _project(d0, yaw + eps, pitch, fov_deg, w, h)
    gx, gy = (x1 - px) / eps, (y1 - py) / eps
    reproject = np.zeros_like(ref); linear = np.zeros_like(ref)
    inside_all = np.ones((h, w), dtype=bool)
    for t in ts:
        # the pixel p of the camera at yaw+t looks along d_t(p); in the centre frame that direction appears at proj0(d_t(p))
        xt, yt = _project(_rays(yaw + t, pitch, fov_deg, w, h), yaw, pitch, fov_deg, w, h)
        img, ok = _warp(centre, xt, yt); reproject += img / len(ts); inside_all &= ok
        img, ok = _warp(centre, px - t * gx, py - t * gy); linear += img / len(ts); inside_all &= ok

    def rms(a, mask):
        return float(np.sqrt(np.mean(((a - ref)[mask]) ** 2)) / max(np.sqrt(np.mean(ref[mask] ** 2)), 1e-12)) if mask.any() else float("nan")

    return {"sweep_deg": sweep_deg, "fov_deg": fov_deg, "border_fraction": float(1 - inside_all.mean()),
            "reproject_interior": rms(reproject, inside_all), "linear_interior": rms(linear, inside_all),
            "reproject_border": rms(reproject, ~inside_all), "linear_border": rms(linear, ~inside_all)}
