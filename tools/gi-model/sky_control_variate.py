# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The sky at the crevices corridor's turn, by a control variate on the texel's patch (uv run
tools/gi-model/sky_control_variate.py [HS_DIR]).

The texels at the turn see the courtyard's sky only through the doorway, along lines that barely clear the corner
block: a few rare rays carry it, their sky part swinging +-20-45% seed to seed with a heavy bright tail (2026-10-23).
The patch hierarchy knows each patch's sky by direction (its cooked couplings' escaped rays, 16 x 16 bins of 1,024
rays). Estimator, per texel, on the same rays:

  plain   S = mean f_i,                f_i = the ray's sky contribution (a miss: weight x sky radiance; else 0)
  cv      S = H + mean (f_i - h_i),    h_i = weight x sky(w_i) x V_patch(w_i), H = int cos sky V_patch dw

Same expectation; only the difference between the texel's sky and its patch's is left to its rays.

Inputs: the lean fine round's capture with each hit's distance and the ray's weight (BLIX_CAPTURE_FINE_ONLY=1
--lean-shade --ray-capture 2: dumps cd0, cd1 at two gather seeds), the scene's triangles. Stood in for the cook's bins:
V_patch by 32 x 32 octahedral bins cast from the patch's centre, 2 cm off it, against the triangles; the sky by
direction from every captured miss (sky = contribution / weight). Reported: the turn's texels' sky part and whole
light, seed-to-seed noise plain against cv, and the bias between the two (cv's mean against plain's).

