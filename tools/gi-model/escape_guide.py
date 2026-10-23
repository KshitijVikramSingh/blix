# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///

2026-10-23 (7 foot texels captured at both seeds, sky part's noise at 128 rays): cosine 1.03; today's binned guide ~0.29.
The patches' centres mostly miss the slit (patch 8396: 0 of 1,024 escaped; 8433: 13) -- own patch's list for 43% of
the texels, 0.24 there at 10 deg; the neighbours' within 0.5 m for all, but from the texel 5-6% of those directions still
pass the slit: 0.36-0.45. The texel's own sky finds in its first 128 rays (79% found one), 5 deg: 47% of guided rays find
the sky, 0.15 -- for 128 rays more, where today's guide at 256 rays would be ~0.20. A parallax problem: a direction list
is only good from where it was cast.
"""A guide from the patch's escaped directions, for the sky through a slit (uv run tools/gi-model/escape_guide.py [HS_DIR]).

At the crevices corridor's end wall's foot near z 0.8 the sky through the doorway is 46-49% of the light; the gather's
binned guide leans ~6x toward it, yet a guided ray finds it 0.7-1.5% of the time -- the slit past the corner block is a
sliver of a bin (tools/gi-model/guide_check.py, 2026-10-23). The cook already traces each patch's coupling rays and knows
which escaped. Guide here: one of the patch's escaped directions (1,024 cosine rays from its centre, 2 cm off it, as the
couplings), jittered uniformly in a cone of half-angle A. Per foot texel, 128 rays -- 64 cosine, 64 from that guide,
balance heuristic -- each cast from the texel itself against the scene's triangles, an escape taking the sky by direction
(binned from the capture's misses, tools/gi-model/sky_control_variate.py). Over 300 trials: the sky part's relative
noise and its bias against a 20,000-ray truth, the chance a guided ray finds the sky, and the patch's escaped directions'
count (the list's size) -- against cosine alone (128 rays) and the capture's measured noise of today's guide at the foot
(0.131 at 128 rays, tools/gi-model/rare_light_flag.py).
"""
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
import sky_control_variate as scv  # noqa: E402

HS = Path(sys.argv[1] if len(sys.argv) > 1 else '/private/tmp/blix-dbg/hs')
scv.HS = HS
RAYS, TRIALS = 128, 300


def frame(n):
    a = np.array([1.0, 0, 0]) if abs(n[0]) < 0.9 else np.array([0, 1.0, 0])
    t1 = np.cross(n, a)
    t1 /= np.linalg.norm(t1)
    return t1, np.cross(n, t1)


def cosine_dirs(rng, n, k):
    t1, t2 = frame(n)
    u1, u2 = rng.random(k), rng.random(k)
    r, phi = np.sqrt(u1), 2 * np.pi * u2
    return t1 * (r * np.cos(phi))[:, None] + t2 * (r * np.sin(phi))[:, None] + n * np.sqrt(1 - u1)[:, None]


def cone_dirs(rng, axes, cos_a):
    k = len(axes)
    z = 1 - rng.random(k) * (1 - cos_a)
    phi = 2 * np.pi * rng.random(k)
    s = np.sqrt(np.maximum(1 - z * z, 0))
    out = np.empty((k, 3))
    for i, ax in enumerate(axes):
        t1, t2 = frame(ax)
        out[i] = t1 * s[i] * np.cos(phi[i]) + t2 * s[i] * np.sin(phi[i]) + ax * z[i]
    return out


def guide_pdf(dirs, esc, cos_a):
    inside = (dirs @ esc.T) >= cos_a
    return inside.mean(1) / (2 * np.pi * (1 - cos_a))


def main():
    d0 = scv.dump('cd0')
    n = json.loads((d0 / 'meta.json').read_text())['texelCount']
    t = np.frombuffer((d0 / 'texels.f32x8').read_bytes(), np.float32).reshape(-1, 8)[:n]
    P, N = t[:, :3].astype(float), t[:, 4:7].astype(float)
    N /= np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-9)
    patch_of = np.frombuffer((d0 / 'patch.i32').read_bytes(), np.int32)[:n]
    pp = np.frombuffer((d0 / 'patches.f32x12').read_bytes(), np.float32).reshape(-1, 12)
    C, CN = pp[:, :3].astype(float), pp[:, 4:7].astype(float)
    live = np.abs(t[:, 3]) >= 2
    foot = np.flatnonzero(live & (np.abs(P[:, 0] - 9.2) < 0.01) & (N[:, 0] < -0.9) & (P[:, 1] < 0.25) &
                          (np.abs(P[:, 2] - 0.8) < 0.15) & (patch_of >= 0))
    T = scv.triangles()
    rng = np.random.default_rng(7)
    # The sky by direction, from the capture's misses.
    caps = [scv.capture(tag, N) for tag in ('cd0', 'cd1')]
    s_sum, s_cnt = np.zeros(scv.RES ** 2), np.zeros(scv.RES ** 2)
    for k in caps:
        q = k['miss'] & (k['w'] > 0)
        b = scv.oct_bin(k['dirs'][q])
        np.add.at(s_sum, b, k['c'][q] / k['w'][q])
        np.add.at(s_cnt, b, 1)
    sky = np.where(s_cnt > 0, s_sum / np.maximum(s_cnt, 1), 0.0)
    caught = [np.isin(foot, k['ids']) for k in caps]
    both = foot[caught[0] & caught[1]]
    pick = both if len(both) else rng.choice(foot, min(8, len(foot)), replace=False)
    print(f"the end wall's foot: {len(foot)} live texels; modelled {len(pick)}, over patches {sorted(set(patch_of[pick].tolist()))}")
    near = lambda p: [q for q in range(len(C)) if np.linalg.norm(C[q] - C[p]) < 0.5 and CN[q] @ CN[p] > 0.9]
    esc_cache = {}

    def patch_escapes(p):
        if p not in esc_cache:
            d = cosine_dirs(rng, CN[p], 1024)
            esc_cache[p] = d[scv.escapes(T, C[p] + CN[p] * 0.02, d)]
        return esc_cache[p]

    for p in sorted(set(patch_of[pick].tolist())):
        ns = near(p)
        print(f'  patch {p} at {C[p].round(2).tolist()}: {len(patch_escapes(p))} of 1,024 coupling rays escaped; '
              f'with its {len(ns) - 1} neighbours within 0.5 m: {sum(len(patch_escapes(q)) for q in ns)}')
    # the texel's own sky finds in its first 128 rays, per seed (the capture)
    own = {}
    for k in caps:
        first = k['passid'] < 4
        for tx in pick:
            m = (k['ids'] == tx) & first & k['miss']
            own.setdefault(tx, []).append(k['dirs'][m])

    def estimate(o, nn, esc, cos_a, trials, rays):
        out, found = [], []
        for _ in range(trials):
            if esc is None:
                d = cosine_dirs(rng, nn, rays)
                wgt = np.full(rays, np.pi)
            else:
                dc = cosine_dirs(rng, nn, rays // 2)
                dg = cone_dirs(rng, esc[rng.integers(0, len(esc), rays // 2)], cos_a)
                d = np.vstack([dc, dg])
                cs = np.maximum(d @ nn, 0)
                mix = 0.5 * cs / np.pi + 0.5 * guide_pdf(d, esc, cos_a)
                wgt = np.where((cs > 0) & (mix > 0), cs / np.maximum(mix, 1e-12), 0)
            e = scv.escapes(T, o, d) & ((d @ nn) > 0)
            out.append(np.mean(wgt * e * sky[scv.oct_bin(d)]))
            if esc is not None:
                found.append(e[rays // 2:].mean())
        return np.array(out), (np.mean(found) if found else np.nan)

    results = {}
    cos5 = np.cos(np.radians(5))
    for tx in pick:
        o, nn = P[tx] + N[tx] * 0.01, N[tx]
        union = np.vstack([patch_escapes(q) for q in near(patch_of[tx])] + [np.zeros((0, 3))])
        # the truth: the neighbours' escape guide at 5 deg, 40 x 512 rays (unbiased whatever its guide)
        ref = union if len(union) else None
        tr, _ = estimate(o, nn, ref, cos5, 40, 512)
        truth = tr.mean()
        variants = [('cosine', None, None)]
        for deg in (2, 5, 10):
            variants.append((f'own patch {deg} deg', patch_escapes(patch_of[tx]), np.cos(np.radians(deg))))
            variants.append((f'neighbours {deg} deg', union, np.cos(np.radians(deg))))
        for seed_i, finds in enumerate(own[tx]):
            variants.append((f'own finds 5 deg', finds, cos5))
        for label, esc, cos_a in variants:
            if esc is not None and len(esc) == 0:
                results.setdefault(label, []).append((np.nan, np.nan, 0.0))
                continue
            est, found = estimate(o, nn, esc, cos_a, 150, RAYS)
            results.setdefault(label, []).append((est.std() / max(truth, 1e-12), est.mean() / max(truth, 1e-12) - 1, found))
    print(f"\nthe sky part at {RAYS} rays over {len(pick)} foot texels (median; a texel whose list is empty counts as cosine-only "
          f"there and is left out): noise over the sky part, bias, a guided ray finds the sky, texels with a list")
    print("  today's binned guide (the capture): ~0.29 of the sky part (0.131 of the texel's light, sky 45% of it)")
    for label, rows in results.items():
        r = np.array(rows, float)
        has = ~np.isnan(r[:, 0])
        if not has.any():
            print(f'  {label:20s} no texel has a list')
            continue
        print(f'  {label:20s} noise {np.median(r[has, 0]):.3f} (max {r[has, 0].max():.3f}), bias {np.median(r[has, 1]):+.3f}, '
              f'guided rays finding the sky {np.nanmedian(r[has, 2]):.1%}; texels with a list {has.mean():.0%}')


if __name__ == '__main__':
    main()
