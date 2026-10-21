# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""A contact's gather split at a reach R: its own short rays for what lies near, its coarse cell's rays for what lies
beyond, by direction (uv run tools/gi-model/contact_far.py [--seeds S]).

The ray capture (tools/gi-model/ray_capture.py, 2026-10-22): the fine round's contact texels took ~1,300 rays each in
Cornell and still carried 5x the open texels' noise -- 80-90% of their per-ray variance from hits beyond 1 m and misses
(the sunlit floor across the room, ray by ray), 5-13% from the rays under 25 cm. Each fine texel re-estimated a far field
its neighbours and its coarse cell had already gathered.

The scene: a room 4 x 3 x 4 m, a 1 m skylight in the ceiling (a miss: the sky, radiance 1), the sun through it on a
patch of the floor (irradiance ES, albedo RHO), every other surface a smooth bounce radiance. Receivers on the wall
x = 0, facing into the room, up a line from the floor (the crease) at heights t from 1 cm; coarse cells CELL tall along
it, each gathering at its centre. Radiance at a hit is exact here: what is modelled is the sampling, not the cook's lookups. Irradiance E = pi x
the mean radiance of cosine rays. Estimators, per fine texel:

  full N        today's fine gather: N cosine rays over the whole hemisphere
  split N R     N cosine rays cut at R: a hit within R its radiance; past R, its direction's bin in the coarse cell's
                record -- the mean radiance of the cell's M rays that went past R into that bin (bins over the cell's
                hemisphere, concentric); a bin the cell's rays did not know (under K rays): the ray traced on, its own
  split+blend   the same, the record blended linearly between the two nearest cells' centres (no step at a cell's edge)

Reported per estimator at each t: the bias (mean over seeds, against a 1 M-ray truth), the noise (the spread over
seeds) and, per cell, the blotch -- the part of the error shared by a cell's texels (the record's own noise and
parallax, which a filter cannot tell from light) -- each over the truth, in %.
"""
import sys
import numpy as np

ROOM = np.array([4.0, 3.0, 4.0])
SKY = (1.5, 2.5, 1.5, 2.5)        # the skylight on the ceiling: x0 x1 z0 z1
RHO, ES, LSKY = 0.5, 10.0, 1.0
SEEDS = int(sys.argv[sys.argv.index("--seeds") + 1]) if "--seeds" in sys.argv else 24


def sun_patch(sun):
    """The skylight's image on the floor along the sun."""
    s = sun / sun[1]
    x0, x1, z0, z1 = SKY
    return (x0 - 3.0 * s[0], x1 - 3.0 * s[0], z0 - 3.0 * s[2], z1 - 3.0 * s[2])


def bounce_radiance(p):
    """Every surface's radiance but the sunlit patch's: smooth, brighter toward the patch's side of the room."""
    return 0.08 * (1.0 + 0.4 * np.cos(p[:, 0] * 0.9) + 0.25 * np.sin(p[:, 2] * 1.3 + 0.4 * p[:, 1]))


def trace(o, d, patch, tmax=np.inf):
    """Rays from inside the room: distance to the first wall (or tmax), and the radiance there (the sky for the
    skylight). Returns (t, L, stopped) -- stopped: cut at tmax before any hit."""
    with np.errstate(divide="ignore", invalid="ignore"):
        tx = np.where(d[:, 0] > 0, (ROOM[0] - o[:, 0]) / d[:, 0], -o[:, 0] / d[:, 0])
        ty = np.where(d[:, 1] > 0, (ROOM[1] - o[:, 1]) / d[:, 1], -o[:, 1] / d[:, 1])
        tz = np.where(d[:, 2] > 0, (ROOM[2] - o[:, 2]) / d[:, 2], -o[:, 2] / d[:, 2])
    tx[~np.isfinite(tx)] = np.inf; ty[~np.isfinite(ty)] = np.inf; tz[~np.isfinite(tz)] = np.inf
    t = np.minimum(np.minimum(tx, ty), tz)
    p = o + d * t[:, None]
    L = bounce_radiance(p)
    floor = (ty <= t) & (d[:, 1] < 0)
    ceil = (ty <= t) & (d[:, 1] > 0)
    x0, x1, z0, z1 = patch
    lit = floor & (p[:, 0] > x0) & (p[:, 0] < x1) & (p[:, 2] > z0) & (p[:, 2] < z1)
    L = np.where(lit, RHO * ES / np.pi, L)
    sx0, sx1, sz0, sz1 = SKY
    sky = ceil & (p[:, 0] > sx0) & (p[:, 0] < sx1) & (p[:, 2] > sz0) & (p[:, 2] < sz1)
    L = np.where(sky, LSKY, L)
    stopped = t > tmax
    return np.minimum(t, tmax), L, stopped


