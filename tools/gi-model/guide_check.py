# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""Do the gather's guides find the doorway's sky at the crevices corridor's turn? (uv run tools/gi-model/guide_check.py
[HS_DIR]).

The end wall's foot near z 0.8 takes 39% of its light from the courtyard's sky through the doorway, along lines that
barely clear the corner block; its sky part stays 9% noisy at 512 rays (tools/gi-model/rare_light_flag.py, 2026-10-23).
Half a texel's rays are cosine, half its patch's guide (texel_nodes_rays.comp, no lights here); a ray's weight is
cos / (0.5 cos / pi + 0.5 guidePdf), so each captured ray gives the guide's density in its direction:
guidePdf = 2 (cos / w - 0.5 cos / pi). Per texel: whether it has a guide (every weight pi: none); for its sky rays,
the guide's density over the cosine's (> 1: the guide leans toward the sky); the chance a cosine ray finds the sky,
exact by casting against the scene's triangles; and from the captured share of misses, the chance a guided one does.

2026-10-23 (cd0/cd1): every texel has a guide. At the end wall's foot (sky 46-49% of its light) the guide's density
on its sky rays is ~6x the cosine's (p90 6.8), yet a guided ray finds the sky 0.7-1.5% of the time (a cosine one
0.2-0.3%): it has the direction, not the slit -- the doorway, seen there past the corner block, is a sliver of one of its
bins. So the foot's sky rides on one or two of its 128 rays.
"""
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).parent))
from sky_control_variate import LUM, dump, escapes, oct_decode, triangles  # noqa: E402

HS = Path(sys.argv[1] if len(sys.argv) > 1 else '/private/tmp/blix-dbg/hs')


def main():
    import sky_control_variate
    sky_control_variate.HS = HS
    d0 = dump('cd0')
    n = json.loads((d0 / 'meta.json').read_text())['texelCount']
    t = np.frombuffer((d0 / 'texels.f32x8').read_bytes(), np.float32).reshape(-1, 8)[:n]
    P, N = t[:, :3].astype(float), t[:, 4:7].astype(float)
    N /= np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-9)
    f = P + N * 0.02
    groups = {
        "the end wall's foot near z 0.8": (np.abs(P[:, 0] - 9.2) < 0.01) & (N[:, 0] < -0.9) & (P[:, 1] < 0.25) & (np.abs(P[:, 2] - 0.8) < 0.15),
        'the rest of the turn': ((((f[:, 0] > 8.2) & (f[:, 0] < 9.2) & (f[:, 2] > -0.25) & (f[:, 2] < 1.0)) |
                                  ((f[:, 0] > 7.2) & (f[:, 0] < 9.2) & (f[:, 2] > 1.0) & (f[:, 2] < 2.0))) &
                                 (f[:, 1] > 0) & (f[:, 1] < 2.5)),
        'the first leg near the doorway (x < 6.5)': (f[:, 0] > 5.2) & (f[:, 0] < 6.5) & (f[:, 2] > 1.0) & (f[:, 2] < 2.0) & (f[:, 1] > 0) & (f[:, 1] < 2.5),
    }
    groups['the rest of the turn'] &= ~groups["the end wall's foot near z 0.8"]
    T = triangles()
    rng = np.random.default_rng(5)
    for tag in ('cd0', 'cd1'):
        raw = (dump(tag) / 'rays_captured.u32x8').read_bytes()
        cnt = np.frombuffer(raw[:4], np.uint32)[0]
        R = np.frombuffer(raw[16:], np.uint32).reshape(-1, 8)[:cnt]
        ids = R[:, 0].astype(np.int64)
        miss = ((R[:, 6] >> 8) & 0xFF) == 0
        dirs = oct_decode(R[:, 1]).astype(float)
        w = R[:, 5].copy().view(np.float32).astype(float)
        L = np.stack([R[:, 2], R[:, 3], R[:, 4]], 1).copy().view(np.float32).astype(float) @ LUM
        cos = np.maximum(np.einsum('ij,ij->i', dirs, N[ids]), 0)
        c = L * cos
        print(f'== {tag}')
        for name, g in groups.items():
            sel = np.isin(ids, np.flatnonzero(g))
            texels = np.unique(ids[sel])
            if len(texels) == 0:
                print(f'  {name}: none captured')
                continue
            ws = w[sel]
            guided = np.mean(np.abs(ws - np.pi) > 1e-3 * np.pi)
            q = sel & miss & (cos > 1e-3) & (w > 0)
            gpdf = 2 * (cos[q] / w[q] - 0.5 * cos[q] / np.pi)
            boost = gpdf / (cos[q] / np.pi)
            sky_share = c[q].sum() / max(c[sel].sum(), 1e-12)
            # the chance a cosine ray finds the sky, exact (up to 12 texels, 2,048 rays each); the guided ray's from the misses
            pc, pm = [], []
            for tx in rng.choice(texels, min(12, len(texels)), replace=False):
                u1, u2 = rng.random(2048), rng.random(2048)
                r, phi = np.sqrt(u1), 2 * np.pi * u2
                nn = N[tx]
                a = np.array([1.0, 0, 0]) if abs(nn[0]) < 0.9 else np.array([0, 1.0, 0])
                t1 = np.cross(nn, a); t1 /= np.linalg.norm(t1); t2 = np.cross(nn, t1)
                dd = t1 * (r * np.cos(phi))[:, None] + t2 * (r * np.sin(phi))[:, None] + nn * np.sqrt(1 - u1)[:, None]
                pc.append(escapes(T, P[tx] + nn * 0.01, dd).mean())
                mt = ids == tx
                pm.append(miss[mt].mean())
            pc, pm = np.array(pc), np.array(pm)
            pg = np.clip(2 * pm - pc, 0, 1) if guided > 0.5 else np.full_like(pc, np.nan)
            print(f'  {name}: {len(texels)} texels, guided rays {guided:.0%}; sky {sky_share:.0%} of their light; '
                  f'sky rays: guide density over cosine median {np.median(boost) if len(boost) else float("nan"):.2f} '
                  f'(p10 {np.percentile(boost, 10) if len(boost) else float("nan"):.2f}, p90 {np.percentile(boost, 90) if len(boost) else float("nan"):.2f}); '
                  f'a ray finds the sky: cosine {np.median(pc):.2%}, all rays {np.median(pm):.2%}, guided {np.nanmedian(pg):.2%}')


if __name__ == '__main__':
    main()
