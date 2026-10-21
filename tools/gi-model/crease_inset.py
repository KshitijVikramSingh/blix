# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The light a pixel beside a crease reads from texels that start `inset` out (uv run tools/gi-model/crease_inset.py).

The lightmap's edge texels sit half a texel in (Lightmap.cs LightmapEdgeInset, 2 cm): a pixel nearer the crease than the
first texel's centre reads that texel's light (the lookup does not extrapolate), so it carries the light's slope over the
gap. From crease_bounce.py's exact profiles: the error at a pixel 0.5 cm and 1 cm off the crease, each inset."""
import numpy as np
from crease_bounce import SCENES, truth

for sc in SCENES:
    xa, ea, _, _ = truth(sc)
    e = lambda t: np.interp(t, xa, ea)
    cells = []
    for inset in (0.02, 0.01, 0.005):
        cells.append(f"inset {100*inset:.1f} cm: " + " ".join(f"{100*(e(inset)/e(p)-1):+5.1f}%@{10*p*100:.0f}mm" for p in (0.005, 0.01) if p <= inset))
    print(f"{sc.name:<36}" + "   ".join(cells))
