#!/usr/bin/env python3
"""Compare two RTGI cooks dumped with BLIX_RTGI_COOK_DUMP=dir (SponzaLoop.RtgiCook.cs's CookDumpPath).

    BLIX_RTGI_COOK_DUMP=/tmp/cpu blix run Blix.Demos.VulkanSponza --scene=crevices --rtgi-cook-cpu
    BLIX_RTGI_COOK_DUMP=/tmp/gpu blix run Blix.Demos.VulkanSponza --scene=crevices
    tools/testbeds/cookcmp.py /tmp/cpu /tmp/gpu

Cells: keys by level, those in both whose points (|normal sum|) or sub-squares differ. Surfels: keys, centroid moves,
area ratios. Pieces: the cubes of several pieces by level, and in both. The CPU's walk is the reference for the GPU's
(rtgi_cookwalk.comp); on the crevices testbed the two agreed to 0.3% of cells and 0.1% of surfels (2026-11-01).
"""
import math
import os
import struct
import sys
from collections import Counter


def records(path, fmt):
    size = struct.calcsize(fmt)
    data = open(path, 'rb').read()
    return [struct.unpack_from(fmt, data, o) for o in range(0, len(data), size)]


def cells(d):
    return {(l, k): (n, a, m) for l, k, n, a, m in records(os.path.join(d, 'cells.bin'), '<iqffi')}


def surfels(d):
    return {r[0]: (r[1:4], r[4:7], r[7]) for r in records(os.path.join(d, 'surfels.bin'), '<q7f')}


def multi(d):
    return {(l, k): n for l, k, n in records(os.path.join(d, 'multi.bin'), '<iqi')}


def main(a, b):
    ca, cb = cells(a), cells(b)
    both = ca.keys() & cb.keys()
    print(f'cells: {len(ca):,} / {len(cb):,}, in both {len(both):,}; only first by level {dict(sorted(Counter(k[0] for k in ca.keys() - cb.keys()).items()))}, '
          f'only second {dict(sorted(Counter(k[0] for k in cb.keys() - ca.keys()).items()))}')
    print(f'  in both: points differ {sum(1 for k in both if abs(ca[k][0] - cb[k][0]) > 0.5):,}, sub-squares differ {sum(1 for k in both if ca[k][2] != cb[k][2]):,}')
    sa, sb = surfels(a), surfels(b)
    both = sa.keys() & sb.keys()
    moved = sorted(math.dist(sa[k][0], sb[k][0]) for k in both)
    ratios = [sb[k][2] / max(sa[k][2], 1e-12) for k in both]
    print(f'surfels: {len(sa):,} / {len(sb):,}, in both {len(both):,}; centroid moved p99 {moved[int(len(moved) * .99)] if moved else 0:.2e} m, '
          f'area off by >0.1% {sum(1 for r in ratios if abs(r - 1) > 1e-3):,}')
    ma, mb = multi(a), multi(b)
    levels = max([k[0] for k in ma] + [k[0] for k in mb] + [0]) + 1
    print('cubes of several pieces by level: ' + ' '.join(f'{sum(1 for k in ma if k[0] == l)}/{sum(1 for k in mb if k[0] == l)}' for l in range(levels))
          + f'; in both {len(ma.keys() & mb.keys()):,}')


if __name__ == '__main__':
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    main(sys.argv[1], sys.argv[2])
