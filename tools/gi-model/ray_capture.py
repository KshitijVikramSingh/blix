# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""Where contacts' rays go and what their noise is made of: a view dump with --ray-capture's rays
(SponzaLoop.RayCapture.cs, texel_nodes_shade.glsl RayCapture).
uv run tools/gi-model/ray_capture.py DUMP [DUMP2 (another seed: the texels' real noise)] [--round fine|sweep|refine|all]

Per texel of the sample: its rays, their mean (its estimate) and spread; banded by the cooked contact ratio (the fine
flag's: 1 open, under 0.999 a contact). Per band: the texels, their share of the rays, the rays a texel took, the
estimate's noise (from its rays; with DUMP2, the two seeds' difference too) and one ray's spread (rays for 1% noise).
Then the band's per-ray variance split: by what the ray brought (sky, sun at the hit, bounce), by how the hit was
answered, by hit distance -- each part's own variance, and how much of the whole is between the distance bins (what a
gather that knew each bin's mean -- stratified, or answered without rays -- would not pay)."""
import json, sys
from pathlib import Path
import numpy as np

HOW = {0: "sky", 1: "far record", 2: "hit cell", 3: "search", 4: "among none"}
ROUND = {1: "sweep", 2: "refine", 3: "fine", 4: "visible-changed"}
DIST = [0.0, 0.02, 0.05, 0.10, 0.25, 0.5, 1.0, 1e29, 1e31]
DIST_NAMES = ["<2cm", "2-5cm", "5-10cm", "10-25cm", "25-50cm", "0.5-1m", ">1m", "miss"]
BANDS = [(0.999, 1.01, "open (>=0.999)"), (0.95, 0.999, "0.95-0.999"), (0.85, 0.95, "0.85-0.95"), (0.7, 0.85, "0.70-0.85"), (0.0, 0.7, "<0.70")]


def load(d):
    d = Path(d)
    meta = json.loads((d / "meta.json").read_text())
    n = meta["texelCount"]
    raw = (d / "rays_captured.u32x8").read_bytes()
    count = np.frombuffer(raw[:4], np.uint32)[0]
    rec = np.frombuffer(raw[16:], np.uint32).reshape(-1, 8)
    contact = np.frombuffer((d / "contact.u8").read_bytes(), np.uint8)[:n].astype(np.float32)
    contact = np.where(contact > 0, (contact - 1) / 254, np.nan)
    resolved = None
    if (d / "resolved.u32x2").exists():
        w = np.frombuffer((d / "resolved.u32x2").read_bytes(), np.uint32).reshape(n, 2)[:, 0].astype(np.uint64)
        scale = np.exp2((w >> 27).astype(np.int64) - 24)
        resolved = (w & 511) * scale * 0.2126 + ((w >> 9) & 511) * scale * 0.7152 + ((w >> 18) & 511) * scale * 0.0722
    return meta, count, rec, contact, resolved


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    rnd = sys.argv[sys.argv.index("--round") + 1] if "--round" in sys.argv else "all"
    if rnd in args: args.remove(rnd)
    meta, count, rec, contact, resolved = load(args[0])
    d0 = Path(args[0])
    lists = guide = None
    if (d0 / "light_lists.f32x2").exists() and (d0 / "guide_patch.u32").exists():
        ll = np.frombuffer((d0 / "light_lists.f32x2").read_bytes(), np.float32).reshape(-1, 16, 2)
        lists = ll[..., 0].copy().view(np.int32)
        guide = np.frombuffer((d0 / "guide_patch.u32").read_bytes(), np.uint32)
    print(f"{Path(args[0]).name}: {count:,} rays met, {len(rec):,} kept; seed {meta.get('seed')}")
    tid = rec[:, 0].astype(np.int64)
    tag = rec[:, 1] & 0xFFFF
    stage = tag & 15
    dist = np.ascontiguousarray(rec[:, 2]).view(np.float32)
    how = rec[:, 3]
    whole = np.ascontiguousarray(rec[:, 4]).view(np.float32).astype(np.float64)
    bounce = np.ascontiguousarray(rec[:, 5]).view(np.float32).astype(np.float64)
    cw = np.ascontiguousarray(rec[:, 6]).view(np.float16).reshape(-1, 2).astype(np.float64)
    hit = (how & 7) >= 2
    sky = np.where((how & 7) == 0, whole, 0.0)
    sun = np.where(hit, whole - bounce, 0.0)
    bnc = np.where(hit, bounce, 0.0) + np.where((how & 7) == 1, whole, 0.0)

    print("   rays by round: " + ", ".join(f"{ROUND.get(int(t) & 15, t)}{f' r{int(t) >> 4 & 15}' if t >> 4 & 15 else ''}{f' near {int(t) >> 8}' if t >> 8 else ''} {np.sum(tag == t):,}" for t in np.unique(tag)))
    if rnd != "all":
        keep = stage == {v: k for k, v in ROUND.items()}[rnd]
        tid, tag, stage, dist, how, whole, bounce, cw, hit, sky, sun, bnc = (a[keep] for a in (tid, tag, stage, dist, how, whole, bounce, cw, hit, sky, sun, bnc))
        print(f"   the {rnd} round's rays alone: {len(tid):,}")

    # Per texel: its rays' mean and spread. The estimate is the mean of its passes' means, so of its rays.
    u, inv, nrays = np.unique(tid, return_inverse=True, return_counts=True)
    mean = np.bincount(inv, whole) / nrays
    sq = np.bincount(inv, whole * whole) / nrays
    var1 = np.maximum(sq - mean * mean, 0)            # one ray's variance
    c = contact[u]
    rel_ray = np.sqrt(var1) / np.maximum(mean, 1e-12)  # one ray's spread over the mean
    rel_est = rel_ray / np.sqrt(nrays)                  # the estimate's noise, from its own rays
    seed_diff = None
    if len(args) > 1:
        m2, _, _, _, res2 = load(args[1])
        if resolved is not None and res2 is not None:
            r1, r2 = resolved[u], res2[u]
            seed_diff = np.abs(r1 - r2) / np.maximum(0.5 * (r1 + r2), 1e-12) / np.sqrt(2)
    lit = mean > 1e-6
    total_rays = nrays.sum()
    print(f"   {len(u):,} texels sampled; rays a texel: median {np.median(nrays):.0f}, mean {nrays.mean():.0f}")
    print()
    print(f"   {'contact band':16s} {'texels':>7s} {'rays %':>7s} {'rays/tx':>8s} {'noise est':>9s} {'seeds':>7s} {'1 ray':>7s} {'for 1%':>8s}")
    for lo, hi, name in BANDS:
        sel = lit & (c >= lo) & (c < hi)
        if not sel.any(): continue
        sd = f"{100 * np.median(seed_diff[sel]):6.2f}%" if seed_diff is not None else "      -"
        print(f"   {name:16s} {sel.sum():7,d} {100 * nrays[sel].sum() / total_rays:6.1f}% {np.mean(nrays[sel]):8.0f} {100 * np.median(rel_est[sel]):8.2f}% {sd} {np.median(rel_ray[sel]):6.2f}x {np.median((rel_ray[sel] / 0.01) ** 2):8.0f}")
    print("   (noise est: the estimate's, from its own rays, median; seeds: two cooks' difference / sqrt 2, median;"
          " 1 ray: one ray's spread over the mean, median; for 1%: rays one texel needs for 1% noise, median)")

    # The per-ray variance split, per band: each texel's rays centred on its own mean, so the parts add as a texel's do.
    ray_band = np.full(len(tid), -1)
    for b, (lo, hi, _) in enumerate(BANDS):
        ray_band[(c[inv] >= lo) & (c[inv] < hi) & lit[inv]] = b
    dbin = np.clip(np.searchsorted(DIST, dist, side="right") - 1, 0, len(DIST_NAMES) - 1)
    dbin[(how & 7) == 0] = len(DIST_NAMES) - 1
    norm = 1.0 / np.maximum(mean[inv], 1e-12)          # each texel's rays over its mean: texels weigh alike
    x = whole * norm
    xm = mean[inv] * norm
    for b, (_, _, name) in enumerate(BANDS):
        sel = ray_band == b
        if sel.sum() < 1000: continue
        texels_in = np.unique(inv[sel]).size
        tot = np.sum((x[sel] - xm[sel]) ** 2)
        print(f"\n   == {name}: {texels_in:,} texels, {sel.sum():,} rays; one ray's variance over the mean^2 {tot / sel.sum():.2f}")

        # By what it brought: each part's own variance about its own texel mean.
        parts = {"sky (misses)": sky, "sun at the hit": sun, "bounce": bnc}
        line = []
        for pname, p in parts.items():
            pn = p * norm
            pmean = np.bincount(inv, pn, minlength=len(u)) / nrays
            v = np.sum((pn[sel] - pmean[inv[sel]]) ** 2)
            line.append(f"{pname} {100 * v / tot:4.0f}% (light {100 * pn[sel].sum() / x[sel].sum():3.0f}%)")
        print("      by what it brought (own variance; light share): " + "; ".join(line))

        # By how it was answered, and by distance: share of rays, light, and the variance of the rays in it.
        for title, key, names in (("how answered", how & 31, None), ("hit distance", dbin, DIST_NAMES)):
            print(f"      by {title}:")
            for k in np.unique(key[sel]):
                s2 = sel & (key == k)
                if names is None:
                    nm = HOW.get(int(k) & 7, str(k)) + (" +near texel" if k & 8 else "") + (" +no patch" if k & 16 else "")
                else:
                    nm = names[int(k)]
                v = np.sum((x[s2] - xm[s2]) ** 2)
                print(f"         {nm:28s} rays {100 * s2.sum() / sel.sum():5.1f}%  light {100 * x[s2].sum() / x[sel].sum():5.1f}%  variance {100 * v / tot:5.1f}%  light a ray {x[s2].mean():6.2f}x")
        # By strategy (the ray's place in its pass, texel_nodes_rays.comp: 0-15 cosine, 16-23 the guide, 24-31 the light
        # lists, where the texel has them): rays, light, variance, and how often a ray brought sun from its hit.
        place = (how >> 8) & 31
        strat = np.where(place < 16, 0, np.where(place < 24, 1, 2))
        print("      by strategy (the ray's place in its pass):")
        for k, nm in enumerate(("cosine 0-15", "guide 16-23", "light lists 24-31")):
            s2 = sel & (strat == k)
            if not s2.any(): continue
            v = np.sum((x[s2] - xm[s2]) ** 2)
            sunny = np.mean(sun[s2] > 1e-9 * np.maximum(mean[inv[s2]], 1e-12))
            print(f"         {nm:28s} rays {100 * s2.sum() / sel.sum():5.1f}%  light {100 * x[s2].sum() / x[sel].sum():5.1f}%  variance {100 * v / tot:5.1f}%"
                  f"  light a ray {x[s2].mean():5.2f}x  sun at the hit {100 * sunny:4.1f}% of its rays, their sun a ray {np.mean((sun * norm)[s2][sun[s2] > 0]) if (sun[s2] > 0).any() else 0:6.2f}x")
        if lists is not None:
            # The light lists: the texel's guide patch's (guide_patch.u32: + 2), its LIGHT_K entries' patches. Which of the
            # sun the rays brought came from a hit on a listed patch -- the light strategy's aim, against where it was.
            gp = guide[tid[sel]].astype(np.int64) - 2
            hp = (how[sel] >> 13).astype(np.int64) - 1
            ok = (gp >= 0) & (gp < len(lists)) & (hp >= 0)
            listed = np.zeros(sel.sum(), bool)
            idx = np.where(ok)[0]
            listed[idx] = (lists[gp[idx]] == hp[idx, None]).any(1)
            sn = (sun * norm)[sel]
            st_ = strat[sel]
            print(f"      the sun by its hit's patch: from listed patches {100 * sn[listed].sum() / max(sn.sum(), 1e-12):.0f}% of the sun the rays brought"
                  f" (the guide patch known for {100 * np.mean(gp >= 0):.0f}% of rays); lists' entries used {np.mean((lists[gp[gp >= 0]] >= 0).sum(1)) if (gp >= 0).any() else 0:.1f} of 16")
            for k, nm in enumerate(("cosine", "guide", "light lists")):
                m = (st_ == k) & (sn > 0)
                if m.any(): print(f"         {nm:12s} rays with sun: on a listed patch {100 * np.mean(listed[m]):.0f}%")
            # The patches that brought the sun to this band, by how much: how many it takes for 90%.
            sp = np.bincount(np.maximum(hp, 0), sn * (hp >= 0))
            order = np.sort(sp)[::-1]
            if order.sum() > 0:
                c90 = int(np.searchsorted(np.cumsum(order) / order.sum(), 0.9)) + 1
                print(f"      the sun came from {np.count_nonzero(sp):,} patches; 90% of it from {c90}")
        # Between the distance bins: per texel, the variance its bins' means explain (stratifying on the bin removes it).
        key = inv[sel] * len(DIST_NAMES) + dbin[sel]
        ks, kinv, kn = np.unique(key, return_inverse=True, return_counts=True)
        kmean = np.bincount(kinv, x[sel]) / kn
        within = np.sum((x[sel] - kmean[kinv]) ** 2)
        print(f"      between the distance bins: {100 * (1 - within / tot):.0f}% of the variance (the rest within a bin)")
        # Near rays alone (< 10 cm): their share of the variance if their light were each texel's mean of them.
        near = dbin[sel] <= 2
        xs = x[sel].copy()
        nm_ = np.bincount(inv[sel][near], xs[near], minlength=len(u)) / np.maximum(np.bincount(inv[sel][near], minlength=len(u)), 1)
        xs[near] = nm_[inv[sel][near]]
        print(f"      the near rays (<10 cm) each at their texel's mean of them: the variance {100 * np.sum((xs - xm[sel]) ** 2) / tot:.0f}% of now")
        # The cosine: are the contact's rays spent at grazing angles?
        cs = cw[sel, 0]
        print(f"      rays' cosine to the normal: median {np.median(cs):.2f}; the near rays' {np.median(cs[near]) if near.any() else float('nan'):.2f}")


if __name__ == "__main__":
    main()
