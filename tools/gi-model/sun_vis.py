# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The sun at a gather ray's hit, modelled exactly on two crease geometries (uv run tools/gi-model/sun_vis.py).

A texel on a receiver R gathers the sun reflected off a source surface S. With S Lambertian, lit by the sun at
irradiance Es and angle cos_s, the sun's bounce at the receiver point x is

    E(x) = rho_s Es cos_s * Int_S V(y) K(x, y) dA,   K = cos_x cos_y / (pi r^2),   Int_S K = F (the form factor)

and every estimator the cook could use differs only in the V(y) it puts in that integral. Here S and R are two planes
meeting at a crease, and the integral over the crease's length is done in closed form, so each case is a 1D integral
the error of each V-hat can be read from exactly -- no rays, no noise.

  case P   the shadow edge parallel to the crease, a distance e out on S; the lit side nearer or farther than e.
           Receivers on R a height d above the crease. Per unit length along t (S's distance from the crease), with
           t = d tan(phi): K dt = sin(phi)/2 dphi, phi in [0, pi/2).
  case X   the shadow edge across the crease (S lit for u > 0, u along the crease). Receivers on R at (u0, d). With
           u - u0 = d tan(psi): K du = dpsi / (2 pi), psi in (-pi/2, pi/2).

The estimators are the code's (texel_nodes.glsl nodeSetAt, hit_cells.comp mode 1, texel_nodes_shade.glsl) and the
candidates (a light-space map, a shadow ray at the hit):

  search   the shade's blend of the sun bits of the 2x2 nodes about the hit on the 5 cm grid, Gaussian sigma 0.6 a node
  cell     the search's value at the centre of the hit's cell (per triangle, from its edge: the crease), one per cell
  near     hits nearer the receiver than 10 cm take the search instead of their cell (--cell-near 0.1, the default)
  map      a light-space depth map, its texels r apart on S, read with bilinear PCF (a point sun), or of the disc's sun
  ray      a shadow ray at the hit: exact, a point sun, or a point on the disc

Truth is the point sun (what the GPU reference traces, reference_wave.comp) and the 0.53 degree disc (what the frame's
direct sun has). A grid's phase against the edge is the texel-scale pattern: each estimator is run at 32 phases, and the
spread over phases is what neighbouring texels along the surface see differently.
"""
import numpy as np

SUN_HALF = np.radians(0.53 / 2)
PHASES = 32


def disc_fraction(s, w):
    """Visible share of the sun's disc at signed distance s from the shadow line (lit side positive), penumbra width w."""
    if w <= 0:
        return (s > 0).astype(float)
    xi = np.clip(s / (w / 2), -1, 1)
    return 0.5 + (xi * np.sqrt(1 - xi * xi) + np.arcsin(xi)) / np.pi


class Edge:
    """A straight shadow edge on S at coordinate `at`, lit on the side `sign` (+1: coordinate > at)."""
    def __init__(self, at, sign, penumbra):
        self.at, self.sign, self.w = at, sign, penumbra

    def point(self, x):
        return (self.sign * (x - self.at) > 0).astype(float)

    def disc(self, x):
        return disc_fraction(self.sign * (x - self.at), self.w)


def search(v_at, x, spacing, phase, sigma=0.6, floor=None):
    """nodeSetAt along one axis: the two nodes bracketing x on the grid, Gaussian-weighted (in node units). A node whose
    grid cell lies wholly past `floor` (the crease) is missing; one straddling it sits at its part's centre."""
    f = (x - phase) / spacing - 0.5
    base = np.floor(f)
    total = np.zeros_like(x)
    acc = np.zeros_like(x)
    for k in (0, 1):
        lo = phase + (base + k) * spacing
        hi = lo + spacing
        pos = lo + spacing / 2
        ok = np.ones_like(x, dtype=bool)
        if floor is not None:
            ok = hi > floor
            pos = np.where(lo < floor, (np.maximum(lo, floor) + hi) / 2, pos)
        wgt = np.where(ok, np.exp(-((pos - x) / spacing) ** 2 / (2 * sigma * sigma)), 0.0)
        acc += wgt * v_at(pos)
        total += wgt
    return np.where(total > 0, acc / np.maximum(total, 1e-30), 0.0)


def cell(v_at, x, size, phase, node_spacing, node_phase, floor=None):
    """hit_cells.comp mode 1: one value per cell, the search's at its centre."""
    centre = phase + (np.floor((x - phase) / size) + 0.5) * size
    if floor is not None:
        centre = np.maximum(centre, floor + size / 2 * 0)  # cells start at the triangle's edge: phase is 0 there
    return search(v_at, centre, node_spacing, node_phase, floor=floor)


def light_map(v_at, x, texel, phase):
    """A light-space map read with bilinear PCF: the visibility at the map's texel centres, linearly interpolated."""
    f = (x - phase) / texel
    j = np.floor(f)
    a = f - j
    return (1 - a) * v_at(phase + j * texel) + a * v_at(phase + (j + 1) * texel)


def estimators(edge, along_crease, near_share_fn):
    """Each estimator as V-hat(coordinate on S, phase index). `along_crease`: case X (cells' phase free along u)."""
    s5, c20, c10 = 0.05, 0.20, 0.10

    def ph(i, size, k):
        return ((i * (0.6180339887, 0.7548776662, 0.5698402910)[k] + 0.5 / PHASES) % 1.0) * size

    floor = None if along_crease else 0.0

    def cell_phase(i, size):
        return ph(i, size, 1) if along_crease else 0.0

    def srch(x, i):
        return search(edge.point, x, s5, ph(i, s5, 0), floor=floor)

    def cel(x, i, size):
        return cell(edge.point, x, size, cell_phase(i, size), s5, ph(i, s5, 0), floor=floor)

    def mixed(x, i, size, near):
        nu = near(x)
        return nu * srch(x, i) + (1 - nu) * cel(x, i, size)

    return {
        "search 5cm": srch,
        "cell 20cm": lambda x, i: cel(x, i, c20),
        "cell 20 + near 10 (now)": lambda x, i: mixed(x, i, c20, near_share_fn),
        "cell 10 + near 10": lambda x, i: mixed(x, i, c10, near_share_fn),
        "map 2cm": lambda x, i: light_map(edge.point, x, 0.02, ph(i, 0.02, 2)),
        "map 5cm": lambda x, i: light_map(edge.point, x, 0.05, ph(i, 0.05, 2)),
        "map 10cm": lambda x, i: light_map(edge.point, x, 0.10, ph(i, 0.10, 2)),
        "map 5cm, disc": lambda x, i: light_map(edge.disc, x, 0.05, ph(i, 0.05, 2)),
        "ray, point sun": lambda x, i: edge.point(x),
        "ray, disc": lambda x, i: edge.disc(x),
    }


def case_p(d, edge, n=20000):
    """Parallel edge: nodes t = d tan(phi), weights sin(phi)/2 dphi. Returns (t, weights, near share)."""
    phi = (np.arange(n) + 0.5) / n * (np.pi / 2)
    t = d * np.tan(phi)
    wts = np.sin(phi) / 2 * (np.pi / 2 / n)

    def near(tt):
        # Along the crease, the share of the strip's kernel within 10 cm of the receiver: Int du / (a^2 + u^2)^2.
        a2 = tt * tt + d * d
        a = np.sqrt(a2)
        U = np.sqrt(np.maximum(0.0, 0.01 - a2))
        return (U * a / (a2 + U * U) + np.arctan(U / a)) / (np.pi / 2)

    return t, wts, near


def case_x(u0, d, n=20000):
    psi = (np.arange(n) + 0.5) / n * np.pi - np.pi / 2
    u = u0 + d * np.tan(psi)
    wts = np.full(n, 1 / (2 * np.pi) * (np.pi / n))

    def near(uu):
        b2 = (uu - u0) ** 2 + d * d
        return np.clip(1 - b2 / 0.01, 0, 1)

    return u, wts, near


def run(title, rows, make):
    """rows: (label, receiver args); make(args) -> (coordinates, weights, near, edge)."""
    print(f"\n== {title}")
    print("   relative error of the sun's bounce against the point sun (mean over grid phases, +- its spread over them)")
    header = None
    for label, args in rows:
        x, w, near, edge = make(*args)
        ests = estimators(edge, along_crease=make is make_x, near_share_fn=near)
        truth = np.sum(edge.point(x) * w)
        disc = np.sum(edge.disc(x) * w)
        if header is None:
            header = list(ests)
            print("   " + f"{'receiver':<26}{'E/F':>6}{'disc':>8}" + "".join(f"{h:>25}" for h in header))
        cells = []
        for name, f in ests.items():
            errs = np.array([np.sum(f(x, i) * w) / truth - 1 for i in range(PHASES)]) if truth > 1e-9 else np.zeros(1)
            cells.append(f"{100 * errs.mean():+7.1f}% +-{100 * errs.std():5.1f}%".rjust(25))
        print("   " + f"{label:<26}{truth / 0.5:6.3f}{100 * (disc / truth - 1):+7.1f}%" + "".join(cells))


def make_p(d, e, sign, penumbra):
    t, w, near = case_p(d, None)
    return t, w, near, Edge(e, sign, penumbra)


def make_x(u0, d, penumbra):
    u, w, near = case_x(u0, d)
    return u, w, near, Edge(0.0, +1, penumbra)


def noise(title, rows, make, rays=384):
    """The sun's bounce's own noise at `rays` cosine rays: sqrt((E[V^2] - E[V]^2) / N) / E[V] over the ray's hits."""
    print(f"\n== {title}: relative noise of the sun's bounce at {rays} rays (phase-averaged)")
    for label, args in rows:
        x, w, near, edge = make(*args)
        ests = estimators(edge, along_crease=make is make_x, near_share_fn=near)
        parts = []
        for name in ("cell 20 + near 10 (now)", "search 5cm", "map 5cm", "ray, point sun", "ray, disc"):
            f = ests[name]
            out = []
            for i in range(8):
                v = f(x, i)
                m, m2 = np.sum(v * w), np.sum(v * v * w)
                out.append(np.sqrt(max(m2 - m * m, 0) / rays) / max(m, 1e-12))
            parts.append(f"{name}: {100 * np.mean(out):4.1f}%")
        print(f"   {label:<26}" + "   ".join(parts))


if __name__ == "__main__":
    pen = 0.056   # Cornell: the skylight's edge 6 m above the floor -> a 5.6 cm penumbra
    rows_p = []
    for sign, side in ((-1, "lit near"), (+1, "lit far")):
        for e in (0.02, 0.05, 0.10, 0.20, 0.50):
            for d in (0.02, 0.05, 0.10, 0.20):
                rows_p.append((f"{side} e={100*e:.0f} d={100*d:.0f}cm", (d, e, sign, pen)))
    run("case P: shadow edge parallel to the crease, e out on the source; receiver d up the other face", rows_p, make_p)
    rows_x = [(f"u0={100*u0:+.0f} d={100*d:.0f}cm", (u0, d, pen))
              for d in (0.02, 0.05, 0.10) for u0 in (-0.20, -0.10, -0.05, -0.02, 0.0, 0.02, 0.05, 0.10, 0.20)]
    run("case X: shadow edge across the crease (source lit for u > 0); receiver at (u0, d)", rows_x, make_x)
    noise("case P", [r for r in rows_p if "d=5cm" in r[0] or "d=2cm" in r[0]][:10], make_p)
    noise("case X", rows_x[:9], make_x)
