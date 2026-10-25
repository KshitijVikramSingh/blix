# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy", "scipy"]
# ///
"""How fine the hierarchy must be near the sun's edges for a sun change's bounce (uv run tools/gi-model/sun_edge_levels.py
A B_SEED0 B_SEED1 [B_SEED0 B_SEED1 ...]; the inputs directional_ratio.py reads).

directional_ratio.py: the patches' change tracks the truth's but leaves ~0.3 in log a texel, and misses a third of the
texels whose light changed over 2x at a 5 degree step -- sun moving on geometry finer than a patch (25 cm, blended).
Here each texel keeps its own rays at sun A (the capture: direction, hit distance -- the hit exact) and each ray's
light is moved to sun B by its hit's outgoing sun at B over at A, read at a level of the hierarchy:
  S_X(j) = n.l_X+ x bit_X(j) + sunpart_X(j)   (a texel j's direct, by its sun bit, and its bounce: per unit irradiance)
the hit's texel j its nearest, and the level the mean of S over a group of texels holding j:
  texel        j alone (the bound: as fine as the texels)
  patch        j's patch (today's resolution)
  patch x sun  j's patch split by the sun's edge at each sun: its texels on j's side (bit_X equal) -- a patch split where
               the edge crosses it, for the sun at hand
  patch x 12 / 6 cm   j's patch cut into cells of the size, the edge or not
A texel's estimate: its gathered sun at A x (sum of its rays' moved light / sum of their light at A). Rays the move
cannot place (a miss -- no sun in the sun column --, a far answer, a hit with no texel near) keep the texel's scalar
ratio. Errors as directional_ratio.py's (judged on the second B seed; a fresh gather at B the floor). The bounce at a hit is
its patch's mean always (a texel's own part is its gather's noise); only the sun at the hit takes the level -- "texel blend
8" the gather's own (its 8 nearest texels' bits). "list of top K": the patch level's move as a per-texel list of the
patches its rays hit (their light at A summed by patch), the top K by light kept, the rest by the scalar ratio.

Crytek Sponza, 33 K captured texels, steps 5 / 20 / 60 deg (2026-10-25), mean error:
  floor (fresh gather)        0.108 / 0.099 / 0.104
  scalar ratio (today)        0.130 / 0.213 / 0.232
  rays moved at patch         0.124 / 0.183 / 0.167
  ... at texel / blend 8      0.145 / 0.187 / 0.182  ;  0.125 / 0.182 / 0.165
  ... patch x sun edge        0.145 / 0.187 / 0.182
  ... patch x 12 / 6 cm       0.127 / 0.183 / 0.167  ;  0.133 / 0.184 / 0.172
  list, top 8 / 32 patches    0.126 / 0.187 / 0.194  ;  0.127 / 0.190 / 0.183
The sun at a hit finer than a patch buys nothing: the patch level is as good as the texels' own bits. What the scalar
ratio misses is the texel's own transfer (which patches its rays reach): a list of its top 8 patches takes most of it at
20 deg, less at 60. The rest (~0.07 over the floor even at the texels' resolution) is the rays at A themselves -- drawn
by A's guides, a sample of A's light -- which only rays at B remove. A texel's rays hit 113 patches (p90 418); its top
8 / 16 / 32 hold 61 / 73 / 84% of its light."""
import sys
from pathlib import Path

import numpy as np
from scipy.spatial import cKDTree

sys.path.insert(0, str(Path(__file__).parent))
import directional_ratio as dr  # noqa: E402


