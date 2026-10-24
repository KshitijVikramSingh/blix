"""Export tone-mapped HDR stills a viewer can show with extended range: 16-bit PQ / Rec.2020 PNGs with a cICP chunk.

    uv run python -m tmlab.hdr_export [--anchor 250] [--factor 2] [--peaks 1000,1600]

cICP is the PNG chunk that says "these values are PQ-encoded BT.2020" (colour primaries 9, transfer 16, matrix 0,
full range), so a viewer that understands it treats the file as HDR. Which ones do is a property of the viewer and
the OS, not of this file, and this module cannot check it: open the files on the display and look. A control is
written beside each set, the same picture through GT7 SDR as an ordinary sRGB PNG.

What the numbers mean on screen: the file stores ABSOLUTE luminance (PQ), computed for the peak in its name. macOS
maps PQ onto what the panel can do at the current brightness setting, so how close a viewer comes to those nits
depends on its headroom. `--anchor` is the scene-white level (see pipeline.py): it decides where diffuse white sits.
"""
from __future__ import annotations

import argparse
import json
import shutil
import struct
import subprocess
import zlib
from pathlib import Path

import numpy as np

from . import REFERENCE_LUMINANCE, aces2, capture, color, pipeline
from .gt7 import GT7Params, GT7ToneMapping
from .pipeline import SceneConvention


def pq_encode(fb_rec2020) -> np.ndarray:
    """Linear Rec.2020 in frame-buffer units (1.0 = 100 nits) -> PQ signal, 0..1."""
    return np.clip(color.inverse_eotf_st2084(np.maximum(fb_rec2020, 0.0)), 0.0, 1.0)


def _chunk(kind: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)


def write_png16_cicp(path: Path, signal, primaries: int = 9, transfer: int = 16) -> None:
    """16-bit RGB PNG; `signal` is (h, w, 3) in 0..1. cICP precedes the image data, as the spec requires."""
    h, w, _ = signal.shape
    pix = np.round(np.clip(signal, 0.0, 1.0) * 65535.0).astype(">u2")
    raw = b"".join(b"\x00" + pix[y].tobytes() for y in range(h))
    ihdr = struct.pack(">IIBBBBB", w, h, 16, 2, 0, 0, 0)
    cicp = bytes([primaries, transfer, 0, 1])
    Path(path).write_bytes(b"\x89PNG\r\n\x1a\n" + _chunk(b"IHDR", ihdr) + _chunk(b"cICP", cicp)
                           + _chunk(b"IDAT", zlib.compress(raw, 6)) + _chunk(b"IEND", b""))


def convert_with_sips(png: Path, fmt: str) -> Path | None:
    """macOS `sips`: PNG -> heic/avif. Keeps the BT.2100 PQ tag and 10 bits (checked in test_hdr_export), and these are
    the HDR still formats Apple's viewers are most dependable with. None where sips is not available."""
    if shutil.which("sips") is None:
        return None
    dest = png.with_suffix(f".{fmt}")
    done = subprocess.run(["sips", "-s", "format", fmt, str(png), "--out", str(dest)], capture_output=True, text=True)
    return dest if done.returncode == 0 and dest.exists() else None


def write_png8_srgb(path: Path, linear709) -> None:
    x = np.clip(linear709, 0.0, 1.0)
    enc = np.where(x <= 0.0031308, 12.92 * x, 1.055 * np.power(x, 1 / 2.4) - 0.055)
    h, w, _ = enc.shape
    pix = np.round(enc * 255.0).astype(np.uint8)
    raw = b"".join(b"\x00" + pix[y].tobytes() for y in range(h))
    Path(path).write_bytes(b"\x89PNG\r\n\x1a\n" + _chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
                           + _chunk(b"IDAT", zlib.compress(raw, 6)) + _chunk(b"IEND", b""))


def read_png_chunks(path: Path):
    data = Path(path).read_bytes()
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    i, chunks = 8, []
    while i < len(data):
        n, kind = struct.unpack(">I4s", data[i:i + 8])
        chunks.append((kind.decode(), data[i + 8:i + 8 + n]))
        i += 12 + n
    return chunks


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--anchor", type=float, default=250.0)
    ap.add_argument("--factor", type=int, default=2)
    ap.add_argument("--peaks", default="1000,1600")
    ap.add_argument("--scenes", default="emissive_ramp__dim,materials_grid__grazing")
    ap.add_argument("--floor-label", default="8 params, floor 0.15")
    ap.add_argument("--formats", default="heic,avif", help="extra formats made with sips from each PNG (PNG is always written)")
    ap.add_argument("--data", default="data")
    ap.add_argument("--out", default="out/hdr_stills")
    a = ap.parse_args()
    out = Path(a.out); out.mkdir(parents=True, exist_ok=True)
    conv = SceneConvention(scene_white_nits=a.anchor)
    fit_params = GT7Params(**json.loads(Path("out/fit_hdr.json").read_text())[a.floor_label]["params"])
    peaks = [float(p) for p in a.peaks.split(",")]

    written = []
    for scene in a.scenes.split(","):
        cap = capture.load(Path(a.data) / f"{scene}.pfm").downsampled(a.factor)
        rgb = cap.rgb.astype(np.float64)
        for peak in peaks:
            for name, params in (("gt7", GT7Params()), ("tuned", fit_params)):
                tm = GT7ToneMapping.hdr_display(peak, params)
                fb = tm(pipeline.to_gt7_input(rgb, cap.exposure, conv))
                f = out / f"{scene}__{name}_hdr{peak:g}.png"
                write_png16_cicp(f, pq_encode(fb))
                written.append(f.name)
            if peak in aces2.HDR_STYLES:
                mid = float(GT7ToneMapping.hdr_display(peak)(pipeline.to_gt7_input(np.full(3, 0.18), 1.0, conv))[1])
                e = aces2.matching_exposure(lambda x: aces2.hdr(x, int(peak)), mid)
                f = out / f"{scene}__aces2_hdr{peak:g}.png"
                write_png16_cicp(f, pq_encode(aces2.hdr(rgb * cap.exposure * e, int(peak))))
                written.append(f.name)
        f = out / f"{scene}__control_sdr.png"
        write_png8_srgb(f, pipeline.gt7_sdr(rgb, exposure=cap.exposure, conv=conv))
        written.append(f.name)

    for name in list(written):
        if "__control_sdr" in name:
            continue
        for fmt in (x for x in a.formats.split(",") if x):
            if (dest := convert_with_sips(out / name, fmt)) is not None:
                written.append(dest.name)
    rows = "".join(f'<figure><img src="{n}" width="640"><figcaption>{n}</figcaption></figure>' for n in written
                   if n.endswith((".png", ".heic", ".avif")))
    (out / "index.html").write_text(
        f"<!doctype html><meta charset=utf-8><title>HDR stills</title><body style='background:#000;color:#ccc;font:13px sans-serif'>"
        f"<p>anchor {a.anchor:g} nits. PQ/Rec.2020 PNGs with cICP; whether they show as HDR depends on the viewer.</p>{rows}")
    (out / "exports.json").write_text(json.dumps({"anchor_nits": a.anchor, "files": written}, indent=1))
    print(f"wrote {len(written)} files to {out.resolve()}")
    for n in written:
        print("  ", n)


if __name__ == "__main__":
    main()
