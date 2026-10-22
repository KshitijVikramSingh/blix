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
a few thousand put a point within ~2-3%. --maps writes the ratio (grey 1, red brighter, blue darker, full at 50%).

The reference's materials are per triangle row (ray_surface_bake.comp: albedo, T and c, each a 16-point mean over the
triangle), the renderer's per pixel: on a patterned texture a point reads its pixel's texel against the row's mean.
DiffuseTransmissionTest's checker rows (T by the factor texture's alpha): the points fell where the alpha means 0.60,
not the panel's 0.50 -- +12-16% a row, and the checker's dark and light squares p90 ~80% -- with the renderer within
+-2% of its own pixels' T (2026-10-22). Judge textured surfaces by their means over many points, untextured ones by
the point."""
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
    s = np.frombuffer((d / "reference_view.f32x4").read_bytes(), np.float32)[:rv["height"] * rv["width"] * 4].reshape(rv["height"], rv["width"], 4)
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
    # --ref-view-records: lightmap records as surface points (the indirect light arriving, sum / paths), against the GI's
    # resolved light at each (resolved.u32x2: rgb9 with a shared exponent, as ray_capture.py reads it).
    if rv.get("records") and (d / "reference_records.u32").exists():
        ids = np.frombuffer((d / "reference_records.u32").read_bytes(), np.uint32).astype(np.int64)
        all4 = np.frombuffer((d / "reference_view.f32x4").read_bytes(), np.float32).reshape(-1, 4)
        extra = all4[rv["height"] * rv["width"]:][:len(ids)]
        E = lum(extra[:, :3] / np.maximum(extra[:, 3:4], 1))
        rw = np.frombuffer((d / "resolved.u32x2").read_bytes(), np.uint32).reshape(n, 2)[ids, 0].astype(np.uint64)
        sc = np.exp2((rw >> 27).astype(np.int64) - 24)
        Gr = (rw & 511) * sc * 0.2126 + ((rw >> 9) & 511) * sc * 0.7152 + ((rw >> 18) & 511) * sc * 0.0722
        wr = tex[ids, 3]
        print(f"   records as surface points ({len(ids)}): the GI's resolved light against the indirect irradiance traced there")
        # A pair's two records lie side by side (SponzaLoop.Lightmap.cs): the first and the second face, by their place.
        second = ((ids - m["lightmapFirstRecord"]) & 1) == 1
        for name, sel in (("opaque", np.abs(wr) < 4.5), ("lets light through, a pair's first", (np.abs(wr) > 4.5) & ~second),
                          ("lets light through, a pair's second", (np.abs(wr) > 4.5) & second)):
            sel = sel & (E > 1e-6)
            if sel.sum() < 3: continue
            r = Gr[sel] / E[sel]
            print(f"      {name:36s} {sel.sum():5d}: resolved {Gr[sel].mean():.4f} against {E[sel].mean():.4f} (ratio of sums {Gr[sel].sum() / E[sel].sum():.3f}, "
                  f"median {np.median(r):.3f}, p10 {np.percentile(r, 10):.3f}, p90 {np.percentile(r, 90):.3f})")
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
