#!/usr/bin/env python3
"""The closed form for light through a closed thin-walled shell over a floor (blix sheetslab --shell M; the scene
sheetshell): an infinite slab of two parallel thin sheets (KHR_materials_diffuse_transmission, no volume: each face a
sheet, the gap between them air), the sun on its top, a floor under it. Every pair of facing planes sees the other whole
(view factor 1), so four irradiances close the system:

  A  on the top wall's inner face (from the gap, the bottom wall's upper face sends it)
  B  on the bottom wall's upper face (from the gap, the top wall's inner face sends it)
  C  on the bottom wall's lower face (the floor sends it)
  D  on the floor (the bottom wall's lower face sends it)

A thin sheet sends on the side light reached (1 - T) rho of it, and T c of the light on its far side through:

  B = (1 - T) rho A + T c E_s cos_s        (the top wall's inner face: its gap side, and the sun through it)
  A = (1 - T) rho B + T c C                (the bottom wall's upper face: the gap, and the floor's light through)
  D = (1 - T) rho C + T c B                (the bottom wall's lower face: the floor side, and the gap's through)
  C = rho_f D

With no sky (--ref-sky 0 for the reference). The single sheet's (sheetslab) is the case with one wall:
D = T c E cos / (1 - rho_f rho (1 - T)).

usage: shell_slab.py [T] [rho] [rho_f] [E_cos]   (defaults the testbed's: 0.5 0.8 0.5, E cos 1 -- per unit sun)
"""
import sys
import numpy as np


def solve(T, rho, rho_f, e_cos, c=1.0):
    r = (1 - T) * rho
    # unknowns A, B, C, D
    m = np.array([
        [-r, 1.0, 0.0, 0.0],          # B - r A = T c E
        [1.0, -r, -T * c, 0.0],       # A - r B - T c C = 0
        [0.0, -T * c, -r, 1.0],       # D - T c B - r C = 0
        [0.0, 0.0, 1.0, -rho_f],      # C - rho_f D = 0
    ])
    rhs = np.array([T * c * e_cos, 0.0, 0.0, 0.0])
    a, b, cc, d = np.linalg.solve(m, rhs)
    return {"top inner (A)": a, "bottom upper (B)": b, "bottom lower (C)": cc, "floor (D)": d}


if __name__ == "__main__":
    args = [float(x) for x in sys.argv[1:]]
    T, rho, rho_f, e = (args + [0.5, 0.8, 0.5, 1.0][len(args):])[:4]
    for k, v in solve(T, rho, rho_f, e).items():
        print(f"{k:18s} {v:.5f}")
    print(f"(one sheet, the slab: floor {T * e / (1 - rho_f * rho * (1 - T)):.5f})")
