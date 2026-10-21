# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""Read a view dump (the texel picker's "Dump view", --view-dump; SponzaLoop.ViewDump.cs) over a rectangle of the frame:
uv run tools/gi-model/view_dump.py DUMP [x0 y0 x1 y1 (frame fractions, default the whole frame)] [--maps OUT]

Each pixel's lightmap texel (the pre-pass's atlas texel, its nearest record: the incident pass reads the four about it),
the GI's incident light there against the per-pixel path trace (--gi-reference, G; a difference counted only past twice
the trace's own standard error), and the texels' rays, who wrote them and their light. --maps writes PPM images of the
rectangle: the GI, the trace, the error (grey within the trace's noise, red brighter, blue darker, full at 25%), the
texels' rays (blue 128 .. red 1,024+, grey a copy) and their provenance -- the patterns as seen, side by side."""
import json, sys
from pathlib import Path
import numpy as np


def rgb9e5(w):
    w = w.astype(np.uint64)
    scale = np.exp2((w >> 27).astype(np.int64) - 15 - 9)
    return np.stack([(w & 511) * scale, ((w >> 9) & 511) * scale, ((w >> 18) & 511) * scale], -1)


def lum(c):
    return c[..., 0] * 0.2126 + c[..., 1] * 0.7152 + c[..., 2] * 0.0722


def ppm(path, rgb):
    rgb = np.clip(rgb * 255, 0, 255).astype(np.uint8)
    h, w, _ = rgb.shape
    Path(path).write_bytes(f"P6 {w} {h} 255\n".encode() + rgb.tobytes())


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    maps = sys.argv[sys.argv.index("--maps") + 1] if "--maps" in sys.argv else None
    if maps in args: args.remove(maps)
    d = Path(args[0])
    meta = json.loads((d / "meta.json").read_text())
    f = meta["files"]
    W, H = f["incident.rgba16f"]["width"], f["incident.rgba16f"]["height"]
    rect = [float(v) for v in args[1:5]] if len(args) >= 5 else [0, 0, 1, 1]
    x0, y0, x1, y1 = int(rect[0] * W), int(rect[1] * H), int(rect[2] * W), int(rect[3] * H)
    inc = np.frombuffer((d / "incident.rgba16f").read_bytes(), np.float16).reshape(H, W, 4).astype(np.float32)
    nrm = np.frombuffer((d / "normal.rgba16f").read_bytes(), np.float16).reshape(H, W, 4).astype(np.float32)[..., :3]
    field = lum(inc[..., :3])
    n = meta["texelCount"]
    tex = np.frombuffer((d / "texels.f32x8").read_bytes(), np.float32).reshape(n, 8)
    resolved = lum(rgb9e5(np.frombuffer((d / "resolved.u32x2").read_bytes(), np.uint32).reshape(n, 2)[:, 0])) if (d / "resolved.u32x2").exists() else np.zeros(n)
    prov = np.frombuffer((d / "provenance.u8").read_bytes(), np.uint8)[:n] & 15 if (d / "provenance.u8").exists() else np.zeros(n, np.uint8)
    rays = np.frombuffer((d / "rays.u16").read_bytes(), np.uint16)[:n].astype(np.int64) * 32 if (d / "rays.u16").exists() else np.zeros(n, np.int64)
    # The pixel's texel.
    ids = np.full((H, W), -1, np.int64)
    if (d / "lightmap_texel.r32u").exists() and (d / "lightmap_index.r32u").exists():
        lt = np.frombuffer((d / "lightmap_texel.r32u").read_bytes(), np.uint32).reshape(H, W)
        aw, ah = f["lightmap_index.r32u"]["width"], f["lightmap_index.r32u"]["height"]
        index = np.frombuffer((d / "lightmap_index.r32u").read_bytes(), np.uint32).reshape(ah, aw)
        tx = np.clip(((lt & 0xFFFF) / 8.0).astype(np.int64), 0, aw - 1)
        ty = np.clip(((lt >> 16) / 8.0).astype(np.int64), 0, ah - 1)
        rec = index[ty, tx].astype(np.int64)
        ok = (lt != 0) & (rec != 0xFFFFFFFF)
        ids[ok] = rec[ok]
        # A pair's other side where the pixel faces the record's back (texelsAroundLightmap).
        pair = ok & (tex[np.maximum(ids, 0), 3] < 0) & ((tex[np.maximum(ids, 0), 4:7] * nrm).sum(-1) < 0)
        ids[pair] += 1
    # The path trace (half resolution: each pixel its 2x2 block's).
    ref = np.full((H, W), np.nan); err = np.full((H, W), np.nan)
    if (d / "reference.f32x4").exists():
        rw, rh = meta["reference"]["width"], meta["reference"]["height"]
        acc = np.frombuffer((d / "reference.f32x4").read_bytes(), np.float32).reshape(rh, rw, 4)
        mom = np.frombuffer((d / "reference_moments.f32").read_bytes(), np.float32).reshape(rh, rw)
        cnt = np.maximum(acc[..., 3], 1e-9)
        mean = lum(acc[..., :3] / cnt[..., None])
        se = np.sqrt(np.maximum(mom / cnt - mean * mean, 0) / cnt)
        yy, xx = np.mgrid[0:H, 0:W]
        ref = mean[np.minimum(yy * rh // H, rh - 1), np.minimum(xx * rw // W, rw - 1)]
        err = se[np.minimum(yy * rh // H, rh - 1), np.minimum(xx * rw // W, rw - 1)]
    # A dump from before the field was always the GI's (meta's fieldIsGi): the pixel's nearest texel's resolved light.
    if not meta.get("fieldIsGi"):
        print("   (the field may hold a texel view's colours in this dump: the GI is read as the nearest texel's resolved light)")
        field = np.where(ids >= 0, resolved[np.maximum(ids, 0)], np.nan)
    R = (slice(y0, y1), slice(x0, x1))
    fid, fld, rf, se = ids[R], field[R], ref[R], err[R]
    have = fid >= 0
    print(f"{d.name}: frame {meta['frame']}, rect ({x0},{y0})-({x1},{y1}) of {W}x{H}; {have.sum()} of {fid.size} pixels on lightmap texels; trace samples {meta['reference']['samples']}")
    if np.isfinite(rf).any():
        ok = np.isfinite(rf) & (rf > 1e-6) & np.isfinite(fld)
        rel = (fld[ok] - rf[ok]) / rf[ok]
        tell = np.abs(fld[ok] - rf[ok]) > 2 * se[ok]
        print(f"   GI {fld[ok].mean():.4f} against the trace {rf[ok].mean():.4f} ({100 * (fld[ok].mean() / rf[ok].mean() - 1):+.1f}%); per pixel |diff| {100 * np.abs(rel).mean():.1f}%,"
              f" past the trace's noise at {100 * tell.mean():.0f}% of pixels (its error {100 * np.median(se[ok] / rf[ok]):.1f}% a pixel)")
    u = np.unique(fid[have])
    if u.size:
        r, p = rays[u], prov[u]
        names = {0: "never", 1: "coarse sweep", 2: "refine", 3: "fine gather", 4: "visible-changed", 5: "fine copy", 6: "instant", 7: "ratio", 8: "provisional", 9: "CPU"}
        print(f"   {u.size} texels read: " + ", ".join(f"{names.get(int(k), k)} {np.sum(p == k)}" for k in np.unique(p)))
        g = (p >= 1) & (p <= 4)
        if g.any():
            vals, counts = np.unique(r[g], return_counts=True)
            print("   gathered texels by rays: " + ", ".join(f"{int(v)}: {c}" for v, c in zip(vals, counts)))
            lt_ = resolved[u]
            for v in vals:
                sel = g & (r == v)
                print(f"      {int(v):5d} rays: {sel.sum():6d} texels, their light mean {lt_[sel].mean():.4f}, spread (std/mean) {100 * lt_[sel].std() / max(lt_[sel].mean(), 1e-9):.1f}%")
    if maps:
        out = Path(maps); out.mkdir(parents=True, exist_ok=True)
        scale = np.nanpercentile(np.where(np.isfinite(rf), rf, fld), 99) or 1.0
        grey = lambda v: np.repeat(np.clip(v / scale, 0, 1)[..., None], 3, -1)
        ppm(out / "gi.ppm", grey(fld))
        if np.isfinite(rf).any():
            ppm(out / "trace.ppm", grey(np.nan_to_num(rf)))
            e = np.nan_to_num((fld - rf) / np.maximum(rf, 1e-6)); within = np.abs(fld - rf) <= 2 * np.nan_to_num(se)
            t = np.clip(np.abs(e) / 0.25, 0, 1)
            col = np.where((e > 0)[..., None], np.stack([np.ones_like(t), 1 - t, 1 - t], -1), np.stack([1 - t, 1 - t, np.ones_like(t)], -1))
            col[within] = 0.5
            ppm(out / "error.ppm", col)
        s = np.clip(np.log2(np.maximum(rays[np.maximum(fid, 0)], 1) / 128) / 3, 0, 1)
        rc = np.stack([s, 1 - np.abs(2 * s - 1), 1 - s], -1)
        pr = prov[np.maximum(fid, 0)]
        rc[(pr == 5)] = 0.35; rc[~have] = [0.6, 0, 0.6]
        ppm(out / "rays.ppm", rc)
        pal = np.array([[.15] * 3, [.1, .3, 1], [0, .85, .9], [.1, .9, .2], [1, .9, .1], [1, .5, .05], [.9, .1, .1], [1, .45, .75], [.6] * 3, [1] * 3])
        pc = pal[np.minimum(pr, 9)]; pc[~have] = [0.6, 0, 0.6]
        ppm(out / "provenance.ppm", pc)
        print(f"   maps: {out}/{{gi,trace,error,rays,provenance}}.ppm")


if __name__ == "__main__":
    main()
