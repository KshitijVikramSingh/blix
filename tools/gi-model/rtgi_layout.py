# The RTGI cache's slot layout modelled on a frame's real training: what the training touches -- each point's cells on
# its five levels (thinned as the shader thins them) and its hit's cell -- as cache lines and 16 KB pages of the keys
# (4 B a slot), the accumulators (64 B) and the readout record (32 B), under today's layout (each cell hashed to its own
# slot) and under bricks (a block of cells hashed together, the cell's place in it its offset: neighbours in neighbouring
# slots). Per frame and per subgroup of 32 points (the training's unit of memory traffic), and the bricks' fill (the share
# of their slots a held cell uses: their memory cost).
# Dump: BLIX_RTGI_BENCH_DUMP=DIR blix run Blix.Demos.VulkanSponza ... --rtgi-bench 1 (SponzaLoop.RtgiBench.cs).
# Run: uv run --with numpy python tools/gi-model/rtgi_layout.py DIR
import json, sys
from pathlib import Path
import numpy as np

d = Path(sys.argv[1])
m = json.loads((d / 'meta.json').read_text())
n = m['points']
P = np.frombuffer((d / 'points.bin').read_bytes(), np.float32).reshape(n, 2, 4)
R = np.frombuffer((d / 'rays.bin').read_bytes(), np.float32).reshape(n, 2, 4)
H = np.frombuffer((d / 'hits.bin').read_bytes(), np.uint32).reshape(n, 3, 4)
M32 = np.uint64(0xFFFFFFFF)


def h32(x):
    # rtgi_cache.glsl's rtgiHash, on uint64 holding uint32.
    x = x & M32
    x ^= x >> np.uint64(16); x = (x * np.uint64(0x7feb352d)) & M32
    x ^= x >> np.uint64(15); x = (x * np.uint64(0x846ca68b)) & M32
    x ^= x >> np.uint64(16)
    return x


def u(v):
    return np.asarray(v).astype(np.int64).astype(np.uint64) & M32


def cell_hash(c, level, side):
    # rtgiCellHash's x: the slot it starts probing at (before the mask).
    a = h32(u(c[:, 0]) * np.uint64(0x9E3779B1) ^ h32(u(c[:, 1]) * np.uint64(0x85EBCA77) ^ h32(u(c[:, 2]) * np.uint64(0xC2B2AE3D) ^ (u(level) * np.uint64(6) + u(side)))))
    return a


def side_of(nv):
    a = np.abs(nv)
    s = np.where((a[:, 0] >= a[:, 1]) & (a[:, 0] >= a[:, 2]), np.where(nv[:, 0] >= 0, 0, 1),
                 np.where(a[:, 1] >= a[:, 2], np.where(nv[:, 1] >= 0, 2, 3), np.where(nv[:, 2] >= 0, 4, 5)))
    return s.astype(np.int64)


cam = np.array(m['camera'], np.float64)


def level_of(p):
    cell = m['cell'] + m['grow'] * np.maximum(np.linalg.norm(p - cam, axis=1) - m['near'], 0.0)
    return np.clip(np.floor(np.log2(cell / m['cell'])), 0, m['top']).astype(np.int64)


# The training's touches, in dispatch order: (point index, cell coordinates, level, side, kind 0 own levels / 1 hit).
p = P[:, 0, :3].astype(np.float64)
nrm = P[:, 1, :3].astype(np.float64)
live = P[:, 0, 3] >= 0.5
idx = np.arange(n, dtype=np.uint64)
hd = h32(idx * np.uint64(0x9E3779B9) ^ P[:, 0, 0].view(np.uint32).astype(np.uint64) ^ R[:, 1, 1].view(np.uint32).astype(np.uint64))
thin = h32(hd ^ np.uint64(0x9E3779B9))
lvl = level_of(p)
sd = side_of(nrm)
rows = []
for k in range(m['levels']):
    L = lvl + k
    keep = live & (L <= m['top'])
    if k >= 3:
        keep &= ((thin >> np.uint64(4 * k)) & np.uint64((1 << (k - 2)) - 1)) == 0
    i = np.nonzero(keep)[0]
    size = m['cell'] * np.exp2(L[i])
    c = np.floor(p[i] / size[:, None]).astype(np.int64)
    rows.append((i, c, L[i], sd[i], np.zeros(len(i), np.int64)))
