# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""Light-free lists, cut and packed (uv run tools/gi-model/list_packing.py DIR_A DIR_B [DIR_C ...]).

Each DIR is a BLIX_LIST_ENTRY_OUT dump of the same cooked lists (SponzaLoop.Lists.cs, --lists --list-debug, before the
lists were packed: the dump's writer went with the 16-byte entries, 2026-10-23) summed under one sun: the lists as loaded (texels, start, entries, norm) and each entry's light (entry_light.f32x2: the sky's and the
sun's luminance, before the list's 1 / rays and scale). DIR_A is the sun the lists are cut under (the cook's); the rest
judge the cut under suns it never saw. What it found (the crevices corridor, suns (120, -40) and (300, -25) against
the cook's): ranked by light at A, a cut drops the entries dark at A (cells in A's shadow) and misses 8% at the others even
keeping 99.5%; ranked by weight, 98% keeps 77% of the entries for a p90 of 5-6% (cells carrying their bounce and sun alike):
not worth it, the lists stay whole. ids 1.4-5.2 bits a delta; rgb at 22 bits (log luminance 12, chroma 2 x 5) 0.01% median.
A list's light is norm.x * norm.y * its entries' sum; the truth at a sun is the
uncut list's.

Kinds (texel_lists.comp): 0 a sky bin, 1 a near texel, 2 a cell side (the sun at a far hit), 4 a patch (a far hit's
bounce). Reported:
  the cut     each list's entries by their light under A, kept to COVER of it, lit under each other sun:
                none     the dropped entries' light lost
                scaled   the kept times the list's whole over its kept, at A (one number a list)
                columns  that per column (the sky's, the sun's: two numbers)
                kinds    that per kind (four)
              each against the truth at that sun: bias, |error| median and p90, over lists weighed by their light
  the format  ids delta-coded within a kind of a list (sorted): the bits each takes; rgb quantised (log luminance at
              12 bits, chroma at 2 x 6; or three 8-bit mantissas on a shared exponent): the list's light off by
"""
import sys
from pathlib import Path

import numpy as np

KINDS = {0: "sky", 1: "texels", 2: "cells", 4: "patches"}
COVERS = (0.8, 0.9, 0.95, 0.98, 0.99)


def load(d):
    d = Path(d)
    start = np.fromfile(d / "start.u32", np.uint32).astype(np.int64)
    ent = np.fromfile(d / "entries.u32x4", np.uint32).reshape(-1, 4)
    norm = np.fromfile(d / "norm.f32x2", np.float32).reshape(-1, 2).astype(np.float64)
    light = np.fromfile(d / "entry_light.f32x2", np.float32).reshape(-1, 2).astype(np.float64)
    sun = (d / "sun.txt").read_text().split()
    return dict(start=start, ent=ent, norm=norm, light=light, sun=[float(x) for x in sun])


def halves(w):
    return np.stack([(w & 0xFFFF).astype(np.uint16).view(np.float16), (w >> 16).astype(np.uint16).view(np.float16)], -1).astype(np.float64)


def dev(pred, truth, weight):
    ok = truth > 1e-9
    r = pred[ok] / truth[ok] - 1
    w = weight[ok]
    order = np.argsort(np.abs(r))
    cw = np.cumsum(w[order]) / w.sum()
    med = np.abs(r[order][np.searchsorted(cw, 0.5)])
    p90 = np.abs(r[order][np.searchsorted(cw, 0.9)])
    return f"bias {100 * (pred[ok].sum() / truth[ok].sum() - 1):+.2f}%, |error| median {100 * med:.2f}% p90 {100 * p90:.2f}%"


def main():
    dirs = sys.argv[1:]
    runs = [load(d) for d in dirs]
    A = runs[0]
    start, ent, norm = A["start"], A["ent"], A["norm"]
    n = len(start) - 1
    m = len(ent)
    owner = np.repeat(np.arange(n), np.diff(start))
    kind = (ent[:, 0] & 7).astype(np.int64)
    target = ent[:, 1].astype(np.int64)
    scale = norm[:, 0] * norm[:, 1]
    print(f"{n:,} lists, {m:,} entries ({m / n:.1f} a list): " + ", ".join(
        f"{KINDS[k]} {np.mean(kind == k) * m / n:.1f}" for k in KINDS))
    for r, d in zip(runs, dirs):
        tot = np.bincount(owner, r["light"].sum(1), minlength=n) * scale
        share = {KINDS[k]: r["light"][kind == k].sum() / max(r["light"].sum(), 1e-30) for k in KINDS}
        print(f"  {Path(d).name}: sun toward {np.round(r['sun'][:3], 3).tolist()}, the lists' light a list {tot.mean():.4f}; by kind "
              + ", ".join(f"{k} {100 * v:.1f}%" for k, v in share.items()))

    # The cut under A: per list, entries by their light at A, descending; the sky's always kept (20 a list, the sun's
    # own column's light every time the sky changes: kept whole).
    la = A["light"].sum(1)
    rgb = np.concatenate([halves(ent[:, 2]), halves(ent[:, 3])[:, :1]], 1)
    lum = rgb @ np.array([0.2126, 0.7152, 0.0722])
    for rank_name, rank in (("light at A", la), ("weight", lum)):
        cut(rank_name, rank, A, runs, dirs, owner, kind, scale, n, m)
    format_report(ent, owner, kind, target, rgb, lum, runs, dirs, scale, n)


def cut(rank_name, rank, A, runs, dirs, owner, kind, scale, n, m):
    la = A["light"].sum(1)
    key = np.lexsort((-rank, owner))
    own_s = owner[key]
    cum = np.zeros(m)
    la_s = rank[key]
    cs = np.cumsum(la_s)
    first = np.r_[0, np.flatnonzero(np.diff(own_s)) + 1]
    base = np.repeat(cs[first] - la_s[first], np.diff(np.r_[first, m]))
    cum = cs - base                                    # the list's light through this entry
    whole_r = np.bincount(owner, rank, minlength=n)
    whole_a = np.bincount(owner, la, minlength=n)
    print(f"\nthe cut by {rank_name}, against the uncut lists at each sun; lists weighed by their light there:")
    for cover in COVERS:
        # Kept: every entry until the list's running sum passes COVER of it (that entry kept), and every sky bin.
        before = cum - la_s
        kept_s = (before < cover * whole_r[own_s]) | (kind[key] == 0)
        kept = np.zeros(m, bool)
        kept[key] = kept_s
        count = np.bincount(owner, kept, minlength=n)
        line = [f"  cover {100 * cover:.1f}%: {count.mean():.1f} entries a list ({100 * count.sum() / m:.0f}% of them)"]
        for r, d in list(zip(runs, dirs))[1:]:
            L = r["light"]
            truth = np.bincount(owner, L.sum(1), minlength=n) * scale
            keptL = np.bincount(owner, L.sum(1) * kept, minlength=n)
            keptA = np.bincount(owner, la * kept, minlength=n)
            ratio = np.where(keptA > 0, whole_a / np.maximum(keptA, 1e-30), 1.0)
            none = keptL * scale
            scaled = keptL * ratio * scale
            cols = np.zeros(n)
            for c in (0, 1):
                kc = np.bincount(owner, L[:, c] * kept, minlength=n)
                ac = np.bincount(owner, A["light"][:, c] * kept, minlength=n)
                wc = np.bincount(owner, A["light"][:, c], minlength=n)
                cols += kc * np.where(ac > 0, wc / np.maximum(ac, 1e-30), 1.0)
            kinds = np.zeros(n)
            for k in KINDS:
                s = kind == k
                kk = np.bincount(owner[s], L.sum(1)[s] * kept[s], minlength=n)
                ak = np.bincount(owner[s], la[s] * kept[s], minlength=n)
                wk = np.bincount(owner[s], la[s], minlength=n)
                kinds += kk * np.where(ak > 0, wk / np.maximum(ak, 1e-30), 1.0)
            line.append(f"    at {Path(d).name}: none {dev(none, truth, truth)}; scaled {dev(scaled, truth, truth)}; "
                        f"columns {dev(cols * scale, truth, truth)}; kinds {dev(kinds * scale, truth, truth)}")
            if cover == COVERS[0]:
                # The sun's change itself, for scale: each list's light at this sun against A's.
                line.append(f"    (the sun changed each list's light by {dev(truth, np.bincount(owner, la, minlength=n) * scale, truth).split(', ', 1)[1]})")
        print("\n".join(line))



def format_report(ent, owner, kind, target, rgb, lum, runs, dirs, scale, n):
    # The format: ids delta-coded within a list's kind, sorted by target.
    print("\nthe format:")
    srt = np.lexsort((target, kind, owner))
    o, k, t = owner[srt], kind[srt], target[srt]
    newrun = np.r_[True, (np.diff(o) != 0) | (np.diff(k) != 0)]
    delta = np.where(newrun, t, np.diff(np.r_[0, t]))
    bits = np.ceil(np.log2(np.maximum(delta, 1) + 1))
    for kk in KINDS:
        s = (k == kk) & ~newrun
        f = (k == kk) & newrun
        if s.any():
            print(f"  {KINDS[kk]:8s}: ids up to {t[k == kk].max():,} ({int(np.ceil(np.log2(t[k == kk].max() + 2)))} bits); a delta "
                  f"{bits[s].mean():.1f} bits on average, p90 {np.percentile(bits[s], 90):.0f}, p99 {np.percentile(bits[s], 99):.0f}; "
                  f"a run's first {bits[f].mean():.1f}")
    for r, d in list(zip(runs, dirs))[1:2]:
        L = r["light"].sum(1)
        truth = np.bincount(owner, L, minlength=n) * scale
        for name, q in quantisers(rgb, lum):
            ql = q @ np.array([0.2126, 0.7152, 0.0722])
            f = np.where(lum > 1e-30, ql / np.maximum(lum, 1e-30), 1.0)
            pred = np.bincount(owner, L * f, minlength=n) * scale
            print(f"  rgb as {name}: at {Path(d).name} {dev(pred, truth, truth)}")


def quantisers(rgb, lum):
    out = []
    pos = np.maximum(lum, 1e-30)
    lg = np.log2(pos)
    lo, hi = np.percentile(lg[lum > 0], 0.01), lg.max()
    for b in (10, 12):
        step = (hi - lo) / (2 ** b - 1)
        ql = 2.0 ** (lo + np.round((np.clip(lg, lo, hi) - lo) / step) * step)
        # Chroma: r and g over r + g + b (b the rest), the luminance restoring the scale.
        chroma = rgb / np.maximum(rgb.sum(1), 1e-30)[:, None]
        for cb in (5, 6):
            qrg = np.round(chroma[:, :2] * (2 ** cb - 1)) / (2 ** cb - 1)
            qc = np.concatenate([qrg, np.maximum(1 - qrg.sum(1, keepdims=True), 0)], 1)
            qn = ql[:, None] * qc / np.maximum(qc @ np.array([0.2126, 0.7152, 0.0722]), 1e-30)[:, None]
            out.append((f"log luminance {b} bits, chroma 2 x {cb}", np.where(lum[:, None] > 0, qn, 0.0)))
    mx = np.maximum(rgb.max(1), 1e-30)
    e = np.ceil(np.log2(mx))
    for b in (6, 8):
        sc = 2.0 ** (e - b)
        out.append((f"shared exponent, 3 x {b} bits", np.round(rgb / sc[:, None]) * sc[:, None]))
    return out


if __name__ == "__main__":
    main()