def cosine_dirs(rng, n):
    """Cosine rays about +x (the wall's normal); also the unit-disc point each came from (the bins' map)."""
    u1, u2 = rng.random(n), rng.random(n)
    r, phi = np.sqrt(u1), 2 * np.pi * u2
    x, z = r * np.cos(phi), r * np.sin(phi)
    return np.stack([np.sqrt(np.maximum(1 - u1, 0)), x, z], -1), np.stack([x, z], -1)


def disc_bin(xz, side):
    """Bins over the hemisphere: the unit disc (concentric's square, inverted) cut side x side -- equal solid-angle-cosine
    bins."""
    x, z = xz[:, 0], xz[:, 1]
    r = np.sqrt(x * x + z * z)
    phi = np.arctan2(z, x)
    # Inverse concentric map (Shirley-Chiu): disc -> square.
    phi = np.where(phi < -np.pi / 4, phi + 2 * np.pi, phi)
    a = np.where(phi < np.pi / 4, r, np.where(phi < 3 * np.pi / 4, r * (np.pi / 2 - phi) / (np.pi / 4),
                 np.where(phi < 5 * np.pi / 4, -r, r * (phi - 3 * np.pi / 2) / (np.pi / 4))))
    b = np.where(phi < np.pi / 4, r * phi / (np.pi / 4), np.where(phi < 3 * np.pi / 4, r,
                 np.where(phi < 5 * np.pi / 4, r * (np.pi - phi) / (np.pi / 4), -r)))
    i = np.clip(((a + 1) / 2 * side).astype(int), 0, side - 1)
    j = np.clip(((b + 1) / 2 * side).astype(int), 0, side - 1)
    return i * side + j


def record(rng, origins, patch, R, side):
    """A record: per bin, the summed radiance and count of the rays that went past R, from each (point, rays) origin."""
    s = np.zeros(side * side); n = np.zeros(side * side)
    for centre, m in origins:
        d, xz = cosine_dirs(rng, m)
        o = np.repeat(centre[None], m, 0)
        t, L, _ = trace(o, d, patch)
        far = t >= R
        b = disc_bin(xz, side)
        s += np.bincount(b[far], L[far], minlength=side * side)
        n += np.bincount(b[far], minlength=side * side)
    return s, n


def split(rng, x, z, patch, n, R, side, recs, known):
    """The split estimate at the floor point (x, z): recs a list of (weight, (sum, count)) blended."""
    d, xz = cosine_dirs(rng, n)
    o = np.repeat(np.array([[0.0, x, z]]), n, 0)
    t, L, stopped = trace(o, d, patch, tmax=R)
    b = disc_bin(xz, side)
    s = sum(w * r[0] for w, r in recs)
    c = sum(w * r[1] for w, r in recs)
    ok = c >= known
    binL = np.where(ok, s / np.maximum(c, 1e-9), 0.0)
    far = stopped
    use = far & ok[b]
    Lr = np.where(use, binL[b], L)
    # Unknown bins: the ray traced on to its end.
    on = far & ~ok[b]
    if on.any():
        _, L2, _ = trace(o[on], d[on], patch)
        Lr[on] = L2
    return np.pi * Lr.mean(), (np.count_nonzero(on) / n)


