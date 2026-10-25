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
