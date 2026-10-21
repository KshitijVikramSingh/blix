# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""What the coarse filter's linear-fit residual says about the fine copy's error (uv run tools/gi-model/copy_residual.py).

A fine texel copies its coarse cells' light (transfer_fine.comp: a Gaussian blend of the cells about it); the coarse
filter fits c + g.x over a cell's 3x3 neighbours (coarse_filter.comp). Where the light curves -- E = 1 + a x^2 + b x^3,
x in cells -- a copy errs by the curvature over a cell, and the fit's weighted residual grows with the same curvature,
noise aside. Here, for a range of curvatures, the worst copy error over a cell against the residual of the fit there, so
a flag on the residual can be set to an error it guarantees: residual / mean above r -> gathered."""
import numpy as np

w = lambda d: np.exp(-0.5 * d * d)          # the filter's distance weight (sigma a cell)
xs = np.array([-1.0, 0.0, 1.0])


def fit(E, centre):
    """The filter's linear fit about cell `centre` (its neighbours' light), c and its weighted residual."""
    x = xs; v = E(centre + x); ww = w(x)
    A = np.array([[ww.sum(), (ww * x).sum()], [(ww * x).sum(), (ww * x * x).sum() + 0.1 * ww.sum()]])
    c, g = np.linalg.solve(A, [(ww * v).sum(), (ww * x * v).sum()])
    resid = np.sqrt((ww * (v - (c + g * x)) ** 2).sum() / ww.sum())
    return c, resid


print(f"{'curvature a':>12}{'resid/mean':>12}{'worst copy err':>16}")
for a in (0.005, 0.01, 0.02, 0.04, 0.08, 0.16, 0.32):
    E = lambda x, a=a: 1 + a * x * x + 0.3 * a * x ** 3
    cells = {j: fit(E, j)[0] for j in range(-3, 4)}
    worst = 0.0
    for u in np.linspace(-0.5, 0.5, 21):
        js = np.arange(-2, 3)
        ww = w(u - js)
        copy = (ww * np.array([cells[j] for j in js])).sum() / ww.sum()
        worst = max(worst, abs(copy / E(u) - 1))
    _, r = fit(E, 0)
    print(f"{a:12.3f}{100 * r / E(0):11.2f}%{100 * worst:15.2f}%")