def mis(rng, x, z, patch, n, share):
    """N rays, a share of them at points on the sunlit patch (area-sampled), the rest cosine; each weighed by the balance
    heuristic over the two. The room is convex: a light sample's visibility is its facing alone."""
    o1 = np.array([0.0, x, z])
    na = int(round(n * share)); nc = n - na
    x0, x1, z0, z1 = patch
    x0, x1, z0, z1 = max(x0, 0.0), min(x1, ROOM[0]), max(z0, 0.0), min(z1, ROOM[2])
    area = (x1 - x0) * (z1 - z0)
    def pdfs(d):
        pc = np.maximum(d[:, 0], 0) / np.pi
        # The patch's solid-angle pdf along d, where d meets it.
        with np.errstate(divide="ignore", invalid="ignore"):
            tf = -o1[1] / d[:, 1]
        p = o1 + d * tf[:, None]
        on = (d[:, 1] < 0) & (p[:, 0] > x0) & (p[:, 0] < x1) & (p[:, 2] > z0) & (p[:, 2] < z1)
        pa = np.where(on, tf * tf / (area * np.maximum(-d[:, 1], 1e-9)), 0.0)
        return pc, pa
    d1, _ = cosine_dirs(rng, nc)
    q = np.stack([x0 + (x1 - x0) * rng.random(na), np.zeros(na), z0 + (z1 - z0) * rng.random(na)], -1)
    v = q - o1
    d2 = v / np.linalg.norm(v, axis=1, keepdims=True)
    d = np.concatenate([d1, d2])
    o = np.repeat(o1[None], n, 0)
    _, L, _ = trace(o, d, patch)
    pc, pa = pdfs(d)
    f = L * np.maximum(d[:, 0], 0)
    mix = (nc * pc + na * pa) / n
    return (f / np.maximum(mix, 1e-12)).mean()


SPACING, LIGHT_K = 0.25, 16


def light_list(guide, patch, with_sun):
    """The guide patch's light list as texel_live_guides.comp builds it: the floor's SPACING patches ranked by coupling
    (a point form factor from the guide) x outgoing luminance -- with_sun: the patch's direct sun counted (its sunlit
    share x RHO ES / pi, plus the bounce); without (today's nodeOut): the bounce alone. The LIGHT_K best, by share."""
    nx, nz = int(ROOM[0] / SPACING), int(ROOM[2] / SPACING)
    cx, cz = np.meshgrid((np.arange(nx) + 0.5) * SPACING, (np.arange(nz) + 0.5) * SPACING, indexing="ij")
    cx, cz = cx.ravel(), cz.ravel()
    x0, x1, z0, z1 = patch
    ox = np.clip(np.minimum(cx + SPACING / 2, x1) - np.maximum(cx - SPACING / 2, x0), 0, None)
    oz = np.clip(np.minimum(cz + SPACING / 2, z1) - np.maximum(cz - SPACING / 2, z0), 0, None)
    lit = ox * oz / SPACING ** 2
    c = np.stack([cx, np.zeros_like(cx), cz], -1)
    out = bounce_radiance(c) + (lit * RHO * ES / np.pi if with_sun else 0.0)
    v = c - guide
    d2 = (v * v).sum(1)
    ff = np.maximum(v[:, 0], 0) / np.sqrt(d2) * np.maximum(-v[:, 1], 0) / np.sqrt(d2) * SPACING ** 2 / (np.pi * d2)
    score = ff * out
    top = np.argsort(-score)[:LIGHT_K]
    w = score[top] / score[top].sum()
    return c[top], w


def sunlet_list(guide, patch, spacing):
    """The guide patch's sunlets (the solve's sun points, SPACING apart over the floor), the sunlit ones (exact: the
    sunlet's own shadow ray), weighed by their coupling to the guide x their outgoing sun."""
    nx, nz = int(ROOM[0] / spacing), int(ROOM[2] / spacing)
    cx, cz = np.meshgrid((np.arange(nx) + 0.5) * spacing, (np.arange(nz) + 0.5) * spacing, indexing="ij")
    cx, cz = cx.ravel(), cz.ravel()
    x0, x1, z0, z1 = patch
    lit = (cx > x0) & (cx < x1) & (cz > z0) & (cz < z1)
    c = np.stack([cx[lit], np.zeros(lit.sum()), cz[lit]], -1)
    v = c - guide
    d2 = (v * v).sum(1)
    score = np.maximum(v[:, 0], 0) / np.sqrt(d2) * np.maximum(-v[:, 1], 0) / np.sqrt(d2) / d2
    return c, score / score.sum()


