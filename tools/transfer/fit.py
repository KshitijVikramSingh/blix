#!/usr/bin/env python3
"""The surface transfer atlas's first experiment (plan.md, stage 5): fit each texel's weights over its 8 level-0 probes.

    tools/transfer/fit.py sunA.bin sunB.bin sunC.bin ...    (VulkanSponza --transfer-dump, one per --sun, same --cam)
    tools/transfer/fit.py --write weights.bin sunA.bin ...  (fit on every sun; write them for --transfer-weights)

Per texel, truth is its path-traced irradiance (or, in an older dump, its gathered light); a probe's value is its irradiance at the texel's normal. For each sun held
out in turn, non-negative weights are fitted on the others (each sun's rgb: three equations) and judged on the one left
out, against today's blend (trilinear x facing^2 x Chebyshev^3 over the valid probes, normalised: probe_clipmap.glsl).
Reported as the GI gate does: |error| over the mean light, the median ratio, energy. 'fit (seen)' is the same weights
on the suns they were fitted on: how far a blend of probes can reach at all.

Only the probes in FRONT of a texel's surface may carry weight. A probe behind it (one of the cell's two layers sits
5 cm beyond a 0.2 m wall: outdoors) varies with the sun unlike the room, and a free fit borrowed it: 28% of the
Cornell ceiling's weight, which then read 0.71 of the truth at a low sun (12.9% error held out, against 5.5% with the
probes behind left out; every other surface the same or better).
"""
import struct
import sys

import numpy as np

# Level 0's spacing (VulkanSponza's clipmap, both scenes), and the lift the dump picks a texel's cell from (0.25 spacing
# along the face normal: SponzaLoop.TransferFit.cs, as the incident pass does).
SPACING = 0.5
CORNERS = np.array([[k & 1, (k >> 1) & 1, (k >> 2) & 1] for k in range(8)], dtype=np.float64)

def load(path):
    """Per texel: its id, its truth, its 8 probes, and which of them stand in front of its surface. A 69-float row
    carries the path-traced truth (--transfer-dump's ReferenceWave) after the probes, and that is the truth; a 66-float
    row only the gathered light."""
    with open(path, 'rb') as f:
        stride, count = struct.unpack('<ii', f.read(8))
        assert stride in (66, 69), stride
        a = np.frombuffer(f.read(), dtype='<f4', count=count * stride).reshape(count, stride)
    ids = a[:, 0].astype(np.int64)
    light = a[:, 66:69] if stride == 69 else a[:, 7:10]
    probes = a[:, 10:66].reshape(count, 8, 7)
    position, normal = a[:, 1:4].astype(np.float64), a[:, 4:7].astype(np.float64)
    base = np.floor((position + normal * 0.25 * SPACING) / SPACING - 0.5)
    probe = (base[:, None, :] + CORNERS[None] + 0.5) * SPACING
    front = np.einsum('nkc,nc->nk', probe - position[:, None, :], normal) >= 0.0
    return ids, light, probes, front


def nnls_batch(A, b, allowed, iterations=600, ridge=1e-4):
    """min |A x - b|^2 + ridge |x|^2, x >= 0, x = 0 where not allowed, for every texel at once (A: n x m x k, b: n x m,
    allowed: n x k)."""
    n, m, k = A.shape
    A = A * allowed[:, None, :]
    AtA = np.einsum('nmi,nmj->nij', A, A) + ridge * np.eye(k)[None]
    Atb = np.einsum('nmi,nm->ni', A, b)
    step = 1.0 / np.maximum(np.linalg.eigvalsh(AtA)[:, -1], 1e-12)
    x = np.zeros((n, k))
    for _ in range(iterations):
        grad = np.einsum('nij,nj->ni', AtA, x) - Atb
        x = np.maximum(x - step[:, None] * grad, 0.0) * allowed
    return x


def metrics(pred, truth):
    lum = lambda c: c @ np.array([0.2126, 0.7152, 0.0722])
    p, t = lum(pred), lum(truth)
    keep = t > 1e-6
    rel = np.abs(p - t) / t.mean()
    return f"|err|/mean {np.median(rel):.3f} (p90 {np.percentile(rel, 90):.3f})  median ratio {np.median(p[keep] / t[keep]):.3f}  energy {p.sum() / t.sum():.3f}"


def main(paths):
    runs = [load(p) for p in paths]
    common = runs[0][0]
    for ids, _, _, _ in runs[1:]:
        common = np.intersect1d(common, ids)
    aligned = []
    for ids, light, probes, front in runs:
        order = np.argsort(ids)
        pos = order[np.searchsorted(ids[order], common)]
        aligned.append((light[pos], probes[pos]))
        allowed = front[pos]
    n = len(common)
    print(f"{n:,} texels in all {len(paths)} runs")
    for held in range(len(paths)):
        train = [i for i in range(len(paths)) if i != held]
        valid = aligned[held][1][:, :, 0]
        A = np.concatenate([np.transpose(aligned[i][1][:, :, 4:7], (0, 2, 1)) for i in train], axis=1)   # n x 3S x 8
        b = np.concatenate([aligned[i][0] for i in train], axis=1)                                          # n x 3S
        w = nnls_batch(A, b, allowed)
        truth, probes = aligned[held]
        fitted = np.einsum('nk,nkc->nc', w, probes[:, :, 4:7])
        today_w = valid * probes[:, :, 1] * probes[:, :, 2] * probes[:, :, 3]
        today = np.einsum('nk,nkc->nc', today_w, probes[:, :, 4:7]) / np.maximum(today_w.sum(1, keepdims=True), 1e-12)
        seen = np.concatenate([np.einsum('nk,nkc->nc', w, aligned[i][1][:, :, 4:7]) for i in train])
        seen_truth = np.concatenate([aligned[i][0] for i in train])
        print(f"held out {paths[held]}:")
        print(f"  today's blend  {metrics(today, truth)}")
        print(f"  fit (unseen)   {metrics(fitted, truth)}")
        print(f"  fit (seen)     {metrics(seen, seen_truth)}")
        print(f"  weights: mean sum {w.sum(1).mean():.3f}, mean non-zero {(w > 1e-4).sum(1).mean():.2f} of 8")


def write(out, paths):
    """Fit on every sun and write, per texel, its record id and 8 weights (float16) for --transfer-weights."""
    runs = [load(p) for p in paths]
    common = runs[0][0]
    for ids, _, _, _ in runs[1:]:
        common = np.intersect1d(common, ids)
    A, b = [], []
    for ids, light, probes, front in runs:
        order = np.argsort(ids)
        pos = order[np.searchsorted(ids[order], common)]
        allowed = front[pos]
        A.append(np.transpose(probes[pos][:, :, 4:7], (0, 2, 1)))
        b.append(light[pos])
    w = nnls_batch(np.concatenate(A, axis=1), np.concatenate(b, axis=1), allowed)
    with open(out, 'wb') as f:
        f.write(struct.pack('<i', len(common)))
        f.write(common.astype('<u4').tobytes())
        f.write(w.astype('<f2').tobytes())
    print(f"wrote {len(common):,} texels' weights (fitted on {len(paths)} suns): mean sum {w.sum(1).mean():.3f}, non-zero {(w > 1e-4).sum(1).mean():.2f} of 8 -> {out}")


if __name__ == '__main__':
    if sys.argv[1] == '--write':
        write(sys.argv[2], sys.argv[3:])
    else:
        main(sys.argv[1:])
