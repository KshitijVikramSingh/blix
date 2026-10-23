# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""A texel's light as a function of the normal, in a few bases, from the gather's own rays (uv run
tools/gi-model/directional_bases.py DUMP [DUMP ...]).

The light-agnostic texel record (one irradiance function of the normal for every light) needs a basis. Each DUMP is a
view dump of a run with --lean-shade --ray-capture B at many rays a texel (--live-rays 2048 --texel-max-rays 2048): every
ray of 1 in 2^B texels, its direction, its light over its density about the texel's normal (L / pdf, both columns) and
that cosine (texel_nodes_lean.comp). A texel's irradiance at a normal n is the rays' mean of L / pdf x max(n . w, 0);
all its rays are the reference. Each basis is fitted (least squares) over normals within 45 degrees of the texel's -- the
normals a normal map gives -- from a random subset of RAYS rays, and judged at normals tilted up to 20 and 40 degrees
against the reference, as |error| over the texel's irradiance at its own normal (luminance, and the worst channel). The
same fit from every ray gives the basis's own error, without the noise.
  flat     the irradiance at the texel's normal, at every normal (no direction)
  tilt     today's detail: that and its first-order change as the normal tilts (the rays' gradient)
  H4       1, x, y, z per channel
  H6       H4 and xy, x^2 - y^2 per channel (the H-basis' span)
  HL2      the nine quadratic terms per channel
  hybrid   H4 per channel + the texel's colour x (luminance HL2 - luminance H4): 17 numbers
"""
import json
import sys
from pathlib import Path

import numpy as np

RAYS = 128
SHAPE_ONLY = '--shape' in sys.argv
SUBSETS = 4
LUM = np.array([0.2126, 0.7152, 0.0722])
rng = np.random.default_rng(7)


def oct_decode(o):
    x, y = o[:, 0], o[:, 1]
    z = 1 - np.abs(x) - np.abs(y)
    neg = z < 0
    x2 = np.where(neg, (1 - np.abs(y)) * np.sign(x + 1e-30), x)
    y2 = np.where(neg, (1 - np.abs(x)) * np.sign(y + 1e-30), y)
    d = np.stack([x2, y2, z], 1)
    return d / np.linalg.norm(d, axis=1, keepdims=True)


def frame(n):
    t = np.cross([0.0, 1.0, 0.0] if abs(n[1]) < 0.999 else [1.0, 0.0, 0.0], n)
    t /= np.linalg.norm(t)
    return t, np.cross(n, t)


def cone(m, maxdeg, seed):
    g = np.random.default_rng(seed)
    t = np.arccos(1 - g.random(m) * (1 - np.cos(np.radians(maxdeg))))
    p = 2 * np.pi * g.random(m)
    return np.stack([np.sin(t) * np.cos(p), np.sin(t) * np.sin(p), np.cos(t)], 1)


FIT, T20, T40 = cone(300, 45, 1), cone(200, 20, 2), cone(200, 40, 3)


def basis(n, name):
    x, y, z = n[:, 0], n[:, 1], n[:, 2]
    one = np.ones_like(x)
    f = {"H4": [one, x, y, z], "H6": [one, x, y, z, x * y, x * x - y * y],
         "HL2": [one, x, y, z, x * y, y * z, x * z, x * x - y * y, 3 * z * z - 1]}[name]
    return np.stack(f, 1)


def irradiance(w, l, n):
    """w: ray directions in the texel's frame, l: L/pdf rgb; n: normals in the frame. Mean over rays."""
    return np.clip(n @ w.T, 0, None) @ l / len(w)


def predict(name, w, l, n_eval):
    e_fit = irradiance(w, l, FIT)
    e0 = irradiance(w, l, np.array([[0.0, 0.0, 1.0]]))[0]
    if name == "flat":
        return np.repeat(e0[None], len(n_eval), 0)
    if name == "tilt":
        # the gradient of the irradiance at the normal: d/dn max(n . w, 0) = w for w above the horizon
        up = w[:, 2] > 0
        g = (w[up, :2].T @ l[up]) / len(w)          # 2 x 3
        return e0[None] + n_eval[:, :2] @ g
    if name == "hybrid":
        c4 = np.linalg.lstsq(basis(FIT, "H4"), e_fit, rcond=None)[0]
        lum = e_fit @ LUM
        c2 = np.linalg.lstsq(basis(FIT, "HL2"), lum, rcond=None)[0]
        c1 = np.linalg.lstsq(basis(FIT, "H4"), lum, rcond=None)[0]
        chroma = e0 / max(e0 @ LUM, 1e-12)
        resid = basis(n_eval, "HL2") @ c2 - basis(n_eval, "H4") @ c1
        return basis(n_eval, "H4") @ c4 + resid[:, None] * chroma[None]
    c = np.linalg.lstsq(basis(FIT, name), e_fit, rcond=None)[0]
    return basis(n_eval, name) @ c


def main():
    variants = ["flat", "tilt", "H4", "H6", "HL2", "hybrid"]
    for dump in [a for a in sys.argv[1:] if not a.startswith('--')]:
        d = sorted(Path(dump).glob("view-*"))[-1]
        meta = json.loads((d / "meta.json").read_text())
        n_tex = meta["texelCount"]
        tex = np.frombuffer((d / "texels.f32x8").read_bytes(), np.float32).reshape(-1, 8)[:n_tex]
        raw = (d / "rays_captured.u32x8").read_bytes()
        rec = np.frombuffer(raw[16:], np.uint32).reshape(-1, 8)
        ids = rec[:, 0].astype(np.int64)
        o = np.stack([(rec[:, 1] & 0xFFFF).astype(np.uint16).view(np.int16), (rec[:, 1] >> 16).astype(np.uint16).view(np.int16)], 1) / 32767.0
        dirs = oct_decode(o)
        l = np.stack([rec[:, 2], rec[:, 3], rec[:, 4]], 1).copy().view(np.float32).astype(np.float64)
        order = np.argsort(ids, kind="stable")
        ids, dirs, l = ids[order], dirs[order], l[order]
        starts = np.r_[0, np.flatnonzero(np.diff(ids)) + 1, len(ids)]
        stats = {v: {"basis": [], "noisy": []} for v in variants}
        used = 0
        for a, b in zip(starts[:-1], starts[1:]):
            if b - a < 1500:
                continue
            n0 = tex[ids[a], 4:7].astype(np.float64)
            if np.linalg.norm(n0) < 1e-6:
                continue
            n0 /= np.linalg.norm(n0)
            t, bt = frame(n0)
            w = dirs[a:b] @ np.stack([t, bt, n0], 1)       # into the texel's frame
            lt = l[a:b]
            ref = {k: irradiance(w, lt, T) for k, T in (("20", T20), ("40", T40))}
            e0 = irradiance(w, lt, np.array([[0.0, 0.0, 1.0]]))[0]
            scale = max(e0 @ LUM, 1e-9)
            if scale < 1e-6:
                continue
            used += 1
            for v in variants:
                for kind, sub in (("basis", None), ("noisy", True)):
                    errs = []
                    for s in range(SUBSETS if sub else 1):
                        pick = rng.choice(b - a, RAYS, replace=False) if sub else slice(None)
                        row = []
                        for k, T in (("20", T20), ("40", T40)):
                            p = predict(v, w[pick], lt[pick], T)
                            if SHAPE_ONLY:   # the direction alone: the prediction scaled to the reference at the texel's normal
                                p0 = predict(v, w[pick], lt[pick], np.array([[0.0, 0.0, 1.0]]))[0]
                                p = p * (e0 / np.where(np.abs(p0) > 1e-12, p0, 1.0))[None]
                            e = np.abs(p - ref[k])
                            row += [(e @ LUM).max() / scale, np.median(e @ LUM) / scale, (e / np.maximum(e0, 1e-9)).max()]
                        errs.append(row)
                    stats[v][kind].append(np.mean(errs, 0))
        print(f"\n{d.parent.name}: {used} texels of >= 1500 rays; fit from {RAYS} rays ({SUBSETS} subsets) and from all of them")
        print("  per texel: max |error| over normals tilted <=20 / <=40 (luminance, over the texel's own irradiance); "
              "median and p90 over texels")
        print(f"  {'':8s} {'from all rays (the basis)':>34s}   {'from ' + str(RAYS) + ' rays (basis + noise)':>34s}")
        print(f"  {'':8s} {'<=20 med':>9s} {'p90':>7s} {'<=40 med':>9s} {'p90':>7s}   {'<=20 med':>9s} {'p90':>7s} {'<=40 med':>9s} {'p90':>7s}")
        for v in variants:
            cells = []
            for kind in ("basis", "noisy"):
                arr = np.array(stats[v][kind])
                for col in (0, 3):
                    cells += [np.median(arr[:, col]), np.percentile(arr[:, col], 90)]
            print(f"  {v:8s} " + " ".join(f"{100 * c:8.1f}%" if i % 2 == 0 else f"{100 * c:6.1f}%" for i, c in enumerate(cells[:4]))
                  + "   " + " ".join(f"{100 * c:8.1f}%" if i % 2 == 0 else f"{100 * c:6.1f}%" for i, c in enumerate(cells[4:])))


if __name__ == "__main__":
    main()