def lists(rng, x, z, patch, n, guide, with_sun, share=0.25, sunlets=0.0):
    """Today's mix: (1 - share) cosine rays (the guide's quarter modelled as cosine), share at the light list's disks
    (radius SPACING / sqrt pi, a patch's area, on the floor), every ray weighed by the mixture's density."""
    o1 = np.array([0.0, x, z])
    if sunlets > 0:
        centres, w = sunlet_list(guide, patch, sunlets)
        radius = sunlets / np.sqrt(np.pi)
    else:
        centres, w = light_list(guide, patch, with_sun)
        radius = SPACING / np.sqrt(np.pi)
    na = int(round(n * share)); nc = n - na
    d1, _ = cosine_dirs(rng, nc)
    pick = rng.choice(len(w), na, p=w)
    r, phi = radius * np.sqrt(rng.random(na)), 2 * np.pi * rng.random(na)
    q = centres[pick] + np.stack([r * np.cos(phi), np.zeros(na), r * np.sin(phi)], -1)
    v = q - o1
    d2 = v / np.linalg.norm(v, axis=1, keepdims=True)
    d = np.concatenate([d1, d2])
    pc = np.maximum(d[:, 0], 0) / np.pi
    with np.errstate(divide="ignore", invalid="ignore"):
        tf = np.where(d[:, 1] < 0, -o1[1] / d[:, 1], np.inf)
    p = o1 + d * np.where(np.isfinite(tf), tf, 0)[:, None]
    pl = np.zeros(n)
    for k in range(len(w)):
        inside = np.isfinite(tf) & (((p[:, 0] - centres[k, 0]) ** 2 + (p[:, 2] - centres[k, 2]) ** 2) < radius ** 2)
        pl += np.where(inside, w[k] * tf * tf / (np.pi * radius ** 2 * np.maximum(-d[:, 1], 1e-9)), 0.0)
    o = np.repeat(o1[None], n, 0)
    _, L, _ = trace(o, d, patch)
    f = L * np.maximum(d[:, 0], 0)
    mix = (nc * pc + na * pl) / n
    return (f / np.maximum(mix, 1e-12)).mean()


def full(rng, x, z, patch, n):
    d, _ = cosine_dirs(rng, n)
    o = np.repeat(np.array([[0.0, x, z]]), n, 0)
    _, L, _ = trace(o, d, patch)
    return np.pi * L.mean()


