# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""Light-free lists under a light change, and what they take to store (uv run tools/gi-model/light_lists_change.py
DUMP_A DUMP_B [DUMP_GROWTH]).

light_lists.py judged a list at the sun it was made under. A list is cooked once and lit by every sun after; what it
dropped (its tail) is judged here under ANOTHER sun. DUMP_A and DUMP_B are view dumps of the same scene, camera and
texels under two suns, each with --ray-capture B --ray-capture-sun --sun-nodes-no-guide (cosine rays alone: the guides
aim by the light, and the rays must be the same under both). Only the sweep's rays are compared: the refine's and the
fine round's texels are picked by the light. A ray is matched by its texel, its pass and its place in the pass.

The list is cut under sun A (its targets by the bounce light each carried at A, kept to COVER of it) and lit at sun B:
  no remainder  the dropped targets' light lost
  scaled        the tail's light at A, scaled by how the kept part's changed from A to B (one number a texel)
The sun at the hit at B: the hit's texels' bits or its cell, against B's shadow ray per hit (as light_lists.py).
Then the list's format: entries a texel, target ids delta-coded within a texel's sorted list (the bits each takes), and
weights at 8, 12 and 16 bits (the error their rounding leaves). With DUMP_GROWTH (A at more rays a texel): how the
entries grow with the rays a list is cooked from."""
import json
import sys
from pathlib import Path

import numpy as np

COVERS = (0.9, 0.95, 0.99)
NEAR = 0.25


def load(dump):
    d = sorted(Path(dump).glob("view-*"))[-1]
    m = json.loads((d / "meta.json").read_text())
    n = m["texelCount"]
    raw = (d / "rays_captured.u32x8").read_bytes()
    rec = np.frombuffer(raw[16:], np.uint32).reshape(-1, 8)
    tag = rec[:, 1] & 0x7FFF
    if not (rec[:, 1] & 0x8000).any():
        raise SystemExit(f"{dump}: no --ray-capture-sun")
    sweep = (tag & 15) == 1
    rec = rec[sweep]
    f = dict(
        tid=rec[:, 0].astype(np.int64), pas=(rec[:, 1] >> 16).astype(np.int64), r=((rec[:, 3] >> 8) & 31).astype(np.int64),
        dist=rec[:, 2].copy().view(np.float32), kind=rec[:, 3] & 7, patch=(rec[:, 3] >> 13).astype(np.int64) - 1,
        whole=rec[:, 4].copy().view(np.float32).astype(np.float64), bounce=rec[:, 5].copy().view(np.float32).astype(np.float64),
        ntex=np.where(rec[:, 7] < n, rec[:, 7].astype(np.int64), -1))
    hv = rec[:, 6].copy().view(np.float16).reshape(-1, 2).astype(np.float64)
    f["full"], f["bits"] = hv[:, 0], hv[:, 1]
    f["hit"] = (f["kind"] >= 2) & (f["dist"] < 1e20)
    f["sun"] = np.where(f["hit"], f["whole"] - f["bounce"], 0.0)
    f["key"] = (f["tid"] << 24) | (f["pas"] << 5) | f["r"]
    f["target"] = np.where(f["hit"] & (f["dist"] < NEAR) & (f["ntex"] >= 0), f["ntex"] * 2 + 1, np.where(f["hit"] & (f["patch"] >= 0), f["patch"] * 2, -1))
    return d.parent.name, m, f


def matched(a, b):
    ia = np.argsort(a["key"]); ib = np.argsort(b["key"])
    ka, kb = a["key"][ia], b["key"][ib]
    common, pa, pb = np.intersect1d(ka, kb, return_indices=True)
    A = {k: v[ia][pa] for k, v in a.items()}
    B = {k: v[ib][pb] for k, v in b.items()}
    same = (A["target"] == B["target"]) & (np.abs(A["dist"] - B["dist"]) <= 1e-4 * np.maximum(A["dist"], 1))
    return A, B, same


def dev(pred, truth):
    ok = truth > 1e-9
    r = pred[ok] / truth[ok] - 1
    return f"bias {100 * (pred[ok].sum() / truth[ok].sum() - 1):+.2f}%, |error| median {100 * np.median(np.abs(r)):.2f}% p90 {100 * np.percentile(np.abs(r), 90):.2f}%"


def lists(tid, target, light):
    """Per texel, its targets sorted by light (descending): (texel index of each entry, the entries' light, ids)."""
    ok = target >= 0
    u, inv = np.unique(tid, return_inverse=True)
    key = inv[ok].astype(np.int64) * (1 << 40) + target[ok]
    uk, kinv = np.unique(key, return_inverse=True)
    lk = np.bincount(kinv, light[ok], minlength=len(uk))
    owner = (uk >> 40).astype(np.int64)
    ids = (uk & ((1 << 40) - 1)).astype(np.int64)
    order = np.lexsort((-lk, owner))
    return u, inv, owner[order], lk[order], ids[order], kinv, ok, uk[order]


def main():
    na, ma, a = load(sys.argv[1])
    nb, mb, b = load(sys.argv[2])
    A, B, same = matched(a, b)
    print(f"{na} (sun {np.round(ma['sunToward'], 3).tolist()}) against {nb} (sun {np.round(mb['sunToward'], 3).tolist()}): "
          f"{len(A['key']):,} sweep rays matched, the same hit {same.mean():.4%}")
    u, inv = np.unique(A["tid"], return_inverse=True)
    rays = np.bincount(inv).astype(np.float64)
    truth = np.bincount(inv, B["whole"]) / rays
    nonb = np.bincount(inv, (B["whole"] - B["bounce"]) * 1.0) / rays          # B's sky and sun parts, exact
    print(f"  {len(u):,} texels, {rays.mean():.0f} sweep rays a texel; the sun changed each texel's light by a median "
          f"{100 * np.median(np.abs(truth / np.maximum(np.bincount(inv, A['whole']) / rays, 1e-12) - 1)):.0f}%")
    # Cut under A, lit at B.
    tgt = A["target"]
    ok = tgt >= 0
    key = inv[ok].astype(np.int64) * (1 << 40) + tgt[ok]
    uk, kinv = np.unique(key, return_inverse=True)
    la = np.bincount(kinv, A["bounce"][ok], minlength=len(uk))
    lb = np.bincount(kinv, B["bounce"][ok], minlength=len(uk))
    owner = (uk >> 40).astype(np.int64)
    order = np.lexsort((-la, owner))
    own_o, la_o, lb_o = owner[order], la[order], lb[order]
    starts = np.r_[0, np.flatnonzero(np.diff(own_o)) + 1]
    ends = np.r_[starts[1:], len(own_o)]
    for c in COVERS:
        keptB = np.zeros(len(u)); keptA = np.zeros(len(u)); tailA = np.zeros(len(u)); ent = np.zeros(len(u))
        for s, e in zip(starts, ends):
            t = own_o[s]
            cs = np.cumsum(la_o[s:e]); tot = cs[-1]
            k = int(np.searchsorted(cs, c * tot)) + 1 if tot > 0 else e - s
            keptA[t] = cs[k - 1]; tailA[t] = tot - cs[k - 1]; keptB[t] = lb_o[s:s + k].sum(); ent[t] = k
        none = nonb + keptB / rays
        scaled = nonb + (keptB + tailA * np.where(keptA > 0, keptB / np.maximum(keptA, 1e-12), 1.0)) / rays
        print(f"  cut at {100 * c:.0f}% under A ({ent.mean():.0f} entries a texel), lit at B -- no remainder: {dev(none, truth)}; scaled remainder: {dev(scaled, truth)}")
    # The sun at the hit at B, light-free.
    alt = np.bincount(inv, np.where(B["hit"], B["full"] * B["bits"], 0.0)) / rays
    print(f"  the sun at the hit at B by the hit's texels' bits / its cell: {dev(truth - np.bincount(inv, B['sun']) / rays + alt, truth)}")
    # Format: ids delta-coded within each texel's sorted list; weights quantised.
    ids = (uk & ((1 << 40) - 1)).astype(np.int64)
    srt = np.lexsort((ids, owner))
    own_s, ids_s = owner[srt], ids[srt]
    first = np.r_[True, np.diff(own_s) != 0]
    delta = np.where(first, ids_s, np.diff(np.r_[0, ids_s]))
    bitsneed = np.ceil(np.log2(np.maximum(delta, 1) + 1))
    print(f"  ids: {len(ids_s) / len(u):.0f} entries a texel; a delta-coded id {bitsneed[~first].mean():.1f} bits on average "
          f"(p90 {np.percentile(bitsneed[~first], 90):.0f}), a list's first {bitsneed[first].mean():.1f}")
    w = la / np.maximum(np.bincount(owner, la, minlength=len(u))[owner], 1e-12)   # each entry's share of its texel's bounce
    for qb in (8, 12, 16):
        # Quantised on a log scale (shares span decades): relative rounding error of each, and of the texel's sum at B.
        lw = np.log2(np.maximum(w, 2.0 ** -24))
        step = 24.0 / (2 ** qb)
        q = 2.0 ** (np.round(lw / step) * step)
        sumB = np.bincount(owner, lb, minlength=len(u))
        sumQ = np.bincount(owner, lb * q / np.maximum(w, 1e-30), minlength=len(u))
        okb = sumB > 1e-12
        rr = np.abs(sumQ[okb] / sumB[okb] - 1)
        print(f"  weights at {qb} bits (log): the texel's bounce at B off by median {100 * np.median(rr):.3f}%, p90 {100 * np.percentile(rr, 90):.3f}%")
    if len(sys.argv) > 3:
        ng, mg, g = load(sys.argv[3])
        ug, ig = np.unique(g["tid"], return_inverse=True)
        okg = g["target"] >= 0
        ent_g = np.bincount(ig[okg], minlength=len(ug)) * 0
        keyg = np.unique(ig[okg].astype(np.int64) * (1 << 40) + g["target"][okg])
        ent_g = np.bincount((keyg >> 40).astype(np.int64), minlength=len(ug))
        keya = np.unique(inv[ok].astype(np.int64) * (1 << 40) + tgt[ok])
        ent_a = np.bincount((keya >> 40).astype(np.int64), minlength=len(u))
        rg = np.bincount(ig).mean()
        print(f"  growth: {rays.mean():.0f} sweep rays -> {ent_a.mean():.0f} distinct targets a texel; {rg:.0f} rays ({ng}) -> {ent_g.mean():.0f}")


if __name__ == "__main__":
    main()
