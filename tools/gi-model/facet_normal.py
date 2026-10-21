# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""Irradiance at a faceted fold's face normal against its smooth (shading) normal (uv run tools/gi-model/facet_normal.py).

drapes' curtain is 2 cm flat strips with smooth vertex normals, its folds a sine 6 cm deep and 30 cm long. The gate's
reference measures a point's irradiance about its triangle's face normal (SponzaLoop.GpuReference.cs, FaceNormal); the
incident pass reads it at the pre-pass normal, the smooth one the frame shades with. Where light comes from one side
those differ by the cosine's slope times up to half a facet's turn. Light here: a uniform dome plus a bright patch
(the room's sunlit floor) 60 degrees below the curtain's mean normal -- its share of the light swept."""
import numpy as np

period, depth, step = 0.30, 0.06, 0.02
k = 2 * np.pi / period
slope = lambda x: 0.5 * depth * k * np.cos(k * x)
normal = lambda s: np.array([-s, 0.0, 1.0]) / np.hypot(s, 1.0)      # in the x-z plane, facing +z


def irradiance(n, patch_dir, patch_share):
    """Cosine-weighted: a uniform dome over n's hemisphere (E = 1 there) and a small bright source along patch_dir."""
    return (1 - patch_share) + patch_share * max(np.dot(n, patch_dir), 0.0) / max(np.dot(np.array([0, 0, 1.0]), patch_dir), 1e-6)


for share in (0.3, 0.6):
    for az in (0.0, 90.0):
        # 60 degrees down from the mean normal, turned az about it (0: across the folds, 90: along them)
        a = np.radians(60.0); b = np.radians(az)
        d = np.array([np.sin(a) * np.cos(b), -np.sin(a) * np.sin(b), np.cos(a)])
        errs = []
        for x in np.arange(0.0, period, 0.001):
            x0 = np.floor(x / step) * step
            face = normal((0.5 * depth * (np.sin(k * (x0 + step)) - np.sin(k * x0))) / step)   # the facet's own slope
            smooth = normal(slope(x))
            errs.append(irradiance(face, d, share) / irradiance(smooth, d, share) - 1)
        errs = np.abs(errs) * 100
        print(f"bright patch {100*share:.0f}% of the light, {'across' if az == 0 else 'along'} the folds: face over smooth |diff| median {np.median(errs):4.1f}%  p90 {np.percentile(errs, 90):4.1f}%  max {errs.max():4.1f}%")