def sun_bits(path, n):
    raw = Path(path).read_bytes()
    off = 4 + 16 * n
    words, column = np.frombuffer(raw[off:off + 8], np.int32)
    bits = np.frombuffer(raw[off + 8:off + 8 + 4 * n * words], np.uint32).reshape(n, words)
    return ((bits[:, column // 32] >> np.uint32(column % 32)) & 1).astype(np.float64)


def group_mean(key, value):
    u, inv = np.unique(key, return_inverse=True)
    return (np.bincount(inv, value) / np.bincount(inv))[inv]


def main():
    if len(sys.argv) < 4 or (len(sys.argv) - 2) % 2:
        raise SystemExit(__doc__)
    a = dr.run(sys.argv[1])
    irr = dr.irradiance(a['meta'])
    n = len(a['sky'])
    view = a['view']
    tex = np.frombuffer((view / 'texels.f32x8').read_bytes(), np.float32).reshape(-1, 8)[:n]
    pos = tex[:, :3].astype(np.float64)
    nrm = tex[:, 4:7].astype(np.float64)
    nl = np.linalg.norm(nrm, axis=1)
    valid = nl > 1e-6
    nrm = nrm / np.maximum(nl, 1e-9)[:, None]
    patch = np.frombuffer((view / 'patch.i32').read_bytes(), np.int32)[:n].astype(np.int64)
    rec = np.frombuffer((view / 'rays_captured.u32x8').read_bytes()[16:], np.uint32).reshape(-1, 8)
    rec = rec[rec[:, 0] < n]
    tid = rec[:, 0].astype(np.int64)
    dist = np.ascontiguousarray(rec[:, 2]).view(np.float32).astype(np.float64)
    how = rec[:, 3] & 7
    Y = np.ascontiguousarray(rec[:, 5]).view(np.float32).astype(np.float64)
    _, d = dr_oct(rec[:, 6])
    hit = (how >= 2) & (dist < 1e20) & np.isfinite(Y)
    hp = pos[tid[hit]] + d[hit] * dist[hit][:, None]
    tree = cKDTree(pos[valid])
    vidx = np.flatnonzero(valid)
    dd, k = tree.query(hp, k=1, distance_upper_bound=0.1, workers=-1)
    near = np.isfinite(dd)
    j = np.full(hit.sum(), -1, np.int64)
    j[near] = vidx[k[near]]
    print(f'{len(rec):,} captured rays; hits {hit.mean():.0%}, of them a texel within 10 cm {near.mean():.1%} (median {np.median(dd[near]) * 100:.1f} cm)')
    hits = np.flatnonzero(hit)[near]
    jh = j[near]
    u, inv = np.unique(tid, return_inverse=True)
    YA = np.bincount(inv, Y, minlength=len(u))
    GA = a['sun'][u]
    IA = a['oct'][u].sum(1)
    toA = np.array(a['meta']['sunToward'])
    bitA = sun_bits(Path(sys.argv[1]) / 'parts.instant.0', n)
    # The bounce at a hit: its patch's mean (a texel's own part is its gather's noise); the sun at the hit by the level.
    liveA = valid & (patch >= 0)
    partA = a['sun'].copy(); partA[liveA] = group_mean(patch[liveA], a['sun'][liveA])
    # The gather's own visibility at a hit: the blend of its 8 nearest texels' bits (texel_nodes_shade.glsl's hit sun).
    _, k8 = tree.query(hp[near], k=8, workers=-1)
    k8 = vidx[np.minimum(k8, len(vidx) - 1)]
    cell12 = np.floor(pos / 0.12).astype(np.int64)
    cell6 = np.floor(pos / 0.06).astype(np.int64)
    def key(level, bit):
        if level == 'texel': return np.arange(n)
        if level == 'patch': return patch
        if level == 'patch x sun': return patch * 2 + bit.astype(np.int64)
        c = cell12 if level == 'patch x 12 cm' else cell6
        return (patch * 1000003 + c[:, 0] * 73856093 + c[:, 1] * 19349663 + c[:, 2] * 83492791)
    levels = ('texel', 'texel blend 8', 'patch', 'patch x sun', 'patch x 12 cm', 'patch x 6 cm')
    # The levels' means over the texels a ray can hit only (the live: valid records); a texel outside every patch its own.
    live = valid & (patch >= 0)
    for pair in range(2, len(sys.argv), 2):
        b0, b1 = dr.run(sys.argv[pair]), dr.run(sys.argv[pair + 1])
        toB = np.array(b0['meta']['sunToward'])
        bitB = sun_bits(Path(sys.argv[pair]) / 'parts.instant.0', n)
        partB = b0['sun'].copy(); partB[live] = group_mean(patch[live], b0['sun'][live])
        truth = b1['sky'][u] + irr * b1['sun'][u]
        floor = 0.1 * np.median(truth[truth > 0])
        IB = b0['oct'][u].sum(1)
        R = np.where(IA > 1e-6, np.clip(IB / np.maximum(IA, 1e-30), 0, 16), 1.0)
        def report(name, sunEst, sky=None):
            sky = a['sky'][u] if sky is None else sky
            e = np.abs(sky + irr * sunEst - truth) / np.maximum(truth, floor)
            return f'  {name:28s} {e.mean():.4f} mean, {(e > 0.1).mean():5.1%} over 10%, {(e > 0.5).mean():5.2%} over 50%, energy {(sky + irr * sunEst).sum() / truth.sum():.3f}'
        ang = np.degrees(np.arccos(np.clip(toA @ toB, -1, 1)))
        print(f'== {Path(sys.argv[pair]).name} / {Path(sys.argv[pair + 1]).name} (step {ang:.0f} deg)')
        print(report('a fresh gather (the floor)', b0['sun'][u], b0['sky'][u]))
        print(report('scalar ratio (today)', R * GA))
        print(report('A kept', GA))
        for level in levels:
            vA, vB = bitA.copy(), bitB.copy()
            if level not in ('texel', 'texel blend 8'):
                kA, kB = key(level, bitA), key(level, bitB)
                vA[live] = group_mean(kA[live], bitA[live]); vB[live] = group_mean(kB[live], bitB[live])
            nlA, nlB = np.maximum(nrm[jh] @ toA, 0), np.maximum(nrm[jh] @ toB, 0)
            if level == 'texel blend 8': hvA, hvB = bitA[k8].mean(1), bitB[k8].mean(1)
            else: hvA, hvB = vA[jh], vB[jh]
            sa, sb = nlA * hvA + partA[jh], nlB * hvB + partB[jh]
            # Each ray: moved by its hit's light at B over at A; the rest by the texel's scalar ratio.
            YB = Y * R[inv]
            okr = sa > 1e-6
            YB[hits[okr]] = Y[hits[okr]] * np.clip(sb[okr] / sa[okr], 0, 64)
            est = GA * np.bincount(inv, YB, minlength=len(u)) / np.maximum(YA, 1e-30)
            est = np.where(YA > 1e-12, est, R * GA)
            print(report(f'rays moved at {level}', est))
            if level != 'patch': continue
            # The transfer as a list a texel: its rays' light at A summed by hit patch, the top K kept (the rest by the
            # scalar ratio) -- what a sun change would read instead of tracing.
            hp_patch = np.maximum(patch[jh], -1)   # ids under -1 (none of several kinds) as none
            kk = inv[hits] * (patch.max() + 2) + hp_patch + 1
            uk, kinv = np.unique(kk, return_inverse=True)
            ent_t = uk // (patch.max() + 2)
            ent_y = np.bincount(kinv, Y[hits])
            ent_m = np.bincount(kinv, (np.where(okr, np.clip(sb / np.maximum(sa, 1e-30), 0, 64), R[inv][hits])) * Y[hits])
            cnt = np.bincount(ent_t, minlength=len(u))
            if pair == 2:
                print(f'    patches a texel\'s rays hit: median {np.median(cnt[cnt > 0]):.0f}, p90 {np.percentile(cnt[cnt > 0], 90):.0f}; '
                      f'the light in its top 8 / 16 / 32: ' + ' / '.join(
                    f'{_topk_share(ent_t, ent_y, len(u), kq):.0%}' for kq in (8, 16, 32)))
            for kq in (4, 8, 16, 32, 10**9):
                order = np.lexsort((-ent_y, ent_t))
                rank = np.empty(len(order), np.int64)
                starts = np.r_[0, np.cumsum(np.bincount(ent_t[order], minlength=len(u)))[:-1]]
                rank[order] = np.arange(len(order)) - starts[ent_t[order]]
                keepk = rank < kq
                moved = np.bincount(ent_t[keepk], ent_m[keepk], minlength=len(u))
                kept_y = np.bincount(ent_t[keepk], ent_y[keepk], minlength=len(u))
                est = GA * (moved + (YA - kept_y) * R) / np.maximum(YA, 1e-30)
                est = np.where(YA > 1e-12, est, R * GA)
                print(report(f'  list of top {kq} patches', est))


def _topk_share(ent_t, ent_y, nt, k):
    order = np.lexsort((-ent_y, ent_t))
    starts = np.r_[0, np.cumsum(np.bincount(ent_t[order], minlength=nt))[:-1]]
    rank = np.arange(len(order)) - starts[ent_t[order]]
    return ent_y[order][rank < k].sum() / ent_y.sum()


def dr_oct(word):
    xy = np.stack([(word & 0xFFFF).astype(np.uint16).view(np.int16), (word >> 16).astype(np.uint16).view(np.int16)], -1) / 32767.0
    z = 1.0 - np.abs(xy[:, 0]) - np.abs(xy[:, 1])
    x, y = xy[:, 0].copy(), xy[:, 1].copy()
    neg = z < 0
    x[neg], y[neg] = (1 - np.abs(xy[neg, 1])) * np.sign(xy[neg, 0]), (1 - np.abs(xy[neg, 0])) * np.sign(xy[neg, 1])
    v = np.stack([x, y, z], -1)
    return xy, v / np.maximum(np.linalg.norm(v, axis=1, keepdims=True), 1e-12)


if __name__ == '__main__':
    main()
