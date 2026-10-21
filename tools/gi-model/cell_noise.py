# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The coarse cells' ray noise as it reaches the fine copies, and what each remedy buys (uv run tools/gi-model/cell_noise.py).

A fine texel copies its coarse cells' light (transfer_fine.comp: a Gaussian blend of the cells about it), after the coarse
filter (coarse_filter.comp: a linear fit over the 3x3 neighbours in the surface, weights exp(-d^2/2), the slope damped).
Each cell's light carries its gather's noise, sigma_c ~ 1/sqrt(rays): copied, it becomes smooth clouds a few cells wide
-- what a converged path trace shows against the GI on every wall (crevices' view dump, 2026-10-21: +-1-1.5% blobs).
Here: unit white noise on a grid of cells, through each remedy, the clouds' amplitude (std at the fine texels, over
sigma_c); and the bias each puts on curved light, what a wider filter costs. --interleave: the nine cells of a 3x3 tile
sharing their samples, each its own shift -- 1.3-1.4x less here, but tried in the engine (texel_nodes_rays.comp, every
random number shifted by its place's radical inverses) no less noise on the testbeds' surfaces and crevices' creases worse
(2026-10-21): its cells come at several levels and sizes and the filter weighs edges, not the model's even tiling."""
import numpy as np

rng = np.random.default_rng(5)
N = 192


def fit(E, radius, ridge=0.1):
    """coarse_filter.comp's linear fit at every cell over its (2r+1)^2 neighbours (no edges, a flat surface)."""
    offs = [(dy, dx) for dy in range(-radius, radius + 1) for dx in range(-radius, radius + 1)]
    w = np.array([np.exp(-0.5 * (dx * dx + dy * dy)) for dy, dx in offs])
    S = w.sum(); sxx = sum(wi * dx * dx for wi, (dy, dx) in zip(w, offs)); syy = sxx
    V = sum(wi * np.roll(np.roll(E, -dy, 0), -dx, 1) for wi, (dy, dx) in zip(w, offs))
    # the offsets' first moments vanish over a full neighbourhood: c is the weighted mean (the fit's value at the cell)
    return V / S


def copy(E, upsample=4, sigma=0.6):
    """transfer_fine.comp's copy: fine texels `upsample` to a cell, each a Gaussian blend (sigma in cells) of the 4x4 cells about it."""
    H, W = E.shape
    ys = (np.arange(H * upsample) + 0.5) / upsample - 0.5
    xs = (np.arange(W * upsample) + 0.5) / upsample - 0.5
    out = np.zeros((H * upsample, W * upsample)); tot = np.zeros_like(out)
    for cy in range(-1, 3):
        for cx in range(-1, 3):
            jy = (np.floor(ys) + cy).astype(int); jx = (np.floor(xs) + cx).astype(int)
            wy = np.exp(-0.5 * ((ys - jy) / sigma) ** 2); wx = np.exp(-0.5 * ((xs - jx) / sigma) ** 2)
            w = wy[:, None] * wx[None, :]
            out += w * E[np.ix_(jy % H, jx % W)]; tot += w
    return out / tot


def chain(E, passes, radius):
    for _ in range(passes): E = fit(E, radius)
    return copy(E)


if __name__ == "__main__":
    noise = rng.standard_normal((N, N))
    fine = (np.arange(4 * N) + 0.5) / 4 - 0.5          # fine texels' positions, in cells
    print(f"{'remedy':<34}{'clouds (std / sigma_c)':>23}{'light 8 cells across':>22}{'4 cells':>9}   rays a cell")
    print(f"{'':<34}{'':>23}{'(error, its swing 20%)':>22}")
    rows = [("as now: 384 rays, 1 pass, 3x3", 1, 1, 384),
            ("2 passes, 3x3", 2, 1, 384),
            ("1 pass, 5x5", 1, 2, 384),
            ("2 passes, 5x5", 2, 2, 384),
            ("768 rays, 1 pass, 3x3", 1, 1, 768),
            ("1,536 rays, 1 pass, 3x3", 1, 1, 1536)]
    for name, passes, radius, rays in rows:
        amp = chain(noise, passes, radius).std() * np.sqrt(384 / rays)
        errs = []
        for lam in (8.0, 4.0):
            cell = 1 + 0.2 * np.cos(2 * np.pi * np.arange(N) / lam)
            E = np.ones((N, 1)) * cell[None, :]
            out = chain(E, passes, radius)[N // 2]
            truth = 1 + 0.2 * np.cos(2 * np.pi * fine / lam)
            errs.append(np.abs(out / truth - 1).max())
        print(f"{name:<34}{amp:23.3f}{100 * errs[0]:21.1f}%{100 * errs[1]:8.1f}%   {rays}")


def interleaved(M=384, cells=40000, bright=(0.45, 0.2, 0.08, 20.0)):
    """Each cell estimates a disk integral (the cosine-weighted hemisphere, mapped to the unit disk) with M stratified
    samples (a sqrt(M) x sqrt(M) jittered grid on the unit square, then to the disk) under a toroidal shift; the 3x3
    filter's average of nine neighbours (the same integral: light varying slowly across them). Independent shifts (as
    now: a random rotation per texel) against interleaved ones (the nine of a 3x3 tile offset by i/3, j/3 of a stratum,
    one random shift for the tile). The integrand: a dim sky and a bright source (cx, cy, radius, contrast), the doorway."""
    k = int(np.sqrt(M)); M = k * k
    cx, cy, rad, con = bright
    def f(u, v):
        r = np.sqrt(u); t = 2 * np.pi * v
        x, y = r * np.cos(t), r * np.sin(t)
        return 1 + con * (((x - cx) ** 2 + (y - cy) ** 2) < rad * rad)
    g = (np.arange(k) + 0.5) / k
    gu, gv = np.meshgrid(g, g)
    ex = []
    for _ in range(4000):
        truth = None
        def est(shift):
            return f((gu + shift[0]) % 1, (gv + shift[1]) % 1).mean()
        w = np.array([np.exp(-0.5 * (dx * dx + dy * dy)) for dy in (-1, 0, 1) for dx in (-1, 0, 1)])
        ind = [est(rng.random(2)) for _ in range(9)]
        base = rng.random(2)
        inter = [est(base + np.array([i % 3, i // 3]) / (3 * k)) for i in range(9)]
        ex.append(((w * ind).sum() / w.sum(), (w * inter).sum() / w.sum(), ind[4]))
    ex = np.array(ex)
    mean = ex[:, 2].mean()
    return ex[:, 2].std() / mean, ex[:, 0].std() / mean, ex[:, 1].std() / mean


if __name__ == "__main__" and "--interleave" in __import__("sys").argv:
    for M in (128, 384):
        one, iid, inter = interleaved(M)
        print(f"{M} rays a cell: one cell's noise {100 * one:.2f}%; the 3x3 filter's: independent shifts {100 * iid:.2f}%, interleaved {100 * inter:.2f}%  ({iid / inter:.1f}x less)")
