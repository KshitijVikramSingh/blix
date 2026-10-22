# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""What the camera sees, against the path trace through the material (uv run tools/gi-model/view_reference.py DUMP
[--maps OUT]).

A view dump of a run with --viz 26 (the lit pass's "Diffuse response": the base lobe 1 - T and the sheet's transmission
T c, the direct sun on either side and the indirect light, no specular) and --ref-view STRIDE (SponzaLoop.ViewReference.cs:
camera paths through a grid of pixels, by the spec's rules). Per grid pixel the renderer's radiance against the
reference's, overall and by the surface the pixel's texel lies on: opaque, two-sided (a pair), and letting light through
(|w| 5, SponzaLoop.Lightmap.cs). The reference's own noise from its paths' spread is not kept: its paths are given, and
a few thousand put a point within ~2-3%. --maps writes the ratio (grey 1, red brighter, blue darker, full at 50%)."""
import json, sys
from pathlib import Path
import numpy as np


def f11(v, mb):
    e = (v >> mb) & 31; mt = v & ((1 << mb) - 1)
    return np.where(e == 0, mt / (1 << mb) * 2.0 ** -14, (1 + mt / (1 << mb)) * 2.0 ** (e.astype(float) - 15))


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    maps = sys.argv[sys.argv.index("--maps") + 1] if "--maps" in sys.argv else None
    if maps in args: args.remove(maps)
    d = Path(args[0])
    m = json.loads((d / "meta.json").read_text()); f = m["files"]; rv = m.get("refView")
    if not rv: raise SystemExit("no --ref-view in this dump")
    hw, hh = f["hdr.r11g11b10f"]["width"], f["hdr.r11g11b10f"]["height"]
    w = np.frombuffer((d / "hdr.r11g11b10f").read_bytes(), np.uint32).reshape(hh, hw).astype(np.int64)
    lit = np.stack([f11(w & 2047, 6), f11((w >> 11) & 2047, 6), f11((w >> 22) & 1023, 5)], -1)
    lum = lambda c: c[..., 0] * 0.2126 + c[..., 1] * 0.7152 + c[..., 2] * 0.0722
    s = np.frombuffer((d / "reference_view.f32x4").read_bytes(), np.float32).reshape(rv["height"], rv["width"], 4)
    paths = s[..., 3]
    ref = s[..., :3] / np.maximum(paths[..., None], 1) / np.pi
    st = rv["stride"]
    gy, gx = np.mgrid[0:rv["height"], 0:rv["width"]]
    py, px = np.minimum(gy * st, hh - 1), np.minimum(gx * st, hw - 1)
    g = lit[py, px]
    print(f"{d.name}: {rv['width']} x {rv['height']} points every {st} px, paths a point {int(np.median(paths))} "
          f"(frames {rv['frames']} of {rv['framesWanted']})")
    # The surface under each point: its lightmap record (opaque / pair / transmitting), or none (sky, unmapped).
    n = m["texelCount"]
    tex = np.frombuffer((d / "texels.f32x8").read_bytes(), np.float32).reshape(n, 8)
    lt = np.frombuffer((d / "lightmap_texel.r32u").read_bytes(), np.uint32).reshape(f["lightmap_texel.r32u"]["height"], f["lightmap_texel.r32u"]["width"])
    aw, ah = f["lightmap_index.r32u"]["width"], f["lightmap_index.r32u"]["height"]
    idx = np.frombuffer((d / "lightmap_index.r32u").read_bytes(), np.uint32).reshape(ah, aw)
    ltp = lt[np.minimum(py, lt.shape[0] - 1), np.minimum(px, lt.shape[1] - 1)]
    tx = np.clip(((ltp & 0xFFFF) / 8).astype(int), 0, aw - 1); ty = np.clip(((ltp >> 16) / 8).astype(int), 0, ah - 1)
    rec = idx[ty, tx].astype(np.int64); ok = (ltp != 0) & (rec != 0xFFFFFFFF)
    wv = np.where(ok, tex[np.where(ok, rec, 0), 3], 0)
    kind = np.where(~ok, 0, np.where(np.abs(wv) > 4.5, 3, np.where(wv < 0, 2, 1)))
    G, R = lum(g), lum(ref)
    valid = (paths > 0) & (R > 1e-5)
    for k, name in ((1, "opaque"), (2, "two-sided"), (3, "lets light through"), (0, "no lightmap texel")):
        sel = valid & (kind == k)
        if sel.sum() < 5: continue
        r = G[sel] / R[sel] - 1
        print(f"   {name:18s} {sel.sum():6d} points: renderer {G[sel].mean():.4f} against {R[sel].mean():.4f} ({100 * (G[sel].sum() / R[sel].sum() - 1):+.1f}%), "
              f"|error| median {100 * np.median(np.abs(r)):.1f}%, p90 {100 * np.percentile(np.abs(r), 90):.1f}%")
    if maps:
        out = Path(maps); out.mkdir(parents=True, exist_ok=True)
        e = np.where(valid, G / np.maximum(R, 1e-9) - 1, 0)
        t = np.clip(np.abs(e) / 0.5, 0, 1)
        col = np.where((e > 0)[..., None], np.stack([np.ones_like(t), 1 - t, 1 - t], -1), np.stack([1 - t, 1 - t, np.ones_like(t)], -1))
        col[~valid] = 0.2
        img = (np.clip(col, 0, 1) * 255).astype(np.uint8)
        (out / "view_reference_ratio.ppm").write_bytes(f"P6 {img.shape[1]} {img.shape[0]} 255\n".encode() + img.tobytes())
        print(f"   map: {out}/view_reference_ratio.ppm")


if __name__ == "__main__":
    main()
