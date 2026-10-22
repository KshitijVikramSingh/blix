# /// script
# requires-python = ">=3.10"
# dependencies = []
# ///
"""A closed thin-walled solid that lets light through: how many of the GI's rounds the light in its walls needs
(uv run tools/gi-model/thin_shell.py).

Khronos' DiffuseTransmissionTeacup is closed ceramic, single-sided, diffuseTransmissionFactor 1 (by a texture) and no
KHR_materials_volume: by the spec each face is a thin wall, reflecting (1 - T) rho of the light on its side and passing
T c of it to the other. The GI kept no record on a single-sided face's far side, so the light behind the outer shell --
in the gap between it and the inner shell -- was dark, and a face with T near 1 reflects almost nothing: the cup black
(the path trace grey, the user, 2026-10-22). With a record on each face's far side (as double-sided sheets have), the
gap's light is gathered like any other: the outer shell's back sees the inner shell's back across the gap (a few mm,
the view between them ~1), and each round of the GI takes the light the last round left (the near rounds: the near
texels' light, texel_nodes_shade.glsl nearTexelLight).

A wall: outside irradiance Eo on the outer shell's face, Ei inside the cup on the inner's. Radiosity of each shell's
back, Jb = (1 - T) rho G + T c E (G what reaches it across the gap, the other back's J); the outer face as seen from
outside Jf = (1 - T) rho Eo + T c Go. The fixed point against the rounds: round 0 the fronts and backs gathered with
the gap dark (no back light yet), each round after on the last's. Reported: the outer face's light after k rounds
over the fixed point's, for the rounds the cook runs (the fine round and --near-rounds 2: three)."""


def fixed(T, rho, c, Eo, Ei):
    r = (1 - T) * rho
    # Jo = r Go + T c Eo, Ji = r Gi + T c Ei, Go = Ji, Gi = Jo (each back reflects what reaches it, the other's light).
    Jo = (T * c * Eo + r * T * c * Ei) / (1 - r * r)
    Ji = (T * c * Ei + r * T * c * Eo) / (1 - r * r)
    return (1 - T) * rho * Eo + T * c * Ji     # the outer face: its reflection, and the inner back's light through it


def rounds(T, rho, c, Eo, Ei, k):
    Jo = Ji = 0.0
    out = []
    for _ in range(k):
        Go, Gi = Ji, Jo            # what each back reads: the other's last round
        Jo, Ji = (1 - T) * rho * Go + T * c * Eo, (1 - T) * rho * Gi + T * c * Ei
        out.append((1 - T) * rho * Eo + T * c * Go)
    return out


def main():
    rho, c = 0.8, 0.8
    print("a wall: outside 1.0, inside the cup 0.3 (its shade); rho 0.8, c 0.8\n")
    print(f"{'T':>5}  {'fixed':>7}  " + "  ".join(f"round {k}" for k in range(1, 6)) + "   (the outer face's light over the fixed point's)")
    for T in (0.25, 0.5, 0.75, 1.0):
        f = fixed(T, rho, c, 1.0, 0.3)
        rs = rounds(T, rho, c, 1.0, 0.3, 5)
        print(f"{T:5.2f}  {f:7.3f}  " + "  ".join(f"{v / f:7.3f}" for v in rs))
    print("\nwithout a far side (today): the outer face", f"{(1 - 1.0) * rho * 1.0:.3f}", "at T 1, the fixed point", f"{fixed(1.0, rho, c, 1.0, 0.3):.3f}")


if __name__ == "__main__":
    main()
