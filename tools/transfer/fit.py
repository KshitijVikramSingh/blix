#!/usr/bin/env python3
"""The surface transfer atlas's fit (plan.md, stage 5): per atlas texel, how its 8 level-0 probes answer for it.

    tools/transfer/fit.py DIR/sun_*.bin                       (each sun held out in turn, judged; VulkanSponza
                                                               --transfer-dump DIR --transfer-suns "y,p;y,p;...")
    tools/transfer/fit.py --write weights.bin DIR/sun_*.bin   (fit on every sun; write them for --transfer-weights)
    --free                                                    (the free fit below in place of the scale)

What is fitted: ONE number a texel, a SCALE on today's blend of its probes (trilinear x facing^2 x visibility over the
valid probes, normalised: probe_clipmap.glsl), least squares over every sun's rgb, then SMOOTHED over its neighbours on
the same plane (3 cm cells, 3x3x3). The weights written are scale x today's weights. Why one number: across suns a
texel's 8 probes move almost together (12 Cornell suns pin ~1.6 of 8 directions above 10% of the largest), so the
rest of a free fit is wherever the solver lands -- and it fits the truth's noise, which printed the atlas grid as
blocks. Why smoothed: the truth is per texel and noisy (3.2% a texel at 1,024 paths); the correction it pins (contact,
corners, where bounce lands) is smooth on a plane. Against a 16k-path truth, held-out suns: 1.9-2.1% (the probe blend
7.8-9.0%), once the probes the dump read had their 255 solves (--transfer-probe-solves) -- before, run-to-run probe
noise (2.4% a texel) was the floor whatever the paths.

Visibility: a dump of 77 floats a row carries each probe's TRACED visibility from its texel, and it takes the place of
the Chebyshev estimate: a probe beyond the adjacent wall (a corner) weighs 0. Chebyshev let it in, and a scale cannot
cancel what its outdoor light does with the sun (a pale strip down Cornell's corner, a pink line along the red wall).

--free: non-negative weights over the probes in front of the surface and seen from it, fitted per texel. A probe
behind the surface (one of the cell's two layers sits 5 cm beyond a 0.2 m wall: outdoors) varies with the sun unlike
the room, and an unrestricted fit borrowed it (28% of the Cornell ceiling's weight; 12.9% error at a low sun).

Reported as the GI gate does: |error| over the mean light, the median ratio, energy.
"""
import struct
import sys

import numpy as np

# Level 0's spacing (VulkanSponza's clipmap), and the lift the dump picks a texel's cell from (0.25 spacing along the
# face normal: SponzaLoop.TransferFit.cs, as the incident pass does).
SPACING = 0.5
CORNERS = np.array([[k & 1, (k >> 1) & 1, (k >> 2) & 1] for k in range(8)], dtype=np.float64)
SMOOTH_CELL = 0.03   # metres


def load(path):
    """One sun's dump: per texel its atlas index, truth, 8 probes (valid, trilinear, facing^2, visibility, rgb), the
    probes allowed weight (in front, and seen where the dump traced it), position and normal."""
    with open(path, 'rb') as f:
        stride, count = struct.unpack('<ii', f.read(8))
        # A negative stride: the ids are atlas indices (the record less the hashed ones), as weights are keyed. A
        # positive one is an older dump, by record -- which shifts whenever the hashed texel count does.
        if stride > 0:
            raise SystemExit(f"{path}: an older dump, keyed by record rather than atlas index; capture it again.")
        stride = -stride
        assert stride in (66, 69, 77), stride
        a = np.frombuffer(f.read(), dtype='<f4', count=count * stride).reshape(count, stride)
    light = a[:, 66:69] if stride >= 69 else a[:, 7:10]
    probes = a[:, 10:66].reshape(count, 8, 7).copy()
    position, normal = a[:, 1:4].astype(np.float64), a[:, 4:7].astype(np.float64)
    base = np.floor((position + normal * 0.25 * SPACING) / SPACING - 0.5)
    probe = (base[:, None, :] + CORNERS[None] + 0.5) * SPACING
    allowed = np.einsum('nkc,nc->nk', probe - position[:, None, :], normal) >= 0.0
    if stride == 77:
        probes[:, :, 3] = a[:, 69:77]
        allowed &= a[:, 69:77] > 0.5
    return dict(ids=a[:, 0].astype(np.int64), truth=light, probes=probes, allowed=allowed, position=position, normal=normal)


def align(paths):
    """The suns' dumps over the texels all of them hold, in one order."""
    runs = [load(p) for p in paths]
    common = runs[0]['ids']
    for r in runs[1:]:
        common = np.intersect1d(common, r['ids'])
    out = []
    for r in runs:
        order = np.argsort(r['ids'])
        at = order[np.searchsorted(r['ids'][order], common)]
        out.append({k: (v[at] if isinstance(v, np.ndarray) else v) for k, v in r.items()})
    return common, out