# The hit's own cell: front-face hits (flags 3), q = origin + direction * t, its normal facing the ray.
flags = H[:, 0, 3]
hit = live & ((flags & 3) == 3)
i = np.nonzero(hit)[0]
t = H[i, 0, 0].view(np.float32).astype(np.float64)
q = R[i, 0, :3].astype(np.float64) + R[i, 1, :3].astype(np.float64) * t[:, None]
nq = H[i, 2, :3].view(np.float32).astype(np.float64)
dvec = R[i, 1, :3].astype(np.float64)
flip = np.sum(nq * dvec, axis=1) > 0
nq[flip] *= -1
ok = np.isfinite(q).all(axis=1) & (np.linalg.norm(nq, axis=1) > 1e-6)
i, q, nq = i[ok], q[ok], nq[ok]
Lq = level_of(q)
cq = np.floor(q / (m['cell'] * np.exp2(Lq))[:, None]).astype(np.int64)
rows.append((i, cq, Lq, side_of(nq), np.ones(len(i), np.int64)))
pt = np.concatenate([r[0] for r in rows])
C = np.concatenate([r[1] for r in rows])
LV = np.concatenate([r[2] for r in rows])
SD = np.concatenate([r[3] for r in rows])
KIND = np.concatenate([r[4] for r in rows])
mask = np.uint64(m['slots'] - 1)
print(f"{d.name}: {live.sum():,} live points, {hit.sum():,} front-face hits; {len(pt):,} cell touches ({(KIND == 0).sum():,} own levels, {(KIND == 1).sum():,} hits)")


def layout_today():
    return (cell_hash(C, LV, SD) & mask).astype(np.int64), None


def layout_tangent(b):
    # b x b cells in the side's plane (the two axes other than its normal's), one along it.
    axis = SD // 2
    a0 = (axis + 1) % 3
    a1 = (axis + 2) % 3
    r = np.arange(len(C))
    u0, u1, w = C[r, a0], C[r, a1], C[r, axis]
    sh = int(np.log2(b))
    bc = np.stack([u0 >> sh, u1 >> sh, w], axis=1)
    base = (cell_hash(bc, LV, SD) & mask) & ~np.uint64(b * b - 1)
    off = (u0 & (b - 1)) + b * (u1 & (b - 1))
    return (base + off.astype(np.uint64)).astype(np.int64), (bc, b * b)


def layout_cube(b):
    sh = int(np.log2(b))
    bc = C >> sh
    base = (cell_hash(bc, LV, SD) & mask) & ~np.uint64(b ** 3 - 1)
    off = (C[:, 0] & (b - 1)) + b * (C[:, 1] & (b - 1)) + b * b * (C[:, 2] & (b - 1))
    return (base + off.astype(np.uint64)).astype(np.int64), (bc, b ** 3)


def distinct_per_group(group, key):
    # Mean distinct keys per group (consecutive points of the dispatch).
    o = np.lexsort((key, group))
    g, k = group[o], key[o]
    new = np.ones(len(g), bool)
    new[1:] = (g[1:] != g[:-1]) | (k[1:] != k[:-1])
    return np.bincount(g[new]).mean() if new.any() else 0.0


sub = (pt // 32).astype(np.int64)
arrays = (('keys', 4), ('accum', 64), ('record', 32))
print(f"{'layout':>14} {'fill':>5} | frame: cells  " + "  ".join(f"{a} lines/pages" for a, _ in arrays) + " | a subgroup's: " + "  ".join(f"{a} lines/pages" for a, _ in arrays))
for name, f in (('today', layout_today), ('tangent 4x4', lambda: layout_tangent(4)), ('tangent 8x8', lambda: layout_tangent(8)), ('cube 2x2x2', lambda: layout_cube(2))):
    slot, bricks = f()
    cells = np.unique(np.stack([C[:, 0], C[:, 1], C[:, 2], LV, SD], 1), axis=0).shape[0]
    fill = ''
    if bricks is not None:
        bc, per = bricks
        nb = np.unique(np.stack([bc[:, 0], bc[:, 1], bc[:, 2], LV, SD], 1), axis=0).shape[0]
        fill = f"{cells / (nb * per):.0%}"
    frame, group = [], []
    for a, bytes_ in arrays:
        line = slot * bytes_ // 64
        page = slot * bytes_ // 16384
        frame.append(f"{len(np.unique(line)):>9,}/{len(np.unique(page)):>7,}")
        group.append(f"{distinct_per_group(sub, line):>6.1f}/{distinct_per_group(sub, page):>5.1f}")
    print(f"{name:>14} {fill:>5} | {cells:>11,}  " + "  ".join(frame) + " | " + "  ".join(group))
