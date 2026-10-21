# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The bounce a texel gathers from the face across a crease, modelled exactly (uv run tools/gi-model/crease_bounce.py).

Two faces A and B meet at a right angle along a crease, everything uniform along it, so the transport is flatland: a point
at t on A sees the strip at s on B with the form-factor density

    k(s; t) ds = t s / (2 (t^2 + s^2)^(3/2)) ds,      G(s; t) = Int_0^s k = (1 - t / sqrt(t^2 + s^2)) / 2

(and the same with the faces swapped). Each face also sees the room through the directions the other face leaves open,
uniform radiance L0 (E = pi L0 times the open share), and may carry a sunlit band (irradiance Es, its visibility exact,
as the gather's hit rays now have it). Irradiance (indirect: everything but a point's own sun) solves

    E_A(t) = pi L0 open_A(t) + Int_B rho_B (E_B(s) + Es band_B(s)) k(s; t) ds,      and the same for B.

In log coordinates k(s; t) s depends only on s / t, so a log-graded grid resolves the corner exactly. The truth is that
fixed point. The estimators differ only in the E at a gather ray's hit on the other face (what the cook's shade uses):

  patch      the patch solve's answer (SunNodesSolve / texel_nodes_solve.comp): patches of size P, each's irradiance at its
             centre by collocation -- exact couplings to the other face's patches (no ray noise), their sun the mean of the
             band over them -- read flat over the patch the hit lies in, or blended (linear between centres: the hit blend)
  texel      the proposal: hits nearer the receiver than R read the other face's texels' light (4 cm, the first 2 cm from
             the crease, linear between centres) -- the texels' own gathers, iterated to their fixed point; farther hits
             the patch's (blended)

Errors are at texel centres on A (2, 6, 10, ... cm), against the truth there.
"""
import numpy as np

N = 900
TEXEL, INSET = 0.04, 0.02


def grid(length):
    """Log-graded nodes on (0, length] and their weights (midpoint in log)."""
    u = np.linspace(np.log(1e-5), np.log(length), N + 1)
    m = 0.5 * (u[1:] + u[:-1])
    s = np.exp(m)
    return s, s * (u[1] - u[0])


def kmat(recv, src, w):
    """K[i, j]: the density at receiver recv[i] of src[j], times src's quadrature weight."""
    t = recv[:, None]
    s = src[None, :]
    return t * s / (2 * (t * t + s * s) ** 1.5) * w[None, :]


def G(s, t):
    return 0.5 * (1 - t / np.sqrt(t * t + s * s))


class Scene:
    def __init__(self, name, la, lb, rho_a, rho_b, band_a=None, band_b=None, es=10.0, l0=1.0):
        self.name, self.la, self.lb = name, la, lb
        self.rho = (rho_a, rho_b)
        self.band = (band_a, band_b)
        self.es, self.l0 = es, l0

    def open_share(self, face, x):
        other = self.lb if face == 0 else self.la
        return 1 - G(other, x)

    def band_at(self, face, x):
        b = self.band[face]
        return np.zeros_like(x) if b is None else ((x >= b[0]) & (x <= b[1])).astype(float)

    def band_mean(self, face, lo, hi):
        b = self.band[face]
        if b is None:
            return 0.0
        return max(0.0, min(hi, b[1]) - max(lo, b[0])) / (hi - lo)


def truth(sc):
    xa, wa = grid(sc.la)
    xb, wb = grid(sc.lb)
    kab = kmat(xa, xb, wb)   # A receives from B
    kba = kmat(xb, xa, wa)
    ra = np.pi * sc.l0 * sc.open_share(0, xa)
    rb = np.pi * sc.l0 * sc.open_share(1, xb)
    ea, eb = ra.copy(), rb.copy()
    for _ in range(200):
        ea = ra + kab @ (sc.rho[1] * (eb + sc.es * sc.band_at(1, xb)))
        eb = rb + kba @ (sc.rho[0] * (ea + sc.es * sc.band_at(0, xa)))
    return xa, ea, xb, eb


def patch_solve(sc, size):
    """Collocation at the patches' centres, exact couplings; returns each face's centres and irradiance there."""
    ca = (np.arange(int(round(sc.la / size))) + 0.5) * size
    cb = (np.arange(int(round(sc.lb / size))) + 0.5) * size
    fab = G(cb[None, :] + size / 2, ca[:, None]) - G(cb[None, :] - size / 2, ca[:, None])
    fba = G(ca[None, :] + size / 2, cb[:, None]) - G(ca[None, :] - size / 2, cb[:, None])
    da = np.array([sc.band_mean(0, c - size / 2, c + size / 2) for c in ca])
    db = np.array([sc.band_mean(1, c - size / 2, c + size / 2) for c in cb])
    ra = np.pi * sc.l0 * sc.open_share(0, ca)
    rb = np.pi * sc.l0 * sc.open_share(1, cb)
    ea, eb = ra.copy(), rb.copy()
    for _ in range(200):
        ea = ra + fab @ (sc.rho[1] * (eb + sc.es * db))
        eb = rb + fba @ (sc.rho[0] * (ea + sc.es * da))
    return (ca, ea), (cb, eb)


def read_patch(centres, values, x, size, blend):
    if not blend:
        j = np.clip((x / size).astype(int), 0, len(centres) - 1)
        return values[j]
    return np.interp(x, centres, values)


def texel_centres(length):
    return INSET + np.arange(int((length - INSET) / TEXEL) + 1) * TEXEL


