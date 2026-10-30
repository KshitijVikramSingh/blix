# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The BSDF reference's camera paths as a picture, and the frame beside it (uv run tools/testbeds/bsdf_view.py DUMP OUT.png [--exposure EV]).

DUMP is a --view-dump directory of a run with --ref-view STRIDE (and usually --ref-bsdf): reference_view.f32x4 holds, per grid pixel, the radiance sum (rgb, x pi) and
its paths. The picture is that radiance (/ pi), scaled by 2^EV (default: the mean to 0.18), a plain sRGB curve, no tone map: what the reference sees, to look at.

With the frame's own HDR in the dump (a run without --viz: hdr.r11g11b10f is the lit pass, scene-linear, 32x down) it also writes OUT-compare.png (frame | reference | ratio, grey 1,
red brighter, blue darker, full at 50%) and prints how the two agree over the grid's pixels -- all of them, and the ones the reference's paths found no sky first on."""
import json, struct, sys, zlib
from pathlib import Path
import numpy as np


def write_png(path, rgb8):
    h, w, _ = rgb8.shape
    raw = b"".join(b"\x00" + rgb8[y].tobytes() for y in range(h))
    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    Path(path).write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))


def f11(v, mb):
    e = (v >> mb) & 31; mt = v & ((1 << mb) - 1)
    return np.where(e == 0, mt / (1 << mb) * 2.0 ** -14, (1 + mt / (1 << mb)) * 2.0 ** (e.astype(float) - 15))


def encode(rgb, scale):
    lin = np.clip(rgb * scale, 0, 1)
    return (np.where(lin <= 0.0031308, lin * 12.92, 1.055 * lin ** (1 / 2.4) - 0.055) * 255 + 0.5).astype(np.uint8)


def frame_hdr(d, m, rv):
    """The frame's lit pass at the reference grid's pixels (scene-linear), or None where the dump has none."""
    f = m.get("files", {}).get("hdr.r11g11b10f")
    if f is None or not (d / "hdr.r11g11b10f").exists(): return None
    hw, hh = f["width"], f["height"]
    w = np.frombuffer((d / "hdr.r11g11b10f").read_bytes(), np.uint32).reshape(hh, hw).astype(np.int64)
    lit = np.stack([f11(w & 2047, 6), f11((w >> 11) & 2047, 6), f11((w >> 22) & 1023, 5)], -1)
    gy, gx = np.mgrid[0:rv["height"], 0:rv["width"]]
    return lit[np.minimum(gy * rv["stride"], hh - 1), np.minimum(gx * rv["stride"], hw - 1)]


def main():
    d = Path(sys.argv[1]); out = sys.argv[2]
    ev = float(sys.argv[sys.argv.index("--exposure") + 1]) if "--exposure" in sys.argv else None
    m = json.loads((d / "meta.json").read_text()); rv = m["refView"]
    s = np.frombuffer((d / "reference_view.f32x4").read_bytes(), np.float32)[: rv["height"] * rv["width"] * 4].reshape(rv["height"], rv["width"], 4)
    rgb = s[..., :3] / np.maximum(s[..., 3:4], 1) / np.pi
    scale = 2.0 ** ev if ev is not None else 0.18 / max(float(rgb.mean()), 1e-6)
    lin = np.clip(rgb * scale, 0, 1)
    srgb = np.where(lin <= 0.0031308, lin * 12.92, 1.055 * lin ** (1 / 2.4) - 0.055)
    write_png(out, (srgb * 255 + 0.5).astype(np.uint8))
    print(f"{out}: {rv['width']}x{rv['height']}, mean {rgb.mean():.4f}, exposure x{scale:.3f}")
    frame = frame_hdr(d, m, rv)
    if frame is not None:
        lum = lambda c: c[..., 0] * 0.2126 + c[..., 1] * 0.7152 + c[..., 2] * 0.0722
        ratio = lum(frame) / np.maximum(lum(rgb), 1e-4)
        surface = lum(rgb) > 0.0  # every grid pixel; the sky's (a miss at once) reads as the sky cube in both
        print(f"  frame / reference: ratio of means {lum(frame).mean() / max(lum(rgb).mean(), 1e-9):.3f}, per-pixel median {np.median(ratio):.3f}, "
              f"p10 {np.percentile(ratio, 10):.3f}, p90 {np.percentile(ratio, 90):.3f}")
        # Where they part: the box round the pixels more than 15% apart is the object (a scene's floor and sky agree), and its own numbers are the ones that move.
        far = np.abs(ratio - 1.0) > 0.15
        if far.sum() > 20:
            ys, xs = np.where(far)
            box = (slice(max(ys.min() - 4, 0), ys.max() + 5), slice(max(xs.min() - 4, 0), xs.max() + 5))
            fb, rb = lum(frame)[box], lum(rgb)[box]
            print(f"  in the box round the {far.sum()} pixels that part by 15% ({box[1].start}..{box[1].stop} x {box[0].start}..{box[0].stop}): ratio of means "
                  f"{fb.mean() / max(rb.mean(), 1e-9):.3f}, per-pixel median {np.median(fb / np.maximum(rb, 1e-4)):.3f}, mean |error| {np.mean(np.abs(fb - rb) / np.maximum(rb, 1e-3)):.3f}")
        t = np.clip((ratio - 1.0) / 0.5, -1, 1)[..., None]
        heat = np.where(t > 0, 0.5 + 0.5 * (1 - t) * np.array([1, 1, 1]) * 0 + t * np.array([0.5, -0.5, -0.5]), 0.5 + (-t) * np.array([-0.5, -0.5, 0.5])) + 0.0
        heat = np.clip(np.where(t > 0, np.array([1, 1, 1]) * (1 - t) + np.array([1, 0, 0]) * t, np.array([1, 1, 1]) * (1 + t) + np.array([0, 0, 1]) * (-t)), 0, 1)
        write_png(out.replace(".png", "-compare.png"), np.concatenate([encode(frame, scale), encode(rgb, scale), (heat * 255 + 0.5).astype(np.uint8)], axis=1))


main()
