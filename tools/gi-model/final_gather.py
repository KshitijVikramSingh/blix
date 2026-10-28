# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""A per-pixel final gather over the RTGI cache, against the cache as displayed (uv run tools/gi-model/final_gather.py CAPTURE
TRUTH_POINTS [--patch-from K] [--seed S]).

CAPTURE an --rtgi-capture directory (SponzaLoop.RtgiCapture.cs): per frame and pinned pixel one gather sample -- a cosine (or
guided) ray from the pixel's surface, its hit lit by the sun traced there and the cache there, PI x L_in x the ray's weight
(rtgi_train.comp's pointSample) -- the points' positions and normals, and the run's --rtgi-dump of the cache's field (the
light the lit pass reads today). TRUTH_POINTS "x y ref" lines (the truth's path-traced points, 16,384 paths each), the first
lines of the capture's pixels; the pixels past them (from --patch-from, default the truth's count) a dense patch, every second
full-res pixel of a block.

Printed:
  1 at the truth points: the gather converged (all frames' samples), the cache as displayed (the dump nearest the capture's
    end), each against the truth (energy, rel |error| as the gate's, the darkest quartile's own) -- the floor each approach
    sits on;
  2 convergence: the gather's error with k frames of samples (1 .. 256), the time a pixel takes to be right after a change
    (a disocclusion, a sun turn), sample-for-sample;
  3 denoised at a ray budget, on the patch: of its pixels a rotating 1/D trace each frame; a spatial filter over the frame's
    samples (a radius in patch pixels, weighed by plane distance and normal), then temporal accumulation (an exponential mean
    over N frames); error against the patch's own converged gather, and flicker (frame-to-frame |change| over the converged
    mean), each by D and N; the cache's own error there against the same reference;
  4 rays a frame at 1920 x 1080 for each D, and the frame cost at S0's measured ~80 M rays/s on Crytek (a gather ray and a
    shadow ray at its hit each), 3-5x that in San Miguel / Intel Sponza."""
import json, sys
from pathlib import Path
import numpy as np

LUM = np.array([0.2126, 0.7152, 0.0722], np.float32)


def load(cap):
    meta = json.loads((cap / "capture.json").read_text())
    k, f = meta["points"], meta["frames"]
    a = np.frombuffer((cap / "capture.f32").read_bytes(), np.float32).reshape(-1, 4)
    s = a[: f * k].reshape(f, k, 4)
    geo = a[f * k: f * k + 2 * k].reshape(k, 2, 4)
    return meta, s, geo


def field(dump):
    m = json.loads((dump / "meta.json").read_text())
    w, h = m["width"], m["height"]
    inc = np.frombuffer((dump / "incident.rgba16f").read_bytes(), np.float16).astype(np.float32).reshape(h, w, 4)
    return m, inc[..., :3] @ LUM


def err(f, r):
    ok = np.isfinite(f) & (r > 0)
    f, r = f[ok], r[ok]
    q = r <= np.quantile(r, 0.25)
    return f.sum() / r.sum(), np.abs(f - r).mean() / r.mean(), np.abs(f[q] - r[q]).mean() / r[q].mean()


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    cap, truth = Path(args[0]), Path(args[1])
    seed = int(sys.argv[sys.argv.index("--seed") + 1]) if "--seed" in sys.argv else 1
    rng = np.random.default_rng(seed)
    meta, s, geo = load(cap)
    F, K = s.shape[:2]
    tr = np.array([[float(v) for v in l.split()] for l in truth.read_text().splitlines() if l.strip()])
    T = len(tr)
    patch0 = int(sys.argv[sys.argv.index("--patch-from") + 1]) if "--patch-from" in sys.argv else T
    flags = s[..., 3].astype(int)
    live, counted, sky = (flags & 4) != 0, (flags & 1) != 0, (flags & 2) != 0
    L = s[..., :3] @ LUM
    use = live & counted
    print(f"capture: {K} pixels x {F} frames; samples live {100 * live.mean():.1f}%, of those counted {100 * use.sum() / max(live.sum(), 1):.1f}%, "
          f"sky {100 * (sky & live).sum() / max(live.sum(), 1):.1f}%")

    # 1. The floors at the truth points.
    ref = tr[:, 2]
    Lt, ut = L[:, :T], use[:, :T]
    conv = np.where(ut.sum(0) > 0, (Lt * ut).sum(0) / np.maximum(ut.sum(0), 1), np.nan)
    e, r, d = err(conv, ref)
    print(f"\n1 at the truth points ({T}):")
    print(f"  gather, converged ({F} frames):  energy {e:.3f}, rel |error| {r:.3f}, darkest quartile {d:.3f}")
    dumps = sorted((cap / "dump").glob("f*"), key=lambda p: int(p.name[1:]))
    for dump in dumps:
        m, fl = field(dump)
        px = np.clip(tr[:, 0].astype(int), 0, m["width"] - 1); py = np.clip(tr[:, 1].astype(int), 0, m["height"] - 1)
        e, r, d = err(fl[py, px], ref)
        print(f"  cache as displayed ({dump.name}):   energy {e:.3f}, rel |error| {r:.3f}, darkest quartile {d:.3f}")

    # 2. Convergence: k frames' samples, from a random start, against the truth; the gather's own noise at k.
    print("\n2 the gather's error with k frames of samples at a pixel (the truth points; mean over 8 starts):")
    for k in (1, 2, 4, 8, 16, 32, 64, 128, 256):
        rs = []
        for _ in range(8):
            a = rng.integers(0, F - k + 1)
            w = ut[a:a + k]
            est = np.where(w.sum(0) > 0, (Lt[a:a + k] * w).sum(0) / np.maximum(w.sum(0), 1), np.nan)
            rs.append(err(est, ref)[1])
        print(f"  k {k:4d} ({k / 120:5.2f} s at 120 fps): rel |error| {np.mean(rs):.3f}")

    # 3. Denoised at a budget, on the patch.
    P = K - patch0
    if P <= 0:
        return
    pix = np.array(meta["pixels"][patch0:])
    gx, gy = (pix[:, 0] - pix[:, 0].min()) // 2, (pix[:, 1] - pix[:, 1].min()) // 2
    W, H = gx.max() + 1, gy.max() + 1
    idx = np.full((H, W), -1); idx[gy, gx] = np.arange(P)
    Lp, up = L[:, patch0:], use[:, patch0:]
    pos, nrm = geo[patch0:, 0, :3], geo[patch0:, 1, :3]
    refp = np.where(up.sum(0) > 0, (Lp * up).sum(0) / np.maximum(up.sum(0), 1), np.nan)
    good = np.isfinite(refp) & (refp > 1e-4) & (np.abs(nrm).sum(1) > 0)
    camera = np.array(meta["camera"])
    dist = np.linalg.norm(pos - camera, axis=1)
    print(f"\n3 denoised on the patch ({W} x {H} points, every second pixel; its {good.sum()} surface points' converged gather the "
          f"reference, {F} samples each):")
    for dump in dumps[-1:]:
        m, fl = field(dump)
        cache = fl[pix[:, 1], pix[:, 0]]
        print(f"  the cache as displayed ({dump.name}) against it: energy {cache[good].sum() / refp[good].sum():.3f}, rel |error| "
              f"{np.abs(cache[good] - refp[good]).mean() / refp[good].mean():.3f}")

    # Neighbour weights within radius R (patch points, 2 px each): plane distance (relative to distance) and normals.
    def neighbours(R):
        offs = [(dx, dy) for dy in range(-R, R + 1) for dx in range(-R, R + 1) if dx * dx + dy * dy <= R * R]
        nb, wt = [], []
        for dx, dy in offs:
            jx, jy = gx + dx, gy + dy
            ok = (jx >= 0) & (jx < W) & (jy >= 0) & (jy < H)
            j = np.where(ok, idx[np.clip(jy, 0, H - 1), np.clip(jx, 0, W - 1)], -1)
            ok &= j >= 0
            jj = np.where(ok, j, 0)
            plane = np.abs(((pos[jj] - pos) * nrm).sum(1)) / np.maximum(dist, 1e-3)
            ndot = (nrm[jj] * nrm).sum(1)
            w = np.where(ok, np.exp(-(plane / 0.01) ** 2) * np.clip((ndot - 0.8) / 0.2, 0, 1) ** 2 * np.exp(-(dx * dx + dy * dy) / (0.5 * R * R + 1e-6)), 0)
            nb.append(jj); wt.append(w)
        return np.stack(nb), np.stack(wt)

    print("  D: 1 in D of the patch's pixels trace a frame (rotating); R the spatial radius (patch points, 2 px each); N the "
          "temporal frames. Error after 256 frames settled, flicker its frame-to-frame |change|, and after a reset (the error "
          "at 8 and 32 frames from nothing):")
    for D in (4, 16, 64):
        for R in ((0, 2, 4) if D == 4 else (2, 4, 6)):
            nb, wt = neighbours(R) if R > 0 else (np.arange(P)[None], np.ones((1, P)))
            for N in (8, 32):
                acc = None; prev = None; errs = {}; flick = []
                phase = rng.integers(0, D, P)
                a0 = rng.integers(0, F - 300)
                for t in range(300):
                    fr = a0 + t
                    on = ((phase + t) % D == 0) & up[fr]
                    val = np.where(on, Lp[fr], 0.0)
                    num = (wt * val[nb] * on[nb]).sum(0); den = (wt * on[nb]).sum(0)
                    est = np.where(den > 1e-6, num / np.maximum(den, 1e-6), np.nan)
                    if acc is None: acc = est.copy(); cnt = np.isfinite(est).astype(float)
                    else:
                        a = np.maximum(1.0 / N, 1.0 / (cnt + 1))
                        has = np.isfinite(est)
                        acc = np.where(has, np.where(np.isfinite(acc), acc + a * (est - acc), est), acc)
                        cnt = cnt + has
                    if t in (7, 31): errs[t + 1] = np.nanmean(np.abs(acc[good] - refp[good])) / refp[good].mean()
                    if t >= 256 and prev is not None: flick.append(np.nanmean(np.abs(acc[good] - prev[good])) / refp[good].mean())
                    prev = acc.copy()
                settled = np.nanmean(np.abs(acc[good] - refp[good])) / refp[good].mean()
                print(f"    D {D:2d} R {R} N {N:2d}: settled rel |error| {settled:.3f}, flicker {np.mean(flick) * 100:.2f}%; "
                      f"from a reset {errs.get(8, np.nan):.3f} at 8 frames, {errs.get(32, np.nan):.3f} at 32")

    print("\n4 rays a frame at 1920 x 1080 (a gather ray and a shadow ray at its hit each), at ~80 M rays/s (Crytek, S0):")
    for D in (4, 16, 64):
        rays = 1920 * 1080 / D
        print(f"  D {D:2d}: {rays / 1e3:6.0f} K gather + as many shadow rays: ~{2 * rays / 80e6 * 1e3:.1f} ms Crytek, "
              f"~{3 * 2 * rays / 80e6 * 1e3:.1f}-{5 * 2 * rays / 80e6 * 1e3:.1f} ms San Miguel / Intel Sponza")


if __name__ == "__main__":
    main()
