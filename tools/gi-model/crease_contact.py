# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The fine copy's contact ratio against the light it stands for, at a crease (uv run tools/gi-model/crease_contact.py).

transfer_fine.comp lights a fine texel as its coarse cells' light times min(1, its contact visibility / theirs): the share of
its cosine rays that meet nothing within the contact reach. Beside a crease the other face takes up to half of them, so
the ratio darkens the texel as if that face were black. It is not: it reflects, and the light it sends makes up most of the
room light it hides (crease_bounce.py's truth). Here both, for a texel at t from the crease and its coarse cell's centre."""
import numpy as np
from crease_bounce import SCENES, truth, G

REACH = float(__import__("sys").argv[1]) if len(__import__("sys").argv) > 1 else 0.5


def contact(t, reach):
    """Share of a point's cosine rays that miss the perpendicular face within `reach` (the face's strip nearer than it)."""
    s = np.sqrt(np.maximum(reach * reach - t * t, 0.0))
    return 1 - G(s, t)


for sc in SCENES:
    xa, ea, _, _ = truth(sc)
    print(f"\n== {sc.name} (contact reach {REACH} m)")
    for cell, centre in ((0.10, 0.05), (0.20, 0.10)):
        ec = np.interp(centre, xa, ea)
        row = []
        for t in (0.02, 0.06):
            if t > cell: continue
            ratio = min(1.0, contact(t, REACH) / contact(centre, REACH))
            row.append(f"texel {100*t:.0f} cm: copy x{ratio:.3f} -> {100*(ratio*ec/np.interp(t, xa, ea)-1):+5.1f}%")
        print(f"   {100*cell:.0f} cm cell (centre {100*centre:.0f} cm): " + "   ".join(row))
