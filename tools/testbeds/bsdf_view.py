# /// script
# requires-python = ">=3.10"
# dependencies = ["numpy"]
# ///
"""The BSDF reference's camera paths as a picture (uv run tools/testbeds/bsdf_view.py DUMP OUT.png [--exposure EV]).

DUMP is a --view-dump directory of a run with --ref-view STRIDE (and usually --ref-bsdf): reference_view.f32x4 holds, per grid pixel, the radiance sum (rgb, x pi) and
its paths. The picture is that radiance (/ pi), scaled by 2^EV (default: the mean to 0.18), a plain sRGB curve, no tone map: what the reference sees, to look at."""
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


main()