def run(name, sun, z0, cell, M, side, known, estimators, ts):
    patch = sun_patch(sun)
    print(f"\n== {name}: sun patch x {patch[0]:.2f}..{patch[1]:.2f}, z {patch[2]:.2f}..{patch[3]:.2f}; receivers at z {z0}; cells {cell} m, the record {M} rays, {side}x{side} bins, known at {known}")
    truth_rng = np.random.default_rng(12345)
    truth = np.array([full(truth_rng, t, z0, patch, 1_000_000) for t in ts])
    cells = np.floor(ts / cell).astype(int)
    centres = (np.arange(cells.max() + 2) + 0.5) * cell
    print(f"   t (cm):       " + " ".join(f"{100 * t:6.1f}" for t in ts))
    print(f"   truth:        " + " ".join(f"{e:6.3f}" for e in truth))
    fine = np.arange(0.01, cells.max() * cell + 2 * cell, 0.02)   # the fine texels' centres (2 cm apart)
    for label, kind, n, R, blend in estimators:
        est = np.zeros((SEEDS, len(ts)))
        traced_on = []
        for s in range(SEEDS):
            rng = np.random.default_rng(1000 + s)
            recs = {}
            for i, t in enumerate(ts):
                if kind == "full":
                    est[s, i] = full(rng, t, z0, patch, n)
                    continue
                if kind == "mis":
                    est[s, i] = mis(rng, t, z0, patch, n, R)
                    continue
                if kind in ("lists", "lists+sun"):
                    est[s, i] = lists(rng, t, z0, patch, n, np.array([0.0, centres[cells[i]], z0]), kind == "lists+sun")
                    continue
                if kind == "sunlets":
                    est[s, i] = lists(rng, t, z0, patch, n, np.array([0.0, centres[cells[i]], z0]), True, share=R, sunlets=0.0625)
                    continue
                def rec(ci):
                    # cell: the coarse cell's M rays; pool: and its fine texels' own far rays (each n, the gather's);
                    # pool3: the cell's and its two neighbours' M rays.
                    if ci not in recs:
                        origins = [(np.array([0.0, centres[ci], z0]), M)]
                        if kind == "pool":
                            origins += [(np.array([0.0, f, z0]), n) for f in fine if int(f // cell) == ci]
                        if kind == "pool3":
                            origins += [(np.array([0.0, centres[cj], z0]), M) for cj in (ci - 1, ci + 1) if cj >= 0]
                        recs[ci] = record(rng, origins, patch, R, side)
                    return recs[ci]
                if blend:
                    f = t / cell - 0.5
                    c0 = int(np.floor(f)); w1 = f - c0
                    if c0 < 0: c0, w1 = 0, 0.0
                    use = [(1 - w1, rec(c0)), (w1, rec(c0 + 1))]
                else:
                    use = [(1.0, rec(cells[i]))]
                est[s, i], on = split(rng, t, z0, patch, n, R, side, use, known)
                traced_on.append(on)
        err = (est - truth) / truth
        bias = err.mean(0)
        noise = err.std(0)
        # Blotch: per seed, each cell's mean error (its texels' shared part), its spread over seeds.
        cell_err = np.stack([np.array([err[:, cells == c].mean(1) for c in np.unique(cells)]).T])[0]
        blotch = cell_err.std(0).mean()
        extra = f"  rays traced on {100 * np.mean(traced_on):.0f}%" if traced_on else ""
        print(f"   {label:13s} bias " + " ".join(f"{100 * b:+6.1f}" for b in bias))
        print(f"   {'':13s} noise" + " ".join(f"{100 * v:6.2f}" for v in noise) + f"   | mean noise {100 * noise.mean():.2f}%, |bias| {100 * np.abs(bias).mean():.2f}%, blotch {100 * blotch:.2f}%{extra}")


def main():
    ts = np.array([0.01, 0.02, 0.04, 0.06, 0.08, 0.12, 0.16, 0.20, 0.24, 0.30, 0.40, 0.50])
    estimators = [
        ("full 1300", "full", 1300, None, False),
        ("full 384", "full", 384, None, False),
        ("cell 384 R25", "split", 384, 0.25, False),
        ("pool 128 R25", "pool", 128, 0.25, False),
        ("pool 384 R25", "pool", 384, 0.25, False),
        ("pool 128 R50", "pool", 128, 0.50, False),
        ("pblend 128 R25", "pool", 128, 0.25, True),
        ("pool3 128 R25", "pool3", 128, 0.25, False),
        ("mis 384 1/4", "mis", 384, 0.25, False),
        ("mis 128 1/4", "mis", 128, 0.25, False),
        ("mis 128 1/2", "mis", 128, 0.5, False),
        ("lists 1300", "lists", 1300, None, False),
        ("lists 384", "lists", 384, None, False),
        ("lists+sun 384", "lists+sun", 384, None, False),
        ("lists+sun 128", "lists+sun", 128, None, False),
        ("sunlets 384 1/4", "sunlets", 384, 0.25, False),
        ("sunlets 384 1/2", "sunlets", 384, 0.5, False),
        ("sunlets 128 1/4", "sunlets", 128, 0.25, False),
    ]
    only = sys.argv[sys.argv.index("--scene") + 1] if "--scene" in sys.argv else None
    scenes = [
        # Cornell-like: the sun's patch a metre or so from the crease, in line with the receivers.
        ("near", "a near sunlit patch", np.array([0.3, 1.0, 0.0]), 2.0, 8),
        ("near16", "a near sunlit patch, 16x16 bins", np.array([0.3, 1.0, 0.0]), 2.0, 16),
        # The patch across the room, off the receivers' line: a small bright bin from the crease.
        ("far", "a far sunlit patch, off the line", np.array([-0.4, 1.0, 0.5]), 0.6, 8),
    ]
    for key, name, sun, z0, side in scenes:
        if only is None or only == key: run(name, sun, z0, 0.25, 512, side, 3, estimators, ts)


if __name__ == "__main__":
    main()
