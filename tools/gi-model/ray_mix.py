# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The gather's real ray mix, and what interleaving its cells can cancel (uv run tools/gi-model/ray_mix.py).

texel_nodes_rays.comp, a pass of 32 rays a cell: 16 cosine (radius stratified, azimuth by the golden angle plus a rotation),
8 by the guide (a CDF over 64 bins of the patch's incoming light), 8 at the light list (the patches sending it most, a
point of one picked by its share); every ray weighed by the balance heuristic over the three (counts 16/8/8). A cell's
light is the mean over its passes; the coarse filter averages 3x3 neighbours. Interleaving (tried, 2026-10-21) shares the
tile's random numbers and shifts each of the nine by its place's radical inverses.

The hemisphere here is the unit square (u, v) -> the cosine-weighted disk, three sources: a sky window (the doorway, far),
a bright patch (the sunlit floor, 1.5 m), a near wall (0.3 m) with a gradient. A neighbour 20 cm off sees each shifted by
its parallax (spacing / distance). --far-cut: the sky window's rays past the reach take the record instead (no noise).
Reported: one cell's noise, and the 3x3 average's for independent cells and interleaved ones."""
import sys
import numpy as np

rng = np.random.default_rng(11)
GOLD = 0.6180339887


def disk(u, v):
    r = np.sqrt(u); t = 2 * np.pi * v
    return r * np.cos(t), r * np.sin(t)


class Scene:
    def __init__(self, parallax, far_cut, near_share):
        self.parallax, self.far_cut, self.near = parallax, far_cut, near_share

    def radiance(self, x, y, shift):
        """Radiance at disk point (x, y) for a cell offset `shift` (in cells) from the tile's centre."""
        sx, sy = shift
        L = np.full_like(x, 0.05)                                                 # dim surroundings
        wx, wy = x - 0.35 - 0.04 * sx * self.parallax, y - 0.1 - 0.04 * sy * self.parallax
        sky = (np.abs(wx) < 0.15) & (np.abs(wy) < 0.25)                          # the doorway's sky, 5 m off
        if not self.far_cut: L = L + 1.0 * sky
        bx, by = x + 0.3 - 0.13 * sx * self.parallax, y - 0.35 - 0.13 * sy * self.parallax
        L = L + 6.0 * ((bx * bx + by * by) < 0.08 ** 2)                          # the sunlit patch, 1.5 m off
        nx = x - 0.6 * sx * self.parallax                                         # the near wall, 0.3 m off
        L = L + self.near * (nx < -0.45) * (0.5 + 0.5 * y)
        return L

    def far_part(self):
        return 0.0 if not self.far_cut else 1.0 * 0.3 * 0.5 / np.pi   # the record's (deterministic) share


def guide_pdf_table(scene, bins=8):
    u = (np.arange(bins) + 0.5) / bins
    U, V = np.meshgrid(u, u, indexing="ij")
    x, y = disk(U, V)
    p = scene.radiance(x, y, (0, 0)) + 0.05
    return p / p.sum()