2026-10-23 (cd0/cd1, 528 turn texels over 129 patches): per-ray variance of the sky term 0.65x; the texels' noise
4.3% -> 4.4% (sky over 20% of their light: 5.0% -> 4.7%), unbiased (-0.2%). Not the lever: the turn is quiet (records'
spread over four shipped seeds 1.7% median); the noisy points' records at the end wall's foot near z 0.8 took 4 passes
(128 rays) at the shipped budget, some 0 -- settled on their own variance before a rare doorway ray showed.
"""
import json
import sys
from pathlib import Path

import numpy as np

HS = Path(sys.argv[1] if len(sys.argv) > 1 else '/private/tmp/blix-dbg/hs')
GLTF = Path.home() / 'dev/testbeds-src/crevices-dark/crevices-dark.gltf'
RES = 32
LUM = np.array([.2126, .7152, .0722])


def dump(tag):
    return sorted((HS / f'dump-{tag}').glob('view-*'))[-1]


def oct_decode(w):
    o = np.stack([(w & 0xFFFF).astype(np.int16), (w >> 16).astype(np.uint16).astype(np.int16)], 1) / 32767.0
    z = 1 - np.abs(o).sum(1)
    x, y = o[:, 0].copy(), o[:, 1].copy()
    neg = z < 0
    x[neg], y[neg] = (1 - np.abs(o[neg, 1])) * np.sign(o[neg, 0]), (1 - np.abs(o[neg, 0])) * np.sign(o[neg, 1])
    v = np.stack([x, y, z], 1)
    return v / np.linalg.norm(v, axis=1, keepdims=True)


def oct_bin(d):
    d = d / np.abs(d).sum(1, keepdims=True)
    x, y = d[:, 0].copy(), d[:, 1].copy()
    neg = d[:, 2] < 0
    x[neg], y[neg] = (1 - np.abs(d[neg, 1])) * np.sign(d[neg, 0]), (1 - np.abs(d[neg, 0])) * np.sign(d[neg, 1])
    i = np.clip(((x * 0.5 + 0.5) * RES).astype(int), 0, RES - 1)
    j = np.clip(((y * 0.5 + 0.5) * RES).astype(int), 0, RES - 1)
    return j * RES + i


def bin_centres():
    u = (np.arange(RES) + 0.5) / RES * 2 - 1
    x, y = np.meshgrid(u, u)
    x, y = x.ravel(), y.ravel()
    z = 1 - np.abs(x) - np.abs(y)
    neg = z < 0
    x2, y2 = x.copy(), y.copy()
    x2[neg], y2[neg] = (1 - np.abs(y[neg])) * np.sign(x[neg]), (1 - np.abs(x[neg])) * np.sign(y[neg])
    v = np.stack([x2, y2, z], 1)
    return v / np.linalg.norm(v, axis=1, keepdims=True)


def triangles():
    import base64
    g = json.loads(GLTF.read_text())

    def buf(i):
        u = g['buffers'][i]['uri']
        return base64.b64decode(u.split(',')[1]) if u.startswith('data:') else (GLTF.parent / u).read_bytes()

    def acc(i):
        a = g['accessors'][i]
        bv = g['bufferViews'][a['bufferView']]
        comp = {5126: np.float32, 5125: np.uint32, 5123: np.uint16}[a['componentType']]
        nc = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3}[a['type']]
        off = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
        stride = bv.get('byteStride', 0)
        data = buf(bv['buffer'])
        if stride and stride != nc * np.dtype(comp).itemsize:
            raw = np.frombuffer(data, np.uint8, count=stride * a['count'], offset=off).reshape(a['count'], stride)
            return raw[:, :nc * np.dtype(comp).itemsize].copy().view(comp).reshape(a['count'], nc)
        return np.frombuffer(data, comp, count=a['count'] * nc, offset=off).reshape(a['count'], nc)

    out = []
    for mesh in g['meshes']:
        for pr in mesh['primitives']:
            pos = acc(pr['attributes']['POSITION']).astype(float)
            out.append(pos[acc(pr['indices']).reshape(-1, 3).astype(int)])
    return np.concatenate(out)


def escapes(T, o, dirs):
    """Per direction: does a ray from o escape every triangle (both faces)?"""
    e1, e2 = T[:, 1] - T[:, 0], T[:, 2] - T[:, 0]
    s = o - T[:, 0]
    q = np.cross(s, e1)
    p = np.cross(dirs[:, None, :], e2[None])                       # (D, T, 3)
    det = np.einsum('tk,dtk->dt', e1, p)
    ok = np.abs(det) > 1e-12
    inv = np.where(ok, 1 / np.where(ok, det, 1), 0)
    u = np.einsum('tk,dtk->dt', s, p) * inv
    v = (dirs @ q.T) * inv
    t = np.einsum('tk,tk->t', e2, q)[None] * inv
    hit = ok & (u >= 0) & (v >= 0) & (u + v <= 1) & (t > 1e-5)
    return ~hit.any(1)


def capture(tag, N):
    raw = (dump(tag) / 'rays_captured.u32x8').read_bytes()
    cnt = np.frombuffer(raw[:4], np.uint32)[0]
    R = np.frombuffer(raw[16:], np.uint32).reshape(-1, 8)[:cnt]
    ids = R[:, 0].astype(np.int64)
    kind = (R[:, 6] >> 8) & 0xFF
    dirs = oct_decode(R[:, 1]).astype(float)
    L = np.stack([R[:, 2], R[:, 3], R[:, 4]], 1).copy().view(np.float32).astype(float) @ LUM
    w = R[:, 5].copy().view(np.float32).astype(float)              # the ray's weight (cos / mixture pdf)
    c = L * np.maximum(np.einsum('ij,ij->i', dirs, N[ids]), 0)    # its contribution
    return dict(ids=ids, miss=kind == 0, dirs=dirs, w=w, c=c)


def main():
    d0 = dump('cd0')
    m = json.loads((d0 / 'meta.json').read_text())
    n = m['texelCount']
    t = np.frombuffer((d0 / 'texels.f32x8').read_bytes(), np.float32).reshape(-1, 8)[:n]
    P, N = t[:, :3].astype(float), t[:, 4:7].astype(float)
    N /= np.maximum(np.linalg.norm(N, axis=1, keepdims=True), 1e-9)
    patch_of = np.frombuffer((d0 / 'patch.i32').read_bytes(), np.int32)[:n]
    pp = np.frombuffer((d0 / 'patches.f32x12').read_bytes(), np.float32).reshape(-1, 12)
    C, CN = pp[:, :3].astype(float), pp[:, 4:7].astype(float)
    caps = [capture('cd0', N), capture('cd1', N)]

    # The sky by direction, from every miss of both seeds.
    s_sum, s_cnt = np.zeros(RES * RES), np.zeros(RES * RES)
    for k in caps:
        q = k['miss'] & (k['w'] > 0)
        b = oct_bin(k['dirs'][q])
        np.add.at(s_sum, b, k['c'][q] / k['w'][q])
        np.add.at(s_cnt, b, 1)
    sky = np.where(s_cnt > 0, s_sum / np.maximum(s_cnt, 1), 0.0)
    print(f'sky by direction: {np.count_nonzero(s_cnt)} of {RES * RES} bins seen')

    # The turn's texels: their front (2 cm off) in the leg's air, near the turn; captured at both seeds.
    f = P + N * 0.02
    turn = (((f[:, 0] > 8.2) & (f[:, 0] < 9.2) & (f[:, 2] > -0.25) & (f[:, 2] < 1.0)) |
            ((f[:, 0] > 7.2) & (f[:, 0] < 9.2) & (f[:, 2] > 1.0) & (f[:, 2] < 2.0))) & (f[:, 1] > 0) & (f[:, 1] < 2.5)
    have = [np.bincount(k['ids'], minlength=n) > 0 for k in caps]
    texels = np.flatnonzero(turn & have[0] & have[1] & (patch_of >= 0))
    patches = np.unique(patch_of[texels])
    print(f'turn texels captured at both seeds: {len(texels)}, over {len(patches)} patches')

    # Each patch's visibility by bin (the cook's escaped rays, from its centre 2 cm off it).
    T = triangles()
    centres = bin_centres()
    V = {}
    for p in patches:
        o = C[p] + CN[p] * 0.02
        up = centres @ CN[p] > 0
        vis = np.zeros(RES * RES, bool)
        vis[up] = escapes(T, o, centres[up])
        V[p] = vis

    # H per texel: int cos sky V_patch dw, by the same binned functions (Monte Carlo over the sphere).
    rng = np.random.default_rng(1)
    mc = rng.normal(size=(200000, 3))
    mc /= np.linalg.norm(mc, axis=1, keepdims=True)
    mb = oct_bin(mc)
    H = {}
    for tx in texels:
        p = patch_of[tx]
        cos = np.maximum(mc @ N[tx], 0)
        H[tx] = 4 * np.pi * np.mean(cos * sky[mb] * V[p][mb])

    rows = []
    for k in caps:
        sel = np.isin(k['ids'], texels)
        ids, miss, dirs, w, c = k['ids'][sel], k['miss'][sel], k['dirs'][sel], k['w'][sel], k['c'][sel]
        fsky = np.where(miss, c, 0.0)
        b = oct_bin(dirs)
        h = w * sky[b] * np.array([V[p][bb] for p, bb in zip(patch_of[ids], b)])
        cnt = np.bincount(ids, minlength=n)
        S = np.bincount(ids, fsky, minlength=n) / np.maximum(cnt, 1)
        D = np.bincount(ids, fsky - h, minlength=n) / np.maximum(cnt, 1)
        Tot = np.bincount(ids, c, minlength=n) / np.maximum(cnt, 1)
        Hv = np.zeros(n)
        for tx in texels: Hv[tx] = H[tx]
        rows.append(dict(S=S[texels], Scv=(Hv + D)[texels], T=Tot[texels], fv=np.var(fsky), dv=np.var(fsky - h)))
    a, b_ = rows
    T_mean = (a['T'] + b_['T']) / 2
    sky_share = np.median(((a['S'] + b_['S']) / 2) / np.maximum(T_mean, 1e-12))
    print(f"the sky's share of their light: median {sky_share:.0%}")
    print(f"per-ray variance of the sky term: plain {a['fv']:.3g}, cv {a['dv']:.3g} ({a['dv'] / a['fv']:.2f}x)")
    for lab, x, y in (('sky part', a['S'], b_['S']), ('sky part, cv', a['Scv'], b_['Scv'])):
        noise = np.abs(x - y) / np.maximum(T_mean, 1e-12) / np.sqrt(2)
        print(f'  {lab:14s} seed-to-seed noise over the texel\'s light: median {np.median(noise):.3f}, p90 {np.percentile(noise, 90):.3f}')
    Tcv_a, Tcv_b = a['T'] - a['S'] + a['Scv'], b_['T'] - b_['S'] + b_['Scv']
    for lab, x, y in (('whole light', a['T'], b_['T']), ('whole light, cv', Tcv_a, Tcv_b)):
        noise = np.abs(x - y) / np.maximum(T_mean, 1e-12) / np.sqrt(2)
        print(f'  {lab:14s} seed-to-seed noise: median {np.median(noise):.3f}, p90 {np.percentile(noise, 90):.3f}')
    share = ((a['S'] + b_['S']) / 2) / np.maximum(T_mean, 1e-12)
    for lo in (0.05, 0.2):
        q = share > lo
        if q.sum() < 3: continue
        nz = lambda x, y: np.median(np.abs(x[q] - y[q]) / np.maximum(T_mean[q], 1e-12) / np.sqrt(2))
        print(f'  texels whose sky is over {lo:.0%} of their light ({q.sum()}): whole-light noise plain {nz(a["T"], b_["T"]):.3f}, '
              f'cv {nz(Tcv_a, Tcv_b):.3f}; sky part plain {nz(a["S"], b_["S"]):.3f}, cv {nz(a["Scv"], b_["Scv"]):.3f}')
    bias = ((Tcv_a + Tcv_b) / 2 - T_mean) / np.maximum(T_mean, 1e-12)
    print(f'  cv against plain, two-seed means: median {np.median(bias):+.2%}, mean {np.mean(bias):+.2%}, '
          f'over all their light {((Tcv_a + Tcv_b).sum() / 2) / T_mean.sum() - 1:+.2%}')


if __name__ == '__main__':
    main()