def today_weights(run):
    p = run['probes']
    w = p[:, :, 0] * p[:, :, 1] * p[:, :, 2] * p[:, :, 3]
    return w / np.maximum(w.sum(1, keepdims=True), 1e-12)


def blend(weights, run):
    return np.einsum('nk,nkc->nc', weights, run['probes'][:, :, 4:7])


def smooth(values, position, normal):
    """Each texel's value averaged with its neighbours' on the same plane (normal to 0.1, offset to 1 cm), over the 3 x 3 x 3
    cells of SMOOTH_CELL around it."""
    nq = np.round(normal * 10).astype(np.int64)
    off = np.round((position * normal).sum(1) * 100).astype(np.int64)
    cell = np.floor(position / SMOOTH_CELL).astype(np.int64)

    def key(c):
        return ((c[:, 0] * 73856093) ^ (c[:, 1] * 19349663) ^ (c[:, 2] * 83492791)
                ^ (nq[:, 0] * 7 + nq[:, 1] * 131 + nq[:, 2] * 1031) ^ (off * 2654435761)) & ((1 << 62) - 1)

    keys = key(cell)
    order = np.argsort(keys)
    sorted_keys = keys[order]
    sums = np.concatenate([[0.0], np.cumsum(values[order])])
    total = np.zeros_like(values)
    count = np.zeros(len(values))
    for d in np.array(np.meshgrid([-1, 0, 1], [-1, 0, 1], [-1, 0, 1])).T.reshape(-1, 3):
        k = key(cell + d)
        lo, hi = np.searchsorted(sorted_keys, k, 'left'), np.searchsorted(sorted_keys, k, 'right')
        m = hi > lo
        total[m] += sums[hi[m]] - sums[lo[m]]
        count[m] += (hi - lo)[m]
    return total / np.maximum(count, 1)


def fit_scale(runs, smoothed=True):
    """Scale x today's weights (the weights averaged over the suns: they are geometry, bar a probe's validity)."""
    weights = np.mean([today_weights(r) for r in runs], axis=0)
    num = sum((blend(weights, r) * r['truth']).sum(1) for r in runs)
    den = sum((blend(weights, r) ** 2).sum(1) for r in runs)
    scale = num / np.maximum(den, 1e-12)
    if smoothed:
        scale = smooth(scale, runs[0]['position'], runs[0]['normal'])
    return scale[:, None] * weights


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


def fit_free(runs):
    A = np.concatenate([np.transpose(r['probes'][:, :, 4:7], (0, 2, 1)) for r in runs], axis=1)   # n x 3S x 8
    b = np.concatenate([r['truth'] for r in runs], axis=1)                                          # n x 3S
    return nnls_batch(A, b, runs[0]['allowed'])


def metrics(pred, truth):
    lum = lambda c: c @ np.array([0.2126, 0.7152, 0.0722])
    p, t = lum(pred), lum(truth)
    keep = t > 1e-6
    rel = np.abs(p - t) / t.mean()
    return f"|err|/mean {np.median(rel):.3f} (p90 {np.percentile(rel, 90):.3f})  median ratio {np.median(p[keep] / t[keep]):.3f}  energy {p.sum() / t.sum():.3f}"


def main(paths, free):
    common, runs = align(paths)
    print(f"{len(common):,} texels in all {len(paths)} dumps")
    for held in range(len(runs)):
        train = [r for i, r in enumerate(runs) if i != held]
        r = runs[held]
        print(f"held out {paths[held]}:")
        print(f"  today's blend   {metrics(blend(today_weights(r), r), r['truth'])}")
        print(f"  scale           {metrics(blend(fit_scale(train, smoothed=False), r), r['truth'])}")
        print(f"  scale, smoothed {metrics(blend(fit_scale(train), r), r['truth'])}")
        if free:
            print(f"  free weights    {metrics(blend(fit_free(train), r), r['truth'])}")


def write(out, paths, free):
    """Fit on every sun and write, per texel, its atlas index and 8 weights (float16) for --transfer-weights."""
    common, runs = align(paths)
    w = fit_free(runs) if free else fit_scale(runs)
    with open(out, 'wb') as f:
        f.write(b'BXTW')                              # --transfer-weights' file: version 2, keyed by atlas index
        f.write(struct.pack('<ii', 2, len(common)))
        f.write(common.astype('<u4').tobytes())
        f.write(w.astype('<f2').tobytes())
    print(f"wrote {len(common):,} texels' weights ({'free' if free else 'smoothed scale'}, fitted on {len(paths)} suns): mean sum {w.sum(1).mean():.3f} -> {out}")


if __name__ == '__main__':
    args = sys.argv[1:]
    free = '--free' in args
    args = [a for a in args if a != '--free']
    if args and args[0] == '--write':
        write(args[1], args[2:], free)
    else:
        main(args, free)