def pass_estimate(scene, shift, rnd, table, bins=8):
    """One pass of 32 rays: returns the MIS estimate of the mean radiance over the cosine lobe (irradiance / pi)."""
    rot, jit, g1, g2, l1, l2 = rnd
    total = 0.0
    nc, ng, nl = 16, 8, 8
    # cosine
    k = np.arange(nc)
    u_c = (k + jit[:nc]) / nc; v_c = np.mod(k * GOLD + rot, 1.0)
    # guide: a bin by its CDF at a stratified point, uniform inside it
    cdf = np.cumsum(table.ravel())
    kg = np.arange(ng)
    pick = np.searchsorted(cdf, (kg + jit[nc:nc + ng]) / ng).clip(0, bins * bins - 1)
    u_g = (pick // bins + g1) / bins; v_g = (pick % bins + g2) / bins
    # light: a point of the bright patch's disk (here the one source on the list)
    kl = np.arange(nl)
    ang = 2 * np.pi * np.mod(kl * GOLD + l1, 1.0); rr = 0.08 * np.sqrt(np.mod((kl + l2) / nl, 1.0))
    lx = -0.3 + 0.13 * shift[0] * scene.parallax + rr * np.cos(ang); ly = 0.35 + 0.13 * shift[1] * scene.parallax + rr * np.sin(ang)
    def uv_of(x, y):
        return np.clip(x * x + y * y, 0, 1), np.mod(np.arctan2(y, x) / (2 * np.pi), 1.0)
    u_l, v_l = uv_of(lx, ly)
    U = np.concatenate([u_c, u_g, u_l]); V = np.concatenate([v_c, v_g, v_l])
    x, y = disk(U, V)
    f = scene.radiance(x, y, shift)
    # densities on the unit square (cosine: 1; guide: table bin * bins^2; light: uniform on the patch's disk, in (u, v))
    gi = (np.floor(U * bins).clip(0, bins - 1) * bins + np.floor(V * bins).clip(0, bins - 1)).astype(int)
    p_c = np.ones_like(U); p_g = table.ravel()[gi] * bins * bins
    cx, cy = -0.3 + 0.13 * shift[0] * scene.parallax, 0.35 + 0.13 * shift[1] * scene.parallax
    inside = ((x - cx) ** 2 + (y - cy) ** 2) < 0.08 ** 2
    p_l = np.where(inside, 1.0 / (np.pi * 0.08 ** 2) * np.pi, 0.0)   # disk area density -> (u, v): the map's Jacobian is pi
    mix = (nc * p_c + ng * p_g + nl * p_l) / 32.0
    return (f / mix).mean() + scene.far_part()


def run(scene, passes=12, tiles=1500, interleave=False):
    table = guide_pdf_table(scene)
    w = np.array([np.exp(-0.5 * (dx * dx + dy * dy)) for dy in (-1, 0, 1) for dx in (-1, 0, 1)])
    offsets = [(dx, dy) for dy in (-1, 0, 1) for dx in (-1, 0, 1)]
    one, avg = [], []
    def ri(i, b):
        f, r = 1.0, 0.0
        while i: f /= b; r += f * (i % b); i //= b
        return r
    for _ in range(tiles):
        ests = np.zeros(9)
        for p in range(passes):
            shared = (rng.random(), rng.random(32), rng.random(), rng.random(), rng.random(), rng.random())
            for c, off in enumerate(offsets):
                if interleave:
                    rot, jit, g1, g2, l1, l2 = shared
                    rnd = ((rot + ri(c, 3)) % 1, (jit + ri(c, 2)) % 1, (g1 + ri(c, 5)) % 1, (g2 + ri(c, 7)) % 1, (l1 + ri(c, 5)) % 1, (l2 + ri(c, 7)) % 1)
                else:
                    rnd = (rng.random(), rng.random(32), rng.random(), rng.random(), rng.random(), rng.random())
                ests[c] += pass_estimate(scene, off, rnd, table) / passes
        one.append(ests[4]); avg.append((w * ests).sum() / w.sum())
    one, avg = np.array(one), np.array(avg)
    return one.std() / one.mean(), avg.std() / avg.mean()


if __name__ == "__main__":
    print(f"{'case':<44}{'one cell':>9}{'3x3 independent':>17}{'3x3 interleaved':>17}{'gain':>7}")
    for name, par, cut, near in (("no parallax, all sampled", 0.0, False, 0.0),
                                 ("parallax, all sampled", 1.0, False, 0.0),
                                 ("parallax, a near wall", 1.0, False, 0.6),
                                 ("parallax, far cut (the record)", 1.0, True, 0.0),
                                 ("parallax, far cut, a near wall", 1.0, True, 0.6)):
        sc = Scene(par, cut, near)
        o, a = run(sc, interleave=False)
        _, b = run(sc, interleave=True)
        print(f"{name:<44}{100 * o:8.2f}%{100 * a:16.2f}%{100 * b:16.2f}%{a / b:6.2f}x")
