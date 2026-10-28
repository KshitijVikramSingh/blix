# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""ReSTIR GI over the RTGI cache's gather samples, against a plain filtered gather and the cache as displayed
(uv run tools/gi-model/restir_gi.py CAPTURE PATCH_FROM [--seed S]).

CAPTURE an --rtgi-capture directory with three words a sample (SponzaLoop.RtgiCapture.cs): the sample (PI x L_in x the ray's
weight, flags), its hit (xyz; the ray's weight), the hit's normal (xyz; its distance, 1e30 a miss). Its pixels past PATCH_FROM
are a dense patch, every second full-res pixel of a block -- a half-res pixel each.

The capture's frames split in two: the estimators run on the first half's samples, and are scored against the second half's
converged gather (independent of them), its own noise the floor: the first half's converged gather against it.

ReSTIR GI (Ouyang et al. 2021), as its renderers run it:
  candidate  the pixel's sample this frame (where it traces: 1 in D, turning), its RIS weight p^/p_src = the sample's
             luminance (the target p^ = luminance of L_in cos, the source the ray's pdf);
  temporal   last frame's reservoir at the same pixel (the camera still), its M capped at Mmax;
  spatial    k neighbours within r patch pixels, on the pixel's plane and facing (the usual depth / normal test), each
             neighbour's SAMPLE re-evaluated here -- this pixel's cosine to its hit, and the Jacobian of the hit's solid angle
             (the hit's cosine and distance from each); a miss's direction kept. Visibility from this pixel to the neighbour's
             hit is not tested (it cannot be, offline): optimistic at contacts -- the darkest quartile's error says by how much;
  output     the reservoir's estimate (its weight sum over M, in the target's luminance), then a temporal mean over N frames.
The plain gather the same budget: the frame's samples averaged over the same neighbours (no re-evaluation), the same
temporal mean. The cache as displayed: the dump at the capture's end, the same pixels.

Printed per configuration: settled rel |error| against the reference (and its darkest quartile), the frame-to-frame flicker,
the error 8 and 32 frames after a reset (a disocclusion), and the rays a frame at 1920 x 1080 (half-res pixels / D)."""
import json, sys
from pathlib import Path
import numpy as np

LUM = np.array([0.2126, 0.7152, 0.0722])


def main():
    cap, patch0 = Path(sys.argv[1]), int(sys.argv[2])
    seed = int(sys.argv[sys.argv.index("--seed") + 1]) if "--seed" in sys.argv else 1
    rng = np.random.default_rng(seed)
    meta = json.loads((cap / "capture.json").read_text())
    K, F = meta["points"], meta["frames"]
    a = np.frombuffer((cap / "capture.f32").read_bytes(), np.float32).reshape(-1, 4)
    s = a[: 3 * F * K].reshape(F, K, 3, 4)[:, patch0:]
    geo = a[3 * F * K: 3 * F * K + 2 * K].reshape(K, 2, 4)[patch0:]
    P = K - patch0
    e = s[:, :, 0, :3].astype(np.float64)
    flags = s[:, :, 0, 3].astype(int)
    use = ((flags & 4) != 0) & ((flags & 1) != 0)
    hit = s[:, :, 1, :3].astype(np.float64)
    hitN = s[:, :, 2, :3].astype(np.float64)
    hitD = s[:, :, 2, 3].astype(np.float64)
    miss = hitD > 1e29
    pos, nrm = geo[:, 0, :3].astype(np.float64), geo[:, 1, :3].astype(np.float64)
    Le = e @ LUM   # the sample's luminance: its RIS weight

    half = F // 2
    A, B = slice(0, half), slice(half, F)
    def conv(sl):
        u = use[sl]
        return np.where(u.sum(0) > 0, (Le[sl] * u).sum(0) / np.maximum(u.sum(0), 1), np.nan)
    refA, ref = conv(A), conv(B)
    good = np.isfinite(ref) & np.isfinite(refA) & (ref > 1e-4) & (np.abs(nrm).sum(1) > 0)
    dark = good & (ref <= np.quantile(ref[good], 0.25))
    def score(est, sel=None):
        g = good if sel is None else sel
        return np.nanmean(np.abs(est[g] - ref[g])) / ref[g].mean()
    print(f"patch: {P} half-res pixels, {half} frames a half; the reference's own floor (half A converged against half B): "
          f"{score(refA):.3f}, darkest quartile {score(refA, dark):.3f}")
    dumps = sorted((cap / "dump").glob("f*"), key=lambda p: int(p.name[1:]))
    if dumps:
        m = json.loads((dumps[-1] / "meta.json").read_text())
        inc = np.frombuffer((dumps[-1] / "incident.rgba16f").read_bytes(), np.float16).astype(np.float32).reshape(m["height"], m["width"], 4)
        pix = np.array(meta["pixels"][patch0:])
        cache = inc[pix[:, 1], pix[:, 0], :3] @ LUM
        print(f"the cache as displayed ({dumps[-1].name}): rel |error| {score(cache):.3f}, darkest quartile {score(cache, dark):.3f}")

    pix = np.array(meta["pixels"][patch0:])
    gx, gy = (pix[:, 0] - pix[:, 0].min()) // 2, (pix[:, 1] - pix[:, 1].min()) // 2
    W, H = gx.max() + 1, gy.max() + 1
    grid = np.full((H, W), -1); grid[gy, gx] = np.arange(P)
    camera = np.array(meta["camera"])
    dist = np.linalg.norm(pos - camera, axis=1)

    def neighbour_table(r, k):
        """k random neighbours per pixel a frame, within r patch pixels: their indices, -1 off the patch."""
        def draw():
            ang = rng.uniform(0, 2 * np.pi, (k, P)); rad = r * np.sqrt(rng.uniform(0, 1, (k, P)))
            jx = np.round(gx + rad * np.cos(ang)).astype(int); jy = np.round(gy + rad * np.sin(ang)).astype(int)
            ok = (jx >= 0) & (jx < W) & (jy >= 0) & (jy < H)
            j = np.where(ok, grid[np.clip(jy, 0, H - 1), np.clip(jx, 0, W - 1)], -1)
            # The usual geometry test: the neighbour on this pixel's plane (within 1% of the distance) and facing alike.
            jj = np.maximum(j, 0)
            plane = np.abs(((pos[jj] - pos) * nrm).sum(-1)) / np.maximum(dist, 1e-3)
            same = (j >= 0) & (plane < 0.01) & ((nrm[jj] * nrm).sum(-1) > 0.9)
            return np.where(same, j, -1)
        return draw

    def restir(D, r, k, Mmax, N, spatial_reuse=True, frames=320):
        """One run over the first half; returns settled error, dark error, flicker, error at 8 and 32 frames after a reset."""
        # The reservoir: its sample (its hit, the hit's normal, a miss, L luminance the sample's light along it, its colour
        # ignored: luminance is what is scored), its weight sum, M.
        ys_hit = np.zeros((P, 3)); ys_n = np.zeros((P, 3)); ys_miss = np.zeros(P, bool); ys_L = np.zeros(P)
        wsum = np.zeros(P); M = np.zeros(P)
        phase = rng.integers(0, D, P)
        draw = neighbour_table(r, k) if r > 0 else None
        acc = np.full(P, np.nan); cnt = np.zeros(P); prev = None; flick = []; errs = {}
        a0 = rng.integers(0, half - frames)
        def phat(L, h, hm, at):
            """The target at pixel(s) at: L x the cosine there toward the hit (a miss's direction from its own pixel)."""
            d = np.where(hm[:, None], h - pos[at], h - pos[at])
            dn = d / np.maximum(np.linalg.norm(d, axis=1, keepdims=True), 1e-9)
            return L * np.maximum((dn * nrm[at]).sum(1), 0.0)
        for t in range(frames):
            fr = a0 + t
            on = ((phase + t) % D == 0) & use[fr]
            # The candidate: L_in's luminance is the sample over pi x weight; its weight p^/p_src the sample's luminance.
            wr = s[fr, :, 1, 3].astype(np.float64)
            Lc = np.where(on, Le[fr] / np.maximum(np.pi * np.maximum(wr, 1e-9), 1e-12) / np.maximum(
                np.einsum('ij,ij->i', (hit[fr] - pos) / np.maximum(np.linalg.norm(hit[fr] - pos, axis=1, keepdims=True), 1e-9), nrm), 1e-6), 0.0)
            wc = np.where(on, Le[fr], 0.0)
            # Temporal: last frame's reservoir (the same pixel: its sample's target unchanged), M capped.
            scale = np.where(M > Mmax, Mmax / np.maximum(M, 1e-9), 1.0)
            wsum *= scale; M *= scale
            tot = wsum + wc
            take = rng.uniform(0, 1, P) * np.maximum(tot, 1e-30) < wc
            ys_hit = np.where(take[:, None], hit[fr], ys_hit); ys_n = np.where(take[:, None], hitN[fr], ys_n)
            ys_miss = np.where(take, miss[fr], ys_miss); ys_L = np.where(take, Lc, ys_L)
            wsum = tot; M = M + on
            # The reservoir's own target at its sample, and its W = wsum / (M p^).
            ph_self = phat(ys_L, ys_hit, ys_miss, np.arange(P))
            Wres = np.where((M > 0) & (ph_self > 0), wsum / np.maximum(M * ph_self, 1e-30), 0.0)
            if spatial_reuse and draw is not None:
                nb = draw()
                n_wsum = wsum.copy(); n_M = M.copy(); n_hit = ys_hit.copy(); n_n = ys_n.copy(); n_miss = ys_miss.copy(); n_L = ys_L.copy()
                for kk in range(k):
                    j = nb[kk]; ok = j >= 0; jj = np.maximum(j, 0)
                    # The neighbour's sample here: this pixel's target, and the Jacobian of its hit's solid angle.
                    ph_here = phat(ys_L[jj], ys_hit[jj], ys_miss[jj], np.arange(P))
                    dq = ys_hit[jj] - pos; dr = ys_hit[jj] - pos[jj]
                    lq = np.linalg.norm(dq, axis=1); lr = np.linalg.norm(dr, axis=1)
                    cq = np.abs((ys_n[jj] * dq).sum(1)) / np.maximum(lq, 1e-9); cr = np.abs((ys_n[jj] * dr).sum(1)) / np.maximum(lr, 1e-9)
                    J = np.where(ys_miss[jj], 1.0, (cq / np.maximum(cr, 1e-6)) * (lr * lr) / np.maximum(lq * lq, 1e-12))
                    J = np.clip(J, 0.0, 10.0)
                    w = np.where(ok, ph_here * Wres[jj] * M[jj] * J, 0.0)
                    take = rng.uniform(0, 1, P) * np.maximum(n_wsum + w, 1e-30) < w
                    n_hit = np.where(take[:, None], ys_hit[jj], n_hit); n_n = np.where(take[:, None], ys_n[jj], n_n)
                    n_miss = np.where(take, ys_miss[jj], n_miss); n_L = np.where(take, ys_L[jj], n_L)
                    n_wsum = n_wsum + w; n_M = n_M + np.where(ok, M[jj], 0.0)
                ys_hit, ys_n, ys_miss, ys_L, wsum, M = n_hit, n_n, n_miss, n_L, n_wsum, n_M
                ph_self = phat(ys_L, ys_hit, ys_miss, np.arange(P))
                Wres = np.where((M > 0) & (ph_self > 0), wsum / np.maximum(M * ph_self, 1e-30), 0.0)
            # The estimate in luminance: f / p^ x the target's sum over M = the weight sum over M.
            est = np.where(M > 0, wsum / np.maximum(M, 1e-9), np.nan)
            has = np.isfinite(est)
            alpha = np.maximum(1.0 / N, 1.0 / (cnt + 1))
            acc = np.where(has, np.where(np.isfinite(acc), acc + alpha * (est - acc), est), acc)
            cnt += has
            if t in (7, 31): errs[t + 1] = score(acc)
            if t >= frames - 48 and prev is not None:
                flick.append(np.nanmean(np.abs(acc[good] - prev[good])) / ref[good].mean())
            prev = acc.copy()
        return score(acc), score(acc, dark), np.mean(flick), errs[8], errs[32]

    def plain(D, r, N, frames=320):
        """The plain gather: the frame's samples averaged over the neighbourhood's plane- and normal-tested pixels (the same
        test), then the temporal mean."""
        offs = [(dx, dy) for dy in range(-r, r + 1) for dx in range(-r, r + 1) if dx * dx + dy * dy <= r * r]
        nbs, wts = [], []
        for dx, dy in offs:
            jx, jy = gx + dx, gy + dy
            ok = (jx >= 0) & (jx < W) & (jy >= 0) & (jy < H)
            j = np.where(ok, grid[np.clip(jy, 0, H - 1), np.clip(jx, 0, W - 1)], -1); jj = np.maximum(j, 0)
            plane = np.abs(((pos[jj] - pos) * nrm).sum(-1)) / np.maximum(dist, 1e-3)
            same = (j >= 0) & (plane < 0.01) & ((nrm[jj] * nrm).sum(-1) > 0.9)
            nbs.append(jj); wts.append(same.astype(float))
        nbs, wts = np.stack(nbs), np.stack(wts)
        phase = rng.integers(0, D, P); a0 = rng.integers(0, half - frames)
        acc = np.full(P, np.nan); cnt = np.zeros(P); prev = None; flick = []; errs = {}
        for t in range(frames):
            fr = a0 + t
            on = ((phase + t) % D == 0) & use[fr]
            v = np.where(on, Le[fr], 0.0)
            num = (wts * v[nbs] * on[nbs]).sum(0); den = (wts * on[nbs]).sum(0)
            est = np.where(den > 0, num / np.maximum(den, 1e-9), np.nan)
            has = np.isfinite(est)
            alpha = np.maximum(1.0 / N, 1.0 / (cnt + 1))
            acc = np.where(has, np.where(np.isfinite(acc), acc + alpha * (est - acc), est), acc)
            cnt += has
            if t in (7, 31): errs[t + 1] = score(acc)
            if t >= frames - 48 and prev is not None:
                flick.append(np.nanmean(np.abs(acc[good] - prev[good])) / ref[good].mean())
            prev = acc.copy()
        return score(acc), score(acc, dark), np.mean(flick), errs[8], errs[32]

    print("\nsettled error (darkest quartile) | flicker | from a reset: error at 8, 32 frames | rays a frame at 1080p")
    for D in (1, 4, 16):
        rays = 960 * 540 / D
        print(f"D {D} (1 in {D} half-res pixels a frame, {rays / 1e3:.0f} K gather rays + as many shadow rays):")
        for (r, k, Mmax, N) in ((0, 0, 32, 1), (4, 5, 32, 1), (4, 5, 32, 8), (8, 5, 32, 8), (8, 8, 64, 8)):
            se, sd, fl, e8, e32 = restir(D, r, k, Mmax, N)
            print(f"  ReSTIR r {r} k {k} Mmax {Mmax:2d} N {N}: {se:.3f} ({sd:.3f}) | {100 * fl:5.2f}% | {e8:.3f}, {e32:.3f}")
        for (r, N) in ((4, 8), (8, 8), (8, 32)):
            se, sd, fl, e8, e32 = plain(D, r, N)
            print(f"  plain   r {r}           N {N:2d}: {se:.3f} ({sd:.3f}) | {100 * fl:5.2f}% | {e8:.3f}, {e32:.3f}")


if __name__ == "__main__":
    main()
