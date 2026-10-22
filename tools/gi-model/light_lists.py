# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""A texel's light from a light-free list, against today's gather (uv run tools/gi-model/light_lists.py DUMP [DUMP...]).

Today a light change re-traces every texel's rays (~3.7 G on Sponza, minutes). The rays and what they hit are the
geometry's; only the light at the hits changes. A light-free list keeps, per texel, the targets its rays reach -- a
near hit's texel or a far hit's patch -- with their weights, and the sky's share by direction; a light change is then a
sparse sum over the lists. Two things it cannot keep as today's gather has them:

  truncation  a list keeps the targets carrying COVER of the hit light (the tail dropped, or lumped)
  the sun at the hit  today a shadow ray from each hit (exact, per hit); a list has the hit's texels' sun bits blended
              (near hits: --ray-capture-sun's word 6) or its patch's mean direct sun D_j (far hits: patch_direct.u32x2)

From a view dump of a run with --ray-capture B --ray-capture-sun (the rays of 1 in 2^B texels, every round), per texel:
its estimate today (the mean of its rays' light), and what each change makes of it. Reported per kind of texel (coarse:
the sweep and refine; fine: the fine round, not the near rounds, which re-shade the same rays): the list's entries a
texel at each COVER, the light dropped, and the sun substitutions' deviation (bias and |error| percentiles)."""
import json
import sys
from pathlib import Path

import numpy as np

COVERS = (0.9, 0.95, 0.99, 1.0)
NEAR = 0.25   # m: the near part (texel_nodes_shade.glsl's near cut, --cell-near)


def load(dump):
    d = sorted(Path(dump).glob("view-*"))[-1]
    m = json.loads((d / "meta.json").read_text())
    n = m["texelCount"]
    raw = (d / "rays_captured.u32x8").read_bytes()
    met = int(np.frombuffer(raw[:4], np.uint32)[0])
    rec = np.frombuffer(raw[16:], np.uint32).reshape(-1, 8)
    pd = np.frombuffer((d / "patch_direct.u32x2").read_bytes(), np.uint32).reshape(-1, 2).astype(np.float64)
    dj = np.where(pd[:, 1] > 0, pd[:, 0] / 4096.0 / np.maximum(pd[:, 1], 1), 0.0)
    return d.parent.name, m, n, met, rec, dj


def texel_stats(name, rec, n, dj):
    tag = rec[:, 1] & 0x7FFF
    if not (rec[:, 1] & 0x8000).any():
        raise SystemExit(f"{name}: no --ray-capture-sun in this capture")
    stage, nround = tag & 15, (tag >> 8) & 0x7F
    tid = rec[:, 0].astype(np.int64)
    dist = rec[:, 2].copy().view(np.float32)
    how = rec[:, 3]
    kind = how & 7
    patch = (how >> 13).astype(np.int64) - 1
    whole = rec[:, 4].copy().view(np.float32).astype(np.float64)
    bounce = rec[:, 5].copy().view(np.float32).astype(np.float64)
    hv = rec[:, 6].copy().view(np.float16).reshape(-1, 2).astype(np.float64)
    full, bits = hv[:, 0], hv[:, 1]          # the sun at visibility 1 and n.l 1; n.l x the bits' blend
    ntex = np.where(rec[:, 7] < n, rec[:, 7].astype(np.int64), -1)
    hit = (kind >= 2) & (dist < 1e20)
    sun = np.where(hit, whole - bounce, 0.0)
    near = hit & (dist < NEAR)
    djh = np.where(hit & (patch >= 0) & (patch < len(dj)), dj[np.clip(patch, 0, len(dj) - 1)], 0.0)
    alt_bits = np.where(hit, full * bits, 0.0)
    alt_dj = np.where(hit, full * djh, 0.0)
    out = {}
    for label, sel in (("coarse", (stage == 1) | (stage == 2)), ("fine", (stage == 3) & (nround == 0))):
        if sel.sum() == 0:
            continue
        t = tid[sel]
        u, inv = np.unique(t, return_inverse=True)
        rays = np.bincount(inv).astype(np.float64)
        today = np.bincount(inv, whole[sel]) / rays
        # The sun at the hit, three ways: the bits' blend at every hit; near by bits, far by D_j; D_j everywhere.
        base = np.bincount(inv, (whole - sun)[sel]) / rays
        sun_bits = base + np.bincount(inv, alt_bits[sel]) / rays
        sun_mix = base + np.bincount(inv, np.where(near, alt_bits, alt_dj)[sel]) / rays
        sun_dj = base + np.bincount(inv, alt_dj[sel]) / rays
        # Truncation: per texel its targets (a near hit's texel, else its patch) by the bounce light each carries.
        bsel = hit[sel]
        key_t = np.where(near[sel] & (ntex[sel] >= 0), ntex[sel] * 2 + 1, patch[sel] * 2)
        ok = bsel & (key_t >= 0)
        tex_i = inv[ok]
        key = tex_i.astype(np.int64) * (1 << 40) + key_t[ok]
        uk, kinv = np.unique(key, return_inverse=True)
        light_k = np.bincount(kinv, bounce[sel][ok], minlength=len(uk))
        owner = (uk >> 40).astype(np.int64)
        order = np.lexsort((-light_k, owner))
        own_o, l_o = owner[order], light_k[order]
        starts = np.r_[0, np.flatnonzero(np.diff(own_o)) + 1]
        ends = np.r_[starts[1:], len(own_o)]
        entries = {c: np.zeros(len(u)) for c in COVERS}
        dropped = {c: np.zeros(len(u)) for c in COVERS}
        for a, b in zip(starts, ends):
            ti = own_o[a]
            cs = np.cumsum(l_o[a:b])
            tot = cs[-1]
            for c in COVERS:
                k = len(cs) if c >= 1.0 or tot <= 0 else int(np.searchsorted(cs, c * tot)) + 1
                entries[c][ti] = k
                dropped[c][ti] = (tot - cs[k - 1]) if tot > 0 else 0.0
        out[label] = dict(texels=len(u), rays=rays.mean(), today=today, sun_bits=sun_bits, sun_mix=sun_mix, sun_dj=sun_dj,
                          entries=entries, dropped={c: dropped[c] / rays for c in COVERS},
                          sun_share=np.bincount(inv, sun[sel]) / rays / np.maximum(today, 1e-12))
    return out


def dev(a, b):
    ok = b > 1e-9
    r = a[ok] / b[ok] - 1
    return f"bias {100 * (a[ok].sum() / b[ok].sum() - 1):+.2f}%, |error| median {100 * np.median(np.abs(r)):.2f}% p90 {100 * np.percentile(np.abs(r), 90):.2f}%"


def main():
    for dump in sys.argv[1:]:
        name, m, n, met, rec, dj = load(dump)
        print(f"\n{name}: {met:,} rays met, {len(rec):,} kept{' (the room ran out: a part of them)' if met > len(rec) else ''}")
        for label, s in texel_stats(name, rec, n, dj).items():
            print(f"  {label}: {s['texels']:,} texels, {s['rays']:.0f} rays a texel; the sun at the hit {100 * np.median(s['sun_share']):.1f}% of a texel's light (median), p90 {100 * np.percentile(s['sun_share'], 90):.1f}%")
            for c in COVERS:
                e, dr = s["entries"][c], s["dropped"][c]
                print(f"     list at {100 * c:4.0f}% of the hit light: entries median {np.median(e):4.0f}, mean {e.mean():6.1f}, p90 {np.percentile(e, 90):5.0f}"
                      f"  ({4 * e.mean():5.0f} B a texel at 4 B an entry); light dropped {dev(s['today'] - dr, s['today'])}")
            print(f"     the sun at every hit by its texels' bits:        {dev(s['sun_bits'], s['today'])}")
            print(f"     near hits by bits, far hits by the patch's D_j: {dev(s['sun_mix'], s['today'])}")
            print(f"     the sun at every hit by the patch's D_j:        {dev(s['sun_dj'], s['today'])}")


if __name__ == "__main__":
    main()
