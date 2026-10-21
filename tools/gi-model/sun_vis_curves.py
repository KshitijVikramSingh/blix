# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""sun_vis.py's estimators as curves over the receiver's distance d from the crease, for the write-up: at the edge across
the crease (case X), the worst mean error over receivers within 20 cm along the crease, and the spread over grid phases
(the pattern neighbouring texels see). JSON out (uv run tools/gi-model/sun_vis_curves.py > curves.json)."""
import json
import numpy as np
from sun_vis import estimators, case_x, Edge, PHASES

ds = [0.01, 0.02, 0.03, 0.05, 0.07, 0.10, 0.15, 0.20, 0.30, 0.50]
names = ["cell 20 + near 10 (now)", "search 5cm", "map 2cm", "map 5cm", "map 5cm, disc", "ray, point sun"]
out = {"d": ds, "series": {}}
for name in names:
    worst, spread = [], []
    for d in ds:
        wm, sp = 0.0, 0.0
        for u0 in np.linspace(-0.2, 0.2, 21):
            u, w, near = case_x(u0, d, n=8000)
            edge = Edge(0.0, +1, 0.056)
            f = estimators(edge, along_crease=True, near_share_fn=near)[name]
            truth = np.sum(edge.point(u) * w)
            errs = np.array([np.sum(f(u, i) * w) / truth - 1 for i in range(PHASES)])
            wm = max(wm, abs(errs.mean()))
            sp = max(sp, errs.std())
        worst.append(round(100 * wm, 2))
        spread.append(round(100 * sp, 2))
    out["series"][name] = {"worst_mean": worst, "spread": spread}
print(json.dumps(out))
