# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The texel record's H4 kernel: the per-ray weights that make the gather's H4 coefficients (uv run
tools/gi-model/h4_kernel.py [--glsl] [DUMP]).

The record's direction is H4 per channel, E(n) ~ a + b . n in the record's frame (t1, t2, n0), fitted by least squares
over normals within 45 deg of the record's (tools/gi-model/directional_bases.py). The fit is linear in the rays: with
E_j = mean_i r_i max(n_j . w_i, 0) at the fit's normals n_j (r_i a ray's L / pdf), the coefficients are
c = mean_i r_i K(w_i), K(w) = pinv(A) max(N w, 0), A the fit's [1, n_j]. By symmetry about n0, K(w) for w at polar angle
th and azimuth ph is (k0(th), k1(th) cos ph, k1(th) sin ph, kz(th)): three tables of th, read with w's frame components
(k1 / sin th times w.x, w.y). --glsl prints them for texel_nodes_lean.comp; with a DUMP (a --ray-capture at many rays a
texel, e.g. hb-crev) the table's estimate is held against the least-squares fit of the same rays.
"""
import sys
from pathlib import Path

import numpy as np

STEPS = 256     # th in [0, pi], STEPS + 1 entries
CONE = 45.0


def fit_normals(rings=48, azimuths=96):
    """The fit's normals: rings evenly in th within the cone, evenly in ph, each weighted by its solid angle -- symmetric
    about the record's normal, as the continuous fit is (a random set's asymmetry put 4.5% between this file's table and its
    own least squares)."""
    th = (np.arange(rings) + 0.5) / rings * np.radians(CONE)
    ph = (np.arange(azimuths) + 0.5) / azimuths * 2 * np.pi
    T, Ph = np.meshgrid(th, ph, indexing='ij')
    n = np.stack([np.sin(T) * np.cos(Ph), np.sin(T) * np.sin(Ph), np.cos(T)], -1).reshape(-1, 3)
    return n, np.sin(T).ravel()


FIT, FITW = fit_normals()
_A = np.c_[np.ones(len(FIT)), FIT]
PINV = np.linalg.solve((_A * FITW[:, None]).T @ _A, (_A * FITW[:, None]).T)     # weighted least squares: 4 x M


def kernel(w):
    """K for directions w (k x 3) in the record's frame: k x 4 (a, bx, by, bz)."""
    return (np.clip(w @ FIT.T, 0, None) @ PINV.T)


def tables():
    th = np.linspace(0, np.pi, STEPS + 1)
    # at azimuth 0: w = (sin th, 0, cos th); k1 = K's x term, the y term zero by symmetry
    w = np.stack([np.sin(th), np.zeros_like(th), np.cos(th)], 1)
    K = kernel(w)
    k1s = np.where(np.sin(th) > 1e-6, K[:, 1] / np.maximum(np.sin(th), 1e-6), 0.0)
    # th -> 0: k1 / sin th's limit, from the next entry
    k1s[0] = k1s[1]
    return th, K[:, 0], k1s, K[:, 3]


def table_kernel(w, k0, k1s, kz):
    th = np.arccos(np.clip(w[:, 2], -1, 1))
    x = th / np.pi * STEPS
    i = np.clip(np.floor(x).astype(int), 0, STEPS - 1)
    f = x - i
    lerp = lambda t: t[i] * (1 - f) + t[i + 1] * f
    return np.stack([lerp(k0), lerp(k1s) * w[:, 0], lerp(k1s) * w[:, 1], lerp(kz)], 1)


def main():
    th, k0, k1s, kz = tables()
    if '--glsl' in sys.argv:
        def arr(name, t):
            body = ', '.join(f'{v:.6f}' for v in t)
            return f'const float {name}[{STEPS + 1}] = float[{STEPS + 1}]({body});'
        print(f'// tools/gi-model/h4_kernel.py: the H4 fit over normals within {CONE:.0f} deg, per ray; th in [0, pi], {STEPS} steps')
        print(arr('H4K0', k0)); print(arr('H4K1S', k1s)); print(arr('H4KZ', kz))
        return
    # self-check: the table against the kernel at random directions
    g = np.random.default_rng(2)
    w = g.normal(size=(20000, 3)); w /= np.linalg.norm(w, axis=1, keepdims=True)
    err = np.abs(table_kernel(w, k0, k1s, kz) - kernel(w)).max()
    print(f'table against the kernel: max |diff| {err:.2e} (kernel magnitude ~{np.abs(kernel(w)).max():.2f})')
    dumps = [a for a in sys.argv[1:] if not a.startswith('--')]
    for dump in dumps:
        sys.path.insert(0, str(Path(__file__).parent))
        import directional_bases as db
        import json
        d = sorted(Path(dump).glob('view-*'))[-1]
        n_tex = json.loads((d / 'meta.json').read_text())['texelCount']
        tex = np.frombuffer((d / 'texels.f32x8').read_bytes(), np.float32).reshape(-1, 8)[:n_tex]
        raw = (d / 'rays_captured.u32x8').read_bytes()
        rec = np.frombuffer(raw[16:], np.uint32).reshape(-1, 8)
        ids = rec[:, 0].astype(np.int64)
        o = np.stack([(rec[:, 1] & 0xFFFF).astype(np.uint16).view(np.int16), (rec[:, 1] >> 16).astype(np.uint16).view(np.int16)], 1) / 32767.0
        dirs = db.oct_decode(o)
        l = np.stack([rec[:, 2], rec[:, 3], rec[:, 4]], 1).copy().view(np.float32).astype(np.float64)
        rel = []
        for tx in np.unique(ids)[:200]:
            m = ids == tx
            if m.sum() < 1500:
                continue
            n0 = tex[tx, 4:7].astype(float); n0 /= np.linalg.norm(n0)
            t1, t2 = db.frame(n0)
            wl = dirs[m] @ np.stack([t1, t2, n0], 1)
            lt = l[m]
            c_table = (table_kernel(wl, k0, k1s, kz).T @ lt) / len(wl)            # 4 x 3
            # least squares on the same rays at this file's fit normals
            e_fit = np.clip(FIT @ wl.T, 0, None) @ lt / len(wl)
            c_lsq = PINV @ e_fit
            scale = np.abs(c_lsq[0] + c_lsq[3]).max()
            rel.append(np.abs(c_table - c_lsq).max() / max(scale, 1e-12))
            # and the H4 irradiance at the normal against the rays' plain mean
        print(f'{dump}: {len(rel)} texels; the table coefficients against least squares on the same rays: max |diff| over '
              f'the irradiance, median {np.median(rel):.2e}, max {np.max(rel):.2e}')


if __name__ == '__main__':
    main()
