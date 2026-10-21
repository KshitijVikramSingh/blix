# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""Light through a thin sheet, modelled exactly on drapes' geometry (uv run tools/gi-model/thin_sheet.py).

The sheet (KHR_materials_diffuse_transmission): of the light reaching a side, (1 - T) meets the base diffuse lobe and T the
transmission lobe, Lambertian about the other side, tinted c. With irradiance E_a on side a and E_b on side b, each side's
outgoing radiance is

    L_a = [ rho (1 - T) E_a + T c E_b ] / pi,        L_b = [ rho (1 - T) E_b + T c E_a ] / pi

so a sheet the sun lights on one side glows on the other: pi L_back = T c E_s cos_s, the sun's beam not passing straight
through but scattered. A point behind the sheet gets the glow of the sheet's lit part it sees,

    E(p) = Int_lit  T c E_s cos_s / pi  cos_p cos_q / r^2  dA

The frame's traced shadows (blix_hwx.mm, blix_sheet_light) answer it from the sun's ray at p alone: where that ray crosses a
sheet, a lit disc of radius 0.6 m centred there, E = E_s cos_p T c cos_x^2 R^2 / (R^2 + d^2). The GI's cook leaves the sun
through sheets to the frame (texel_nodes_solve.comp: the twins carry the BOUNCE light through), and the GPU reference meets
a sheet as an opaque surface (reference_path.glsl), so neither has the glow nor its bounce.

Here, along lines on drapes' floor (blix drapes), E(p) / (T c E_s): the exact single scattering, and the frame's disc."""
import numpy as np

L = np.array([0.1120, 0.9568, 0.2682]); L /= np.linalg.norm(L)    # toward the sun (SponzaLoop's default)
HALF = 0.75                                                      # the skylight's half side, at y 6 and 6.2


def lit(q):
    """The sun reaches q through the skylight's shaft (the opening at 6 m and at the shell's top, 6.2 m)."""
    ok = np.ones(q.shape[0], dtype=bool)
    for top in (6.0, 6.2):
        t = (top - q[:, 1]) / L[1]
        x = q[:, 0] + t * L[0]
        z = q[:, 2] + t * L[2]
        ok &= (np.abs(x) <= HALF) & (np.abs(z) <= HALF)
    return ok


def sheet_samples(origin, u, v, n, step=0.02):
    nu, nv = int(np.linalg.norm(u) / step), int(np.linalg.norm(v) / step)
    a = (np.arange(nu) + 0.5) / nu
    b = (np.arange(nv) + 0.5) / nv
    q = origin + a[:, None, None] * u + b[None, :, None] * v
    q = q.reshape(-1, 3)
    return q, np.linalg.norm(u) * np.linalg.norm(v) / (nu * nv), n


def exact_glow(p, n_p, q, dA, n_s, lit_q):
    """Single scattering: the sheet's sunlit side's glow out of its other side, onto the point p with normal n_p."""
    cos_s = abs(np.dot(n_s, L))
    lit_side = n_s if np.dot(n_s, L) > 0 else -n_s     # the side the sun lights; it glows out of -lit_side
    d = p[None, :] - q
    r2 = (d * d).sum(1)
    r = np.sqrt(r2)
    cos_q = np.maximum(-(d @ lit_side) / r, 0.0)       # q's glowing side toward p
    cos_p = np.maximum(-(d @ n_p) / r, 0.0)
    return (lit_q * cos_s / np.pi * cos_q * cos_p / r2).sum() * dA


def frame_glow(p, n_p, origin, u, v, n_s, R=0.6):
    """blix_sheet_light: the sun's ray from p, the sheet it crosses, a lit disc there."""
    denom = np.dot(L, n_s)
    if abs(denom) < 1e-6: return 0.0
    t = np.dot(origin - p, n_s) / denom
    if t <= 0: return 0.0
    x = p + t * L - origin
    a, b = np.dot(x, u) / np.dot(u, u), np.dot(x, v) / np.dot(v, v)
    if not (0 <= a <= 1 and 0 <= b <= 1): return 0.0
    if not lit((p + t * L)[None, :])[0]: return 0.0
    cos_x = abs(denom)
    return max(np.dot(n_p, L), 0.0) * cos_x * cos_x * R * R / (R * R + t * t)


def shaft(y):
    return (6.0 - y) / 0.957 * np.array([-0.112, -0.268])


# drapes' sheets (Blix.Tools.Testbeds, Drapes)
s = 0.75
vo = shaft(1.0)
veil = (np.array([-s - 0.1 + vo[0], 1.0, -s - 0.1 + vo[1]]), np.array([s + 0.1, 0, 0]), np.array([0, 0, 1.7]), np.array([0, 1.0, 0]))
co = shaft(3.0)
curtain = (np.array([-0.3, 1.2, co[1]]), np.array([1.8, 0, 0]), np.array([0, 3.4, 0]), np.array([0, 0, 1.0]))

if __name__ == "__main__":
    up = np.array([0.0, 1.0, 0.0])
    for name, (o, u, v, n), line in (
        ("veil, 1 m over the floor: floor along x at the veil's middle depth", veil,
         [np.array([x, 0.0, veil[0][2] + 0.85]) for x in np.linspace(-1.8, 0.0, 10)]),
        ("curtain (glowing toward -z): floor along z behind it, at x 0.5", curtain,
         [np.array([0.5, 0.0, z]) for z in np.linspace(co[1] - 0.05, -2.95, 10)])):
        q, dA, ns = sheet_samples(o, u, v, n)
        lq = lit(q).astype(float)
        print(f"\n== {name}")
        print(f"   sheet lit {100 * lq.mean():.0f}% of its area; cos to the sun {abs(np.dot(n, L)):.3f}")
        print(f"   {'point':<26}{'exact':>8}{'frame':>8}{'frame/exact':>13}")
        for p in line:
            e = exact_glow(p, up, q, dA, ns, lq)
            f = frame_glow(p, up, o, u, v, ns)
            ratio = f"{f / e:.2f}" if e > 1e-4 else "-"
            print(f"   ({p[0]:+.2f}, {p[2]:+.2f})            {e:8.4f}{f:8.4f}{ratio:>13}")