def near_share(recv, src, reach):
    """The share of the strip at src (along the crease) within `reach` of the receiver at recv, the faces perpendicular."""
    a2 = recv[:, None] ** 2 + src[None, :] ** 2
    a = np.sqrt(a2)
    U = np.sqrt(np.maximum(0.0, reach * reach - a2))
    return (U * a / (a2 + U * U) + np.arctan(U / a)) / (np.pi / 2)


def gather(sc, size, blend, reach, cross=0.0, steps=200):
    """Texels on both faces gathering: hits on the other face take the patch's E, or within `reach` the texels' (fixed
    point). Returns A's texel centres and their irradiance."""
    (ca, pa), (cb, pb) = patch_solve(sc, size)
    xa, wa = grid(sc.la)
    xb, wb = grid(sc.lb)
    ta, tb = texel_centres(sc.la), texel_centres(sc.lb)
    ka, kb = kmat(ta, xb, wb), kmat(tb, xa, wa)         # texels on A from B's strips, and B's from A's
    na = near_share(ta, xb, reach) if reach > 0 else np.zeros_like(ka)
    nb = near_share(tb, xa, reach) if reach > 0 else np.zeros_like(kb)
    pat_b = read_patch(cb, pb, xb, size, blend)          # B's patch light at each strip of B
    pat_a = read_patch(ca, pa, xa, size, blend)
    # `cross`: hits within this of the crease take the other face's nearest patch (a texel's nearest visible patch can be
    # across the crease, its centre as near as its own face's).
    if cross > 0:
        pat_b = np.where(xb < cross, pa[0], pat_b)
        pat_a = np.where(xa < cross, pb[0], pat_a)
    ra = np.pi * sc.l0 * sc.open_share(0, ta)
    rb = np.pi * sc.l0 * sc.open_share(1, tb)
    sun_b = sc.es * sc.band_at(1, xb)
    sun_a = sc.es * sc.band_at(0, xa)
    ea, eb = ra.copy(), rb.copy()
    # `steps` < 200: the cook's lagged feedback -- the texels' light the shade can read is the last answer (the instant
    # layer, then each resolve): start from the patch-lit gather, then that many steps reading it.
    if reach > 0 and steps < 200:
        ea, eb = gather(sc, size, blend, 0.0, cross)[1], gather_b(sc, size, blend, cross)
    for _ in range(steps if reach > 0 else 1):
        tex_b = np.interp(xb, tb, eb)
        tex_a = np.interp(xa, ta, ea)
        hit_b = (1 - na) * pat_b[None, :] + na * tex_b[None, :]
        hit_a = (1 - nb) * pat_a[None, :] + nb * tex_a[None, :]
        new_a = ra + (ka * sc.rho[1] * (hit_b + sun_b[None, :])).sum(1)
        new_b = rb + (kb * sc.rho[0] * (hit_a + sun_a[None, :])).sum(1)
        ea, eb = new_a, new_b
    return ta, ea


def gather_b(sc, size, blend, cross):
    """B's texels under the patch-lit gather (gather's A, the faces swapped)."""
    sw = Scene(sc.name, sc.lb, sc.la, sc.rho[1], sc.rho[0], band_a=sc.band[1], band_b=sc.band[0], es=sc.es, l0=sc.l0)
    return gather(sw, size, blend, 0.0, cross)[1]


SCENES = [
    # Cornell's short box: the floor (A) at its base, the box's side (B) 1.8 m tall; the sun's patch on the floor beyond.
    Scene("box base, sunlit floor 0.5-2 m out", 3.0, 1.8, 0.73, 0.73, band_a=(0.5, 2.0)),
    Scene("box base, sunlit floor up to it", 3.0, 1.8, 0.73, 0.73, band_a=(0.0, 1.5)),
    # A sunlit band on the other face: the receiver sees it across the crease.
    Scene("other face sunlit 0.3-1 m up", 3.0, 3.0, 0.73, 0.73, band_b=(0.3, 1.0)),
    # Room light alone: the crease's own shape.
    Scene("room light alone, 6 m walls", 6.0, 6.0, 0.73, 0.63),
]
ESTIMATORS = [("patch 25, flat", 0.25, False, 0.0), ("patch 25, blend (now)", 0.25, True, 0.0),
              ("patch 12.5, blend", 0.125, True, 0.0), ("texels within 10 cm", 0.25, True, 0.10),
              ("texels within 25 cm", 0.25, True, 0.25), ("patch 25, 6 cm cross", 0.25, True, 0.0, 0.06),
              ("patch 25, 12 cm cross", 0.25, True, 0.0, 0.12),
              ("texels 25, 1 lagged step", 0.25, True, 0.25, 0.0, 1), ("texels 25, 2 lagged steps", 0.25, True, 0.25, 0.0, 2),
              ("cross 12 + texels 25, 1 step", 0.25, True, 0.25, 0.12, 1)]

if __name__ == "__main__":
    show = np.array([0.02, 0.06, 0.10, 0.18, 0.30, 0.50])
    for sc in SCENES:
        xa, ea, _, _ = truth(sc)
        print(f"\n== {sc.name}: the error at A's texels (truth E at them in brackets)")
        tr = np.interp(show, xa, ea)
        print("   " + f"{'texel at':<24}" + "".join(f"{100*d:>7.0f}cm" for d in show))
        print("   " + f"{'truth E':<24}" + "".join(f"{v:9.3f}" for v in tr))
        for name, size, blend, reach, *cross in ESTIMATORS:
            ta, et = gather(sc, size, blend, reach, *cross)
            est = np.interp(show, ta, et)
            print("   " + f"{name:<24}" + "".join(f"{100*(e/t-1):+8.1f}%" for e, t in zip(est, tr)))
